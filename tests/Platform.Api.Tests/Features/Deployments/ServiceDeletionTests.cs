using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Platform.Api.Features.Deployments;
using Platform.Api.Features.Deployments.Models;
using Platform.Api.Features.Promotions;
using Platform.Api.Features.Promotions.Models;
using Platform.Api.Features.Webhooks;
using Platform.Api.Infrastructure.Persistence;

namespace Platform.Api.Tests.Features.Deployments;

/// <summary>
/// Retiring a service: what it takes out of the lists, what it deliberately leaves alone, and the
/// rule that brings a service back on its own.
///
/// <para>These read through <see cref="DeploymentService"/> rather than asserting on the tombstone
/// table, because the table existing is not the feature — the service vanishing from the queries the
/// pages call is.</para>
/// </summary>
public class ServiceDeletionTests : IDisposable
{
    private readonly PlatformDbContext _db;
    private readonly ServiceDeletionService _deletions;
    private readonly DeploymentService _deployments;

    public ServiceDeletionTests()
    {
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _db = new PlatformDbContext(options);

        _deletions = TestServiceDeletions.For(_db);
        _deployments = new DeploymentService(
            _db,
            Substitute.For<IWebhookDispatcher>(),
            Substitute.For<IPromotionIngestHook>(),
            TestOptions.Normalization(), TestEnvironmentAliases.For(_db),
            TestUserPreferences.For(_db),
            _deletions,
            TestProductOverrides.For(_db),
            Substitute.For<ILogger<DeploymentService>>());
    }

    public void Dispose() => _db.Dispose();

    // ── Helpers ─────────────────────────────────────────────────────────────

    private async Task<DeployEvent> SeedDeployAsync(
        string service, string environment = "production", string version = "1.0.0",
        DateTimeOffset? deployedAt = null, string product = "marketplace")
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
            DeployedAt = deployedAt ?? DateTimeOffset.UtcNow,
        };
        _db.DeployEvents.Add(ev);
        await _db.SaveChangesAsync();
        return ev;
    }

    private async Task SeedCandidateAsync(string service, string product = "marketplace")
    {
        _db.PromotionCandidates.Add(new PromotionCandidate
        {
            Id = Guid.NewGuid(),
            Product = product,
            Service = service,
            SourceEnv = "staging",
            TargetEnv = "production",
            Version = "1.0.0",
            Status = PromotionStatus.Pending,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await _db.SaveChangesAsync();
    }

    // ── Hiding ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task RetiredService_LeavesTheStateMatrix_AndTheOthersStay()
    {
        await SeedDeployAsync("legacy-api");
        await SeedDeployAsync("billing-api");

        await _deletions.DeleteAsync("marketplace", "legacy-api", "migrated to billing-api");

        var state = await _deployments.GetState("marketplace", null, null);
        Assert.Equal(["billing-api"], state.Select(s => s.Service));
    }

    /// <summary>
    /// The pair is the identity. Two products can each have an "api", and retiring one must not take
    /// the other with it — the failure a service-name-only filter would produce.
    /// </summary>
    [Fact]
    public async Task RetiringAService_LeavesTheSameNameInAnotherProductAlone()
    {
        await SeedDeployAsync("api", product: "marketplace");
        await SeedDeployAsync("api", product: "storefront");

        await _deletions.DeleteAsync("marketplace", "api", null);

        Assert.Empty(await _deployments.GetState("marketplace", null, null));
        Assert.Single(await _deployments.GetState("storefront", null, null));
    }

    [Fact]
    public async Task RetiredService_LeavesHistoryRecentAndVersionQueries()
    {
        await SeedDeployAsync("legacy-api", deployedAt: DateTimeOffset.UtcNow.AddHours(-1));

        await _deletions.DeleteAsync("marketplace", "legacy-api", null);

        Assert.Empty(await _deployments.GetHistory("marketplace", "legacy-api", null));
        Assert.Empty(await _deployments.GetRecentByProduct("marketplace", DateTimeOffset.UtcNow.AddDays(-1)));
        Assert.Empty(await _deployments.GetRecentByEnvironment(
            "marketplace", "production", DateTimeOffset.UtcNow.AddDays(-1)));
        Assert.Empty(await _deployments.GetVersions("marketplace", "production", "legacy-api"));
    }

    [Fact]
    public async Task RetiredService_StopsCountingTowardsProductSummaries()
    {
        await SeedDeployAsync("legacy-api");
        await SeedDeployAsync("billing-api");

        await _deletions.DeleteAsync("marketplace", "legacy-api", null);

        var summary = Assert.Single(await _deployments.GetProductSummaries());
        Assert.Equal(1, summary.Environments["production"].TotalServices);
    }

    /// <summary>
    /// Nothing is erased — that is the whole difference between this and a delete. The events are
    /// still queryable by id, which is what keeps links from audit entries and old chat messages
    /// working after a tidy-up.
    /// </summary>
    [Fact]
    public async Task RetiringAService_KeepsItsDeploymentsAndTheirDetailPages()
    {
        var ev = await SeedDeployAsync("legacy-api");

        await _deletions.DeleteAsync("marketplace", "legacy-api", null);

        Assert.Equal(1, await _db.DeployEvents.CountAsync());
        Assert.NotNull(await _deployments.GetEventDetail(ev.Id));
    }

    // ── Retire / restore rules ──────────────────────────────────────────────

    [Fact]
    public async Task RetiringAServiceThatNeverDeployed_IsRejected()
    {
        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => _deletions.DeleteAsync("marketplace", "typo-api", null));
    }

    [Fact]
    public async Task RetiringAnAlreadyRetiredService_UpdatesInPlace()
    {
        await SeedDeployAsync("legacy-api");
        var (first, _) = await _deletions.DeleteAsync("marketplace", "legacy-api", "first pass");
        var firstAt = first.DeletedAt;

        var (second, _) = await _deletions.DeleteAsync("marketplace", "legacy-api", "second pass");

        Assert.Equal(1, await _db.DeletedServices.CountAsync());
        Assert.Equal(first.Id, second.Id);
        Assert.Equal("second pass", second.Reason);
        Assert.True(second.DeletedAt >= firstAt);
    }

    [Fact]
    public async Task Retiring_ReportsWhatItHid()
    {
        await SeedDeployAsync("legacy-api", version: "1.0.0");
        await SeedDeployAsync("legacy-api", version: "1.0.1");
        await SeedCandidateAsync("legacy-api");

        var (_, impact) = await _deletions.DeleteAsync("marketplace", "legacy-api", null);

        Assert.Equal(2, impact.Deployments);
        Assert.Equal(1, impact.OpenPromotions);
    }

    [Fact]
    public async Task Restoring_BringsTheServiceBack()
    {
        await SeedDeployAsync("legacy-api");
        await _deletions.DeleteAsync("marketplace", "legacy-api", null);

        Assert.True(await _deletions.RestoreAsync("marketplace", "legacy-api"));
        Assert.Single(await _deployments.GetState("marketplace", null, null));
    }

    [Fact]
    public async Task RestoringAServiceThatWasNotRetired_ReportsNothingToUndo()
    {
        await SeedDeployAsync("legacy-api");
        Assert.False(await _deletions.RestoreAsync("marketplace", "legacy-api"));
    }

    // ── Revival on ingest ───────────────────────────────────────────────────

    [Fact]
    public async Task ANewDeployment_UnRetiresTheService()
    {
        await SeedDeployAsync("legacy-api", deployedAt: DateTimeOffset.UtcNow.AddDays(-1));
        await _deletions.DeleteAsync("marketplace", "legacy-api", null);
        Assert.Empty(await _deployments.GetState("marketplace", null, null));

        await _deployments.IngestEvent(new CreateDeployEventDto(
            Product: "marketplace",
            Service: "legacy-api",
            Environment: "production",
            Version: "2.0.0",
            Source: "github-actions",
            DeployedAt: DateTimeOffset.UtcNow.AddMinutes(1),
            References: null,
            Participants: null,
            Metadata: null));

        Assert.Empty(await _deletions.ListAsync());
        var state = Assert.Single(await _deployments.GetState("marketplace", null, null));
        Assert.Equal("2.0.0", state.Version);
    }

    /// <summary>
    /// Backfilling history is not evidence the service is alive. Only a deploy dated after the
    /// retirement counts, or importing an archive would silently undo every retirement it touched.
    /// </summary>
    [Fact]
    public async Task BackfillingAnOlderDeployment_LeavesTheServiceRetired()
    {
        await SeedDeployAsync("legacy-api");
        await _deletions.DeleteAsync("marketplace", "legacy-api", null);

        await _deployments.IngestEvent(new CreateDeployEventDto(
            Product: "marketplace",
            Service: "legacy-api",
            Environment: "production",
            Version: "0.9.0",
            Source: "backfill",
            DeployedAt: DateTimeOffset.UtcNow.AddYears(-1),
            References: null,
            Participants: null,
            Metadata: null));

        Assert.Single(await _deletions.ListAsync());
        Assert.Empty(await _deployments.GetState("marketplace", null, null));
    }
}
