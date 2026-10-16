using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;

namespace Platform.Integration.Tests;

/// <summary>
/// Integration tests for the cross-product service search and the service detail endpoint.
/// Ingests deploy events via API key, then queries with a bearer-authenticated admin client.
/// </summary>
public class ServiceSearchAndDetailTests : IClassFixture<ServiceSearchAndDetailTests.Factory>, IDisposable
{
    private const string TestApiKey = "test-service-search-key-12345";

    private readonly Factory _factory;
    private readonly HttpClient _apiKeyClient;
    private readonly HttpClient _adminClient;

    public ServiceSearchAndDetailTests(Factory factory)
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

    // ── Ingest helper ───────────────────────────────────────────────────────

    private async Task IngestAsync(
        string product,
        string service,
        string environment = "staging",
        string version = "v1.0.0",
        string status = "succeeded",
        DateTimeOffset? deployedAt = null)
    {
        var response = await _apiKeyClient.PostAsJsonAsync("/api/deployments/events", new
        {
            product,
            service,
            environment,
            version,
            source = "ci",
            deployedAt = deployedAt ?? DateTimeOffset.Parse("2026-04-16T10:00:00Z"),
            status,
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    private static async Task<JsonElement> Deserialize(HttpResponseMessage response)
    {
        var stream = await response.Content.ReadAsStreamAsync();
        var doc = await JsonDocument.ParseAsync(stream);
        return doc.RootElement;
    }

    // ── Search ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Search_FindsServicesAcrossProducts()
    {
        await IngestAsync("shop", "checkout-api", environment: "staging");
        await IngestAsync("shop", "checkout-api", environment: "production");
        await IngestAsync("billing", "checkout-worker", environment: "staging");
        await IngestAsync("billing", "invoice-api", environment: "staging");

        var response = await _adminClient.GetAsync("/api/deployments/services/search?q=checkout");
        response.EnsureSuccessStatusCode();

        var results = (await Deserialize(response)).GetProperty("results");
        var hits = results.EnumerateArray()
            .Select(r => (Product: r.GetProperty("product").GetString(), Service: r.GetProperty("service").GetString()))
            .ToList();

        // The whole point: one query, hits from two different products — and nothing unrelated.
        Assert.Contains(("shop", "checkout-api"), hits);
        Assert.Contains(("billing", "checkout-worker"), hits);
        Assert.DoesNotContain(hits, h => h.Service == "invoice-api");

        // Each hit carries the environments the service was seen in.
        var checkoutApi = results.EnumerateArray()
            .Single(r => r.GetProperty("service").GetString() == "checkout-api");
        var envs = checkoutApi.GetProperty("environments").EnumerateArray()
            .Select(e => e.GetProperty("environment").GetString())
            .ToList();
        Assert.Contains("staging", envs);
        Assert.Contains("production", envs);
    }

    [Fact]
    public async Task Search_IsCaseInsensitiveSubstring()
    {
        await IngestAsync("acme-ci", "Payment-Gateway");

        var response = await _adminClient.GetAsync("/api/deployments/services/search?q=GATEWAY");
        response.EnsureSuccessStatusCode();

        var results = (await Deserialize(response)).GetProperty("results");
        Assert.Contains(results.EnumerateArray(),
            r => r.GetProperty("service").GetString() == "Payment-Gateway");
    }

    [Fact]
    public async Task Search_WithoutQuery_IsBadRequest()
    {
        var response = await _adminClient.GetAsync("/api/deployments/services/search");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Search_ExcludesRetiredServices()
    {
        await IngestAsync("legacy-shop", "legacy-checkout");

        var remove = await _adminClient.PostAsJsonAsync("/api/deployments/admin/deleted-services",
            new { product = "legacy-shop", service = "legacy-checkout", reason = "migrated" });
        remove.EnsureSuccessStatusCode();

        var response = await _adminClient.GetAsync("/api/deployments/services/search?q=legacy-checkout");
        response.EnsureSuccessStatusCode();

        var results = (await Deserialize(response)).GetProperty("results");
        Assert.DoesNotContain(results.EnumerateArray(),
            r => r.GetProperty("service").GetString() == "legacy-checkout");
    }

    // ── Detail ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Detail_ReturnsEnvironmentsAndDistinctVersions()
    {
        var t0 = DateTimeOffset.Parse("2026-04-16T10:00:00Z");
        // v1 goes to staging then production; v2 lands in staging twice (a redeploy) — the version
        // list must fold that into two versions, v2 first, with v1 showing both environments.
        await IngestAsync("orders", "orders-api", "staging", "v1.0.0", deployedAt: t0);
        await IngestAsync("orders", "orders-api", "production", "v1.0.0", deployedAt: t0.AddHours(1));
        await IngestAsync("orders", "orders-api", "staging", "v2.0.0", deployedAt: t0.AddHours(2));
        await IngestAsync("orders", "orders-api", "staging", "v2.0.0", deployedAt: t0.AddHours(3));

        var response = await _adminClient.GetAsync("/api/deployments/services/orders/orders-api");
        response.EnsureSuccessStatusCode();

        var body = await Deserialize(response);
        Assert.Equal("orders", body.GetProperty("product").GetString());
        Assert.Equal("orders-api", body.GetProperty("service").GetString());

        // Current state: staging shows v2.0.0, production still v1.0.0.
        var stateByEnv = body.GetProperty("environments").EnumerateArray()
            .ToDictionary(
                e => e.GetProperty("environment").GetString()!,
                e => e.GetProperty("version").GetString());
        Assert.Equal("v2.0.0", stateByEnv["staging"]);
        Assert.Equal("v1.0.0", stateByEnv["production"]);

        // Distinct versions, newest first, each with the environments it reached.
        var versions = body.GetProperty("recentVersions").EnumerateArray().ToList();
        Assert.Equal(2, versions.Count);
        Assert.Equal("v2.0.0", versions[0].GetProperty("version").GetString());
        Assert.Equal("v1.0.0", versions[1].GetProperty("version").GetString());

        var v1Envs = versions[1].GetProperty("environments").EnumerateArray()
            .Select(e => e.GetProperty("environment").GetString())
            .ToList();
        Assert.Contains("staging", v1Envs);
        Assert.Contains("production", v1Envs);

        // The redeploy folded: v2 lists staging once.
        var v2Envs = versions[0].GetProperty("environments").EnumerateArray().ToList();
        Assert.Single(v2Envs);

        // Promotions ride along as a list (possibly empty — no policy is configured here).
        Assert.Equal(JsonValueKind.Array, body.GetProperty("promotions").ValueKind);
    }

    [Fact]
    public async Task Detail_UnknownService_IsNotFound()
    {
        var response = await _adminClient.GetAsync("/api/deployments/services/nope/never-deployed");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Detail_RetiredService_IsNotFound()
    {
        await IngestAsync("warehouse", "picker-api");

        var remove = await _adminClient.PostAsJsonAsync("/api/deployments/admin/deleted-services",
            new { product = "warehouse", service = "picker-api", reason = (string?)null });
        remove.EnsureSuccessStatusCode();

        var response = await _adminClient.GetAsync("/api/deployments/services/warehouse/picker-api");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ── Factory ─────────────────────────────────────────────────────────────

    public class Factory : TestFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);

            builder.UseSetting("Deployments:ApiKeys:0:Name", "test-key");
            builder.UseSetting("Deployments:ApiKeys:0:Key", TestApiKey);
        }
    }
}
