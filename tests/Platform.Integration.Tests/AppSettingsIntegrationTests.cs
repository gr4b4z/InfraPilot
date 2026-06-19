using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;

namespace Platform.Integration.Tests;

/// <summary>
/// Integration tests for the shared UI settings endpoints (environments, roles, activity
/// template). These moved from browser localStorage to the server so config can no longer
/// silently revert to defaults. Reads are open to any authenticated user; writes are admin-only.
/// </summary>
public class AppSettingsIntegrationTests : IClassFixture<AppSettingsIntegrationTests.SettingsFactory>, IDisposable
{
    private readonly SettingsFactory _factory;
    private readonly HttpClient _adminClient;
    private readonly HttpClient _userClient;

    public AppSettingsIntegrationTests(SettingsFactory factory)
    {
        _factory = factory;
        _adminClient = factory.CreateAdminClient();
        _userClient = CreateUserClient();
    }

    public void Dispose()
    {
        _adminClient.Dispose();
        _userClient.Dispose();
    }

    // ── Tests ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Get_OnFreshDb_ReturnsBuiltInDefaults()
    {
        // Own factory → pristine in-memory DB, independent of write tests in this class.
        using var factory = new SettingsFactory();
        using var admin = factory.CreateAdminClient();
        var body = await Deserialize(await admin.GetAsync("/api/settings"));

        var envs = body.GetProperty("environments");
        Assert.Contains(envs.EnumerateArray(),
            e => e.GetProperty("key").GetString() == "production"
                 && e.GetProperty("displayName").GetString() == "Production");
        Assert.NotEmpty(body.GetProperty("roles").EnumerateArray());
        Assert.NotEmpty(body.GetProperty("activityTemplate").EnumerateArray());
    }

    [Fact]
    public async Task AdminSave_ThenRead_PersistsCustomMapping()
    {
        // The reported bug: a custom mapping (dev → Development) silently vanished. Prove it
        // now survives a round-trip through the server.
        var payload = new
        {
            environments = new[]
            {
                new { key = "dev", displayName = "Development" },
                new { key = "prod", displayName = "Production" },
            },
            roles = new[] { new { key = "qa", displayName = "QA" } },
            activityTemplate = new[] { new { template = "{service} — {time}", style = "muted" } },
        };

        var put = await _adminClient.PutAsJsonAsync("/api/settings", payload);
        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);

        // Re-read with a *different* client to confirm it's server-side, not client memory.
        var body = await Deserialize(await _userClient.GetAsync("/api/settings"));
        var dev = body.GetProperty("environments").EnumerateArray()
            .Single(e => e.GetProperty("key").GetString() == "dev");
        Assert.Equal("Development", dev.GetProperty("displayName").GetString());
    }

    [Fact]
    public async Task AdminSave_StripsBlankKeyRows()
    {
        var payload = new
        {
            environments = new[]
            {
                new { key = "staging", displayName = "Staging" },
                new { key = "   ", displayName = "Ignored" },
            },
            roles = Array.Empty<object>(),
            activityTemplate = Array.Empty<object>(),
        };

        await _adminClient.PutAsJsonAsync("/api/settings", payload);

        var body = await Deserialize(await _adminClient.GetAsync("/api/settings"));
        var envs = body.GetProperty("environments").EnumerateArray().ToList();
        Assert.Single(envs);
        Assert.Equal("staging", envs[0].GetProperty("key").GetString());
    }

    [Fact]
    public async Task NonAdmin_CanRead_ButCannotWrite()
    {
        var read = await _userClient.GetAsync("/api/settings");
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);

        var write = await _userClient.PutAsJsonAsync("/api/settings", new
        {
            environments = Array.Empty<object>(),
            roles = Array.Empty<object>(),
            activityTemplate = Array.Empty<object>(),
        });
        Assert.Equal(HttpStatusCode.Forbidden, write.StatusCode);
    }

    [Fact]
    public async Task Anonymous_CannotRead()
    {
        using var anon = _factory.CreateClient();
        var response = await anon.GetAsync("/api/settings");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private HttpClient CreateUserClient()
    {
        var client = _factory.CreateClient();
        var loginResponse = client.PostAsJsonAsync("/api/auth/login", new { email = "user@localhost", password = "user123" })
            .GetAwaiter().GetResult();
        loginResponse.EnsureSuccessStatusCode();
        var loginBody = Deserialize(loginResponse).GetAwaiter().GetResult();
        var token = loginBody.GetProperty("token").GetString()!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static async Task<JsonElement> Deserialize(HttpResponseMessage response)
    {
        var stream = await response.Content.ReadAsStreamAsync();
        var doc = await JsonDocument.ParseAsync(stream);
        return doc.RootElement;
    }

    // ── Factory ─────────────────────────────────────────────────────────────

    public class SettingsFactory : TestFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
        }
    }
}
