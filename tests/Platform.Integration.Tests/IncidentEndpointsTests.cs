using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;

namespace Platform.Integration.Tests;

/// <summary>
/// Integration tests for <c>/api/incidents</c>: admin-only CRUD over the pointer records that feed
/// change failure rate and time to restore. Deploy events go in through the real ingest endpoint
/// so attribution to a deploy is checked against real rows.
/// </summary>
public class IncidentEndpointsTests : IClassFixture<IncidentEndpointsTests.IncidentFactory>, IDisposable
{
    private const string TestApiKey = "test-incidents-key-12345";

    private readonly IncidentFactory _factory;
    private readonly HttpClient _apiKeyClient;
    private readonly HttpClient _adminClient;

    public IncidentEndpointsTests(IncidentFactory factory)
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
    public async Task Crud_RoundTrips_AndComputesDuration()
    {
        var product = Unique("inc");
        var created = await PostAsync(new
        {
            product, service = "api", environment = "prod",
            startedAt = "2026-08-02T10:00:00Z", resolvedAt = "2026-08-02T12:30:00Z",
            url = "https://incidents.example.com/INC-1", source = "PagerDuty", externalId = "INC-1",
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var dto = await Json(created);
        var id = dto.GetProperty("id").GetGuid();
        Assert.Equal(2.5, dto.GetProperty("durationHours").GetDouble());
        Assert.Equal("pagerduty", dto.GetProperty("source").GetString());
        Assert.Equal("admin@localhost", dto.GetProperty("createdBy").GetString());

        var list = await Json(await _adminClient.GetAsync($"/api/incidents?product={product}"));
        Assert.Equal(1, list.GetProperty("total").GetInt32());

        var updated = await _adminClient.PutAsJsonAsync($"/api/incidents/{id}", new
        {
            product, service = "api", environment = "prod",
            startedAt = "2026-08-02T10:00:00Z", resolvedAt = (string?)null, version = "v9",
        });
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        var after = await Json(updated);
        Assert.Equal(JsonValueKind.Null, after.GetProperty("resolvedAt").ValueKind);
        Assert.Equal("v9", after.GetProperty("version").GetString());

        var open = await Json(await _adminClient.GetAsync($"/api/incidents?product={product}&open=true"));
        Assert.Equal(1, open.GetProperty("total").GetInt32());

        Assert.Equal(HttpStatusCode.NoContent, (await _adminClient.DeleteAsync($"/api/incidents/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _adminClient.GetAsync($"/api/incidents/{id}")).StatusCode);
    }

    [Fact]
    public async Task Create_RejectsRestoreBeforeStart_AndMissingFields()
    {
        var bad = await PostAsync(new { product = Unique("v"), service = "api", environment = "prod", startedAt = "2026-08-02T10:00:00Z", resolvedAt = "2026-08-02T09:00:00Z" });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        var missing = await PostAsync(new { product = "", service = "api", environment = "prod", startedAt = "2026-08-02T10:00:00Z" });
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);

        var badUrl = await PostAsync(new { product = Unique("u"), service = "api", environment = "prod", startedAt = "2026-08-02T10:00:00Z", url = "not a link" });
        Assert.Equal(HttpStatusCode.BadRequest, badUrl.StatusCode);
    }

    [Fact]
    public async Task Create_SameSourceAndExternalId_Conflicts()
    {
        var product = Unique("dup");
        var ext = Unique("INC");
        Assert.Equal(HttpStatusCode.Created, (await PostAsync(new { product, service = "api", environment = "prod", startedAt = "2026-08-02T10:00:00Z", source = "jira", externalId = ext })).StatusCode);
        var second = await PostAsync(new { product, service = "api", environment = "prod", startedAt = "2026-08-03T10:00:00Z", source = "jira", externalId = ext });
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.True((await Json(second)).TryGetProperty("existingId", out _));
    }

    [Fact]
    public async Task Create_WithDeployEvent_MustMatchScope_AndFillsVersion()
    {
        var product = Unique("evt");
        var eventId = await IngestAsync(product, "api", "prod", "v3", "2026-08-02T10:00:00Z");

        var wrong = await PostAsync(new { product, service = "web", environment = "prod", startedAt = "2026-08-02T11:00:00Z", causedByDeployEventId = eventId });
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);

        var ok = await PostAsync(new { product, service = "api", environment = "prod", startedAt = "2026-08-02T11:00:00Z", causedByDeployEventId = eventId });
        Assert.Equal(HttpStatusCode.Created, ok.StatusCode);
        var dto = await Json(ok);
        Assert.Equal("v3", dto.GetProperty("version").GetString());
        Assert.Equal(eventId, dto.GetProperty("causedByDeployEventId").GetGuid());

        var unknown = await PostAsync(new { product, service = "api", environment = "prod", startedAt = "2026-08-02T11:00:00Z", causedByDeployEventId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
    }

    [Fact]
    public async Task NonAdmins_AreRefused()
    {
        using var user = _factory.CreateAuthenticatedClient("user@localhost", "user123");
        Assert.Equal(HttpStatusCode.Forbidden, (await user.GetAsync("/api/incidents")).StatusCode);
        var post = await user.PostAsJsonAsync("/api/incidents", new { product = "p", service = "s", environment = "prod", startedAt = "2026-08-02T10:00:00Z" });
        Assert.Equal(HttpStatusCode.Forbidden, post.StatusCode);

        // A pipeline key is a producer, not an admin.
        Assert.Equal(HttpStatusCode.Forbidden, (await _apiKeyClient.GetAsync("/api/incidents")).StatusCode);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static string Unique(string prefix) => $"{prefix}-{Guid.NewGuid():N}"[..20];

    private Task<HttpResponseMessage> PostAsync(object body) => _adminClient.PostAsJsonAsync("/api/incidents", body);

    private static async Task<JsonElement> Json(HttpResponseMessage response)
    {
        var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        return doc.RootElement.Clone();
    }

    private async Task<Guid> IngestAsync(string product, string service, string environment, string version, string deployedAt)
    {
        var response = await _apiKeyClient.PostAsJsonAsync("/api/deployments/events", new
        {
            product, service, environment, version, source = "ci", deployedAt, status = "succeeded", references = Array.Empty<object>(),
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await Json(response)).GetProperty("id").GetGuid();
    }

    public class IncidentFactory : TestFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("Deployments:ApiKeys:0:Name", "test-key");
            builder.UseSetting("Deployments:ApiKeys:0:Key", TestApiKey);
        }
    }
}
