using System.Data.Common;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Platform.Api.Infrastructure.Persistence;

namespace Platform.Integration.Tests;

/// <summary>
/// Integration tests for the promotion and feature-flag endpoints, exercising the full
/// HTTP pipeline via <see cref="WebApplicationFactory{TEntryPoint}"/>.
/// </summary>
public class PromotionIntegrationTests : IClassFixture<PromotionIntegrationTests.PlatformFactory>, IDisposable
{
    private readonly PlatformFactory _factory;
    private readonly HttpClient _adminClient;
    private readonly HttpClient _userClient;

    public PromotionIntegrationTests(PlatformFactory factory)
    {
        _factory = factory;
        _adminClient = CreateAuthenticatedClient("admin@localhost", "admin123");
        _userClient = CreateAuthenticatedClient("user@localhost", "user123");
    }

    public void Dispose()
    {
        _adminClient.Dispose();
        _userClient.Dispose();
    }

    // ── Test cases ──────────────────────────────────────────────────────────

    [Fact]
    public async Task GetPromotionPolicies_ReturnsEmptyList()
    {
        var response = await _adminClient.GetAsync("/api/promotions/admin/policies");
        response.EnsureSuccessStatusCode();

        var body = await Deserialize(response);
        var policies = body.GetProperty("policies");
        Assert.Equal(JsonValueKind.Array, policies.ValueKind);
        // May contain policies created by other tests in the same fixture, so just assert it's an array.
    }

    // Topology endpoints were removed (D19): the external system is the source of truth for edges,
    // so GetTopology/PutTopology no longer exist. The corresponding tests were dropped.

    [Fact]
    public async Task PostPolicy_CreatesPolicy()
    {
        // §8 step-tree shape: steps[] → { name, requirements[] → { name, groups[], users[], minApprovers } }.
        var policy = new
        {
            product = "my-product",
            service = "my-service",
            sourceEnv = "staging",
            targetEnv = "prod",
            steps = new[]
            {
                new
                {
                    name = "Release Approval",
                    requirements = new[]
                    {
                        new
                        {
                            name = "Approvers",
                            groups = new[] { "release-approvers" },
                            users = Array.Empty<string>(),
                            minApprovers = 1,
                        },
                    },
                },
            },
            timeoutHours = 48,
            escalationGroup = (string?)null,
        };

        var response = await _adminClient.PostAsJsonAsync("/api/promotions/admin/policies", policy);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var body = await Deserialize(response);
        Assert.Equal("my-product", body.GetProperty("product").GetString());
        Assert.Equal("my-service", body.GetProperty("service").GetString());
        Assert.Equal("staging", body.GetProperty("sourceEnv").GetString());
        Assert.Equal("prod", body.GetProperty("targetEnv").GetString());
        Assert.Equal(48, body.GetProperty("timeoutHours").GetInt32());

        // Step tree round-trips.
        var steps = body.GetProperty("steps");
        Assert.Equal(1, steps.GetArrayLength());
        var req = steps[0].GetProperty("requirements")[0];
        // Groups are emitted as { id, name } objects. A bare-string on input (back-compat) is
        // normalised to id == name.
        var group = req.GetProperty("groups")[0];
        Assert.Equal("release-approvers", group.GetProperty("id").GetString());
        Assert.Equal("release-approvers", group.GetProperty("name").GetString());
        Assert.Equal(1, req.GetProperty("minApprovers").GetInt32());

        // Id should be a valid GUID.
        Assert.True(Guid.TryParse(body.GetProperty("id").GetString(), out _));
    }

    [Fact]
    public async Task GetFeatures_ReturnsFlags()
    {
        var response = await _userClient.GetAsync("/api/features");
        response.EnsureSuccessStatusCode();

        var body = await Deserialize(response);
        var flags = body.GetProperty("flags");
        Assert.Equal(JsonValueKind.Array, flags.ValueKind);

        // The seeder always inserts "features.promotions".
        var keys = new List<string>();
        foreach (var flag in flags.EnumerateArray())
            keys.Add(flag.GetProperty("key").GetString()!);

        Assert.Contains("features.promotions", keys);
    }

    [Fact]
    public async Task PutFeatureFlag_TogglesFlag()
    {
        // Enable the flag.
        var enableResponse = await _adminClient.PutAsJsonAsync(
            "/api/features/features.promotions",
            new { enabled = true });
        enableResponse.EnsureSuccessStatusCode();

        var enableBody = await Deserialize(enableResponse);
        Assert.True(enableBody.GetProperty("enabled").GetBoolean());

        // Disable the flag.
        var disableResponse = await _adminClient.PutAsJsonAsync(
            "/api/features/features.promotions",
            new { enabled = false });
        disableResponse.EnsureSuccessStatusCode();

        var disableBody = await Deserialize(disableResponse);
        Assert.False(disableBody.GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public async Task PutFeatureFlag_NonAdmin_Returns403()
    {
        var response = await _userClient.PutAsJsonAsync(
            "/api/features/features.promotions",
            new { enabled = true });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Logs in via the local auth endpoint and returns an <see cref="HttpClient"/> with
    /// the Bearer token pre-configured.
    /// </summary>
    private HttpClient CreateAuthenticatedClient(string email, string password)
    {
        var client = _factory.CreateClient();
        var loginResponse = client.PostAsJsonAsync("/api/auth/login", new { email, password })
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

    /// <summary>
    /// Configures the test server to use an in-memory SQLite database and local-JWT auth.
    /// A single shared connection keeps the in-memory database alive for the lifetime of
    /// the factory. The schema is pre-created with <c>EnsureCreated</c> so the startup
    /// <c>MigrateAsync</c> call is a no-op.
    /// </summary>
    public class PlatformFactory : WebApplicationFactory<Program>
    {
        private readonly SqliteConnection _connection;

        public PlatformFactory()
        {
            _connection = new SqliteConnection("DataSource=:memory:");
            _connection.Open();

            var options = new DbContextOptionsBuilder<PlatformDbContext>()
                .UseSqlite(_connection)
                .Options;
            using var db = new PlatformDbContext(options);
            db.Database.EnsureCreated();
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            // Use a non-Development environment to skip SeedDemoData (which requires
            // catalog items loaded from YAML files that are not present in the test DB).
            // Auth:Mode defaults to "Local" regardless of environment.
            builder.UseEnvironment("Testing");

            builder.ConfigureServices(services =>
            {
                // Remove the real DB registrations (both provider-specific and the base context).
                RemoveService<DbContextOptions<PostgresPlatformDbContext>>(services);
                RemoveService<DbContextOptions<SqlServerPlatformDbContext>>(services);
                RemoveService<DbContextOptions<PlatformDbContext>>(services);
                RemoveService<PostgresPlatformDbContext>(services);
                RemoveService<SqlServerPlatformDbContext>(services);
                RemoveService<PlatformDbContext>(services);

                services.AddSingleton<DbConnection>(_connection);
                services.AddDbContext<PlatformDbContext>((sp, options) =>
                    options.UseSqlite(sp.GetRequiredService<DbConnection>()));
            });
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing) _connection.Dispose();
        }

        private static void RemoveService<T>(IServiceCollection services)
        {
            var descriptors = services.Where(d => d.ServiceType == typeof(T)).ToList();
            foreach (var d in descriptors) services.Remove(d);
        }
    }
}
