using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.Api.Features.Deployments.Models;
using Platform.Api.Features.Diagnostics;
using Platform.Api.Infrastructure.Persistence;

namespace Platform.Api.Tests.Features.Diagnostics;

/// <summary>
/// A deploy that failed in a Helm pre-upgrade hook (the service's migrations Job) used to come back
/// with no probable cause at all: the only flag raised was the generic "UPGRADE FAILED", which no
/// cause keys on. Seen on mpt-extension-producthub dev, 2026-11-04.
/// </summary>
public class DeploymentHookDiagnosisTests : IDisposable
{
    private readonly PlatformDbContext _db;
    private readonly DiagnosticsService _sut;

    public DeploymentHookDiagnosisTests()
    {
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _db = new PlatformDbContext(options);

        var repoRoot = new DirectoryInfo(AppContext.BaseDirectory);
        while (repoRoot is not null && !File.Exists(Path.Combine(repoRoot.FullName, "InfraPilot.slnx")))
            repoRoot = repoRoot.Parent;

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Playbooks:Path"] = Path.Combine(repoRoot!.FullName, "playbooks"),
            })
            .Build();

        _sut = new DiagnosticsService(_db, new PlaybookRegistry(config, NullLogger<PlaybookRegistry>.Instance));
    }

    public void Dispose() => _db.Dispose();

    private Guid GivenFailedDeploy(params (string Name, string Content)[] logs)
    {
        var ev = new DeployEvent
        {
            Id = Guid.NewGuid(),
            Product = "mpt-extensions",
            Service = "mpt-extension-producthub",
            Environment = "dev",
            Version = "6.0.51-g76c3360e",
            PreviousVersion = "6.0.50-g3384906e",
            Status = "failed",
            Source = "helm-deploy",
            DeployedAt = DateTimeOffset.UtcNow.AddHours(-1),
        };
        _db.DeployEvents.Add(ev);
        var sequence = 0;
        foreach (var (name, content) in logs)
        {
            _db.DeployEventLogs.Add(new DeployEventLog
            {
                Id = Guid.NewGuid(),
                DeployEventId = ev.Id,
                Name = name,
                Source = "helm",
                Sequence = sequence++,
                Content = content,
            });
        }
        _db.SaveChanges();
        return ev.Id;
    }

    [Fact]
    public async Task A_Failed_Migrations_Hook_Is_Named_As_The_Cause()
    {
        var id = GivenFailedDeploy(
            ("helm hook logs",
                "[09:59:07 ERR] Schema migrations failed.\nThe following migrations have failed:\n"
                + "- AddErpItemLinkLastErrorIndex: The COMMIT TRANSACTION request has no corresponding BEGIN TRANSACTION.\n"),
            ("helm upgrade output",
                "Error: UPGRADE FAILED: pre-upgrade hooks failed: resource Job/mp-platform/mpt-extension-producthub-migrations "
                + "not ready. status: Failed, message: Job Failed. failed: 1/1\n"));

        var diagnosis = await _sut.DiagnoseDeploymentAsync(id);

        Assert.NotNull(diagnosis);
        Assert.Contains(Observations.LogHelmHookFailed, diagnosis!.Observations);
        Assert.Contains(diagnosis.ProbableCauses, c => c.Cause.Id == "hook-job-failed");
    }

    [Fact]
    public async Task A_Plain_Upgrade_Failure_Does_Not_Blame_A_Hook()
    {
        var id = GivenFailedDeploy(
            ("helm upgrade output", "Error: UPGRADE FAILED: another operation (install/upgrade/rollback) is in progress\n"));

        var diagnosis = await _sut.DiagnoseDeploymentAsync(id);

        Assert.NotNull(diagnosis);
        Assert.DoesNotContain(Observations.LogHelmHookFailed, diagnosis!.Observations);
        Assert.DoesNotContain(diagnosis.ProbableCauses, c => c.Cause.Id == "hook-job-failed");
        Assert.Contains(diagnosis.ProbableCauses, c => c.Cause.Id == "helm-lock");
    }
}
