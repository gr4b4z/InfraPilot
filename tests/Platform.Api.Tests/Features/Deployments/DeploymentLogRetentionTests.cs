using System.Text;
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
/// Log retention purges captured pipeline output for old deploys while keeping the deploy events
/// themselves. Age is the EVENT's DeployedAt — not the log row's own timestamp — so all of one
/// deploy's blocks age together; a block re-posted yesterday for a deploy from March is still March.
/// </summary>
public class DeploymentLogRetentionTests : IDisposable
{
    private readonly PlatformDbContext _db;
    private readonly DeploymentService _sut;

    public DeploymentLogRetentionTests()
    {
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _db = new PlatformDbContext(options);
        _sut = new DeploymentService(
            _db, Substitute.For<IWebhookDispatcher>(), Substitute.For<IPromotionIngestHook>(),
            TestOptions.Normalization(),
            TestUserPreferences.For(_db),
            Substitute.For<ILogger<DeploymentService>>());
    }

    public void Dispose() => _db.Dispose();

    private DeployEvent SeedEventWithLog(string service, DateTimeOffset deployedAt, string content)
    {
        var ev = new DeployEvent
        {
            Id = Guid.NewGuid(),
            Product = "acme",
            Service = service,
            Environment = "production",
            Version = "1.0.0",
            Status = "succeeded",
            Source = "helm-deploy",
            DeployedAt = deployedAt,
            CreatedAt = deployedAt,
            ReferencesJson = "[]",
            ParticipantsJson = "[]",
            MetadataJson = "{}",
        };
        _db.DeployEvents.Add(ev);
        _db.DeployEventLogs.Add(new DeployEventLog
        {
            Id = Guid.NewGuid(),
            DeployEventId = ev.Id,
            Name = "helm upgrade output",
            Content = content,
            ByteCount = Encoding.UTF8.GetByteCount(content),
            OriginalByteCount = Encoding.UTF8.GetByteCount(content),
            LineCount = 1,
            // Deliberately recent, whatever the deploy's age — retention must not key on this.
            CreatedAt = DateTimeOffset.UtcNow,
        });
        return ev;
    }

    [Fact]
    public async Task Retention_RemovesOnlyLogsOfOldDeploys_AndKeepsTheEvents()
    {
        var old = SeedEventWithLog("api", DateTimeOffset.UtcNow.AddDays(-120), "old-log-0123456789");
        var recent = SeedEventWithLog("web", DateTimeOffset.UtcNow.AddDays(-5), "recent");
        await _db.SaveChangesAsync();

        var (previewLogs, previewBytes) = await _sut.CountOldLogs(90);
        Assert.Equal(1, previewLogs);
        Assert.Equal(Encoding.UTF8.GetByteCount("old-log-0123456789"), previewBytes);

        var (logs, bytes) = await _sut.RemoveOldLogs(90);
        Assert.Equal(1, logs);
        Assert.Equal(previewBytes, bytes);

        // The old EVENT survives — only its captured output is gone. The recent log is untouched.
        Assert.NotNull(await _db.DeployEvents.FindAsync(old.Id));
        var remaining = await _db.DeployEventLogs.ToListAsync();
        Assert.Equal(recent.Id, Assert.Single(remaining).DeployEventId);
    }

    [Fact]
    public async Task Retention_NothingOldEnough_IsANoop()
    {
        SeedEventWithLog("api", DateTimeOffset.UtcNow.AddDays(-10), "young");
        await _db.SaveChangesAsync();

        var (logs, bytes) = await _sut.RemoveOldLogs(90);

        Assert.Equal(0, logs);
        Assert.Equal(0, bytes);
        Assert.Equal(1, await _db.DeployEventLogs.CountAsync());
    }
}
