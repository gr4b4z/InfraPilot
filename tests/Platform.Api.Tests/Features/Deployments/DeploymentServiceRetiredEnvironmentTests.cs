using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Platform.Api.Features.Deployments;
using Platform.Api.Features.Deployments.Models;
using Platform.Api.Features.Promotions;
using Platform.Api.Features.Settings;
using Platform.Api.Features.Settings.Models;
using Platform.Api.Features.Webhooks;
using Platform.Api.Infrastructure.Auth;
using Platform.Api.Infrastructure.Persistence;

namespace Platform.Api.Tests.Features.Deployments;

/// <summary>
/// Retiring an environment: what it takes out of the current-state read paths, and the history it
/// deliberately leaves alone.
///
/// <para>The fixture is the case that motivated the flag: <c>stable</c> stopped receiving deploys,
/// but its last events kept rendering as the version "currently" there. Every test seeds the same
/// history — <c>api</c> in dev/stable/prod, <c>legacy</c> only ever in stable — and reads it through
/// <see cref="DeploymentService"/>, because the settings row existing is not the feature; the
/// environment vanishing from the queries the pages call is.</para>
/// </summary>
public class DeploymentServiceRetiredEnvironmentTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 09, 01, 10, 0, 0, TimeSpan.Zero);

    private readonly PlatformDbContext _db;
    private readonly AppSettingsService _settings;

    public DeploymentServiceRetiredEnvironmentTests()
    {
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _db = new PlatformDbContext(options);

        var user = Substitute.For<ICurrentUser>();
        user.Id.Returns("admin-1");
        user.Name.Returns("Ada Admin");
        user.Email.Returns("ada@example.com");
        _settings = new AppSettingsService(_db, user);
    }

    public void Dispose() => _db.Dispose();

    // A fresh service per read, like a fresh request: the resolver memoises settings for its
    // lifetime, so a service built before ConfigureAsync would keep the old snapshot.
    private DeploymentService Sut() => new(
        _db,
        Substitute.For<IWebhookDispatcher>(),
        Substitute.For<IPromotionIngestHook>(),
        TestOptions.Normalization(),
        new EnvironmentAliasResolver(_settings, Substitute.For<ILogger<EnvironmentAliasResolver>>()),
        TestUserPreferences.For(_db),
        TestServiceDeletions.For(_db),
        TestProductOverrides.For(_db),
        Substitute.For<ILogger<DeploymentService>>());

    private Task ConfigureAsync(bool retireStable, params string[] stableAliases)
        => _settings.SaveSettings(new AppSettingsDto(
            [
                new EnvironmentConfigDto("dev", "Development"),
                new EnvironmentConfigDto("stable", "Staging Candidate", Aliases: [.. stableAliases], Retired: retireStable),
                new EnvironmentConfigDto("prod", "Production", IsProduction: true),
            ],
            [], []));

    private DeployEvent Seed(
        string service, string environment, string version, int hoursAfterT0, string product = "marketplace")
    {
        var ev = new DeployEvent
        {
            Id = Guid.NewGuid(),
            Product = product,
            Service = service,
            Environment = environment,
            Version = version,
            Status = "succeeded",
            Source = "github-actions",
            DeployedAt = T0.AddHours(hoursAfterT0),
        };
        _db.DeployEvents.Add(ev);
        return ev;
    }

    /// <summary>
    /// <c>api</c>: 1.0.0 reached dev, stable and prod; 1.1.0 reached dev and stable; 0.9.0 only ever
    /// stable. <c>legacy</c> only ever deployed to stable. <c>storefront</c>, another product, only
    /// to stable as well.
    /// </summary>
    private async Task<DeployEvent> SeedHistoryAsync()
    {
        Seed("api", "stable", "0.9.0", 0);
        Seed("api", "dev", "1.0.0", 1);
        Seed("api", "stable", "1.0.0", 2);
        Seed("api", "prod", "1.0.0", 3);
        Seed("api", "dev", "1.1.0", 4);
        var stableHead = Seed("api", "stable", "1.1.0", 5);
        Seed("legacy", "stable", "3.0.0", 1);
        Seed("web", "stable", "2.0.0", 1, product: "storefront");
        await _db.SaveChangesAsync();
        return stableHead;
    }

    // ── Nothing retired: unchanged ──────────────────────────────────────────

    [Fact]
    public async Task NothingRetired_EveryReadPathStillShowsTheEnvironment()
    {
        await SeedHistoryAsync();
        await ConfigureAsync(retireStable: false);

        var state = await Sut().GetState("marketplace", null, null);
        Assert.Equal(
            ["api/dev", "api/prod", "api/stable", "legacy/stable"],
            state.Select(s => $"{s.Service}/{s.Environment}"));

        var products = await Sut().GetProductSummaries();
        Assert.Equal(["marketplace", "storefront"], products.Select(p => p.Product).OrderBy(p => p));
        Assert.Equal(2, products.Single(p => p.Product == "marketplace").Environments["stable"].TotalServices);

        var hits = await Sut().SearchServices("a");
        Assert.Contains(hits, h => h.Service == "legacy");

        var detail = await Sut().GetServiceDetail("marketplace", "api");
        Assert.NotNull(detail);
        Assert.Contains(detail!.Environments, e => e.Environment == "stable");
        Assert.Equal(["1.1.0", "1.0.0", "0.9.0"], detail.RecentVersions.Select(v => v.Version));
    }

    // ── Current state: filtered ────────────────────────────────────────────

    [Fact]
    public async Task RetiredEnvironment_LeavesTheStateMatrix()
    {
        await SeedHistoryAsync();
        await ConfigureAsync(retireStable: true);

        var state = await Sut().GetState("marketplace", null, null);
        Assert.Equal(["api/dev", "api/prod"], state.Select(s => $"{s.Service}/{s.Environment}"));

        var summary = await Sut().GetStateSummary("marketplace", null, null);
        Assert.Equal(["api/dev", "api/prod"], summary.Select(s => $"{s.Service}/{s.Environment}"));
    }

    [Fact]
    public async Task RetiredEnvironment_HasNoCurrentState_EvenWhenAskedForByName()
    {
        await SeedHistoryAsync();
        await ConfigureAsync(retireStable: true);

        Assert.Empty(await Sut().GetState("marketplace", "stable", null));
        Assert.Empty(await Sut().GetStateSummary(null, "stable", null));
    }

    [Fact]
    public async Task RowsStoredUnderAnUnmergedAlias_LeaveTheMatrixToo()
    {
        // Configured as an alias but never merged: the rows still say "release-track", and the web
        // client renders them under the retired label — so they are the retired environment.
        Seed("api", "dev", "1.0.0", 1);
        Seed("api", "release-track", "1.0.0", 2);
        await _db.SaveChangesAsync();
        await ConfigureAsync(retireStable: true, "release-track");

        var state = await Sut().GetState("marketplace", null, null);
        Assert.Equal(["dev"], state.Select(s => s.Environment));
    }

    [Fact]
    public async Task RetiredEnvironment_LeavesTheProductCards()
    {
        await SeedHistoryAsync();
        await ConfigureAsync(retireStable: true);

        var products = await Sut().GetProductSummaries();

        // storefront was only ever in stable, so it has nothing current to show.
        var marketplace = Assert.Single(products);
        Assert.Equal("marketplace", marketplace.Product);
        Assert.Equal(["dev", "prod"], marketplace.Environments.Keys.OrderBy(k => k));
    }

    [Fact]
    public async Task RetiredEnvironment_LeavesSearchHits()
    {
        await SeedHistoryAsync();
        await ConfigureAsync(retireStable: true);

        var hits = await Sut().SearchServices("a");

        // legacy only ever deployed to stable; api keeps its other environments.
        var api = Assert.Single(hits);
        Assert.Equal("api", api.Service);
        Assert.Equal(["dev", "prod"], api.Environments.Select(e => e.Environment).OrderBy(e => e));
        Assert.Equal(T0.AddHours(4), api.LastDeployedAt);
    }

    [Fact]
    public async Task RetiredEnvironment_LeavesServiceDetail()
    {
        await SeedHistoryAsync();
        await ConfigureAsync(retireStable: true);

        var detail = await Sut().GetServiceDetail("marketplace", "api");

        Assert.NotNull(detail);
        Assert.Equal(["dev", "prod"], detail!.Environments.Select(e => e.Environment).OrderBy(e => e));

        // 0.9.0 only ever reached stable, so it is not a version this service has anywhere now;
        // the others no longer claim to have reached it.
        Assert.Equal(["1.1.0", "1.0.0"], detail.RecentVersions.Select(v => v.Version));
        Assert.Equal(["dev"], detail.RecentVersions[0].Environments.Select(e => e.Environment));
        Assert.Equal(["dev", "prod"], detail.RecentVersions[1].Environments.Select(e => e.Environment).OrderBy(e => e));
        Assert.All(detail.RecentVersions, v => Assert.DoesNotContain(v.Environments, e => e.Environment == "stable"));
    }

    [Fact]
    public async Task ServiceSeenOnlyInARetiredEnvironment_HasNoDetail()
    {
        await SeedHistoryAsync();
        await ConfigureAsync(retireStable: true);

        Assert.Null(await Sut().GetServiceDetail("marketplace", "legacy"));
    }

    // ── History: deliberately untouched ─────────────────────────────────────

    [Fact]
    public async Task RetiredEnvironment_KeepsItsHistory()
    {
        var stableHead = await SeedHistoryAsync();
        await ConfigureAsync(retireStable: true);

        var history = await Sut().GetHistory("marketplace", "api", null);
        Assert.Equal(3, history.Count(h => h.Environment == "stable"));
        Assert.Equal(3, (await Sut().GetHistory("marketplace", "api", "stable")).Count);
        Assert.Single(await Sut().GetHistory("marketplace", "legacy", null));

        var detail = await Sut().GetEventDetail(stableHead.Id);
        Assert.NotNull(detail);
        Assert.Equal("stable", detail!.Event.Environment);

        Assert.Contains(
            await Sut().GetRecentByProduct("marketplace", T0.AddDays(-1)),
            e => e.Environment == "stable");
        Assert.Equal(4, (await Sut().GetRecentByEnvironment("marketplace", "stable", T0.AddDays(-1))).Count);
        Assert.Equal(
            ["1.1.0", "1.0.0", "0.9.0"],
            (await Sut().GetVersions("marketplace", "stable", "api")).Select(v => v.Version));
    }
}
