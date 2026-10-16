using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.Api.Features.Deployments.Models;
using Platform.Api.Features.Diagnostics;
using Platform.Api.Infrastructure.Persistence;

namespace Platform.Api.Tests.Features.Diagnostics;

/// <summary>
/// Log signals of the deployment diagnosis, fed with the output reconcile actually captured.
/// </summary>
public class DeploymentDiagnosisTests : IDisposable
{
    private readonly PlatformDbContext _db;
    private readonly DiagnosticsService _sut;

    public DeploymentDiagnosisTests()
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

    private Guid GivenFailedDeployWithHelmOutput(string helmOutput)
    {
        var ev = new DeployEvent
        {
            Id = Guid.NewGuid(),
            Product = "mpt-support-tools",
            Service = "mpt-platform-stats",
            Environment = "dev",
            Version = "0.1.0-8.9630a3a3",
            Status = "failed",
            Source = "helm-deploy",
            DeployedAt = DateTimeOffset.UtcNow.AddHours(-1),
            CreatedAt = DateTimeOffset.UtcNow.AddHours(-1),
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
            Content = helmOutput,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        _db.SaveChanges();
        return ev.Id;
    }

    [Theory]
    [InlineData("Release \"mpt-platform-stats\" does not exist. Installing it now.\nError: execution error at (swo-service/templates/secret.yaml:8:4): secretValues.airtableApiKey is required (declared in .Values.secrets/.Values.secretFiles)")]
    [InlineData("Error: UPGRADE FAILED: execution error at (swo-service/templates/configmap.yaml:1:4): no workload enabled: at least one of api.enabled, web.enabled, worker.enabled must be true, or cronjobs must be non-empty")]
    public async Task A_Chart_Guard_Failure_Is_Explained_As_Rejected_Values(string helmOutput)
    {
        var id = GivenFailedDeployWithHelmOutput(helmOutput);

        var result = await _sut.DiagnoseDeploymentAsync(id);

        Assert.Contains(Observations.LogChartValuesRejected, result!.Observations);
        Assert.Contains(result.ProbableCauses, c => c.Cause.Id == "chart-values-rejected");
    }
}
