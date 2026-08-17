using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Platform.Api.Infrastructure.Persistence;

namespace Platform.Integration.Tests;

/// <summary>
/// Covers Phase C of the feature-branch-builds plan — promotions from the synthetic
/// <c>build</c> source env:
/// <list type="bullet">
///   <item>Registering a build whose branch matches a policy's <c>autoCreateFromBranches</c>
///         auto-creates a candidate on that edge (D5); non-matching branches create nothing.</item>
///   <item><c>POST /api/promotions/from-build</c> is the human path: server-built change set from
///         the stored manifest (D13), triggered-by stamped, policy gate applies.</item>
///   <item><c>GET /api/promotions/build-targets</c> lists the resolvable build → * edges.</item>
///   <item><c>approvedWebhookDelaySeconds = 0</c> dispatches promotion.approved immediately (D12);
///         edges without an override keep the default undo window.</item>
/// </list>
/// </summary>
public class BuildPromotionFlowTests : IClassFixture<BuildPromotionFlowTests.BuildPromoFactory>, IDisposable
{
    public class BuildPromoFactory : TestFactory
    {
        public const string ApiKey = "build-promo-key-12345";

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("Deployments:ApiKeys:0:Name", "build-promo");
            builder.UseSetting("Deployments:ApiKeys:0:Key", ApiKey);
        }
    }

    private readonly BuildPromoFactory _factory;
    private readonly HttpClient _apiKeyClient;
    private readonly HttpClient _adminClient;

    public BuildPromotionFlowTests(BuildPromoFactory factory)
    {
        _factory = factory;
        _apiKeyClient = factory.CreateClient();
        _apiKeyClient.DefaultRequestHeaders.Add("X-Api-Key", BuildPromoFactory.ApiKey);
        _adminClient = factory.CreateAdminClient();
    }

    public void Dispose()
    {
        _apiKeyClient.Dispose();
        _adminClient.Dispose();
    }

    private static async Task<JsonElement> Deserialize(HttpResponseMessage response) =>
        JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());

    /// <summary>Creates a build → target policy via the admin API and returns its id.</summary>
    private async Task<string> SeedBuildPolicyAsync(
        string product, string targetEnv,
        string[]? autoCreateFromBranches = null,
        int? approvedWebhookDelaySeconds = null,
        bool tracksWorkItems = false,
        object[]? steps = null)
    {
        await _adminClient.PutAsJsonAsync("/api/features/features.promotions", new { enabled = true });

        var response = await _adminClient.PostAsJsonAsync("/api/promotions/admin/policies", new
        {
            product,
            service = (string?)null,
            sourceEnv = "build",
            targetEnv,
            steps = steps ?? [],
            tracksWorkItems,
            sourceRequiresDeploy = false,
            autoCreateFromBranches,
            approvedWebhookDelaySeconds,
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await Deserialize(response)).GetProperty("id").GetString()!;
    }

    private async Task<HttpResponseMessage> RegisterBuildAsync(
        string product, string service, string version, string branch, object? manifest = null)
        => await _apiKeyClient.PostAsJsonAsync("/api/builds", new
        {
            product,
            service,
            version,
            branch,
            commitSha = "1234567890abcdef1234567890abcdef12345678",
            buildUrl = "https://dev.azure.com/org/proj/_build/results?buildId=42",
            manifest,
            artifactRef = $"acr.example.io/{service}/build-metadata:{version}",
            artifactDigest = "sha256:feedface",
        });

    private async Task<List<JsonElement>> GetCandidatesAsync(string product)
    {
        var list = await _adminClient.GetFromJsonAsync<JsonElement>(
            $"/api/promotions?product={product}");
        return list.GetProperty("candidates").EnumerateArray().ToList();
    }

    // ── Auto-create from branch policy (D5) ─────────────────────────────────

    [Fact]
    public async Task MainBuild_AutoCreatesApprovedCandidate()
    {
        var product = "bp-auto";
        await SeedBuildPolicyAsync(product, "dev",
            autoCreateFromBranches: ["refs/heads/main", "refs/heads/master"],
            approvedWebhookDelaySeconds: 0);

        var response = await RegisterBuildAsync(product, "api", "1.0.1-gaaa", "refs/heads/master");
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var candidates = await GetCandidatesAsync(product);
        var candidate = Assert.Single(candidates);
        Assert.Equal("build", candidate.GetProperty("sourceEnv").GetString());
        Assert.Equal("dev", candidate.GetProperty("targetEnv").GetString());
        Assert.Equal("1.0.1-gaaa", candidate.GetProperty("version").GetString());
        // Auto-approve edge: born Approved, no human in the loop.
        Assert.Equal("Approved", candidate.GetProperty("status").GetString());
    }

    [Fact]
    public async Task FeatureBuild_CreatesNoCandidate()
    {
        var product = "bp-feature-noop";
        await SeedBuildPolicyAsync(product, "dev",
            autoCreateFromBranches: ["refs/heads/main", "refs/heads/master"]);

        var response = await RegisterBuildAsync(
            product, "api", "1.0.2-gbbb", "refs/heads/feature/MPT-1234-shiny");
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        Assert.Empty(await GetCandidatesAsync(product));
    }

    [Fact]
    public async Task ReleaseWildcardPattern_Matches()
    {
        var product = "bp-wildcard";
        await SeedBuildPolicyAsync(product, "dev",
            autoCreateFromBranches: ["refs/heads/release/*"]);

        await RegisterBuildAsync(product, "api", "4.2.0-gccc", "refs/heads/release/4");

        var candidate = Assert.Single(await GetCandidatesAsync(product));
        Assert.Equal("4.2.0-gccc", candidate.GetProperty("version").GetString());
    }

    [Fact]
    public async Task RegistrationReplay_DoesNotDuplicateCandidates()
    {
        var product = "bp-replay";
        await SeedBuildPolicyAsync(product, "dev",
            autoCreateFromBranches: ["refs/heads/main"]);

        var first = await RegisterBuildAsync(product, "api", "5.0.0-g111", "refs/heads/main");
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var replay = await RegisterBuildAsync(product, "api", "5.0.0-g111", "refs/heads/main");
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);

        // The hook re-ran on the replay (that's deliberate) but reused the candidate by natural key.
        Assert.Single(await GetCandidatesAsync(product));
    }

    // ── Deploy-a-build (human path) ─────────────────────────────────────────

    [Fact]
    public async Task FromBuild_CreatesCandidateWithManifestReferences()
    {
        var product = "bp-manual";
        await SeedBuildPolicyAsync(product, "test",
            tracksWorkItems: true,
            steps: [new { name = "QA", requirements = new[] { new { name = "qa", users = new[] { "qa@localhost" }, minApprovers = 1 } } }]);

        var manifest = new
        {
            apiVersion = "v1-beta",
            spec = new { service = "api", version = "2.0.0-gddd" },
            references = new Dictionary<string, object>
            {
                ["repository"] = new { branch = "refs/heads/feature/MPT-77-x", revision = "abc123", url = "https://dev.azure.com/org/repo" },
                ["work-item"] = new { key = "MPT-77", url = "https://jira.example.com/browse/MPT-77", title = "Shiny widget" },
            },
        };
        var register = await RegisterBuildAsync(
            product, "api", "2.0.0-gddd", "refs/heads/feature/MPT-77-x", manifest);
        Assert.Equal(HttpStatusCode.Created, register.StatusCode);
        var buildId = (await Deserialize(register)).GetProperty("id").GetString();

        // Nothing auto-created (no branch patterns on the edge).
        Assert.Empty(await GetCandidatesAsync(product));

        var create = await _adminClient.PostAsJsonAsync("/api/promotions/from-build",
            new { buildId, targetEnv = "test" });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var created = await Deserialize(create);
        // Gated edge: parks in Pending until QA signs off.
        Assert.Equal("Pending", created.GetProperty("status").GetString());

        var detail = await _adminClient.GetFromJsonAsync<JsonElement>(
            $"/api/promotions/{created.GetProperty("id").GetString()}");
        var candidate = detail.GetProperty("candidate");
        // The candidate is self-contained; its change set surfaces as sourceEventReferences.
        var references = candidate.GetProperty("sourceEventReferences").EnumerateArray().ToList();

        // Manifest references copied through (D13)…
        Assert.Contains(references, r =>
            r.GetProperty("type").GetString() == "work-item" && r.GetProperty("key").GetString() == "MPT-77");
        Assert.Contains(references, r => r.GetProperty("type").GetString() == "repository");
        // …plus the OCI pointer the deploy workflow pulls by.
        Assert.Contains(references, r =>
            r.GetProperty("type").GetString() == "build-manifest"
            && r.GetProperty("revision").GetString() == "sha256:feedface");

        // The caller is stamped as triggered-by.
        var participants = candidate.GetProperty("participants").EnumerateArray().ToList();
        Assert.Contains(participants, p => p.GetProperty("role").GetString() == "triggered-by");
    }

    [Fact]
    public async Task FromBuild_UnknownTargetEnv_Returns422PolicyMissing()
    {
        var product = "bp-notarget";
        await SeedBuildPolicyAsync(product, "dev");
        var register = await RegisterBuildAsync(product, "api", "3.0.0-geee", "refs/heads/feature/x");
        var buildId = (await Deserialize(register)).GetProperty("id").GetString();

        var create = await _adminClient.PostAsJsonAsync("/api/promotions/from-build",
            new { buildId, targetEnv = "staging" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, create.StatusCode);
        Assert.Equal("policy_missing", (await Deserialize(create)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task FromBuild_UnknownBuild_Returns404()
    {
        var create = await _adminClient.PostAsJsonAsync("/api/promotions/from-build",
            new { buildId = Guid.NewGuid(), targetEnv = "dev" });
        Assert.Equal(HttpStatusCode.NotFound, create.StatusCode);
    }

    // ── Build-targets read surface ──────────────────────────────────────────

    [Fact]
    public async Task BuildTargets_ListsResolvedEdgesWithAutoApproveFlag()
    {
        var product = "bp-targets";
        await SeedBuildPolicyAsync(product, "dev"); // auto-approve (no steps)
        await SeedBuildPolicyAsync(product, "test",
            steps: [new { name = "QA", requirements = new[] { new { name = "qa", users = new[] { "qa@localhost" }, minApprovers = 1 } } }]);

        var response = await _adminClient.GetFromJsonAsync<JsonElement>(
            $"/api/promotions/build-targets?product={product}&service=api");
        var targets = response.GetProperty("targets").EnumerateArray().ToList();

        Assert.Equal(2, targets.Count);
        Assert.Contains(targets, t =>
            t.GetProperty("targetEnv").GetString() == "dev" && t.GetProperty("autoApprove").GetBoolean());
        Assert.Contains(targets, t =>
            t.GetProperty("targetEnv").GetString() == "test" && !t.GetProperty("autoApprove").GetBoolean());
    }

    // ── Per-edge approved-webhook delay (D12) ───────────────────────────────

    [Fact]
    public async Task ApprovedWebhookDelayZero_QueuesDeliveryImmediately()
    {
        var product = "bp-delay";
        await SeedBuildPolicyAsync(product, "dev",
            autoCreateFromBranches: ["refs/heads/main"],
            approvedWebhookDelaySeconds: 0);

        // A subscription so promotion.approved produces a delivery row to inspect.
        var sub = await _adminClient.PostAsJsonAsync("/api/webhooks", new
        {
            name = "bp-delay-sub",
            url = "https://example.invalid/hook",
            secret = "test-secret-123",
            events = new[] { "promotion.approved" },
            filterProduct = product,
            active = true,
        });
        Assert.Equal(HttpStatusCode.Created, sub.StatusCode);

        var before = DateTimeOffset.UtcNow;
        await RegisterBuildAsync(product, "api", "9.0.0-gfff", "refs/heads/main");

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var delivery = await db.WebhookDeliveries.AsNoTracking()
            .Where(d => d.EventType == "promotion.approved")
            .OrderByDescending(d => d.CreatedAt)
            .FirstOrDefaultAsync();

        Assert.NotNull(delivery);
        // Delay 0 ⇒ NextRetryAt is "now", not now + the default 10s undo window.
        Assert.NotNull(delivery!.NextRetryAt);
        Assert.True(delivery.NextRetryAt < before.AddSeconds(5),
            $"expected immediate dispatch, got NextRetryAt={delivery.NextRetryAt:o} (before={before:o})");
    }

    // ── Policy admin round-trip ─────────────────────────────────────────────

    [Fact]
    public async Task PolicyAdmin_RoundTripsAutoCreateAndDelay()
    {
        var id = await SeedBuildPolicyAsync("bp-roundtrip", "dev",
            autoCreateFromBranches: ["refs/heads/main", " ", "refs/heads/main"], // blank + dupe dropped
            approvedWebhookDelaySeconds: 30);

        var policy = await _adminClient.GetFromJsonAsync<JsonElement>(
            $"/api/promotions/admin/policies/{id}");
        var patterns = policy.GetProperty("autoCreateFromBranches").EnumerateArray()
            .Select(p => p.GetString()).ToList();

        Assert.Equal(["refs/heads/main"], patterns);
        Assert.Equal(30, policy.GetProperty("approvedWebhookDelaySeconds").GetInt32());
    }

    [Fact]
    public async Task PolicyAdmin_RejectsOutOfRangeDelay()
    {
        await _adminClient.PutAsJsonAsync("/api/features/features.promotions", new { enabled = true });
        var response = await _adminClient.PostAsJsonAsync("/api/promotions/admin/policies", new
        {
            product = "bp-badpolicy",
            sourceEnv = "build",
            targetEnv = "dev",
            approvedWebhookDelaySeconds = -1,
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
