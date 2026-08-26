using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;

namespace Platform.Integration.Tests;

/// <summary>
/// Integration tests for the deployment query endpoints.
/// Ingests deploy events via API key, then queries them with bearer-authenticated clients.
/// </summary>
public class DeploymentQueryIntegrationTests : IClassFixture<DeploymentQueryIntegrationTests.DeployQueryFactory>, IDisposable
{
    private const string TestApiKey = "test-deploy-key-12345";

    private readonly DeployQueryFactory _factory;
    private readonly HttpClient _apiKeyClient;
    private readonly HttpClient _adminClient;

    public DeploymentQueryIntegrationTests(DeployQueryFactory factory)
    {
        _factory = factory;

        // API-key client for deploy event ingest.
        _apiKeyClient = factory.CreateClient();
        _apiKeyClient.DefaultRequestHeaders.Add("X-Api-Key", TestApiKey);

        // Admin client for query endpoints.
        _adminClient = factory.CreateAdminClient();
    }

    public void Dispose()
    {
        _apiKeyClient.Dispose();
        _adminClient.Dispose();
    }

    // ── Ingest helper ───────────────────────────────────────────────────────

    private async Task IngestEventAsync(
        string product = "acme",
        string service = "api",
        string environment = "staging",
        string version = "v1.0.0",
        string status = "succeeded")
    {
        var response = await _apiKeyClient.PostAsJsonAsync("/api/deployments/events", new
        {
            product,
            service,
            environment,
            version,
            source = "ci",
            deployedAt = "2026-04-16T10:00:00Z",
            status,
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    // ── Tests ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetProducts_AfterIngest_ReturnsProductSummary()
    {
        await IngestEventAsync(product: "acme", service: "api", environment: "staging", version: "v1.0.0");
        await IngestEventAsync(product: "acme", service: "web", environment: "staging", version: "v1.0.1");

        var response = await _adminClient.GetAsync("/api/deployments/products");
        response.EnsureSuccessStatusCode();

        var body = await Deserialize(response);
        Assert.Equal(JsonValueKind.Array, body.ValueKind);

        var products = new List<string>();
        foreach (var item in body.EnumerateArray())
            products.Add(item.GetProperty("product").GetString()!);

        Assert.Contains("acme", products);
    }

    [Fact]
    public async Task GetState_ReturnsCurrentState()
    {
        await IngestEventAsync(product: "acme", service: "api", environment: "staging", version: "v2.0.0");

        var response = await _adminClient.GetAsync("/api/deployments/state?product=acme");
        response.EnsureSuccessStatusCode();

        var body = await Deserialize(response);
        Assert.Equal(JsonValueKind.Array, body.ValueKind);
        Assert.True(body.GetArrayLength() > 0, "State matrix should contain at least one entry");
    }

    [Fact]
    public async Task GetHistory_ReturnsDeploymentHistory()
    {
        await IngestEventAsync(product: "acme", service: "api", environment: "staging", version: "v3.0.0");

        var response = await _adminClient.GetAsync("/api/deployments/history/acme/api");
        response.EnsureSuccessStatusCode();

        var body = await Deserialize(response);
        Assert.Equal(JsonValueKind.Array, body.ValueKind);
        Assert.True(body.GetArrayLength() > 0, "History should contain at least one event");
    }

    [Fact]
    public async Task GetRecent_ByProduct_ReturnsEvents()
    {
        await IngestEventAsync(product: "acme", service: "api", environment: "staging", version: "v4.0.0");

        var response = await _adminClient.GetAsync("/api/deployments/recent/acme?since=2026-04-15T00:00:00Z");
        response.EnsureSuccessStatusCode();

        var body = await Deserialize(response);
        Assert.Equal(JsonValueKind.Array, body.ValueKind);
        Assert.True(body.GetArrayLength() > 0, "Recent by product should contain at least one event");
    }

    [Fact]
    public async Task GetRecent_ByEnvironment_ReturnsEvents()
    {
        await IngestEventAsync(product: "acme", service: "api", environment: "staging", version: "v5.0.0");

        var response = await _adminClient.GetAsync("/api/deployments/recent/acme/staging?since=2026-04-15T00:00:00Z");
        response.EnsureSuccessStatusCode();

        var body = await Deserialize(response);
        Assert.Equal(JsonValueKind.Array, body.ValueKind);
        Assert.True(body.GetArrayLength() > 0, "Recent by environment should contain at least one event");
    }

    [Fact]
    public async Task GetVersions_ReturnsDistinctVersions()
    {
        await IngestEventAsync(product: "acme", service: "api", environment: "staging", version: "v6.0.0");
        await IngestEventAsync(product: "acme", service: "api", environment: "staging", version: "v6.1.0");

        var response = await _adminClient.GetAsync("/api/deployments/versions?product=acme&environment=staging");
        response.EnsureSuccessStatusCode();

        var body = await Deserialize(response);
        var versions = body.GetProperty("versions");
        Assert.Equal(JsonValueKind.Array, versions.ValueKind);

        var versionStrings = new List<string>();
        foreach (var v in versions.EnumerateArray())
            versionStrings.Add(v.GetProperty("version").GetString()!);

        Assert.Contains("v6.0.0", versionStrings);
        Assert.Contains("v6.1.0", versionStrings);
    }

    [Fact]
    public async Task GetDuplicatesPreview_ReturnsZeroWhenClean()
    {
        var response = await _adminClient.GetAsync("/api/deployments/admin/duplicates");
        response.EnsureSuccessStatusCode();

        var body = await Deserialize(response);
        Assert.Equal(0, body.GetProperty("groups").GetInt32());
        Assert.Equal(0, body.GetProperty("rows").GetInt32());
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static async Task<JsonElement> Deserialize(HttpResponseMessage response)
    {
        var stream = await response.Content.ReadAsStreamAsync();
        var doc = await JsonDocument.ParseAsync(stream);
        return doc.RootElement;
    }

    // ── Factory ─────────────────────────────────────────────────────────────

    public class DeployQueryFactory : TestFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);

            builder.UseSetting("Deployments:ApiKeys:0:Name", "test-key");
            builder.UseSetting("Deployments:ApiKeys:0:Key", TestApiKey);
        }
    }
}
