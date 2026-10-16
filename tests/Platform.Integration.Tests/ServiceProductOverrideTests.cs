using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;

namespace Platform.Integration.Tests;

/// <summary>
/// Filing a service under the product an admin says it belongs to, rather than the one its pipeline
/// posted.
///
/// <para>Exercised over HTTP end to end, for the same reason the soft-delete suite is: the override is
/// applied inside the create paths, and the only assertion that means anything is that a deploy event,
/// a build and a promotion posted with the wrong product come back out of the read APIs under the
/// right one. A unit test on the resolver would prove the lookup and none of the wiring — and the
/// wiring is where "the build went to one product and the deploy event to another" comes from.</para>
///
/// <para>The remap half is tested by ingesting history <i>before</i> the override exists, which is the
/// real sequence: the mapping is always written after somebody notices the mess.</para>
/// </summary>
public class ServiceProductOverrideTests
    : IClassFixture<ServiceProductOverrideTests.OverrideFactory>, IDisposable
{
    private const string TestApiKey = "test-product-override-key-12345";

    private readonly OverrideFactory _factory;
    private readonly HttpClient _apiKeyClient;
    private readonly HttpClient _adminClient;

    public ServiceProductOverrideTests(OverrideFactory factory)
    {
        _factory = factory;
        _apiKeyClient = factory.CreateClient();
        _apiKeyClient.DefaultRequestHeaders.Add("X-Api-Key", TestApiKey);
        _adminClient = factory.CreateAdminClient();
    }

    public void Dispose()
    {
        _apiKeyClient.Dispose();
        _adminClient.Dispose();
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    /// <summary>Distinct product names per test — the suite shares one database.</summary>
    private static (string Old, string New) Products()
    {
        var suffix = $"{Guid.NewGuid():N}"[..8];
        return ($"legacy-{suffix}", $"target-{suffix}");
    }

    private async Task IngestAsync(
        string product, string service, string env = "staging", string version = "v1.0.0",
        DateTimeOffset? deployedAt = null)
    {
        var res = await _apiKeyClient.PostAsJsonAsync("/api/deployments/events", new
        {
            product,
            service,
            environment = env,
            version,
            source = "ci",
            deployedAt = deployedAt ?? DateTimeOffset.UtcNow,
            status = "succeeded",
        });
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
    }

    private Task<HttpResponseMessage> RegisterBuildAsync(
        string product, string service, string version = "v1.0.0", string branch = "refs/heads/main") =>
        _apiKeyClient.PostAsJsonAsync("/api/builds", new
        {
            product,
            service,
            version,
            branch,
            commitSha = "0123456789abcdef0123456789abcdef01234567",
            buildId = "42",
        });

    private Task<HttpResponseMessage> SaveOverrideAsync(
        string service, string product, string? fromProduct = null, string? reason = null) =>
        _adminClient.PostAsJsonAsync("/api/deployments/admin/product-overrides",
            new { service, product, fromProduct, reason });

    private async Task<List<string>> MatrixServicesAsync(string product)
    {
        var body = await _adminClient.GetFromJsonAsync<JsonElement>(
            $"/api/deployments/state?product={product}");
        return body.EnumerateArray().Select(s => s.GetProperty("service").GetString()!).ToList();
    }

    private async Task<List<string>> BuildVersionsAsync(string product, string service)
    {
        var body = await _adminClient.GetFromJsonAsync<JsonElement>(
            $"/api/builds?product={product}&service={service}");
        return body.GetProperty("results").EnumerateArray()
            .Select(b => b.GetProperty("version").GetString()!)
            .ToList();
    }

    private async Task<JsonElement> ListOverridesAsync()
        => await _adminClient.GetFromJsonAsync<JsonElement>("/api/deployments/admin/product-overrides");

    private async Task<Guid> OverrideIdAsync(string service)
    {
        var rows = await ListOverridesAsync();
        return rows.EnumerateArray()
            .Where(r => r.GetProperty("service").GetString() == service)
            .Select(r => r.GetProperty("id").GetGuid())
            .First();
    }

    // ── Applying the override on the way in ─────────────────────────────────

    /// <summary>
    /// The headline case: one catch-all row, and a pipeline that keeps posting the old product stops
    /// being able to put the service there at all.
    /// </summary>
    [Fact]
    public async Task CatchAllOverride_FilesDeploysUnderTheAdminsProduct()
    {
        var (old, target) = Products();

        (await SaveOverrideAsync("swo-extension-mscsp", target, reason: "MPT migration")).EnsureSuccessStatusCode();
        await IngestAsync(old, "swo-extension-mscsp");

        Assert.Contains("swo-extension-mscsp", await MatrixServicesAsync(target));
        Assert.DoesNotContain("swo-extension-mscsp", await MatrixServicesAsync(old));
    }

    /// <summary>
    /// Builds resolve through the same call as deploy events, which is the point: a build filed under
    /// one product and the deploy event for the same version under another is the failure the whole
    /// feature exists to prevent.
    /// </summary>
    [Fact]
    public async Task Override_AppliesToBuildRegistration()
    {
        var (old, target) = Products();
        var service = $"svc-build-{Guid.NewGuid():N}"[..20];

        (await SaveOverrideAsync(service, target)).EnsureSuccessStatusCode();
        (await RegisterBuildAsync(old, service, "v2.0.0")).EnsureSuccessStatusCode();

        Assert.Equal(["v2.0.0"], await BuildVersionsAsync(target, service));
        Assert.Empty(await BuildVersionsAsync(old, service));
    }

    /// <summary>
    /// A replay must land on the row the first attempt wrote. The natural key includes product, so if
    /// the resolution happened after the key was built the retry would insert a second build instead
    /// of updating the first — and the publish stage is fail-loud, so retries are routine.
    /// </summary>
    [Fact]
    public async Task RegisteringTheSameBuildTwice_StillReplaysUnderTheOverriddenProduct()
    {
        var (old, target) = Products();
        var service = $"svc-replay-{Guid.NewGuid():N}"[..20];

        (await SaveOverrideAsync(service, target)).EnsureSuccessStatusCode();
        (await RegisterBuildAsync(old, service, "v3.0.0")).EnsureSuccessStatusCode();

        var second = await RegisterBuildAsync(old, service, "v3.0.0");
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var body = await second.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("replayed").GetBoolean());

        Assert.Equal(["v3.0.0"], await BuildVersionsAsync(target, service));
    }

    /// <summary>
    /// A row naming the sending product beats the catch-all. That is what makes the feature usable for
    /// a service name that legitimately exists in more than one product.
    /// </summary>
    [Fact]
    public async Task AFromProductRow_WinsOverTheCatchAll()
    {
        var (old, target) = Products();
        var other = $"other-{Guid.NewGuid():N}"[..12];
        var special = $"special-{Guid.NewGuid():N}"[..14];
        var service = $"svc-specific-{Guid.NewGuid():N}"[..22];

        (await SaveOverrideAsync(service, target)).EnsureSuccessStatusCode();
        (await SaveOverrideAsync(service, special, fromProduct: other)).EnsureSuccessStatusCode();

        await IngestAsync(old, service, version: "v1.0.0");
        await IngestAsync(other, service, version: "v2.0.0");

        // The catch-all took the first sender; the specific row took its own.
        Assert.Contains(service, await MatrixServicesAsync(target));
        Assert.Contains(service, await MatrixServicesAsync(special));
        Assert.DoesNotContain(service, await MatrixServicesAsync(old));
        Assert.DoesNotContain(service, await MatrixServicesAsync(other));
    }

    /// <summary>
    /// A scoped row is a scalpel: a sender it does not name is left exactly as it was. Without this the
    /// narrow form would silently behave like the catch-all.
    /// </summary>
    [Fact]
    public async Task AScopedOverride_LeavesOtherSendersAlone()
    {
        var (old, target) = Products();
        var innocent = $"innocent-{Guid.NewGuid():N}"[..14];
        var service = $"svc-scoped-{Guid.NewGuid():N}"[..20];

        (await SaveOverrideAsync(service, target, fromProduct: old)).EnsureSuccessStatusCode();

        await IngestAsync(old, service, version: "v1.0.0");
        await IngestAsync(innocent, service, version: "v1.0.0");

        Assert.Contains(service, await MatrixServicesAsync(target));
        Assert.Contains(service, await MatrixServicesAsync(innocent));
        Assert.DoesNotContain(service, await MatrixServicesAsync(old));
    }

    /// <summary>
    /// The vast majority of traffic must be untouched by the existence of the table.
    /// </summary>
    [Fact]
    public async Task WithNoMatchingOverride_TheSendersProductIsKept()
    {
        var (old, _) = Products();
        var service = $"svc-untouched-{Guid.NewGuid():N}"[..22];

        await IngestAsync(old, service);

        Assert.Contains(service, await MatrixServicesAsync(old));
    }

    /// <summary>
    /// Senders are inconsistent about casing, and an override that missed because a pipeline switched
    /// to TitleCase would look like the feature simply did not work.
    /// </summary>
    [Fact]
    public async Task ServiceNamesMatchIgnoringCase()
    {
        var (old, target) = Products();
        var service = $"svc-case-{Guid.NewGuid():N}"[..18];

        (await SaveOverrideAsync(service.ToLowerInvariant(), target)).EnsureSuccessStatusCode();
        await IngestAsync(old, service.ToUpperInvariant());

        var services = await MatrixServicesAsync(target);
        Assert.Contains(service.ToUpperInvariant(), services);
    }

    /// <summary>
    /// Promotions must be validated against the product they will be stored under — policy resolution,
    /// the source-deployed check and the natural key all run on the resolved product, so a candidate
    /// cannot be gated by one product's policy and filed under another's.
    /// </summary>
    [Fact]
    public async Task ExternalPromotionCreate_ResolvesTheProductBeforeThePolicy()
    {
        var (old, target) = Products();
        var service = $"svc-promo-{Guid.NewGuid():N}"[..20];

        await _adminClient.PutAsJsonAsync("/api/features/features.promotions", new { enabled = true });
        (await SaveOverrideAsync(service, target)).EnsureSuccessStatusCode();

        // The policy lives on the TARGET product — the sender knows nothing about it.
        var policy = await _adminClient.PostAsJsonAsync("/api/promotions/admin/policies", new
        {
            product = target,
            service = (string?)null,
            sourceEnv = "staging",
            targetEnv = "prod",
            steps = Array.Empty<object>(),
            escalationGroup = (string?)null,
        });
        policy.EnsureSuccessStatusCode();

        await IngestAsync(old, service, env: "staging", version: "v4.0.0");

        var created = await _apiKeyClient.PostAsJsonAsync("/api/promotions", new
        {
            product = old,
            service,
            sourceEnv = "staging",
            targetEnv = "prod",
            version = "v4.0.0",
            references = Array.Empty<object>(),
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var listed = await _adminClient.GetFromJsonAsync<JsonElement>($"/api/promotions/?product={target}");
        var candidate = Assert.Single(listed.GetProperty("candidates").EnumerateArray());
        Assert.Equal(service, candidate.GetProperty("service").GetString());
    }

    /// <summary>
    /// When no policy resolves, the 422 has to name the product the lookup actually used. Echoing the
    /// sent product sends the admin off to configure a policy on a product that was never consulted —
    /// during precisely the migration this feature is for.
    /// </summary>
    [Fact]
    public async Task AMissingPolicy_ReportsTheResolvedProduct()
    {
        var (old, target) = Products();
        var service = $"svc-nopolicy-{Guid.NewGuid():N}"[..22];

        await _adminClient.PutAsJsonAsync("/api/features/features.promotions", new { enabled = true });
        (await SaveOverrideAsync(service, target)).EnsureSuccessStatusCode();
        await IngestAsync(old, service, env: "staging", version: "v5.0.0");

        var created = await _apiKeyClient.PostAsJsonAsync("/api/promotions", new
        {
            product = old,
            service,
            sourceEnv = "staging",
            targetEnv = "prod",
            version = "v5.0.0",
            references = Array.Empty<object>(),
        });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, created.StatusCode);

        var body = await created.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("policy_missing", body.GetProperty("code").GetString());
        var error = body.GetProperty("error").GetString()!;
        Assert.Contains(target, error);
        Assert.DoesNotContain(old, error);
    }

    // ── Configuration ───────────────────────────────────────────────────────

    /// <summary>
    /// Re-posting the same service updates in place. Correcting a mapping mid-migration is the normal
    /// case, and a second row for the same key would shadow the first unpredictably.
    /// </summary>
    [Fact]
    public async Task SavingTheSameServiceTwice_CorrectsTheMappingInPlace()
    {
        var (old, target) = Products();
        var typo = $"typo-{Guid.NewGuid():N}"[..12];
        var service = $"svc-upsert-{Guid.NewGuid():N}"[..20];

        (await SaveOverrideAsync(service, typo)).EnsureSuccessStatusCode();
        (await SaveOverrideAsync(service, target, reason: "fixed the target")).EnsureSuccessStatusCode();

        var rows = await ListOverridesAsync();
        var row = Assert.Single(
            rows.EnumerateArray().ToList(), r => r.GetProperty("service").GetString() == service);
        Assert.Equal(target, row.GetProperty("product").GetString());
        Assert.Equal("fixed the target", row.GetProperty("reason").GetString());
        Assert.Null(row.GetProperty("fromProduct").GetString());

        await IngestAsync(old, service);
        Assert.Contains(service, await MatrixServicesAsync(target));
        Assert.DoesNotContain(service, await MatrixServicesAsync(typo));
    }

    [Fact]
    public async Task ARowThatRedirectsAProductOntoItself_IsRejected()
    {
        var (_, target) = Products();
        var service = $"svc-selfmap-{Guid.NewGuid():N}"[..22];

        var res = await SaveOverrideAsync(service, target, fromProduct: target);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task ABlankServiceOrProduct_IsRejected()
    {
        var (_, target) = Products();

        Assert.Equal(HttpStatusCode.BadRequest, (await SaveOverrideAsync("  ", target)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await SaveOverrideAsync($"svc-{Guid.NewGuid():N}"[..12], "  ")).StatusCode);
    }

    /// <summary>
    /// Deleting the mapping stops future redirection and leaves what already moved where it is —
    /// removing a row is not an undo, and pretending otherwise would mean re-splitting the service.
    /// </summary>
    [Fact]
    public async Task DeletingAnOverride_StopsRedirectingWithoutMovingAnythingBack()
    {
        var (old, target) = Products();
        var service = $"svc-delete-{Guid.NewGuid():N}"[..20];

        (await SaveOverrideAsync(service, target)).EnsureSuccessStatusCode();
        await IngestAsync(old, service, version: "v1.0.0");

        var id = await OverrideIdAsync(service);
        var deleted = await _adminClient.DeleteAsync($"/api/deployments/admin/product-overrides/{id}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

        await IngestAsync(old, service, version: "v2.0.0");

        // The first deploy stayed on target; the second went where the sender said.
        Assert.Contains(service, await MatrixServicesAsync(target));
        Assert.Contains(service, await MatrixServicesAsync(old));
    }

    [Fact]
    public async Task DeletingAnOverrideThatIsGone_Is404()
    {
        var res = await _adminClient.DeleteAsync(
            $"/api/deployments/admin/product-overrides/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    /// <summary>
    /// The mapping decides where a whole team's deploy history goes. A signed-in non-admin must not be
    /// able to read or write it.
    /// </summary>
    [Fact]
    public async Task NonAdmins_CannotReadOrWriteOverrides()
    {
        using var qa = _factory.CreateAuthenticatedClient("qa@localhost", "qa123");

        Assert.Equal(HttpStatusCode.Forbidden,
            (await qa.GetAsync("/api/deployments/admin/product-overrides")).StatusCode);

        var write = await qa.PostAsJsonAsync("/api/deployments/admin/product-overrides",
            new { service = "anything", product = "somewhere-else", fromProduct = (string?)null, reason = (string?)null });
        Assert.Equal(HttpStatusCode.Forbidden, write.StatusCode);
    }

    // ── Moving history ──────────────────────────────────────────────────────

    /// <summary>
    /// The real sequence: history accumulates under the wrong product, then somebody notices and
    /// writes the mapping. Preview reports what would move without moving it; apply moves it.
    /// </summary>
    [Fact]
    public async Task Remap_MovesHistoryStoredBeforeTheOverrideExisted()
    {
        var (old, target) = Products();
        var service = $"svc-remap-{Guid.NewGuid():N}"[..20];

        await IngestAsync(old, service, version: "v1.0.0", deployedAt: DateTimeOffset.UtcNow.AddHours(-2));
        await IngestAsync(old, service, version: "v2.0.0", deployedAt: DateTimeOffset.UtcNow.AddHours(-1));
        (await RegisterBuildAsync(old, service, "v2.0.0")).EnsureSuccessStatusCode();

        (await SaveOverrideAsync(service, target)).EnsureSuccessStatusCode();
        var id = await OverrideIdAsync(service);

        var preview = await _adminClient.GetFromJsonAsync<JsonElement>(
            $"/api/deployments/admin/product-overrides/{id}/remap");
        Assert.False(preview.GetProperty("applied").GetBoolean());
        Assert.Equal(2, preview.GetProperty("deployments").GetInt32());
        Assert.Equal(1, preview.GetProperty("builds").GetInt32());
        Assert.Equal(0, preview.GetProperty("buildConflicts").GetInt32());
        Assert.Equal([old], preview.GetProperty("fromProducts").EnumerateArray()
            .Select(p => p.GetString()!).ToList());

        // Preview changed nothing.
        Assert.Contains(service, await MatrixServicesAsync(old));

        var applied = await _adminClient.PostAsync(
            $"/api/deployments/admin/product-overrides/{id}/remap", null);
        applied.EnsureSuccessStatusCode();
        var result = await applied.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(result.GetProperty("applied").GetBoolean());
        Assert.Equal(2, result.GetProperty("deployments").GetInt32());
        Assert.Equal(1, result.GetProperty("builds").GetInt32());

        Assert.Contains(service, await MatrixServicesAsync(target));
        Assert.DoesNotContain(service, await MatrixServicesAsync(old));
        Assert.Equal(["v2.0.0"], await BuildVersionsAsync(target, service));
        Assert.Empty(await BuildVersionsAsync(old, service));
    }

    /// <summary>
    /// Applying twice must be a no-op rather than an error — the admin who is not sure whether the
    /// first click registered will click again.
    /// </summary>
    [Fact]
    public async Task Remap_IsANoOpTheSecondTime()
    {
        var (old, target) = Products();
        var service = $"svc-remap2-{Guid.NewGuid():N}"[..20];

        await IngestAsync(old, service);
        (await SaveOverrideAsync(service, target)).EnsureSuccessStatusCode();
        var id = await OverrideIdAsync(service);

        var first = await _adminClient.PostAsync(
            $"/api/deployments/admin/product-overrides/{id}/remap", null);
        first.EnsureSuccessStatusCode();
        Assert.Equal(1, (await first.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("deployments").GetInt32());

        var second = await _adminClient.PostAsync(
            $"/api/deployments/admin/product-overrides/{id}/remap", null);
        second.EnsureSuccessStatusCode();
        var body = await second.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, body.GetProperty("deployments").GetInt32());
        Assert.Empty(body.GetProperty("fromProducts").EnumerateArray());
    }

    /// <summary>
    /// Builds are unique on (product, service, version). A version the target already has cannot take a
    /// second row, so it is reported and left where it is rather than deleted or silently dropped.
    /// </summary>
    [Fact]
    public async Task Remap_LeavesBuildsThatWouldCollideWithTheTarget()
    {
        var (old, target) = Products();
        var service = $"svc-collide-{Guid.NewGuid():N}"[..22];

        // The same version registered under both products — the state a half-migrated pipeline leaves.
        (await RegisterBuildAsync(old, service, "v1.0.0", branch: "refs/heads/old")).EnsureSuccessStatusCode();
        (await RegisterBuildAsync(target, service, "v1.0.0", branch: "refs/heads/new")).EnsureSuccessStatusCode();
        (await RegisterBuildAsync(old, service, "v1.1.0")).EnsureSuccessStatusCode();

        (await SaveOverrideAsync(service, target)).EnsureSuccessStatusCode();
        var id = await OverrideIdAsync(service);

        var preview = await _adminClient.GetFromJsonAsync<JsonElement>(
            $"/api/deployments/admin/product-overrides/{id}/remap");
        Assert.Equal(1, preview.GetProperty("builds").GetInt32());
        Assert.Equal(1, preview.GetProperty("buildConflicts").GetInt32());

        var applied = await _adminClient.PostAsync(
            $"/api/deployments/admin/product-overrides/{id}/remap", null);
        applied.EnsureSuccessStatusCode();

        // v1.1.0 moved; v1.0.0 stayed put under both, and the target still has the row it had.
        var targetVersions = await BuildVersionsAsync(target, service);
        Assert.Contains("v1.0.0", targetVersions);
        Assert.Contains("v1.1.0", targetVersions);
        Assert.Equal(["v1.0.0"], await BuildVersionsAsync(old, service));
    }

    /// <summary>
    /// A catch-all must not drag along entities a more specific row governs — otherwise history ends up
    /// somewhere new traffic from that sender would never go.
    /// </summary>
    [Fact]
    public async Task Remap_SkipsProductsGovernedByAMoreSpecificRow()
    {
        var (old, target) = Products();
        var other = $"other-{Guid.NewGuid():N}"[..12];
        var special = $"special-{Guid.NewGuid():N}"[..14];
        var service = $"svc-remap3-{Guid.NewGuid():N}"[..20];

        await IngestAsync(old, service, version: "v1.0.0");
        await IngestAsync(other, service, version: "v2.0.0");

        (await SaveOverrideAsync(service, target)).EnsureSuccessStatusCode();
        (await SaveOverrideAsync(service, special, fromProduct: other)).EnsureSuccessStatusCode();

        var rows = await ListOverridesAsync();
        var catchAll = rows.EnumerateArray().First(r =>
            r.GetProperty("service").GetString() == service
            && r.GetProperty("fromProduct").ValueKind == JsonValueKind.Null);

        var preview = await _adminClient.GetFromJsonAsync<JsonElement>(
            $"/api/deployments/admin/product-overrides/{catchAll.GetProperty("id").GetGuid()}/remap");

        // Only the sender the catch-all is responsible for.
        Assert.Equal([old], preview.GetProperty("fromProducts").EnumerateArray()
            .Select(p => p.GetString()!).ToList());
        Assert.Equal(1, preview.GetProperty("deployments").GetInt32());
    }

    [Fact]
    public async Task RemappingAnOverrideThatIsGone_Is404()
    {
        var id = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.NotFound,
            (await _adminClient.GetAsync($"/api/deployments/admin/product-overrides/{id}/remap")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await _adminClient.PostAsync($"/api/deployments/admin/product-overrides/{id}/remap", null)).StatusCode);
    }

    // ── Factory ─────────────────────────────────────────────────────────────

    public class OverrideFactory : TestFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("Deployments:ApiKeys:0:Name", "product-override-test");
            builder.UseSetting("Deployments:ApiKeys:0:Key", TestApiKey);
        }
    }
}
