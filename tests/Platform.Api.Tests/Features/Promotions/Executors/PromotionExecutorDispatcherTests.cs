using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Platform.Api.Features.Promotions.Executors;
using Platform.Api.Features.Promotions.Models;

namespace Platform.Api.Tests.Features.Promotions.Executors;

public class PromotionExecutorDispatcherTests
{
    private static ResolvedPolicySnapshot Snapshot(string? kind, string? config = null) =>
        new(
            PolicyId: Guid.NewGuid(),
            ApproverGroup: "ops",
            Strategy: PromotionStrategy.Any,
            MinApprovers: 1,
            ExcludeDeployer: false,
            TimeoutHours: 0,
            EscalationGroup: null,
            ExecutorKind: kind,
            ExecutorConfigJson: config);

    private static PromotionCandidate Candidate() => new()
    {
        Id = Guid.NewGuid(),
        Product = "acme",
        Service = "api",
        SourceEnv = "staging",
        TargetEnv = "prod",
        Version = "v1",
    };

    private static PromotionExecutorDispatcher Dispatcher(params (string Key, IPromotionExecutor Exec)[] executors)
    {
        var services = new ServiceCollection();
        foreach (var (k, e) in executors)
            services.AddKeyedSingleton(k, e);
        var sp = services.BuildServiceProvider();
        return new PromotionExecutorDispatcher(sp, Substitute.For<ILogger<PromotionExecutorDispatcher>>());
    }

    [Fact]
    public async Task NoExecutor_ReturnsNull()
    {
        var sut = Dispatcher();
        var result = await sut.DispatchAsync(Candidate(), Snapshot(kind: null), CancellationToken.None);
        Assert.Null(result);
    }

    [Fact]
    public async Task UnknownKind_ReturnsFailResult()
    {
        var sut = Dispatcher();
        var result = await sut.DispatchAsync(Candidate(), Snapshot("does-not-exist"), CancellationToken.None);
        Assert.NotNull(result);
        Assert.False(result!.Success);
        Assert.Contains("No promotion executor registered", result.Error);
    }

    [Fact]
    public async Task KnownKind_DelegatesToExecutor()
    {
        var stub = Substitute.For<IPromotionExecutor>();
        stub.DispatchAsync(Arg.Any<PromotionCandidate>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(PromotionDispatchResult.Ok("https://ci/1"));

        var sut = Dispatcher(("webhook", stub));
        var result = await sut.DispatchAsync(Candidate(), Snapshot("webhook", "{}"), CancellationToken.None);

        Assert.NotNull(result);
        Assert.True(result!.Success);
        Assert.Equal("https://ci/1", result.ExternalRunUrl);
    }

    [Fact]
    public async Task ExecutorThrows_ReturnsFailResultInsteadOfBubbling()
    {
        var stub = Substitute.For<IPromotionExecutor>();
        stub.DispatchAsync(Arg.Any<PromotionCandidate>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns<PromotionDispatchResult>(_ => throw new InvalidOperationException("boom"));

        var sut = Dispatcher(("webhook", stub));
        var result = await sut.DispatchAsync(Candidate(), Snapshot("webhook"), CancellationToken.None);

        Assert.NotNull(result);
        Assert.False(result!.Success);
        Assert.Contains("boom", result.Error);
    }
}
