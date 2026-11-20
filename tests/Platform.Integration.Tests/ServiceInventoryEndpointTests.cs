using System.Data.Common;
using System.Net;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.Api.Features.ServiceInventory;
using Platform.Api.Features.ServiceInventory.Sources;
using Platform.Api.Infrastructure.Features;
using Platform.Api.Infrastructure.Persistence;

namespace Platform.Integration.Tests;

/// <summary>Fictional files with the shape of the published artifacts, served by a fake source.</summary>
public static class InventoryFiles
{
    private static string Dir => Path.Combine(AppContext.BaseDirectory, "ServiceInventoryFixtures");
    public static byte[] Bytes(string name) => File.ReadAllBytes(Path.Combine(Dir, name));
}

public sealed class FixtureSource(Func<string?, FetchResult> answer) : IArtifactSource
{
    public string Kind => "https";
    public string Location => "files.example.com/feed.json";
    public Task<FetchResult> FetchIfChangedAsync(string? lastVersion, CancellationToken ct) => Task.FromResult(answer(lastVersion));
    public Task<IReadOnlyList<string>> ListClosedMonthsAsync(string from, ISet<string> stored, DateTimeOffset now, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<string>>([]);
    public IArtifactSource ForMonth(string period) => throw new NotSupportedException();

    public static FetchResult File(string name, string version) =>
        FetchResult.Downloaded(new ArtifactFile(InventoryFiles.Bytes(name), version, "files.example.com/feed.json", "https"));
}

public sealed class FixtureSourceFactory : IArtifactSourceFactory
{
    public Dictionary<string, string> Files { get; } = new()
    {
        ["catalog"] = "catalog.json",
        ["status"] = "status.json",
        ["cost"] = "cost-2026-10.json",
    };

    public IArtifactSource Create(InventoryFeedOptions feed) =>
        new FixtureSource(_ => FixtureSource.File(Files[feed.Name], "v-" + Files[feed.Name]));
}

/// <summary>
/// The read-only endpoints under <c>/api/service-inventory</c>, on data stored by the real
/// refresher from fictional fixtures.
/// </summary>
public class ServiceInventoryEndpointTests : IClassFixture<ServiceInventoryEndpointTests.InventoryFactory>
{
    public class InventoryFactory : TestFactory
    {
        public FixtureSourceFactory Sources { get; } = new();

        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(services =>
            {
                RemoveService<IArtifactSourceFactory>(services);
                services.AddScoped<IArtifactSourceFactory>(_ => Sources);
            });
        }

        private bool _seeded;
        private readonly object _gate = new();

        /// <summary>Turns the flag on and stores the three fixtures through the real refresher, once.</summary>
        public void Seed()
        {
            lock (_gate)
            {
                if (_seeded) return;
                using var scope = Services.CreateScope();
                scope.ServiceProvider.GetRequiredService<IFeatureFlags>()
                    .SetEnabled(FeatureFlagKeys.ServiceInventory, true, "test").GetAwaiter().GetResult();
                var refresher = scope.ServiceProvider.GetRequiredService<InventoryRefresher>();
                foreach (var feed in new[] { "catalog", "status", "cost" })
                    refresher.RefreshAsync(new InventoryFeedOptions { Name = feed, Type = feed, Source = new() { Kind = "https" } }, default)
                        .GetAwaiter().GetResult();
                _seeded = true;
            }
        }
    }

    private readonly InventoryFactory _factory;

    public ServiceInventoryEndpointTests(InventoryFactory factory)
    {
        _factory = factory;
        _factory.Seed();
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    [Fact]
    public async Task A_service_is_found_by_its_portal_name()
    {
        var client = _factory.CreateAdminClient();

        var response = await client.GetAsync("/api/service-inventory/services/Order-Router");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await Json(response);
        Assert.Equal("order-router", body.GetProperty("service").GetProperty("id").GetString());
        Assert.Equal("storefront-team", body.GetProperty("team").GetProperty("id").GetString());
        Assert.Equal("Storefront", body.GetProperty("product").GetProperty("name").GetString());
        var database = body.GetProperty("service").GetProperty("databases")[0];
        Assert.Equal("shared-postgres", database.GetProperty("server").GetString());
        Assert.Equal("catalog v5", body.GetProperty("freshness").GetProperty("version").GetString());
    }

    [Fact]
    public async Task An_unknown_service_is_404_and_still_says_how_old_the_data_is()
    {
        var response = await _factory.CreateAdminClient().GetAsync("/api/service-inventory/services/no-such-service");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.True((await Json(response)).TryGetProperty("freshness", out _));
    }

    [Fact]
    public async Task Observed_state_keeps_unresolved_and_unknown_as_published()
    {
        var response = await _factory.CreateAdminClient().GetAsync("/api/service-inventory/services/order-router/observed");

        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var body = await Json(response);
        var bindings = body.GetProperty("azure").GetProperty("bindings");
        Assert.Equal(2, bindings.GetArrayLength());
        Assert.All(bindings.EnumerateArray(), b => Assert.Equal("present", b.GetProperty("state").GetString()));
        Assert.Equal("shared-postgres", body.GetProperty("azure").GetProperty("shared")[0].GetProperty("id").GetString());
        Assert.Equal("unknown", body.GetProperty("onboarding").GetProperty("namespace").GetProperty("status").GetString());
    }

    [Fact]
    public async Task Cost_comes_back_per_currency_with_direct_and_shared_apart()
    {
        var response = await _factory.CreateAdminClient().GetAsync("/api/service-inventory/services/order-router/cost");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await Json(response);
        Assert.Equal("2026-10", body.GetProperty("period").GetString());
        Assert.True(body.GetProperty("complete").GetBoolean());
        var amounts = body.GetProperty("amounts").EnumerateArray().ToList();
        Assert.Equal(["EUR", "USD"], amounts.Select(a => a.GetProperty("currency").GetString()));
        Assert.Equal(113.40m, amounts[1].GetProperty("direct").GetDecimal());
        Assert.Equal(82.50m, amounts[1].GetProperty("shared").GetDecimal());
        Assert.Equal(205.00m, amounts[0].GetProperty("total").GetDecimal());
        Assert.Equal("shared-postgres", body.GetProperty("sharedFrom")[0].GetProperty("shared").GetString());
    }

    [Fact]
    public async Task A_month_that_was_never_read_is_404()
    {
        var response = await _factory.CreateAdminClient().GetAsync("/api/service-inventory/services/order-router/cost?period=2020-01");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Feed_state_is_admin_only()
    {
        var user = _factory.CreateAuthenticatedClient("user@localhost", "user123");
        Assert.Equal(HttpStatusCode.Forbidden, (await user.GetAsync("/api/service-inventory/feeds")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await user.GetAsync("/api/service-inventory/services/order-router")).StatusCode);

        var admin = await _factory.CreateAdminClient().GetAsync("/api/service-inventory/feeds");
        Assert.Equal(HttpStatusCode.OK, admin.StatusCode);
        var state = (await Json(admin)).GetProperty("state").EnumerateArray().Select(s => s.GetProperty("feed").GetString()).ToList();
        Assert.Contains("catalog", state);
    }
}

/// <summary>The feature flag gates every inventory endpoint.</summary>
public class ServiceInventoryFlagTests : IClassFixture<TestFactory>
{
    private readonly TestFactory _factory;
    public ServiceInventoryFlagTests(TestFactory factory) => _factory = factory;

    [Fact]
    public async Task With_the_flag_off_the_endpoints_answer_404()
    {
        using (var scope = _factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<IFeatureFlags>().SetEnabled(FeatureFlagKeys.ServiceInventory, false, "test");

        var response = await _factory.CreateAdminClient().GetAsync("/api/service-inventory/services/order-router");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("not enabled", await response.Content.ReadAsStringAsync());
    }
}

/// <summary>
/// On a relational database the snapshot, its rows and the pointer are written in one transaction:
/// a failure halfway leaves the previous copy exactly as it was.
/// </summary>
public class ServiceInventoryAtomicityTests
{
    private sealed class FailOnceOnFeedUpdate : DbCommandInterceptor
    {
        public bool Armed { get; set; }

        public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            Throw(command);
            return result;
        }

        public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
        {
            Throw(command);
            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Throw(command);
            return ValueTask.FromResult(result);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Throw(command);
            return ValueTask.FromResult(result);
        }

        private void Throw(DbCommand command)
        {
            if (!Armed || !command.CommandText.Contains("UPDATE \"inventory_feeds\"", StringComparison.Ordinal)) return;
            Armed = false;
            throw new SqliteException("forced failure in the middle of the save", 1);
        }
    }

    [Fact]
    public async Task A_failure_during_the_save_commits_nothing_and_keeps_the_previous_copy()
    {
        await using var connection = TestDatabase.Open();
        var interceptor = new FailOnceOnFeedUpdate();
        await using var db = new SqliteTestDbContext(new DbContextOptionsBuilder<SqliteTestDbContext>()
            .UseSqlite(connection).AddInterceptors(interceptor).Options);
        var files = new Queue<FetchResult>([
            FixtureSource.File("cost-2026-10.json", "v1"),
            FixtureSource.File("cost-2026-11.json", "v2"),
        ]);
        var source = new FixtureSource(_ => files.Dequeue());
        var refresher = new InventoryRefresher(db, new SingleSource(source), TimeProvider.System, NullLogger<InventoryRefresher>.Instance);
        var feed = new InventoryFeedOptions { Name = "cost", Type = "cost", Source = new() { Kind = "https" } };

        await refresher.RefreshAsync(feed, default);
        var before = await db.InventoryFeeds.AsNoTracking().SingleAsync(f => f.FeedKey == "cost");
        var rowsBefore = await db.InventoryCostServices.CountAsync();

        interceptor.Armed = true;
        await refresher.RefreshAsync(feed, default);

        var after = await db.InventoryFeeds.AsNoTracking().SingleAsync(f => f.FeedKey == "cost");
        Assert.Equal(before.CurrentSnapshotId, after.CurrentSnapshotId);
        Assert.Equal("v1", after.LastSourceVersion);
        Assert.NotNull(after.LastError);
        Assert.Equal(1, await db.InventorySnapshots.CountAsync());
        Assert.Equal(rowsBefore, await db.InventoryCostServices.CountAsync());
        Assert.False(await db.InventoryFeeds.AnyAsync(f => f.FeedKey == "cost:2026-11"));
    }

    private sealed class SingleSource(IArtifactSource source) : IArtifactSourceFactory
    {
        public IArtifactSource Create(InventoryFeedOptions feed) => source;
    }
}
