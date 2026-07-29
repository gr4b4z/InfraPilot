using System.Text;
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
/// Tests for the CI-run and captured-output side of ingestion: the run bundle that explains what
/// created a deployment, the log blocks that explain why it failed, and the detail view that ties a
/// deployment to its promotions and work items.
/// </summary>
public class DeploymentServiceRunAndLogTests : IDisposable
{
    private readonly PlatformDbContext _db;
    private readonly DeploymentService _sut;

    public DeploymentServiceRunAndLogTests()
    {
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _db = new PlatformDbContext(options);
        _sut = new DeploymentService(
            _db, Substitute.For<IWebhookDispatcher>(), Substitute.For<IPromotionIngestHook>(),
            TestOptions.Normalization(), Substitute.For<ILogger<DeploymentService>>());
    }

    public void Dispose() => _db.Dispose();

    private static readonly DateTimeOffset DeployedAt = new(2026, 07, 20, 09, 30, 0, TimeSpan.Zero);

    private static CreateDeployEventDto Dto(
        string version = "1.2.3",
        string? status = null,
        DeployRun? run = null,
        List<CreateDeployLogDto>? logs = null,
        DateTimeOffset? deployedAt = null,
        List<ReferenceDto>? references = null) =>
        new(
            Product: "acme", Service: "api", Environment: "production", Version: version,
            Source: "helm-deploy", DeployedAt: deployedAt ?? DeployedAt,
            References: references, Participants: null, Metadata: null,
            Status: status, IsRollback: false, PreviousVersion: null,
            Run: run, Logs: logs);

    private static DeployRun FailedRun(string reason = "pod api-x2klm keeps crash-looping (restartCount=4)") =>
        new(
            Provider: "github-actions", RunId: "30464088990", RunNumber: "294", Attempt: 1,
            WorkflowName: "Reconcile dev", JobName: "Deploy Helm (api)",
            RunUrl: "https://github.com/softwareone-platform/mpt-release/actions/runs/30464088990",
            JobUrl: "https://github.com/softwareone-platform/mpt-release/actions/runs/30464088990/job/90617803378",
            TriggeredBy: "plebann", StartedAt: DeployedAt.AddMinutes(-6), CompletedAt: DeployedAt,
            FailureReason: reason);

    [Fact]
    public async Task Ingest_PersistsRunAndSurfacesItOnRead()
    {
        var run = FailedRun();

        var ev = await _sut.IngestEvent(Dto(status: "failed", run: run));

        var stored = await _db.DeployEvents.FirstAsync(e => e.Id == ev.Id);
        Assert.NotNull(stored.Run);
        Assert.Equal("github-actions", stored.Run!.Provider);
        Assert.Equal(run.JobUrl, stored.Run.JobUrl);
        Assert.Equal(run.FailureReason, stored.Run.FailureReason);

        var detail = await _sut.GetEventDetail(ev.Id);
        Assert.Equal(run.JobUrl, detail!.Event.Run!.JobUrl);
    }

    [Fact]
    public async Task Ingest_WithoutRun_LeavesItNull()
    {
        var ev = await _sut.IngestEvent(Dto());

        var stored = await _db.DeployEvents.FirstAsync(e => e.Id == ev.Id);
        Assert.Null(stored.RunJson);
        Assert.Null(stored.Run);
    }

    [Fact]
    public async Task Ingest_StoresLogBlocks_WithSizesMaterialised()
    {
        const string helm = "Release \"api\" has been upgraded.\nSTATUS: deployed\n";

        var ev = await _sut.IngestEvent(Dto(logs: [new("helm upgrade output", "helm", helm)]));

        var log = await _db.DeployEventLogs.SingleAsync(l => l.DeployEventId == ev.Id);
        Assert.Equal("helm upgrade output", log.Name);
        Assert.Equal("helm", log.Source);
        Assert.Equal(helm, log.Content);
        Assert.False(log.Truncated);
        Assert.Equal(Encoding.UTF8.GetByteCount(helm), log.ByteCount);
        Assert.Equal(3, log.LineCount); // two newline-terminated lines plus the empty tail

        // The summary list must be usable without reading content — that's why it exists.
        var detail = await _sut.GetEventDetail(ev.Id);
        var summary = Assert.Single(detail!.Logs);
        Assert.Equal("helm upgrade output", summary.Name);
        Assert.Equal(Encoding.UTF8.GetByteCount(helm), summary.ByteCount);
    }

    [Fact]
    public async Task Ingest_UnnamedAndDuplicateLogBlocks_AreNormalised()
    {
        var ev = await _sut.IngestEvent(Dto(logs: [
            new("  ", "helm", "dropped: no name to identify it by"),
            new("helm upgrade output", "helm", "first"),
            new("Helm Upgrade Output", "helm", "second"), // same name, different casing
        ]));

        var log = await _db.DeployEventLogs.SingleAsync(l => l.DeployEventId == ev.Id);
        // Last one wins within a single payload: a sender repeating a name meant the later content.
        Assert.Equal("second", log.Content);
    }

    [Fact]
    public async Task Replay_RefreshesRunAndLogs_WithoutCreatingASecondEvent()
    {
        // First attempt: the pipeline had not resolved its job URL and had captured nothing yet.
        var first = await _sut.IngestEvent(Dto(
            status: "failed",
            run: new DeployRun(Provider: "github-actions", RunUrl: "https://example.test/runs/1")));

        var replay = await _sut.IngestEventWithResult(Dto(
            status: "failed",
            run: FailedRun(),
            logs: [new("failure diagnostics", "kubectl", "##[error]Helm deployment failed\n")]));

        Assert.True(replay.Replayed);
        Assert.Equal(first.Id, replay.Event.Id);
        Assert.Equal(1, await _db.DeployEvents.CountAsync());

        var stored = await _db.DeployEvents.FirstAsync(e => e.Id == first.Id);
        Assert.Equal(FailedRun().JobUrl, stored.Run!.JobUrl);
        Assert.Equal(FailedRun().FailureReason, stored.Run.FailureReason);

        var log = await _db.DeployEventLogs.SingleAsync(l => l.DeployEventId == first.Id);
        Assert.Equal("failure diagnostics", log.Name);
    }

    [Fact]
    public async Task Replay_ReplacesABlockOfTheSameName_AndAppendsNewOnes()
    {
        var ev = await _sut.IngestEvent(Dto(logs: [new("helm upgrade output", "helm", "partial")]));

        await _sut.IngestEventWithResult(Dto(logs: [
            new("helm upgrade output", "helm", "complete"),
            new("failure diagnostics", "kubectl", "pods are unhappy"),
        ]));

        var logs = await _db.DeployEventLogs
            .Where(l => l.DeployEventId == ev.Id).OrderBy(l => l.Sequence).ToListAsync();
        Assert.Equal(2, logs.Count);
        Assert.Equal("complete", logs[0].Content);
        // The appended block sorts after the one already stored rather than colliding with it at 0.
        Assert.Equal("failure diagnostics", logs[1].Name);
        Assert.True(logs[1].Sequence > logs[0].Sequence);
    }

    [Fact]
    public async Task LogContent_IsScopedToItsOwnEvent()
    {
        var mine = await _sut.IngestEvent(Dto(logs: [new("helm upgrade output", "helm", "mine")]));
        var other = await _sut.IngestEvent(Dto(
            version: "1.2.4", deployedAt: DeployedAt.AddHours(1),
            logs: [new("helm upgrade output", "helm", "theirs")]));

        var otherLogId = (await _db.DeployEventLogs.FirstAsync(l => l.DeployEventId == other.Id)).Id;

        Assert.Equal("mine", (await _sut.GetLogContent(mine.Id,
            (await _db.DeployEventLogs.FirstAsync(l => l.DeployEventId == mine.Id)).Id))!.Content);
        // Guessing a log id belonging to another deployment must not leak its output.
        Assert.Null(await _sut.GetLogContent(mine.Id, otherLogId));
    }

    [Fact]
    public void CapLogContent_KeepsTheTail_AndSaysSo()
    {
        // A failing deploy prints its diagnostics last, so the end is the part worth keeping.
        var head = new string('a', DeploymentService.LogContentLimitBytes);
        var content = head + "\n##[error]Helm deployment failed for api\n";

        var (capped, truncated, originalBytes) = DeploymentService.CapLogContent(content);

        Assert.True(truncated);
        Assert.Equal(Encoding.UTF8.GetByteCount(content), originalBytes);
        Assert.True(Encoding.UTF8.GetByteCount(capped) <= DeploymentService.LogContentLimitBytes);
        Assert.Contains("##[error]Helm deployment failed for api", capped);
        Assert.StartsWith("[…", capped);
    }

    [Fact]
    public void CapLogContent_LeavesContentUnderTheLimitAlone()
    {
        var (capped, truncated, originalBytes) = DeploymentService.CapLogContent("short\n");

        Assert.Equal("short\n", capped);
        Assert.False(truncated);
        Assert.Equal(6, originalBytes);
    }

    [Fact]
    public async Task Detail_ReportsHistoryForTheSameServiceAndEnvironment()
    {
        var older = await _sut.IngestEvent(Dto(version: "1.2.2", deployedAt: DeployedAt.AddHours(-2)));
        var current = await _sut.IngestEvent(Dto(version: "1.2.3", status: "failed", run: FailedRun()));
        // Same service, different environment — belongs to that environment's story, not this one.
        await _sut.IngestEvent(new CreateDeployEventDto(
            "acme", "api", "staging", "1.2.3", "helm-deploy", DeployedAt,
            null, null, null));

        var detail = await _sut.GetEventDetail(current.Id);

        Assert.Equal([current.Id, older.Id], detail!.History.Select(h => h.Id));
        // The cause travels with the row so a history list can flag what went wrong without a
        // request per entry.
        Assert.Equal(FailedRun().FailureReason, detail.History[0].FailureReason);
        Assert.Null(detail.History[1].FailureReason);
    }

    [Fact]
    public async Task Detail_ClassifiesPromotionsByWhetherThisEnvironmentIsSourceOrTarget()
    {
        var ev = await _sut.IngestEvent(Dto(version: "1.2.3"));

        _db.PromotionCandidates.AddRange(
            // This deployment is what may move forward.
            new PromotionCandidate
            {
                Id = Guid.NewGuid(), Product = "acme", Service = "api",
                SourceEnv = "production", TargetEnv = "dr", Version = "1.2.3",
                Status = PromotionStatus.Pending, CreatedAt = DeployedAt.AddMinutes(1),
            },
            // This deployment is what a promotion delivered.
            new PromotionCandidate
            {
                Id = Guid.NewGuid(), Product = "acme", Service = "api",
                SourceEnv = "staging", TargetEnv = "production", Version = "1.2.3",
                Status = PromotionStatus.Deployed, CreatedAt = DeployedAt.AddMinutes(-30),
            },
            // Different version — not this deployment's business.
            new PromotionCandidate
            {
                Id = Guid.NewGuid(), Product = "acme", Service = "api",
                SourceEnv = "staging", TargetEnv = "production", Version = "9.9.9",
                Status = PromotionStatus.Pending, CreatedAt = DeployedAt,
            });
        await _db.SaveChangesAsync();

        var detail = await _sut.GetEventDetail(ev.Id);

        Assert.Equal(2, detail!.Promotions.Count);
        Assert.Equal("outbound", detail.Promotions.Single(p => p.TargetEnv == "dr").Direction);
        Assert.Equal("inbound", detail.Promotions.Single(p => p.TargetEnv == "production").Direction);
    }

    [Fact]
    public async Task Detail_ListsWorkItemsWithTheEnvironmentsTheyAreGatedFor()
    {
        var ev = await _sut.IngestEvent(Dto(references: [
            new ReferenceDto("work-item", "https://jira.test/browse/MPT-1", "jira", "MPT-1", Title: "Fix billing"),
        ]));

        // The projection is written by the promotion ingest hook, which is substituted out here.
        _db.DeployEventWorkItems.Add(new DeployEventWorkItem
        {
            Id = Guid.NewGuid(), DeployEventId = ev.Id, WorkItemKey = "MPT-1", Product = "acme",
            Provider = "jira", Url = "https://jira.test/browse/MPT-1", Title = "Fix billing (enriched)",
        });
        _db.PromotionCandidates.Add(new PromotionCandidate
        {
            Id = Guid.NewGuid(), Product = "acme", Service = "api",
            SourceEnv = "production", TargetEnv = "dr", Version = "1.2.3",
            Status = PromotionStatus.Pending, CreatedAt = DeployedAt,
        });
        await _db.SaveChangesAsync();

        var detail = await _sut.GetEventDetail(ev.Id);

        var wi = Assert.Single(detail!.WorkItems);
        Assert.Equal("MPT-1", wi.Key);
        // Title comes from the projection, so later Jira enrichment shows through.
        Assert.Equal("Fix billing (enriched)", wi.Title);
        Assert.Equal(["dr"], wi.SignOffTargetEnvs);
    }

    [Fact]
    public async Task Detail_ReturnsNullForAnUnknownEvent()
        => Assert.Null(await _sut.GetEventDetail(Guid.NewGuid()));
}
