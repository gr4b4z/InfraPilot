using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Platform.Api.Features.Deployments.Models;
using Platform.Api.Features.Promotions;
using Platform.Api.Features.Promotions.Executors;
using Platform.Api.Features.Promotions.Models;
using Platform.Api.Infrastructure.Audit;
using Platform.Api.Infrastructure.Auth;
using Platform.Api.Infrastructure.Identity;
using Platform.Api.Infrastructure.Persistence;

namespace Platform.Api.Tests.Features.Promotions;

/// <summary>
/// Focused tests for how <see cref="PromotionService"/> interacts with
/// <see cref="PromotionExecutorDispatcher"/> when a candidate transitions to Approved — both
/// auto-approve on creation and threshold-met on <c>ApproveAsync</c>.
/// </summary>
public class PromotionServiceDispatchTests : IDisposable
{
    private readonly PlatformDbContext _db;
    private readonly ICurrentUser _currentUser = Substitute.For<ICurrentUser>();
    private readonly IIdentityService _identity = Substitute.For<IIdentityService>();
    private readonly IAuditLogger _audit = Substitute.For<IAuditLogger>();
    private readonly IPromotionExecutor _executor = Substitute.For<IPromotionExecutor>();
    private readonly PromotionService _sut;

    public PromotionServiceDispatchTests()
    {
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _db = new PlatformDbContext(options);

        _currentUser.Id.Returns("alice-id");
        _currentUser.Name.Returns("Alice");
        _currentUser.Email.Returns("alice@example.com");
        _currentUser.IsAdmin.Returns(true); // short-circuit group membership for threshold-met test
        _currentUser.Roles.Returns(new List<string>().AsReadOnly());
        _currentUser.Groups.Returns(new List<string>().AsReadOnly());
        _identity.GetGroupMembers(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new List<UserInfo>());

        _executor.Kind.Returns("webhook");

        // Wire a keyed-DI provider so PromotionExecutorDispatcher can find our stub.
        var services = new ServiceCollection();
        services.AddKeyedSingleton<IPromotionExecutor>("webhook", (_, _) => _executor);
        var sp = services.BuildServiceProvider();
        var dispatcher = new PromotionExecutorDispatcher(
            sp, Substitute.For<ILogger<PromotionExecutorDispatcher>>());

        var resolver = new PromotionPolicyResolver(_db);
        _sut = new PromotionService(
            _db, resolver, _identity, _currentUser, _audit,
            Substitute.For<ILogger<PromotionService>>(),
            dispatcher);
    }

    public void Dispose() => _db.Dispose();

    private DeployEvent SeedDeploy(string version = "v1", string deployerEmail = "bob@example.com")
    {
        var participants = JsonSerializer.Serialize(new[] { new { role = "deployer", email = deployerEmail } });
        var e = new DeployEvent
        {
            Id = Guid.NewGuid(),
            Product = "acme",
            Service = "api",
            Environment = "staging",
            Version = version,
            Status = "succeeded",
            Source = "ci",
            DeployedAt = DateTimeOffset.UtcNow,
            ParticipantsJson = participants,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        _db.DeployEvents.Add(e);
        _db.SaveChanges();
        return e;
    }

    private void SeedPolicy(string? approverGroup, string? executorKind, string? config)
    {
        _db.PromotionPolicies.Add(new PromotionPolicy
        {
            Id = Guid.NewGuid(),
            Product = "acme",
            Service = null,
            TargetEnv = "prod",
            ApproverGroup = approverGroup,
            Strategy = PromotionStrategy.Any,
            MinApprovers = 1,
            ExcludeDeployer = false,
            ExecutorKind = executorKind,
            ExecutorConfigJson = config,
        });
        _db.SaveChanges();
    }

    [Fact]
    public async Task AutoApprove_WithExecutor_DispatchesAndTransitionsToDeploying()
    {
        SeedPolicy(approverGroup: null, executorKind: "webhook", config: """{"url":"https://ci"}""");
        _executor.DispatchAsync(Arg.Any<PromotionCandidate>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(PromotionDispatchResult.Ok("https://ci/runs/42"));

        var e = SeedDeploy();
        var candidate = await _sut.CreateCandidateAsync(e, "prod");

        Assert.NotNull(candidate);
        await _executor.Received(1).DispatchAsync(
            Arg.Is<PromotionCandidate>(c => c.Id == candidate!.Id),
            """{"url":"https://ci"}""",
            Arg.Any<CancellationToken>());

        var reloaded = await _db.PromotionCandidates.FindAsync(candidate!.Id);
        Assert.Equal(PromotionStatus.Deploying, reloaded!.Status);
        Assert.Equal("https://ci/runs/42", reloaded.ExternalRunUrl);
    }

    [Fact]
    public async Task AutoApprove_WithoutExecutor_StaysApproved()
    {
        // No policy → implicit auto-approve with no executor binding.
        var e = SeedDeploy();
        var candidate = await _sut.CreateCandidateAsync(e, "prod");

        Assert.NotNull(candidate);
        Assert.Equal(PromotionStatus.Approved, candidate!.Status);
        await _executor.DidNotReceive().DispatchAsync(
            Arg.Any<PromotionCandidate>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AutoApprove_ExecutorFails_CandidateStaysApproved()
    {
        SeedPolicy(approverGroup: null, executorKind: "webhook", config: """{"url":"https://ci"}""");
        _executor.DispatchAsync(Arg.Any<PromotionCandidate>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(PromotionDispatchResult.Fail("boom"));

        var e = SeedDeploy();
        var candidate = await _sut.CreateCandidateAsync(e, "prod");

        var reloaded = await _db.PromotionCandidates.FindAsync(candidate!.Id);
        Assert.Equal(PromotionStatus.Approved, reloaded!.Status);
        Assert.Null(reloaded.ExternalRunUrl);
    }

    [Fact]
    public async Task ThresholdMet_OnApprove_DispatchesExecutor()
    {
        SeedPolicy(approverGroup: "ops", executorKind: "webhook", config: """{"url":"https://ci"}""");
        _executor.DispatchAsync(Arg.Any<PromotionCandidate>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(PromotionDispatchResult.Ok("https://ci/runs/7"));

        var e = SeedDeploy();
        var candidate = await _sut.CreateCandidateAsync(e, "prod");
        Assert.Equal(PromotionStatus.Pending, candidate!.Status);

        var approved = await _sut.ApproveAsync(candidate.Id, comment: null);

        await _executor.Received(1).DispatchAsync(
            Arg.Is<PromotionCandidate>(c => c.Id == candidate.Id),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());

        var reloaded = await _db.PromotionCandidates.FindAsync(candidate.Id);
        Assert.Equal(PromotionStatus.Deploying, reloaded!.Status);
        Assert.Equal("https://ci/runs/7", reloaded.ExternalRunUrl);
    }
}
