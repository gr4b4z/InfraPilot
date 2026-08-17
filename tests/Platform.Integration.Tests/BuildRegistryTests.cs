using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;

namespace Platform.Integration.Tests;

/// <summary>
/// Covers the build registry (plan: feature-branch-builds §3.1, Phase A):
/// <list type="bullet">
///   <item>Registration is idempotent on (product, service, version) — a re-POST updates the
///         existing row in place and reports 200/replayed rather than duplicating.</item>
///   <item>POST is API-key-gated with product scope AND the build:register scope for keys that
///         declare a Scopes list; keys without one stay unrestricted (legacy).</item>
///   <item>The read surface lists newest-first with product/service/branch filters and serves
///         the inline manifest on the detail route.</item>
/// </list>
/// </summary>
public class BuildRegistryTests : IClassFixture<BuildRegistryTests.BuildsFactory>, IDisposable
{
    public class BuildsFactory : TestFactory
    {
        public const string UnrestrictedKey = "builds-test-key-12345";
        public const string ScopedKey = "builds-scoped-key-12345";
        public const string WrongScopeKey = "builds-wrongscope-key-12345";
        public const string ProductScopedKey = "builds-productscoped-key-12345";

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            // Legacy-style key: no Scopes list — unrestricted.
            builder.UseSetting("Deployments:ApiKeys:0:Name", "builds-test");
            builder.UseSetting("Deployments:ApiKeys:0:Key", UnrestrictedKey);
            // Narrowed to exactly build registration.
            builder.UseSetting("Deployments:ApiKeys:1:Name", "builds-scoped");
            builder.UseSetting("Deployments:ApiKeys:1:Key", ScopedKey);
            builder.UseSetting("Deployments:ApiKeys:1:Scopes:0", "build:register");
            // Holds a Scopes list that does NOT include build:register.
            builder.UseSetting("Deployments:ApiKeys:2:Name", "builds-wrongscope");
            builder.UseSetting("Deployments:ApiKeys:2:Key", WrongScopeKey);
            builder.UseSetting("Deployments:ApiKeys:2:Scopes:0", "promotion:create");
            // Limited to a product the tests never post for.
            builder.UseSetting("Deployments:ApiKeys:3:Name", "builds-productscoped");
            builder.UseSetting("Deployments:ApiKeys:3:Key", ProductScopedKey);
            builder.UseSetting("Deployments:ApiKeys:3:AllowedProducts:0", "someone-elses-product");
        }
    }

    private readonly BuildsFactory _factory;
    private readonly HttpClient _apiKeyClient;
    private readonly HttpClient _adminClient;

    public BuildRegistryTests(BuildsFactory factory)
    {
        _factory = factory;
        _apiKeyClient = CreateApiKeyClient(BuildsFactory.UnrestrictedKey);
        _adminClient = factory.CreateAdminClient();
    }

    public void Dispose()
    {
        _apiKeyClient.Dispose();
        _adminClient.Dispose();
    }

    private HttpClient CreateApiKeyClient(string key)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", key);
        return client;
    }

    private static object MakePayload(
        string product, string service, string version,
        string branch = "refs/heads/main", object? manifest = null, string? artifactDigest = null) =>
        new
        {
            product,
            service,
            version,
            branch,
            commitSha = "495d92f0aa11bb22cc33dd44ee55ff6677889900",
            buildId = "123456",
            buildUrl = "https://dev.azure.com/org/proj/_build/results?buildId=123456",
            manifest,
            artifactRef = $"acr.example.io/{service}/build-metadata:{version}",
            artifactDigest,
        };

    private static async Task<JsonElement> Deserialize(HttpResponseMessage response) =>
        JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());

    // ── Registration + idempotency ──────────────────────────────────────────

    [Fact]
    public async Task RegisterBuild_ThenRePost_UpdatesInPlace()
    {
        var manifest = new { apiVersion = "v1-beta", spec = new { service = "api", version = "5.0.347-g495d92f0" } };
        var first = await _apiKeyClient.PostAsJsonAsync("/api/builds",
            MakePayload("bld-acme", "api", "5.0.347-g495d92f0", manifest: manifest));
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var firstBody = await Deserialize(first);
        Assert.False(firstBody.GetProperty("replayed").GetBoolean());
        var firstId = firstBody.GetProperty("id").GetString();

        // The retry carries the digest the first attempt died before resolving.
        var second = await _apiKeyClient.PostAsJsonAsync("/api/builds",
            MakePayload("bld-acme", "api", "5.0.347-g495d92f0", manifest: manifest,
                artifactDigest: "sha256:abc123"));
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var secondBody = await Deserialize(second);
        Assert.True(secondBody.GetProperty("replayed").GetBoolean());
        Assert.Equal(firstId, secondBody.GetProperty("id").GetString());

        // One row, carrying the retry's fuller picture.
        var list = await _adminClient.GetFromJsonAsync<JsonElement>(
            "/api/builds?product=bld-acme&service=api");
        var results = list.GetProperty("results").EnumerateArray().ToList();
        Assert.Single(results);
        Assert.Equal("sha256:abc123", results[0].GetProperty("artifactDigest").GetString());
        Assert.NotEqual(JsonValueKind.Null, results[0].GetProperty("updatedAt").ValueKind);
    }

    [Fact]
    public async Task RePostWithoutManifest_KeepsStoredManifest()
    {
        var manifest = new { spec = new { service = "keeper" } };
        var first = await _apiKeyClient.PostAsJsonAsync("/api/builds",
            MakePayload("bld-acme", "keeper", "1.0.1", manifest: manifest));
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var id = (await Deserialize(first)).GetProperty("id").GetString();

        var second = await _apiKeyClient.PostAsJsonAsync("/api/builds",
            MakePayload("bld-acme", "keeper", "1.0.1", manifest: null, artifactDigest: "sha256:def456"));
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        var detail = await _adminClient.GetFromJsonAsync<JsonElement>($"/api/builds/{id}");
        Assert.Equal("keeper",
            detail.GetProperty("manifest").GetProperty("spec").GetProperty("service").GetString());
        Assert.Equal("sha256:def456", detail.GetProperty("artifactDigest").GetString());
    }

    [Fact]
    public async Task DifferentVersions_CreateSeparateRows()
    {
        var first = await _apiKeyClient.PostAsJsonAsync("/api/builds",
            MakePayload("bld-acme", "web", "2.0.0-g1111111"));
        var second = await _apiKeyClient.PostAsJsonAsync("/api/builds",
            MakePayload("bld-acme", "web", "2.0.1-g2222222"));
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);

        var list = await _adminClient.GetFromJsonAsync<JsonElement>(
            "/api/builds?product=bld-acme&service=web");
        Assert.Equal(2, list.GetProperty("results").GetArrayLength());
    }

    [Fact]
    public async Task MissingRequiredFields_Returns400()
    {
        var response = await _apiKeyClient.PostAsJsonAsync("/api/builds",
            new { product = "bld-acme", service = "api", version = "1.0.0" }); // no branch
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await Deserialize(response);
        Assert.Contains("'branch' is required",
            body.GetProperty("errors").EnumerateArray().Select(e => e.GetString()));
    }

    // ── Auth: key, product scope, build:register scope ──────────────────────

    [Fact]
    public async Task NoApiKey_Returns401()
    {
        using var anon = _factory.CreateClient();
        var response = await anon.PostAsJsonAsync("/api/builds", MakePayload("bld-acme", "api", "9.9.9"));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task KeyScopedToOtherProduct_Returns403()
    {
        using var client = CreateApiKeyClient(BuildsFactory.ProductScopedKey);
        var response = await client.PostAsJsonAsync("/api/builds", MakePayload("bld-acme", "api", "9.9.8"));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task KeyWithoutBuildRegisterScope_Returns403()
    {
        using var client = CreateApiKeyClient(BuildsFactory.WrongScopeKey);
        var response = await client.PostAsJsonAsync("/api/builds", MakePayload("bld-acme", "api", "9.9.7"));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task KeyWithBuildRegisterScope_Succeeds()
    {
        using var client = CreateApiKeyClient(BuildsFactory.ScopedKey);
        var response = await client.PostAsJsonAsync("/api/builds", MakePayload("bld-acme", "scoped", "1.2.3"));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    // ── Read surface ────────────────────────────────────────────────────────

    [Fact]
    public async Task List_FiltersByBranchSubstring_NewestFirst()
    {
        await _apiKeyClient.PostAsJsonAsync("/api/builds",
            MakePayload("bld-branchy", "svc", "3.0.1-g1", branch: "refs/heads/main"));
        await _apiKeyClient.PostAsJsonAsync("/api/builds",
            MakePayload("bld-branchy", "svc", "3.0.2-g2", branch: "refs/heads/feature/MPT-1234-shiny"));
        await _apiKeyClient.PostAsJsonAsync("/api/builds",
            MakePayload("bld-branchy", "svc", "3.0.3-g3", branch: "refs/heads/feature/MPT-1234-shiny"));

        var filtered = await _adminClient.GetFromJsonAsync<JsonElement>(
            "/api/builds?product=bld-branchy&service=svc&branch=MPT-1234");
        var results = filtered.GetProperty("results").EnumerateArray().ToList();
        Assert.Equal(2, results.Count);
        Assert.All(results, r =>
            Assert.Contains("MPT-1234", r.GetProperty("branch").GetString()));
        // Newest first.
        Assert.Equal("3.0.3-g3", results[0].GetProperty("version").GetString());
    }

    [Fact]
    public async Task GetUnknownBuild_Returns404()
    {
        var response = await _adminClient.GetAsync($"/api/builds/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
