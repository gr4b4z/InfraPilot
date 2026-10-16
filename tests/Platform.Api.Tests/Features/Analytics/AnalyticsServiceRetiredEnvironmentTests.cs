using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Platform.Api.Features.Analytics;
using Platform.Api.Features.Deployments.Models;
using Platform.Api.Features.Promotions.Models;
using Platform.Api.Features.Settings;
using Platform.Api.Features.Settings.Models;
using Platform.Api.Infrastructure.Auth;
using Platform.Api.Infrastructure.Persistence;

namespace Platform.Api.Tests.Features.Analytics;

/// <summary>
/// Analytics' per-environment outputs leave a retired environment out — the frequency series by
/// environment, lead time per environment, and the work-item matrix's columns and cells — while the
/// per-service cadence keeps counting what genuinely happened in the window.
///
/// <para>Fixture: two tickets on <c>marketplace/api</c>. MPT-1 reached dev, stable and prod; MPT-2
/// only ever reached stable, plus an open promotion into stable.</para>
/// </summary>
public class AnalyticsServiceRetiredEnvironmentTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 09, 01, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset From = T0.AddDays(-1);
    private static readonly DateTimeOffset To = T0.AddDays(2);

    private readonly PlatformDbContext _db;
    private readonly AppSettingsService _settings;

    public AnalyticsServiceRetiredEnvironmentTests()
    {
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _db = new PlatformDbContext(options);

        var user = Substitute.For<ICurrentUser>();
        user.Email.Returns("admin@example.com");
        user.Name.Returns("Admin");
        _settings = new AppSettingsService(_db, user);
    }

    public void Dispose() => _db.Dispose();

    // Fresh per call, like a fresh request: the service memoises settings for its lifetime.
    private AnalyticsService Sut() => new(_db, _settings);

    private Task ConfigureAsync(bool retireStable)
        => _settings.SaveSettings(new AppSettingsDto(
            [
                new EnvironmentConfigDto("dev", "Development"),
                new EnvironmentConfigDto("stable", "Staging Candidate", Retired: retireStable),
                new EnvironmentConfigDto("prod", "Production", IsProduction: true),
            ],
            [], []));

    private void Deploy(string environment, string version, int hoursAfterT0, params string[] workItems)
    {
        var ev = new DeployEvent
        {
            Id = Guid.NewGuid(),
            Product = "marketplace",
            Service = "api",
            Environment = environment,
            Version = version,
            Status = "succeeded",
            Source = "github-actions",
            DeployedAt = T0.AddHours(hoursAfterT0),
        };
        _db.DeployEvents.Add(ev);
        foreach (var key in workItems)
        {
            _db.DeployEventWorkItems.Add(new DeployEventWorkItem
            {
                Id = Guid.NewGuid(),
                DeployEventId = ev.Id,
                WorkItemKey = key,
                Product = "marketplace",
                Title = key,
                CommittedAt = T0.AddHours(-2),
            });
        }
    }

    private async Task SeedAsync()
    {
        Deploy("dev", "1.0.0", 1, "MPT-1");
        Deploy("stable", "1.0.0", 2, "MPT-1");
        Deploy("prod", "1.0.0", 3, "MPT-1");
        Deploy("stable", "1.1.0", 4, "MPT-2");

        var candidate = new PromotionCandidate
        {
            Id = Guid.NewGuid(),
            Product = "marketplace",
            Service = "api",
            SourceEnv = "dev",
            TargetEnv = "stable",
            Version = "1.2.0",
            Status = PromotionStatus.Pending,
            CreatedAt = T0.AddHours(5),
        };
        _db.PromotionCandidates.Add(candidate);
        _db.PromotionWorkItems.Add(new PromotionWorkItem
        {
            Id = Guid.NewGuid(),
            CandidateId = candidate.Id,
            WorkItemKey = "MPT-3",
            Product = "marketplace",
            Service = "api",
            TargetEnv = "stable",
            CreatedAt = T0.AddHours(5),
        });
        await _db.SaveChangesAsync();
    }

    private Task<Platform.Api.Features.Analytics.Models.FrequencyResponseDto> FrequencyAsync(string groupBy)
        => Sut().GetDeploymentFrequency(
            "marketplace", null, null, From, To, "day", groupBy, TimeZoneInfo.Utc,
            includeRollbacks: false, includeRedeploys: false, summaryOnly: true, default);

    // ── Nothing retired: unchanged ──────────────────────────────────────────

    [Fact]
    public async Task NothingRetired_EveryPerEnvironmentOutputStillHasIt()
    {
        await SeedAsync();
        await ConfigureAsync(retireStable: false);

        var byEnv = await FrequencyAsync("environment");
        Assert.Equal(["dev", "prod", "stable"], byEnv.Series.Select(s => s.Key.Environment).OrderBy(e => e));

        var matrix = await Sut().GetWorkItemMatrix("marketplace", null, null, From, To, 100, 0, default);
        Assert.Equal(["dev", "stable", "prod"], matrix.Environments);
        Assert.Equal(["MPT-1", "MPT-2", "MPT-3"], matrix.Items.Select(i => i.Key).OrderBy(k => k));

        var lead = await Sut().GetLeadTime("marketplace", null, null, From, To, "week", TimeZoneInfo.Utc, default);
        Assert.Equal(["dev", "stable", "prod"], lead.ByEnvironment.Select(e => e.Environment));
    }

    // ── Retired: filtered ───────────────────────────────────────────────────

    [Fact]
    public async Task FrequencyByEnvironment_DropsTheRetiredSeries()
    {
        await SeedAsync();
        await ConfigureAsync(retireStable: true);

        var byEnv = await FrequencyAsync("environment");

        Assert.Equal(["dev", "prod"], byEnv.Series.Select(s => s.Key.Environment).OrderBy(e => e));
    }

    [Fact]
    public async Task FrequencyByService_StillCountsWhatHappenedInTheWindow()
    {
        // Not a per-environment output: the service genuinely deployed four times in this window,
        // and retiring an environment later must not rewrite that.
        await SeedAsync();
        await ConfigureAsync(retireStable: true);

        var byService = await FrequencyAsync("service");

        Assert.Equal(4, Assert.Single(byService.Series).Summary.Total);
    }

    [Fact]
    public async Task WorkItemMatrix_DropsTheRetiredColumnAndItsCells()
    {
        await SeedAsync();
        await ConfigureAsync(retireStable: true);

        var matrix = await Sut().GetWorkItemMatrix("marketplace", null, null, From, To, 100, 0, default);

        Assert.Equal(["dev", "prod"], matrix.Environments);
        Assert.Equal(["dev", "prod"], matrix.Totals.Keys.OrderBy(k => k));

        // MPT-2's only deploy and MPT-3's only open promotion were both in stable: nothing left to show.
        var item = Assert.Single(matrix.Items);
        Assert.Equal("MPT-1", item.Key);
        Assert.Equal(["dev", "prod"], item.Envs.Keys.OrderBy(k => k));
        Assert.Equal("prod", item.FurthestEnv);
    }

    [Fact]
    public async Task LeadTime_DropsTheRetiredEnvironment()
    {
        await SeedAsync();
        await ConfigureAsync(retireStable: true);

        var lead = await Sut().GetLeadTime("marketplace", null, null, From, To, "week", TimeZoneInfo.Utc, default);

        Assert.Equal(["dev", "prod"], lead.ByEnvironment.Select(e => e.Environment));
        Assert.DoesNotContain(lead.Buckets, b => b.Environment == "stable");
        Assert.DoesNotContain(lead.Slowest, s => s.Environment == "stable");
        // MPT-2 only ever reached stable, so it is no longer a measured item.
        Assert.Equal(1, lead.Coverage.WorkItems);
    }
}
