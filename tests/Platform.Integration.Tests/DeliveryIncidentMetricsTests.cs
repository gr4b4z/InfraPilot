using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;

namespace Platform.Integration.Tests;

/// <summary>
/// How recorded incidents become change failure rate and time to restore on
/// <c>GET /api/analytics/delivery</c>: the attribution rules (named deploy, version, live at start,
/// nothing), the attribution window, and who gets to see the incident rows.
/// </summary>
public class DeliveryIncidentMetricsTests : IClassFixture<DeliveryIncidentMetricsTests.Factory>, IDisposable
{
    private const string TestApiKey = "test-delivery-incidents-key";
    private const string Window = "from=2026-08-01T00:00:00Z&to=2026-08-15T00:00:00Z";

    private readonly Factory _factory;
    private readonly HttpClient _apiKeyClient;
    private readonly HttpClient _adminClient;

    public DeliveryIncidentMetricsTests(Factory factory)
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

    [Fact]
    public async Task NoIncidents_ChangeFailureRateShowsNothing_ButStaysAvailable()
    {
        var p = Unique("noinc");
        await IngestAsync(p, "api", "prod", "v1", "2026-08-02T10:00:00Z");

        var cfr = Metric(await GetJson($"/api/analytics/delivery?products={p}&{Window}"), "dora", "changeFailureRate");
        Assert.True(cfr.GetProperty("available").GetBoolean());
        Assert.Equal(JsonValueKind.Null, cfr.GetProperty("value").ValueKind);
        Assert.Equal(1, cfr.GetProperty("n").GetInt32());
    }

    [Fact]
    public async Task Attribution_NamedDeploy_Version_LiveAtStart_AndNothing()
    {
        var p = Unique("attr");
        var v1 = await IngestAsync(p, "api", "prod", "v1", "2026-08-02T10:00:00Z");
        var v2 = await IngestAsync(p, "api", "prod", "v2", "2026-08-05T10:00:00Z");
        await IngestAsync(p, "api", "prod", "v3", "2026-08-08T10:00:00Z");
        await IngestAsync(p, "web", "prod", "w1", "2026-08-03T10:00:00Z");

        // Named deploy: v1, even though v2 was live when it started.
        await RecordAsync(p, "api", "prod", "2026-08-06T09:00:00Z", "2026-08-06T10:00:00Z", causedBy: v1);
        // Version: v2 by name, while v3 is live.
        await RecordAsync(p, "api", "prod", "2026-08-09T09:00:00Z", "2026-08-09T12:00:00Z", version: "v2");
        // Inferred: whatever was live on the 4th → v1 (v2 came on the 5th).
        await RecordAsync(p, "api", "prod", "2026-08-04T09:00:00Z", "2026-08-04T09:30:00Z");
        // Nothing live: the incident predates every deploy of "worker".
        await RecordAsync(p, "worker", "prod", "2026-08-03T09:00:00Z", "2026-08-03T10:00:00Z");

        var body = await GetJson($"/api/analytics/delivery?products={p}&{Window}");

        // Deploys v1 and v2 caused incidents (v1 twice, counted once); 4 successful prod deploys.
        var cfr = Metric(body, "dora", "changeFailureRate");
        Assert.Equal(0.5, cfr.GetProperty("value").GetDouble());
        Assert.Equal(4, cfr.GetProperty("n").GetInt32());
        Assert.Equal(3, cfr.GetProperty("coverage").GetProperty("numerator").GetInt32());
        Assert.Equal(4, cfr.GetProperty("coverage").GetProperty("denominator").GetInt32());

        // Time to restore: 1h, 3h, 0.5h, 1h → p50 1h.
        var ttr = Metric(body, "dora", "timeToRestore");
        Assert.Equal(4, ttr.GetProperty("n").GetInt32());
        Assert.Equal(1.0, ttr.GetProperty("value").GetDouble());

        // Attribution quality: 2 explicit of 4.
        var attribution = Metric(body, "dataQuality", "incidentAttribution");
        Assert.Equal(0.5, attribution.GetProperty("value").GetDouble());

        // Admin drill-down names the rule per incident.
        var rows = body.GetProperty("incidents").EnumerateArray()
            .ToDictionary(r => r.GetProperty("startedAt").GetDateTimeOffset().Day, r => r);
        Assert.Equal("explicit", rows[6].GetProperty("attribution").GetString());
        Assert.Equal(v1, rows[6].GetProperty("deployEventId").GetGuid());
        Assert.Equal("version", rows[9].GetProperty("attribution").GetString());
        Assert.Equal(v2, rows[9].GetProperty("deployEventId").GetGuid());
        Assert.Equal("inferred", rows[4].GetProperty("attribution").GetString());
        Assert.Equal(v1, rows[4].GetProperty("deployEventId").GetGuid());
        Assert.Equal("none", rows[3].GetProperty("attribution").GetString());

        // Per-service and per-product rows carry the counts.
        var api = body.GetProperty("byService").EnumerateArray().Single(r => r.GetProperty("service").GetString() == "api");
        Assert.Equal(3, api.GetProperty("incidents").GetInt32());
        var product = Assert.Single(body.GetProperty("byProduct").EnumerateArray());
        Assert.Equal(4, product.GetProperty("incidents").GetInt32());
        Assert.Equal(0.5, product.GetProperty("changeFailureRate").GetDouble());
    }

    [Fact]
    public async Task DeployAfterStart_IsNotACause_AndRollbackIsNeverOne()
    {
        var p = Unique("after");
        var v1 = await IngestAsync(p, "api", "prod", "v1", "2026-08-02T10:00:00Z");
        await IngestAsync(p, "api", "prod", "v0", "2026-08-03T08:00:00Z", isRollback: true); // rollback: never a cause
        await IngestAsync(p, "api", "prod", "v2", "2026-08-03T12:00:00Z");                   // remediation, after the start

        await RecordAsync(p, "api", "prod", "2026-08-03T09:00:00Z", "2026-08-03T13:00:00Z");

        var body = await GetJson($"/api/analytics/delivery?products={p}&{Window}");
        var row = Assert.Single(body.GetProperty("incidents").EnumerateArray());
        Assert.Equal("inferred", row.GetProperty("attribution").GetString());
        Assert.Equal(v1, row.GetProperty("deployEventId").GetGuid());
        // 1 failed deploy (v1) of 2 successful non-rollback deploys.
        Assert.Equal(0.5, Metric(body, "dora", "changeFailureRate").GetProperty("value").GetDouble());
    }

    [Fact]
    public async Task OpenIncident_CountsForCoverage_NotForTimeToRestore()
    {
        var p = Unique("open");
        await IngestAsync(p, "api", "prod", "v1", "2026-08-02T10:00:00Z");
        await RecordAsync(p, "api", "prod", "2026-08-03T09:00:00Z", resolvedAt: null);
        await RecordAsync(p, "api", "prod", "2026-08-04T09:00:00Z", "2026-08-04T11:00:00Z");

        var ttr = Metric(await GetJson($"/api/analytics/delivery?products={p}&{Window}"), "dora", "timeToRestore");
        Assert.Equal(1, ttr.GetProperty("n").GetInt32());
        Assert.Equal(2.0, ttr.GetProperty("value").GetDouble());
        Assert.Equal(1, ttr.GetProperty("coverage").GetProperty("numerator").GetInt32());
        Assert.Equal(2, ttr.GetProperty("coverage").GetProperty("denominator").GetInt32());
    }

    [Fact]
    public async Task AttributionWindow_FromSettings_StopsBlamingOldDeploys()
    {
        var p = Unique("window");
        await IngestAsync(p, "api", "prod", "v1", "2026-08-01T10:00:00Z");
        // Twelve days later, nothing deployed since: with the default 7-day window this is outside it.
        await RecordAsync(p, "api", "prod", "2026-08-13T09:00:00Z", "2026-08-13T10:00:00Z");

        var body = await GetJson($"/api/analytics/delivery?products={p}&{Window}");
        Assert.Equal("outside-window", Assert.Single(body.GetProperty("incidents").EnumerateArray()).GetProperty("attribution").GetString());
        Assert.Equal(0.0, Metric(body, "dora", "changeFailureRate").GetProperty("value").GetDouble());
        Assert.Contains("7 days", Metric(body, "dora", "changeFailureRate").GetProperty("definition").GetString());

        // Lift the cap: the live version is to blame however old.
        await SaveAnalyticsSettingsAsync(attributionWindowDays: 0, rolloutToleranceMinutes: 15);
        try
        {
            var uncapped = await GetJson($"/api/analytics/delivery?products={p}&{Window}");
            Assert.Equal("inferred", Assert.Single(uncapped.GetProperty("incidents").EnumerateArray()).GetProperty("attribution").GetString());
            Assert.Equal(1.0, Metric(uncapped, "dora", "changeFailureRate").GetProperty("value").GetDouble());
        }
        finally
        {
            await SaveAnalyticsSettingsAsync(attributionWindowDays: 7, rolloutToleranceMinutes: 15);
        }
    }

    [Fact]
    public async Task RolloutTolerance_BlamesTheDeployThatWasFinishing()
    {
        var p = Unique("tol");
        var v1 = await IngestAsync(p, "api", "prod", "v1", "2026-08-02T10:00:00Z");
        var v2 = await IngestAsync(p, "api", "prod", "v2", "2026-08-05T10:00:00Z");
        // Started 10 minutes before v2 "finished": mid-rollout, so v2 — not v1 — is to blame.
        await RecordAsync(p, "api", "prod", "2026-08-05T09:50:00Z", "2026-08-05T10:30:00Z");

        var body = await GetJson($"/api/analytics/delivery?products={p}&{Window}");
        var row = Assert.Single(body.GetProperty("incidents").EnumerateArray());
        Assert.Equal(v2, row.GetProperty("deployEventId").GetGuid());
        Assert.NotEqual(v1, row.GetProperty("deployEventId").GetGuid());
    }

    [Fact]
    public async Task NonAdmins_GetTheFigures_NotTheIncidents()
    {
        var p = Unique("priv");
        await IngestAsync(p, "api", "prod", "v1", "2026-08-02T10:00:00Z");
        await RecordAsync(p, "api", "prod", "2026-08-03T09:00:00Z", "2026-08-03T10:00:00Z");

        using var user = _factory.CreateAuthenticatedClient("user@localhost", "user123");
        var response = await user.GetAsync($"/api/analytics/delivery?products={p}&{Window}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(1.0, Metric(body, "dora", "changeFailureRate").GetProperty("value").GetDouble());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("incidents").ValueKind);

        var admin = await GetJson($"/api/analytics/delivery?products={p}&{Window}");
        Assert.Single(admin.GetProperty("incidents").EnumerateArray());
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static JsonElement Metric(JsonElement body, string section, string key)
        => body.GetProperty("sections").EnumerateArray()
            .Single(s => s.GetProperty("key").GetString() == section)
            .GetProperty("metrics").EnumerateArray()
            .Single(m => m.GetProperty("key").GetString() == key);

    private static string Unique(string prefix) => $"{prefix}-{Guid.NewGuid():N}"[..20];

    private async Task<Guid> IngestAsync(string product, string service, string environment, string version, string deployedAt, bool isRollback = false)
    {
        var response = await _apiKeyClient.PostAsJsonAsync("/api/deployments/events", new
        {
            product, service, environment, version, source = "ci", deployedAt, status = "succeeded", isRollback, references = Array.Empty<object>(),
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
    }

    private async Task RecordAsync(string product, string service, string environment, string startedAt, string? resolvedAt = null, string? version = null, Guid? causedBy = null)
    {
        var response = await _adminClient.PostAsJsonAsync("/api/incidents", new
        {
            product, service, environment, startedAt, resolvedAt, version, causedByDeployEventId = causedBy,
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    private async Task SaveAnalyticsSettingsAsync(int attributionWindowDays, int rolloutToleranceMinutes)
    {
        var current = JsonDocument.Parse(await _adminClient.GetStringAsync("/api/settings")).RootElement;
        var response = await _adminClient.PutAsJsonAsync("/api/settings", new
        {
            environments = current.GetProperty("environments"),
            roles = current.GetProperty("roles"),
            activityTemplate = current.GetProperty("activityTemplate"),
            analytics = new { attributionWindowDays, rolloutToleranceMinutes },
        });
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    private async Task<JsonElement> GetJson(string url)
    {
        var response = await _adminClient.GetAsync(url);
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

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
