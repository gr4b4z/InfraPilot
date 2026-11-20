using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Platform.Api.Features.Settings;
using Platform.Api.Infrastructure.Persistence;

namespace Platform.Integration.Tests;

/// <summary>
/// Settings through an API key, the way an agent reaches them. Before this, an admin key could
/// replace the whole settings row but not read it, so the only write it could make was a blind
/// one that wiped every environment and role it didn't resend. Now the row (and the feature flags)
/// read with any key, and <c>PATCH /api/settings/roles/{key}</c> changes one role and leaves the
/// rest as stored. Writes stay admin-only.
/// </summary>
public class SettingsApiAccessTests : IClassFixture<SettingsApiAccessTests.SettingsFactory>, IDisposable
{
    private readonly SettingsFactory _factory;
    private readonly HttpClient _adminKey;
    private readonly HttpClient _plainKey;

    public SettingsApiAccessTests(SettingsFactory factory)
    {
        _factory = factory;
        _adminKey = factory.CreateClient();
        _adminKey.DefaultRequestHeaders.Add("X-Api-Key", SettingsFactory.AdminKey);
        _plainKey = factory.CreateClient();
        _plainKey.DefaultRequestHeaders.Add("X-Api-Key", SettingsFactory.PlainKey);
    }

    public void Dispose()
    {
        _adminKey.Dispose();
        _plainKey.Dispose();
    }

    // ── Reads ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Get_WithAnyKey_ReturnsTheSettings()
    {
        foreach (var client in new[] { _adminKey, _plainKey })
        {
            var resp = await client.GetAsync("/api/settings");
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            var settings = await Deserialize(resp);
            Assert.Contains(settings.GetProperty("roles").EnumerateArray(),
                r => r.GetProperty("key").GetString() == "qa-owner");
        }
    }

    [Fact]
    public async Task Get_WithoutAKey_IsUnauthorized()
    {
        using var anonymous = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/settings")).StatusCode);
    }

    [Fact]
    public async Task FeatureFlags_ReadWithAKey()
    {
        Assert.Equal(HttpStatusCode.OK, (await _plainKey.GetAsync("/api/features")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _plainKey.GetAsync("/api/features/features.promotions")).StatusCode);
    }

    [Fact]
    public async Task Me_WithAPersonalToken_AnswersForItsOwner()
    {
        using var session = _factory.CreateClient();
        var login = await session.PostAsJsonAsync("/api/auth/login", new { email = "admin@localhost", password = "admin123" });
        login.EnsureSuccessStatusCode();
        session.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer", (await Deserialize(login)).GetProperty("token").GetString());
        var created = await session.PostAsJsonAsync("/api/me/tokens", new { name = "me test", expiresInDays = 7 });
        created.EnsureSuccessStatusCode();
        var token = (await Deserialize(created)).GetProperty("token").GetString()!;

        using var agent = _factory.CreateClient();
        agent.DefaultRequestHeaders.Add("X-Api-Key", token);

        var emails = await agent.GetAsync("/api/me/emails");
        Assert.Equal(HttpStatusCode.OK, emails.StatusCode);
        Assert.Equal("admin@localhost", (await Deserialize(emails)).GetProperty("email").GetString());
        Assert.Equal(HttpStatusCode.OK, (await agent.GetAsync("/api/me/preferences")).StatusCode);
        // Still a session only: a token can't mint another.
        Assert.NotEqual(HttpStatusCode.OK, (await agent.GetAsync("/api/me/tokens")).StatusCode);
    }

    // ── One role at a time ───────────────────────────────────────────────────

    [Fact]
    public async Task PatchRole_SetsTheTeam_AndKeepsEverythingElse()
    {
        try
        {
            await SeedAsync();

            var resp = await _adminKey.PatchAsJsonAsync("/api/settings/roles/qa-owner", new
            {
                team = new object[]
                {
                    new { email = " ada@example.com ", displayName = "Ada Tester" },
                    new { email = "bo@example.com", displayName = "" },
                },
            });
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            var returned = await Deserialize(resp);
            Assert.Equal("qa-owner", returned.GetProperty("key").GetString());
            Assert.Equal(new[] { ("ada@example.com", "Ada Tester"), ("bo@example.com", "bo@example.com") },
                Team(returned));

            var settings = await Deserialize(await _adminKey.GetAsync("/api/settings"));
            var owner = Role(settings, "qa-owner");
            Assert.Equal("QA owner", owner.GetProperty("displayName").GetString());
            Assert.Equal(new[] { "quality" }, Strings(owner, "aliases"));
            Assert.Equal(new[] { ("ada@example.com", "Ada Tester"), ("bo@example.com", "bo@example.com") },
                Team(owner));
            // The rest of the row is what was stored, not defaults.
            Assert.Equal("Reviewer (code)", Role(settings, "reviewer").GetProperty("displayName").GetString());
            var production = settings.GetProperty("environments").EnumerateArray()
                .Single(e => e.GetProperty("key").GetString() == "production");
            Assert.Equal(new[] { "prod" }, Strings(production, "aliases"));
            Assert.Equal("{time}", settings.GetProperty("activityTemplate")[0].GetProperty("template").GetString());
        }
        finally
        {
            await ResetSettingsAsync();
        }
    }

    [Fact]
    public async Task PatchRole_ByAlias_ReachesTheCanonicalRole()
    {
        try
        {
            await SeedAsync();

            var resp = await _adminKey.PatchAsJsonAsync("/api/settings/roles/Quality",
                new { team = new[] { new { email = "ada@example.com", displayName = "Ada" } } });
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            Assert.Equal("qa-owner", (await Deserialize(resp)).GetProperty("key").GetString());
        }
        finally
        {
            await ResetSettingsAsync();
        }
    }

    [Fact]
    public async Task PatchRole_AnEmptyList_ClearsIt()
    {
        try
        {
            await SeedAsync();
            await _adminKey.PatchAsJsonAsync("/api/settings/roles/qa-owner",
                new { team = new[] { new { email = "ada@example.com", displayName = "Ada" } } });

            var resp = await _adminKey.PatchAsJsonAsync("/api/settings/roles/qa-owner",
                new { aliases = Array.Empty<string>() });
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            var owner = await Deserialize(resp);
            Assert.Empty(Strings(owner, "aliases"));
            Assert.Single(Team(owner)); // left out, so kept
        }
        finally
        {
            await ResetSettingsAsync();
        }
    }

    [Fact]
    public async Task PatchRole_UnknownRole_Is404()
    {
        var resp = await _adminKey.PatchAsJsonAsync("/api/settings/roles/qa-lead", new { team = Array.Empty<object>() });

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        Assert.Contains("qa-owner", (await Deserialize(resp)).GetProperty("error").GetString() ?? "");
    }

    [Fact]
    public async Task PatchRole_ATeamMemberWithoutAnAddress_Is400AndSavesNothing()
    {
        try
        {
            await SeedAsync();

            var resp = await _adminKey.PatchAsJsonAsync("/api/settings/roles/qa-owner",
                new { team = new[] { new { email = "Ada Tester", displayName = "Ada Tester" } } });

            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
            Assert.Contains("'Ada Tester'", (await Deserialize(resp)).GetProperty("error").GetString() ?? "");
            var settings = await Deserialize(await _adminKey.GetAsync("/api/settings"));
            Assert.Empty(Team(Role(settings, "qa-owner")));
        }
        finally
        {
            await ResetSettingsAsync();
        }
    }

    [Fact]
    public async Task Writes_NeedTheAdminRole()
    {
        var patch = await _plainKey.PatchAsJsonAsync("/api/settings/roles/qa-owner",
            new { team = new[] { new { email = "ada@example.com", displayName = "Ada" } } });
        Assert.Equal(HttpStatusCode.Forbidden, patch.StatusCode);

        var current = await Deserialize(await _plainKey.GetAsync("/api/settings"));
        var put = await _plainKey.PutAsJsonAsync("/api/settings", current);
        Assert.Equal(HttpStatusCode.Forbidden, put.StatusCode);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>A stored row that differs from the defaults everywhere a PATCH could clobber it.</summary>
    private async Task SeedAsync()
    {
        var resp = await _adminKey.PutAsJsonAsync("/api/settings", new
        {
            environments = new object[]
            {
                new { key = "staging", displayName = "Staging" },
                new { key = "production", displayName = "Production", isProduction = true, aliases = new[] { "prod" } },
            },
            roles = new object[]
            {
                new { key = "reviewer", displayName = "Reviewer (code)" },
                new { key = "qa-owner", displayName = "QA owner", aliases = new[] { "quality" } },
            },
            activityTemplate = new[] { new { template = "{time}", style = "muted" } },
        });
        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);
    }

    private async Task ResetSettingsAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var existing = await db.PlatformSettings.FirstOrDefaultAsync(s => s.Key == AppSettingsService.SettingsKey);
        if (existing is not null)
        {
            db.PlatformSettings.Remove(existing);
            await db.SaveChangesAsync();
        }
    }

    private static JsonElement Role(JsonElement settings, string key)
        => settings.GetProperty("roles").EnumerateArray().Single(r => r.GetProperty("key").GetString() == key);

    private static (string, string)[] Team(JsonElement role)
        => role.TryGetProperty("team", out var team) && team.ValueKind == JsonValueKind.Array
            ? team.EnumerateArray()
                .Select(m => (m.GetProperty("email").GetString()!, m.GetProperty("displayName").GetString()!))
                .ToArray()
            : [];

    private static string[] Strings(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Select(v => v.GetString()!).ToArray()
            : [];

    private static async Task<JsonElement> Deserialize(HttpResponseMessage response)
    {
        var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        return doc.RootElement;
    }

    // ── Factory ──────────────────────────────────────────────────────────────

    public class SettingsFactory : WebApplicationFactory<Program>
    {
        public const string AdminKey = "settings-admin-test-key-13579";
        public const string PlainKey = "settings-plain-test-key-97531";

        private readonly SqliteConnection _connection = TestDatabase.Open();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");

            builder.UseSetting("Deployments:ApiKeys:0:Name", "settings-admin-key");
            builder.UseSetting("Deployments:ApiKeys:0:Key", AdminKey);
            builder.UseSetting("Deployments:ApiKeys:0:Roles:0", "InfraPortal.Admin");
            builder.UseSetting("Deployments:ApiKeys:1:Name", "settings-plain-key");
            builder.UseSetting("Deployments:ApiKeys:1:Key", PlainKey);

            builder.ConfigureServices(services =>
            {
                RemoveService<DbContextOptions<PostgresPlatformDbContext>>(services);
                RemoveService<DbContextOptions<SqlServerPlatformDbContext>>(services);
                RemoveService<DbContextOptions<PlatformDbContext>>(services);
                RemoveService<PostgresPlatformDbContext>(services);
                RemoveService<SqlServerPlatformDbContext>(services);
                RemoveService<PlatformDbContext>(services);

                services.AddSingleton<DbConnection>(_connection);
                services.AddDbContext<PlatformDbContext, SqliteTestDbContext>((sp, options) =>
                    options.UseSqlite(sp.GetRequiredService<DbConnection>()));

                TestHostIsolation.Apply(services);
            });
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing) _connection.Dispose();
        }

        private static void RemoveService<T>(IServiceCollection services)
        {
            var descriptor = services.FirstOrDefault(d => d.ServiceType == typeof(T));
            if (descriptor is not null) services.Remove(descriptor);
        }
    }
}
