using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;

namespace Platform.Integration.Tests;

/// <summary>
/// Integration tests for <c>GET /api/analytics/delivery</c>. Deploy events go in through the real
/// ingest endpoint so the work-item projection is exercised end-to-end. Each test owns its products,
/// so the shared host's other data never enters a test's scope — the <c>products</c> filter is also
/// what isolates the production-set resolution per test.
/// </summary>
public class DeliveryMetricsEndpointsTests : IClassFixture<DeliveryMetricsEndpointsTests.DeliveryFactory>, IDisposable
{
    private const string TestApiKey = "test-delivery-key-12345";
    private const string Window = "from=2026-08-01T00:00:00Z&to=2026-08-15T00:00:00Z";

    private readonly HttpClient _apiKeyClient;
    private readonly HttpClient _adminClient;

    public DeliveryMetricsEndpointsTests(DeliveryFactory factory)
    {
        _apiKeyClient = factory.CreateClient();
        _apiKeyClient.DefaultRequestHeaders.Add("X-Api-Key", TestApiKey);
        _adminClient = factory.CreateAdminClient();
    }

    public void Dispose()
    {
        _apiKeyClient.Dispose();
        _adminClient.Dispose();
    }

    // ── Pooling and filtering ───────────────────────────────────────────────

    [Fact]
    public async Task Delivery_PoolsSelectedProducts_AsOnePopulation()
    {
        await EnablePromotionsAsync();
        var a = Unique("pool-a");
        var b = Unique("pool-b");
        await IngestAsync(a, "api", "prod", "v1", "2026-08-02T10:00:00Z", workItems: ["A-1"], prOccurredAt: "2026-08-01T10:00:00Z"); // 24h
        await IngestAsync(a, "api", "prod", "v2", "2026-08-03T10:00:00Z");
        await IngestAsync(b, "web", "prod", "v1", "2026-08-04T10:00:00Z", workItems: ["B-1"], prOccurredAt: "2026-08-01T10:00:00Z"); // 72h
        await IngestAsync(b, "web", "prod", "v2", "2026-08-05T10:00:00Z", status: "failed");

        var body = await GetJson($"/api/analytics/delivery?products={a},{b}&{Window}");

        Assert.Equal("default-name", body.GetProperty("production").GetProperty("source").GetString());
        Assert.Equal(["prod"], body.GetProperty("production").GetProperty("environments").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal([a, b], body.GetProperty("products").EnumerateArray().Select(e => e.GetString()));

        var freq = Metric(body, "dora", "deploymentFrequency");
        Assert.True(freq.GetProperty("available").GetBoolean());
        Assert.Equal(3, freq.GetProperty("value").GetDouble());

        // Lead time is a percentile over the pooled stories — 24h and 72h → p50 48h.
        var lead = Metric(body, "dora", "leadTime");
        Assert.Equal(2, lead.GetProperty("n").GetInt32());
        Assert.Equal(48.0, lead.GetProperty("value").GetDouble());
        Assert.Equal(1.0, lead.GetProperty("coverage").GetProperty("ratio").GetDouble());

        // Deploy failure rate: 1 failed / (3 succeeded + 1 failed).
        var dfr = Metric(body, "quality", "deployFailureRate");
        Assert.Equal(0.25, dfr.GetProperty("value").GetDouble());
        Assert.Equal(4, dfr.GetProperty("n").GetInt32());

        // The filter narrows the same numbers.
        var onlyA = await GetJson($"/api/analytics/delivery?products={a}&{Window}");
        Assert.Equal(2, Metric(onlyA, "dora", "deploymentFrequency").GetProperty("value").GetDouble());
        Assert.Equal(24.0, Metric(onlyA, "dora", "leadTime").GetProperty("value").GetDouble());

        // Per-product rows carry the split.
        var rows = body.GetProperty("byProduct").EnumerateArray().ToDictionary(r => r.GetProperty("product").GetString()!);
        Assert.Equal(2, rows[a].GetProperty("deploys").GetInt32());
        Assert.Equal(1, rows[b].GetProperty("deploys").GetInt32());
        Assert.Equal(0.5, rows[b].GetProperty("deployFailureRate").GetDouble());
    }

    [Fact]
    public async Task Delivery_SameWorkItemKeyInTwoProducts_IsTwoStories()
    {
        await EnablePromotionsAsync();
        var a = Unique("key-a");
        var b = Unique("key-b");
        // Same ticket key, shipped by two products a day apart — must be two grains, not one dated by the first.
        await IngestAsync(a, "api", "prod", "v1", "2026-08-02T10:00:00Z", workItems: ["SHARED-1"], prOccurredAt: "2026-08-02T00:00:00Z"); // 10h
        await IngestAsync(b, "web", "prod", "v1", "2026-08-04T10:00:00Z", workItems: ["SHARED-1"], prOccurredAt: "2026-08-02T00:00:00Z"); // 58h

        var body = await GetJson($"/api/analytics/delivery?products={a},{b}&{Window}");

        Assert.Equal(2, Metric(body, "flow", "storiesShipped").GetProperty("value").GetDouble());
        var lead = Metric(body, "dora", "leadTime");
        Assert.Equal(2, lead.GetProperty("n").GetInt32());
        Assert.Equal(34.0, lead.GetProperty("value").GetDouble());

        // The legacy per-environment lead-time endpoint keeps the grains apart too.
        var legacy = await GetJson($"/api/analytics/lead-time?{Window}");
        var prod = legacy.GetProperty("byEnvironment").EnumerateArray()
            .Single(e => e.GetProperty("environment").GetString() == "prod");
        Assert.True(prod.GetProperty("n").GetInt32() >= 2);
    }

    // ── Production resolution ───────────────────────────────────────────────

    [Fact]
    public async Task Delivery_WithoutProdLikeName_FallsBackToLastEnvironmentInOrder()
    {
        var p = Unique("conv");
        await IngestAsync(p, "api", "dev", "v1", "2026-08-02T10:00:00Z");
        await IngestAsync(p, "api", "test", "v1", "2026-08-03T10:00:00Z");

        var body = await GetJson($"/api/analytics/delivery?products={p}&{Window}");

        var production = body.GetProperty("production");
        Assert.Equal("convention", production.GetProperty("source").GetString());
        Assert.Equal(["test"], production.GetProperty("environments").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(1, Metric(body, "dora", "deploymentFrequency").GetProperty("value").GetDouble());
    }

    [Fact]
    public async Task Delivery_NoDeployments_IsEmptyNotAnError()
    {
        var body = await GetJson($"/api/analytics/delivery?products={Unique("empty")}&{Window}");

        Assert.Empty(body.GetProperty("production").GetProperty("environments").EnumerateArray());
        var freq = Metric(body, "dora", "deploymentFrequency");
        Assert.Equal(0, freq.GetProperty("value").GetDouble());
        Assert.Equal(JsonValueKind.Null, Metric(body, "dora", "leadTime").GetProperty("value").ValueKind);
    }

    // ── Metrics ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Delivery_StoriesShipped_CountsFirstProductionLandingInsideWindow()
    {
        await EnablePromotionsAsync();
        var p = Unique("shipped");
        // OLD-1 reached prod before the window and was redeployed inside it — not shipped this period.
        await IngestAsync(p, "api", "prod", "v1", "2026-07-20T10:00:00Z", workItems: ["OLD-1"]);
        await IngestAsync(p, "api", "prod", "v2", "2026-08-03T10:00:00Z", workItems: ["OLD-1", "NEW-1"]);
        // NEW-2 is only on test — not shipped.
        await IngestAsync(p, "api", "test", "v3", "2026-08-04T10:00:00Z", workItems: ["NEW-2"]);

        var body = await GetJson($"/api/analytics/delivery?products={p}&{Window}");

        var shipped = Metric(body, "flow", "storiesShipped");
        Assert.Equal(1, shipped.GetProperty("value").GetDouble());
        // Previous period saw OLD-1 land.
        Assert.Equal(1, shipped.GetProperty("delta").GetProperty("previous").GetDouble());
        // No commit times sent → lead time has nothing to measure, and says so through coverage.
        var lead = Metric(body, "dora", "leadTime");
        Assert.Equal(0, lead.GetProperty("n").GetInt32());
        Assert.Equal(0, lead.GetProperty("coverage").GetProperty("ratio").GetDouble());
        Assert.Equal(1, lead.GetProperty("coverage").GetProperty("denominator").GetInt32());
    }

    [Fact]
    public async Task Delivery_RollbackRate_AndFrequencyDelta()
    {
        var p = Unique("rb");
        await IngestAsync(p, "api", "prod", "v0", "2026-07-25T10:00:00Z"); // previous window
        await IngestAsync(p, "api", "prod", "v1", "2026-08-02T10:00:00Z");
        await IngestAsync(p, "api", "prod", "v2", "2026-08-03T10:00:00Z");
        await IngestAsync(p, "api", "prod", "v1", "2026-08-03T12:00:00Z", isRollback: true);
        await IngestAsync(p, "api", "prod", "v2", "2026-08-04T10:00:00Z", previousVersion: "v2"); // redeploy, skipped

        var body = await GetJson($"/api/analytics/delivery?products={p}&{Window}");

        var freq = Metric(body, "dora", "deploymentFrequency");
        Assert.Equal(2, freq.GetProperty("value").GetDouble());
        Assert.Equal(1, freq.GetProperty("delta").GetProperty("previous").GetDouble());
        Assert.Equal(1, freq.GetProperty("delta").GetProperty("change").GetDouble());

        Assert.Equal(0.5, Metric(body, "quality", "rollbackRate").GetProperty("value").GetDouble());
        // (0 failed + 1 rollback) / (2 succeeded + 0 failed)
        Assert.Equal(0.5, Metric(body, "quality", "deployFailureRate").GetProperty("value").GetDouble());
    }

    [Fact]
    public async Task Delivery_StoryCoverage_AndStaleServices()
    {
        await EnablePromotionsAsync();
        var p = Unique("cov");
        await IngestAsync(p, "worker", "prod", "v1", "2026-05-01T10:00:00Z"); // stale: nothing in the window
        await IngestAsync(p, "api", "prod", "v1", "2026-08-02T10:00:00Z", workItems: ["COV-1"]);
        await IngestAsync(p, "api", "dev", "v2", "2026-08-03T10:00:00Z");
        await IngestAsync(p, "api", "dev", "v3", "2026-08-04T10:00:00Z", workItems: ["COV-2"]);

        var body = await GetJson($"/api/analytics/delivery?products={p}&{Window}");

        var coverage = Metric(body, "dataQuality", "storyCoverage");
        Assert.Equal(3, coverage.GetProperty("n").GetInt32());
        Assert.Equal(0.667, coverage.GetProperty("value").GetDouble(), precision: 3);

        var stale = Metric(body, "cadence", "staleServices");
        Assert.Equal(1, stale.GetProperty("value").GetDouble());
        Assert.Equal(2, stale.GetProperty("n").GetInt32());

        var rows = body.GetProperty("byService").EnumerateArray().ToDictionary(r => r.GetProperty("service").GetString()!);
        Assert.Equal(0, rows["worker"].GetProperty("deploys").GetInt32());
        Assert.StartsWith("2026-05-01", rows["worker"].GetProperty("lastDeployedAt").GetString());
        Assert.Equal(3, rows["api"].GetProperty("deploys").GetInt32());
    }

    [Fact]
    public async Task Delivery_IncidentMetrics_AreUnavailableWithAReason()
    {
        var body = await GetJson($"/api/analytics/delivery?products={Unique("inc")}&{Window}");

        foreach (var key in new[] { "changeFailureRate", "timeToRestore" })
        {
            var m = Metric(body, "dora", key);
            Assert.False(m.GetProperty("available").GetBoolean());
            Assert.Contains("incident", m.GetProperty("reason").GetString(), StringComparison.OrdinalIgnoreCase);
            Assert.Equal(JsonValueKind.Null, m.GetProperty("value").ValueKind);
        }
    }

    [Fact]
    public async Task Delivery_MonthBucket_IsHalfOpen()
    {
        var p = Unique("month");
        await IngestAsync(p, "api", "prod", "v1", "2026-07-15T10:00:00Z");
        await IngestAsync(p, "api", "prod", "v2", "2026-08-20T10:00:00Z");

        var body = await GetJson($"/api/analytics/delivery?products={p}&from=2026-07-01T00:00:00Z&to=2026-09-01T00:00:00Z&bucket=month");

        var trend = Metric(body, "dora", "deploymentFrequency").GetProperty("trend").EnumerateArray().ToList();
        Assert.Equal(["2026-07-01", "2026-08-01"], trend.Select(t => t.GetProperty("start").GetString()));
        Assert.All(trend, t => Assert.Equal(1, t.GetProperty("value").GetDouble()));
        Assert.Equal("month", body.GetProperty("definition").GetProperty("bucket").GetString());
    }

    [Fact]
    public async Task Delivery_EveryMetric_CarriesADefinition()
    {
        var body = await GetJson($"/api/analytics/delivery?products={Unique("def")}&{Window}");

        var keys = new List<string>();
        foreach (var section in body.GetProperty("sections").EnumerateArray())
        foreach (var m in section.GetProperty("metrics").EnumerateArray())
        {
            keys.Add(m.GetProperty("key").GetString()!);
            Assert.False(string.IsNullOrWhiteSpace(m.GetProperty("definition").GetString()));
            Assert.Contains(m.GetProperty("goodWhen").GetString(), new[] { "up", "down", "none" });
        }
        Assert.Equal(13, keys.Count);
        Assert.Equal(keys.Count, keys.Distinct().Count());
        Assert.NotEqual(default, body.GetProperty("asOf").GetDateTimeOffset());
    }

    // ── Validation ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Delivery_InvalidBucket_Returns400()
    {
        var response = await _adminClient.GetAsync("/api/analytics/delivery?bucket=year");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Delivery_TooManyProducts_Returns400()
    {
        var products = string.Join(',', Enumerable.Range(0, 101).Select(i => $"p{i}"));
        var response = await _adminClient.GetAsync($"/api/analytics/delivery?products={products}");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static JsonElement Metric(JsonElement body, string section, string key)
        => body.GetProperty("sections").EnumerateArray()
            .Single(s => s.GetProperty("key").GetString() == section)
            .GetProperty("metrics").EnumerateArray()
            .Single(m => m.GetProperty("key").GetString() == key);

    private static string Unique(string prefix) => $"{prefix}-{Guid.NewGuid():N}"[..20];

    private async Task EnablePromotionsAsync()
    {
        var response = await _adminClient.PutAsJsonAsync(
            "/api/features/features.promotions", new { enabled = true });
        response.EnsureSuccessStatusCode();
    }

    private async Task IngestAsync(
        string product, string service, string environment, string version, string deployedAt,
        string status = "succeeded", bool isRollback = false, string? previousVersion = null,
        IReadOnlyList<string>? workItems = null, string? prOccurredAt = null)
    {
        var references = new List<object>();
        foreach (var key in workItems ?? [])
            references.Add(new { type = "work-item", key });
        if (prOccurredAt is not null)
            references.Add(new { type = "pull-request", key = "99", occurredAt = prOccurredAt });

        var response = await _apiKeyClient.PostAsJsonAsync("/api/deployments/events", new
        {
            product, service, environment, version,
            source = "ci", deployedAt, status, isRollback, previousVersion, references,
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    private async Task<JsonElement> GetJson(string url)
    {
        var response = await _adminClient.GetAsync(url);
        response.EnsureSuccessStatusCode();
        var stream = await response.Content.ReadAsStreamAsync();
        var doc = await JsonDocument.ParseAsync(stream);
        return doc.RootElement.Clone();
    }

    public class DeliveryFactory : TestFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("Deployments:ApiKeys:0:Name", "test-key");
            builder.UseSetting("Deployments:ApiKeys:0:Key", TestApiKey);
        }
    }
}
