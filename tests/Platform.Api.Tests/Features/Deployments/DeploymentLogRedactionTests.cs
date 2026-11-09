using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Platform.Api.Features.Deployments;
using Platform.Api.Features.Deployments.Models;
using Platform.Api.Features.Migrations.Models;
using Platform.Api.Features.Promotions;
using Platform.Api.Features.Webhooks;
using Platform.Api.Infrastructure.Persistence;

namespace Platform.Api.Tests.Features.Deployments;

/// <summary>
/// Ingest redacts secrets before storing a log; logs stored before that still hold what the producer
/// sent. The admin scrub re-applies the redactor to every stored deploy and migration log — a preview
/// that writes nothing, then the real pass — and is safe to run again.
/// </summary>
public class DeploymentLogRedactionTests : IDisposable
{
    private readonly PlatformDbContext _db;
    private readonly DeploymentService _sut;

    private const string LeakedDiagnostics =
        "--- Describe pod: export-7d9f ---\n" +
        "    Environment:\n" +
        "      PYRA_ServiceBus__ConnectionString:  Endpoint=sb://fake.servicebus.windows.net/;SharedAccessKeyName=app;SharedAccessKey=FAKEKEY0123=\n";

    public DeploymentLogRedactionTests()
    {
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _db = new PlatformDbContext(options);
        _sut = new DeploymentService(
            _db, Substitute.For<IWebhookDispatcher>(), Substitute.For<IPromotionIngestHook>(),
            TestOptions.Normalization(), TestEnvironmentAliases.For(_db),
            TestUserPreferences.For(_db),
            TestServiceDeletions.For(_db),
            TestProductOverrides.For(_db),
            Substitute.For<ILogger<DeploymentService>>());
    }

    public void Dispose() => _db.Dispose();

    // Stored the way rows from before ingest-time redaction are: content exactly as it was sent.
    private Guid SeedDeployLog(string content)
    {
        var id = Guid.NewGuid();
        _db.DeployEventLogs.Add(new DeployEventLog
        {
            Id = id,
            DeployEventId = Guid.NewGuid(),
            Name = "failure diagnostics",
            Content = content,
            ByteCount = Encoding.UTF8.GetByteCount(content),
            OriginalByteCount = Encoding.UTF8.GetByteCount(content),
            LineCount = 4,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        return id;
    }

    private Guid SeedMigrationLog(string content)
    {
        var id = Guid.NewGuid();
        _db.MigrationRunLogs.Add(new MigrationRunLog
        {
            Id = id,
            MigrationRunId = Guid.NewGuid(),
            Name = "schema migration output",
            Content = content,
            ByteCount = Encoding.UTF8.GetByteCount(content),
            OriginalByteCount = Encoding.UTF8.GetByteCount(content),
            LineCount = 1,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        return id;
    }

    [Fact]
    public async Task Preview_CountsLogsHoldingSecrets_AndWritesNothing()
    {
        SeedDeployLog(LeakedDiagnostics);
        SeedDeployLog("Release \"api\" has been upgraded.\n");
        SeedMigrationLog("Login failed: Server=db;User Id=app;Password=FAKE-PW;");
        await _db.SaveChangesAsync();

        var (deployLogs, migrationLogs) = await _sut.RedactStoredLogs(apply: false);

        Assert.Equal(1, deployLogs);
        Assert.Equal(1, migrationLogs);
        _db.ChangeTracker.Clear();
        Assert.Contains(await _db.DeployEventLogs.ToListAsync(), l => l.Content.Contains("FAKEKEY"));
        Assert.Contains(await _db.MigrationRunLogs.ToListAsync(), l => l.Content.Contains("FAKE-PW"));
    }

    [Fact]
    public async Task Apply_RedactsStoredLogs_UpdatesTheirSizes_AndLeavesCleanOnesAlone()
    {
        var leaked = SeedDeployLog(LeakedDiagnostics);
        const string clean = "Release \"api\" has been upgraded.\n";
        var untouched = SeedDeployLog(clean);
        var migration = SeedMigrationLog("Login failed: Server=db;User Id=app;Password=FAKE-PW;");
        await _db.SaveChangesAsync();

        var (deployLogs, migrationLogs) = await _sut.RedactStoredLogs(apply: true);

        Assert.Equal((1, 1), (deployLogs, migrationLogs));
        _db.ChangeTracker.Clear();
        var deployRow = await _db.DeployEventLogs.SingleAsync(l => l.Id == leaked);
        Assert.DoesNotContain("FAKEKEY", deployRow.Content);
        Assert.Contains("PYRA_ServiceBus__ConnectionString:  [redacted]", deployRow.Content);
        Assert.Equal(Encoding.UTF8.GetByteCount(deployRow.Content), deployRow.ByteCount);
        Assert.Equal(clean, (await _db.DeployEventLogs.SingleAsync(l => l.Id == untouched)).Content);
        var migrationRow = await _db.MigrationRunLogs.SingleAsync(l => l.Id == migration);
        Assert.Equal("Login failed: Server=db;User Id=app;Password=[redacted];", migrationRow.Content);
        Assert.Equal(Encoding.UTF8.GetByteCount(migrationRow.Content), migrationRow.ByteCount);
    }

    [Fact]
    public async Task Apply_IsSafeToRepeat()
    {
        SeedDeployLog(LeakedDiagnostics);
        await _db.SaveChangesAsync();

        await _sut.RedactStoredLogs(apply: true);
        var again = await _sut.RedactStoredLogs(apply: true);

        Assert.Equal((0, 0), again);
    }

    [Fact]
    public async Task Apply_WorksThroughMoreRowsThanOneBatch()
    {
        for (var i = 0; i < 120; i++) SeedDeployLog(LeakedDiagnostics);
        await _db.SaveChangesAsync();

        var (deployLogs, _) = await _sut.RedactStoredLogs(apply: true);

        Assert.Equal(120, deployLogs);
        _db.ChangeTracker.Clear();
        Assert.DoesNotContain(await _db.DeployEventLogs.ToListAsync(), l => l.Content.Contains("FAKEKEY"));
    }
}
