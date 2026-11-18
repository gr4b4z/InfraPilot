using Platform.Api.Features.Integrations.Flux;

namespace Platform.Api.Tests.Features.Integrations.Flux;

public class FluxEventMapperTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 18, 12, 20, 0, TimeSpan.Zero);

    private static FluxEventDto Event(
        string reason = "UpgradeSucceeded",
        string kind = "HelmRelease",
        Dictionary<string, string>? metadata = null,
        string? message = "Helm upgrade succeeded for release checkout/checkout-api.v12 with chart checkout-api@1.3.0") =>
        new(
            new FluxInvolvedObject(kind, "checkout", "checkout-api"),
            "info",
            At,
            message,
            reason,
            "helm-controller",
            metadata ?? new Dictionary<string, string>
            {
                ["revision"] = "1.3.0",
                ["environment"] = "staging",
                ["cluster"] = "staging-weu-1",
                ["product"] = "checkout",
                ["service"] = "checkout-api",
            });

    private static FluxEventMapper.Mapped Mapped(FluxEventDto evt) =>
        Assert.IsType<FluxEventMapper.Mapped>(FluxEventMapper.Map(evt, "staging-weu-1"));

    [Theory]
    [InlineData("InstallSucceeded", "succeeded")]
    [InlineData("UpgradeSucceeded", "succeeded")]
    [InlineData("InstallFailed", "failed")]
    [InlineData("UpgradeFailed", "failed")]
    [InlineData("RollbackFailed", "failed")]
    [InlineData("UninstallFailed", "failed")]
    [InlineData("UninstallSucceeded", "removed")]
    public void InstallOutcome_MapsToStatus(string reason, string status)
    {
        Assert.Equal(status, Mapped(Event(reason)).Event.Status);
    }

    [Theory]
    [InlineData("Progressing")]
    [InlineData("TestSucceeded")]
    [InlineData("ReconciliationSucceeded")]
    [InlineData("DependencyNotReady")]
    public void OtherReasons_AreIgnored(string reason)
    {
        Assert.IsType<FluxEventMapper.Ignored>(FluxEventMapper.Map(Event(reason), "c"));
    }

    [Theory]
    [InlineData("Kustomization")]
    [InlineData("GitRepository")]
    public void OtherKinds_AreIgnored(string kind)
    {
        Assert.IsType<FluxEventMapper.Ignored>(FluxEventMapper.Map(Event(kind: kind), "c"));
    }

    [Fact]
    public void Mapped_CarriesKeyRunAndLog()
    {
        var dto = Mapped(Event()).Event;

        Assert.Equal("checkout", dto.Product);
        Assert.Equal("checkout-api", dto.Service);
        Assert.Equal("staging", dto.Environment);
        Assert.Equal("1.3.0", dto.Version);
        Assert.Equal("helm-deploy", dto.Source);
        Assert.Equal(At, dto.DeployedAt);
        Assert.Null(dto.References);

        Assert.Equal("flux", dto.Run!.Provider);
        Assert.Equal("Flux staging-weu-1", dto.Run.WorkflowName);
        Assert.Equal("HelmRelease/checkout/checkout-api", dto.Run.JobName);
        Assert.Equal(At, dto.Run.CompletedAt);
        Assert.Null(dto.Run.FailureReason);

        var log = Assert.Single(dto.Logs!);
        Assert.Equal("flux event", log.Name);
        Assert.Equal("helm-controller", log.Source);
        Assert.StartsWith("Helm upgrade succeeded", log.Content);
    }

    [Fact]
    public void Failure_SetsFailureReason()
    {
        var dto = Mapped(Event("UpgradeFailed", message: "context deadline exceeded")).Event;
        Assert.Equal("context deadline exceeded", dto.Run!.FailureReason);
    }

    [Fact]
    public void AppVersion_WinsOverChartRevision()
    {
        var dto = Mapped(Event(metadata: new()
        {
            ["revision"] = "1.3.0",
            ["app-version"] = "1.3.0-44.abc1234",
            ["environment"] = "staging",
            ["product"] = "checkout",
        })).Event;

        Assert.Equal("1.3.0-44.abc1234", dto.Version);
    }

    [Fact]
    public void Service_FallsBackToObjectName_AndCluster_ToClient()
    {
        var dto = Mapped(Event(metadata: new()
        {
            ["revision"] = "1.3.0",
            ["environment"] = "staging",
            ["product"] = "checkout",
        })).Event;

        Assert.Equal("checkout-api", dto.Service);
        Assert.Equal("Flux staging-weu-1", dto.Run!.WorkflowName);
    }

    [Fact]
    public void MissingProductAndEnvironment_AreErrors()
    {
        var result = FluxEventMapper.Map(Event(metadata: new() { ["revision"] = "1.3.0" }), "c");

        var invalid = Assert.IsType<FluxEventMapper.Invalid>(result);
        Assert.Contains(invalid.Errors, e => e.Contains("metadata.product"));
        Assert.Contains(invalid.Errors, e => e.Contains("metadata.environment"));
    }
}
