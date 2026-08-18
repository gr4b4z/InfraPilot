using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Platform.Api.Features.Deployments;
using Platform.Api.Features.Deployments.Models;
using Platform.Api.Features.Promotions;
using Platform.Api.Features.Webhooks;
using Platform.Api.Infrastructure.Persistence;

namespace Platform.Api.Tests.Features.Deployments;

/// <summary>
/// A re-POST of an already-ingested deploy event used to return early without running the promotion
/// hook. The original POST can only close promotions that exist at the time, so a promotion created
/// between the two — or one stranded because the hook failed the first time round — had nothing left to
/// close it. The retry is the second chance, and skipping it threw that away.
/// </summary>
public class DeploymentServiceReplayHookTests : IDisposable
{
    private readonly PlatformDbContext _db;
    private readonly IPromotionIngestHook _hook = Substitute.For<IPromotionIngestHook>();
    private readonly DeploymentService _sut;

    public DeploymentServiceReplayHookTests()
    {
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _db = new PlatformDbContext(options);
        _sut = new DeploymentService(_db, Substitute.For<IWebhookDispatcher>(), _hook,
            TestOptions.Normalization(), TestUserPreferences.For(_db),
            TestServiceDeletions.For(_db),
            TestProductOverrides.For(_db),
            Substitute.For<ILogger<DeploymentService>>());
    }

    public void Dispose() => _db.Dispose();

    private static CreateDeployEventDto Dto() => new(
        Product: "mpt",
        Service: "swo-web-billing",
        Environment: "test",
        Version: "6.0.11-gc80d1593",
        Source: "web-deploy",
        DeployedAt: new DateTimeOffset(2026, 06, 26, 13, 05, 03, TimeSpan.FromHours(2)),
        References: null,
        Participants: null,
        Metadata: null);

    [Fact]
    public async Task Replayed_event_still_runs_the_promotion_hook()
    {
        var first = await _sut.IngestEventWithResult(Dto());
        Assert.False(first.Replayed);

        var second = await _sut.IngestEventWithResult(Dto());
        Assert.True(second.Replayed);
        Assert.Equal(first.Event.Id, second.Event.Id); // no new row

        // Once per POST, both times against the same stored event.
        await _hook.Received(2).OnIngestedAsync(
            Arg.Is<DeployEvent>(e => e.Id == first.Event.Id), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Replay_does_not_duplicate_the_deploy_event()
    {
        await _sut.IngestEventWithResult(Dto());
        await _sut.IngestEventWithResult(Dto());
        await _sut.IngestEventWithResult(Dto());

        Assert.Equal(1, await _db.DeployEvents.CountAsync());
    }
}
