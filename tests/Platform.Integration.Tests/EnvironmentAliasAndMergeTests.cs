using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;

namespace Platform.Integration.Tests;

/// <summary>
/// End-to-end tests for environment consolidation: aliases fix what arrives next, the merge brings
/// the history that arrived under the old names across, and the two together are what turn three
/// pipelines' worth of names for one environment into one column.
///
/// <para>Each test that writes uses its own factory (and therefore its own pristine SQLite database)
/// — a merge rewrites the shared settings row and eleven tables, so tests sharing one database would
/// see each other's environments.</para>
/// </summary>
public class EnvironmentAliasAndMergeTests
{
    private const string TestApiKey = "test-env-alias-key";

    // ── Alias configuration ─────────────────────────────────────────────────

    [Fact]
    public async Task SaveSettings_RoundTripsAliases()
    {
        using var f = new EnvFactory();
        using var admin = f.CreateAdminClient();

        await SaveEnvironmentsAsync(admin, new { key = "prod", displayName = "Production", aliases = new[] { "production", "prd" } });

        var prod = await GetEnvironmentAsync(admin, "prod");
        Assert.Equal(["production", "prd"], Strings(prod.GetProperty("aliases")));
    }

    [Fact]
    public async Task SaveSettings_DropsBlankDuplicateAndSelfReferentialAliases()
    {
        using var f = new EnvFactory();
        using var admin = f.CreateAdminClient();

        await SaveEnvironmentsAsync(admin, new
        {
            key = "prod",
            displayName = "Production",
            aliases = new[] { "production", "  ", "production", "PROD", "prd" },
        });

        var prod = await GetEnvironmentAsync(admin, "prod");
        // Redundancy is cleaned rather than rejected — the editor lets an admin type freely.
        Assert.Equal(["production", "prd"], Strings(prod.GetProperty("aliases")));
    }

    [Fact]
    public async Task SaveSettings_RejectsAnAliasThatIsAlsoAnotherEnvironment()
    {
        using var f = new EnvFactory();
        using var admin = f.CreateAdminClient();

        var response = await PutEnvironmentsAsync(admin,
            new { key = "prod", displayName = "Production", aliases = new[] { "production" } },
            new { key = "production", displayName = "Production (old)", aliases = Array.Empty<string>() });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await ReadBody(response);
        // The refusal has to name the way out, or the admin is stuck holding two environments.
        Assert.Contains("Merge", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task SaveSettings_RejectsAnAliasClaimedByTwoEnvironments()
    {
        using var f = new EnvFactory();
        using var admin = f.CreateAdminClient();

        var response = await PutEnvironmentsAsync(admin,
            new { key = "prod", displayName = "Production", aliases = new[] { "live" } },
            new { key = "staging", displayName = "Staging", aliases = new[] { "live" } });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ── Ingest resolution ───────────────────────────────────────────────────

    [Fact]
    public async Task Ingest_UnderAnAlias_LandsOnTheCanonicalEnvironment()
    {
        using var f = new EnvFactory();
        using var admin = f.CreateAdminClient();
        using var apiKey = f.CreateApiKeyClient();

        await SaveEnvironmentsAsync(admin, new { key = "prod", displayName = "Production", aliases = new[] { "production", "productions" } });

        await IngestAsync(apiKey, environment: "productions", version: "v1.0.0");
        await IngestAsync(apiKey, environment: "production", version: "v1.1.0", deployedAt: "2026-04-17T10:00:00Z");

        // One environment on the matrix, not three — and the filter accepts any of the names.
        var underAlias = await Deserialize(await admin.GetAsync("/api/deployments/state?product=acme&environment=productions"));
        var underKey = await Deserialize(await admin.GetAsync("/api/deployments/state?product=acme&environment=prod"));

        Assert.Equal(1, underKey.GetArrayLength());
        Assert.Equal(1, underAlias.GetArrayLength());
        Assert.Equal("prod", underKey[0].GetProperty("environment").GetString());
        Assert.Equal("v1.1.0", underAlias[0].GetProperty("version").GetString());
    }

    // ── Usage ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Usage_ShowsEveryNameInTheDataAndFlagsUnmergedAliases()
    {
        using var f = new EnvFactory();
        using var admin = f.CreateAdminClient();
        using var apiKey = f.CreateApiKeyClient();

        // Deploys land BEFORE the alias exists — the ordinary case, and the reason the merge exists.
        await IngestAsync(apiKey, environment: "productions", version: "v1.0.0");
        await IngestAsync(apiKey, environment: "prod", version: "v1.0.0");
        await SaveEnvironmentsAsync(admin, new { key = "prod", displayName = "Production", aliases = new[] { "productions" } });

        var rows = (await Deserialize(await admin.GetAsync("/api/settings/environments/usage")))
            .EnumerateArray()
            .ToDictionary(r => r.GetProperty("key").GetString()!, r => r);

        Assert.Equal(1, rows["prod"].GetProperty("deployments").GetInt32());
        Assert.True(rows["prod"].GetProperty("configured").GetBoolean());
        Assert.Equal(JsonValueKind.Null, rows["prod"].GetProperty("resolvesTo").ValueKind);

        // The tell that the alias is in place but the history has not followed.
        Assert.Equal(1, rows["productions"].GetProperty("deployments").GetInt32());
        Assert.Equal("prod", rows["productions"].GetProperty("resolvesTo").GetString());
        Assert.False(rows["productions"].GetProperty("configured").GetBoolean());
    }

    [Fact]
    public async Task Usage_IsAdminOnly()
    {
        using var f = new EnvFactory();
        using var user = f.CreateAuthenticatedClient("user@localhost", "user123");

        Assert.Equal(HttpStatusCode.Forbidden, (await user.GetAsync("/api/settings/environments/usage")).StatusCode);
    }

    // ── Merge ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task MergePreview_CountsWithoutMoving()
    {
        using var f = new EnvFactory();
        using var admin = f.CreateAdminClient();
        using var apiKey = f.CreateApiKeyClient();

        await IngestAsync(apiKey, environment: "productions", version: "v1.0.0");
        await IngestAsync(apiKey, environment: "productions", service: "web", version: "v2.0.0");

        var plan = await Deserialize(await admin.PostAsJsonAsync(
            "/api/settings/environments/merge/preview",
            new { into = "prod", from = new[] { "productions" } }));

        Assert.False(plan.GetProperty("applied").GetBoolean());
        Assert.Equal(2, plan.GetProperty("counts").GetProperty("deployments").GetInt32());
        Assert.Equal(2, plan.GetProperty("moved").GetInt32());

        // Nothing moved: the rows are still under the old name.
        var stillThere = await Deserialize(await admin.GetAsync("/api/settings/environments/usage"));
        Assert.Contains(stillThere.EnumerateArray(), r => r.GetProperty("key").GetString() == "productions");
    }

    [Fact]
    public async Task Merge_MovesHistoryRecordsTheAliasAndDropsTheDuplicateEnvironment()
    {
        using var f = new EnvFactory();
        using var admin = f.CreateAdminClient();
        using var apiKey = f.CreateApiKeyClient();

        // Both names configured — the state an admin is actually in before consolidating.
        await SaveEnvironmentsAsync(admin,
            new { key = "productions", displayName = "Productions", color = (string?)"#111111", aliases = Array.Empty<string>() },
            new { key = "prod", displayName = "Production", color = (string?)"#dc2626", aliases = Array.Empty<string>() });

        await IngestAsync(apiKey, environment: "productions", version: "v1.0.0");
        await IngestAsync(apiKey, environment: "prod", service: "web", version: "v2.0.0");

        var result = await Deserialize(await admin.PostAsJsonAsync(
            "/api/settings/environments/merge",
            new { into = "prod", from = new[] { "productions" } }));

        Assert.True(result.GetProperty("applied").GetBoolean());
        Assert.Equal(1, result.GetProperty("counts").GetProperty("deployments").GetInt32());
        Assert.Equal(0, result.GetProperty("leftBehind").GetInt32());
        Assert.Equal(["productions"], Strings(result.GetProperty("removedEnvironments")));

        // The data moved…
        var usage = (await Deserialize(await admin.GetAsync("/api/settings/environments/usage")))
            .EnumerateArray().ToDictionary(r => r.GetProperty("key").GetString()!, r => r);
        Assert.Equal(2, usage["prod"].GetProperty("deployments").GetInt32());
        Assert.DoesNotContain("productions", usage.Keys);

        // …and the settings row moved with it: alias recorded, duplicate environment gone. Doing
        // only one of the two would leave a state the settings PUT itself refuses to hold.
        var environments = (await Deserialize(await admin.GetAsync("/api/settings"))).GetProperty("environments");
        Assert.Single(environments.EnumerateArray());
        var prod = environments[0];
        Assert.Equal("prod", prod.GetProperty("key").GetString());
        Assert.Equal(["productions"], Strings(prod.GetProperty("aliases")));
        // The admin's own label and colour for the target survive.
        Assert.Equal("Production", prod.GetProperty("displayName").GetString());
        Assert.Equal("#dc2626", prod.GetProperty("color").GetString());
    }

    [Fact]
    public async Task Merge_IntoAnUnconfiguredKey_InheritsTheMergedEnvironmentsAppearance()
    {
        using var f = new EnvFactory();
        using var admin = f.CreateAdminClient();
        using var apiKey = f.CreateApiKeyClient();

        await SaveEnvironmentsAsync(admin,
            new { key = "productions", displayName = "Productions", color = (string?)"#123456", isProduction = true, aliases = Array.Empty<string>() });
        await IngestAsync(apiKey, environment: "productions", version: "v1.0.0");

        await admin.PostAsJsonAsync("/api/settings/environments/merge", new { into = "prod", from = new[] { "productions" } });

        var environments = (await Deserialize(await admin.GetAsync("/api/settings"))).GetProperty("environments");
        var prod = Assert.Single(environments.EnumerateArray());
        Assert.Equal("prod", prod.GetProperty("key").GetString());
        // A merge into a key that only ever arrived from pipelines must not come out unconfigured.
        Assert.Equal("Productions", prod.GetProperty("displayName").GetString());
        Assert.Equal("#123456", prod.GetProperty("color").GetString());
        Assert.True(prod.GetProperty("isProduction").GetBoolean());
    }

    [Fact]
    public async Task Merge_AfterwardsTheOldNameKeepsLandingOnTheTarget()
    {
        using var f = new EnvFactory();
        using var admin = f.CreateAdminClient();
        using var apiKey = f.CreateApiKeyClient();

        await IngestAsync(apiKey, environment: "productions", version: "v1.0.0");
        await admin.PostAsJsonAsync("/api/settings/environments/merge", new { into = "prod", from = new[] { "productions" } });

        // The point of recording the alias: the pipeline nobody has updated yet still lands on prod.
        await IngestAsync(apiKey, environment: "productions", version: "v1.1.0", deployedAt: "2026-04-17T10:00:00Z");

        var usage = (await Deserialize(await admin.GetAsync("/api/settings/environments/usage")))
            .EnumerateArray().ToDictionary(r => r.GetProperty("key").GetString()!, r => r);
        Assert.Equal(2, usage["prod"].GetProperty("deployments").GetInt32());
        Assert.DoesNotContain("productions", usage.Keys);
    }

    [Fact]
    public async Task Merge_WithoutRecordingAliases_LeavesTheEnvironmentListAlone()
    {
        using var f = new EnvFactory();
        using var admin = f.CreateAdminClient();
        using var apiKey = f.CreateApiKeyClient();

        await SaveEnvironmentsAsync(admin,
            new { key = "productions", displayName = "Productions", aliases = Array.Empty<string>() });
        await IngestAsync(apiKey, environment: "productions", version: "v1.0.0");

        var result = await Deserialize(await admin.PostAsJsonAsync(
            "/api/settings/environments/merge",
            new { into = "prod", from = new[] { "productions" }, recordAliases = false }));

        Assert.False(result.GetProperty("aliasesRecorded").GetBoolean());
        Assert.Empty(result.GetProperty("removedEnvironments").EnumerateArray());

        // History moved, configuration untouched — so the next deploy under the old name splits it
        // again. That is the documented cost of opting out.
        var environments = (await Deserialize(await admin.GetAsync("/api/settings"))).GetProperty("environments");
        Assert.Equal("productions", Assert.Single(environments.EnumerateArray()).GetProperty("key").GetString());
    }

    [Fact]
    public async Task Merge_LeavesPromotionPoliciesWhoseBothEndsWouldBecomeTheTarget()
    {
        using var f = new EnvFactory();
        using var admin = f.CreateAdminClient();

        // A policy from one name of the environment to another name of the same one. Nonsense already,
        // but it exists in the wild, and the merge must neither rewrite it into a self-edge nor report
        // a different number of them in the preview than in the apply.
        await CreatePolicyAsync(admin, "production", "productions");
        await CreatePolicyAsync(admin, "staging", "productions");

        var preview = await Deserialize(await admin.PostAsJsonAsync(
            "/api/settings/environments/merge/preview",
            new { into = "prod", from = new[] { "production", "productions" } }));
        var applied = await Deserialize(await admin.PostAsJsonAsync(
            "/api/settings/environments/merge",
            new { into = "prod", from = new[] { "production", "productions" } }));

        // Counted once for the whole merge, not once per source, and the same both times.
        Assert.Equal(1, preview.GetProperty("counts").GetProperty("degenerateEdges").GetInt32());
        Assert.Equal(1, applied.GetProperty("counts").GetProperty("degenerateEdges").GetInt32());
        Assert.Equal(1, preview.GetProperty("counts").GetProperty("promotionPolicies").GetInt32());
        Assert.Equal(1, applied.GetProperty("counts").GetProperty("promotionPolicies").GetInt32());

        var policies = (await Deserialize(await admin.GetAsync("/api/promotions/admin/policies")))
            .GetProperty("policies").EnumerateArray()
            .Select(p => $"{p.GetProperty("sourceEnv").GetString()}->{p.GetProperty("targetEnv").GetString()}")
            .ToList();

        // The healthy edge followed; the degenerate one was left exactly as it was.
        Assert.Contains("staging->prod", policies);
        Assert.Contains("production->productions", policies);
    }

    [Fact]
    public async Task Merge_MovesThePerEnvironmentReleaseNoteTemplate()
    {
        using var f = new EnvFactory();
        using var admin = f.CreateAdminClient();

        await SaveTemplateAsync(admin, product: "acme", environment: "productions", template: "# productions notes");
        // A product-scoped template for a product that happens to be named like the environment must
        // NOT be mistaken for a per-environment one.
        await SaveTemplateAsync(admin, product: "productions", environment: null, template: "# product notes");

        var applied = await Deserialize(await admin.PostAsJsonAsync(
            "/api/settings/environments/merge",
            new { into = "prod", from = new[] { "productions" } }));

        Assert.Equal(1, applied.GetProperty("counts").GetProperty("releaseNoteTemplates").GetInt32());
        Assert.Equal("# productions notes", await GetTemplateAsync(admin, "acme", "prod"));
        Assert.Equal("# product notes", await GetTemplateAsync(admin, "productions", null));
    }

    [Fact]
    public async Task Merge_RejectsAnEmptyRequest()
    {
        using var f = new EnvFactory();
        using var admin = f.CreateAdminClient();

        Assert.Equal(HttpStatusCode.BadRequest,
            (await admin.PostAsJsonAsync("/api/settings/environments/merge", new { into = "", from = new[] { "prod" } })).StatusCode);

        // Merging an environment into itself is nothing to do, not an error worth performing.
        Assert.Equal(HttpStatusCode.BadRequest,
            (await admin.PostAsJsonAsync("/api/settings/environments/merge", new { into = "prod", from = new[] { "prod" } })).StatusCode);
    }

    [Fact]
    public async Task Merge_IsAdminOnly()
    {
        using var f = new EnvFactory();
        using var user = f.CreateAuthenticatedClient("user@localhost", "user123");

        var response = await user.PostAsJsonAsync(
            "/api/settings/environments/merge", new { into = "prod", from = new[] { "production" } });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static Task<HttpResponseMessage> PutEnvironmentsAsync(HttpClient admin, params object[] environments)
        => admin.PutAsJsonAsync("/api/settings", new
        {
            environments,
            roles = Array.Empty<object>(),
            activityTemplate = Array.Empty<object>(),
        });

    private static async Task SaveEnvironmentsAsync(HttpClient admin, params object[] environments)
    {
        var response = await PutEnvironmentsAsync(admin, environments);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    private static async Task<JsonElement> GetEnvironmentAsync(HttpClient admin, string key)
    {
        var body = await Deserialize(await admin.GetAsync("/api/settings"));
        return body.GetProperty("environments").EnumerateArray()
            .Single(e => e.GetProperty("key").GetString() == key);
    }

    private static async Task IngestAsync(
        HttpClient apiKey,
        string environment,
        string version,
        string product = "acme",
        string service = "api",
        string deployedAt = "2026-04-16T10:00:00Z")
    {
        var response = await apiKey.PostAsJsonAsync("/api/deployments/events", new
        {
            product,
            service,
            environment,
            version,
            source = "ci",
            deployedAt,
            status = "succeeded",
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    private static async Task CreatePolicyAsync(HttpClient admin, string sourceEnv, string targetEnv)
    {
        var response = await admin.PostAsJsonAsync("/api/promotions/admin/policies", new
        {
            product = "acme",
            service = "api",
            sourceEnv,
            targetEnv,
            steps = Array.Empty<object>(),
            escalationGroup = (string?)null,
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    private static async Task SaveTemplateAsync(
        HttpClient admin, string product, string? environment, string template)
    {
        var response = await admin.PutAsJsonAsync("/api/release-notes/template",
            new { product, environment, template });
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    private static async Task<string?> GetTemplateAsync(HttpClient admin, string product, string? environment)
    {
        var query = $"?product={Uri.EscapeDataString(product)}&exact=true"
                  + (environment is null ? "" : $"&environment={Uri.EscapeDataString(environment)}");
        var body = await Deserialize(await admin.GetAsync("/api/release-notes/template" + query));
        return body.GetProperty("template").GetString();
    }

    private static async Task<JsonElement> Deserialize(HttpResponseMessage response)
    {
        response.EnsureSuccessStatusCode();
        return await ReadBody(response);
    }

    /// <summary>Body of a response whose status the caller has already asserted — including a 4xx.</summary>
    private static async Task<JsonElement> ReadBody(HttpResponseMessage response)
    {
        var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        return doc.RootElement;
    }

    private static string[] Strings(JsonElement array)
        => [.. array.EnumerateArray().Select(e => e.GetString() ?? "")];

    public class EnvFactory : TestFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("Deployments:ApiKeys:0:Name", "test-key");
            builder.UseSetting("Deployments:ApiKeys:0:Key", TestApiKey);
        }

        public HttpClient CreateApiKeyClient()
        {
            var client = CreateClient();
            client.DefaultRequestHeaders.Add("X-Api-Key", TestApiKey);
            return client;
        }
    }
}
