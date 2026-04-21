using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Platform.Api.Features.Deployments.Models;
using Platform.Api.Features.Promotions;
using Platform.Api.Features.Promotions.Models;
using Platform.Api.Infrastructure.Audit;
using Platform.Api.Infrastructure.Auth;
using Platform.Api.Infrastructure.Identity;
using Platform.Api.Infrastructure.Persistence;
using Platform.Api.Features.Webhooks;

namespace Platform.Api.Tests.Features.Promotions;

public class PromotionServiceTests : IDisposable
{
    private readonly PlatformDbContext _db;
    private readonly IIdentityService _identity = Substitute.For<IIdentityService>();
    private readonly ICurrentUser _currentUser = Substitute.For<ICurrentUser>();
    private readonly IAuditLogger _audit = Substitute.For<IAuditLogger>();
    private readonly PromotionService _sut;

    public PromotionServiceTests()
    {
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _db = new PlatformDbContext(options);

        // Default mock: the current user is an ordinary non-admin approver in group "ops".
        _currentUser.Id.Returns("alice-id");
        _currentUser.Name.Returns("Alice");
        _currentUser.Email.Returns("alice@example.com");
        _currentUser.IsAdmin.Returns(false);
        _currentUser.IsQA.Returns(false);
        _currentUser.Roles.Returns(new List<string>().AsReadOnly());
        _currentUser.Groups.Returns(new List<string>().AsReadOnly());
        _currentUser.IsInGroup(Arg.Any<string>()).Returns(false);
        _identity.GetGroupMembers("ops", Arg.Any<CancellationToken>())
            .Returns(new List<UserInfo> { new("alice-id", "Alice", "alice@example.com") });
        _identity.GetGroupMembers(Arg.Is<string>(g => g != "ops"), Arg.Any<CancellationToken>())
            .Returns(new List<UserInfo>());

        var resolver = new PromotionPolicyResolver(_db);
        _sut = new PromotionService(
            _db, resolver, _identity, _currentUser, _audit,
            Substitute.For<ILogger<PromotionService>>(),
            Substitute.For<IWebhookDispatcher>(),
            TestOptions.Normalization());
    }

    public void Dispose() => _db.Dispose();

    // ---------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------

    private DeployEvent SeedDeploy(
        string env = "staging",
        string version = "v1.2.3",
        bool rollback = false,
        string status = "succeeded",
        string? deployerEmail = "bob@example.com")
    {
        var participants = deployerEmail is null
            ? "[]"
            : JsonSerializer.Serialize(new[] { new { role = "triggered-by", email = deployerEmail } });

        var e = new DeployEvent
        {
            Id = Guid.NewGuid(),
            Product = "acme",
            Service = "api",
            Environment = env,
            Version = version,
            Status = status,
            Source = "ci",
            IsRollback = rollback,
            DeployedAt = DateTimeOffset.UtcNow,
            ParticipantsJson = participants,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        _db.DeployEvents.Add(e);
        _db.SaveChanges();
        return e;
    }

    private PromotionPolicy SeedPolicy(
        string approverGroup = "ops",
        PromotionStrategy strategy = PromotionStrategy.Any,
        int minApprovers = 1,
        string? excludeRole = "triggered-by",
        string? service = null)
    {
        var p = new PromotionPolicy
        {
            Id = Guid.NewGuid(),
            Product = "acme",
            Service = service,
            TargetEnv = "prod",
            ApproverGroup = approverGroup,
            Strategy = strategy,
            MinApprovers = minApprovers,
            ExcludeRole = excludeRole,
        };
        _db.PromotionPolicies.Add(p);
        _db.SaveChanges();
        return p;
    }

    // ---------------------------------------------------------------------
    // CreateCandidateAsync
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Create_RollbackEvent_Skipped()
    {
        var e = SeedDeploy(rollback: true);
        var c = await _sut.CreateCandidateAsync(e, "prod");
        Assert.Null(c);
        Assert.Empty(_db.PromotionCandidates);
    }

    [Fact]
    public async Task Create_FailedEvent_Skipped()
    {
        var e = SeedDeploy(status: "failed");
        var c = await _sut.CreateCandidateAsync(e, "prod");
        Assert.Null(c);
    }

    [Fact]
    public async Task Create_NoPolicy_Skipped()
    {
        var e = SeedDeploy();
        var c = await _sut.CreateCandidateAsync(e, "prod");

        Assert.Null(c);
        Assert.Empty(_db.PromotionCandidates);
    }

    [Fact]
    public async Task Create_AutoApprovePolicy_ApprovedImmediately()
    {
        SeedPolicy(approverGroup: null!, minApprovers: 0, excludeRole: null);
        var e = SeedDeploy();
        var c = await _sut.CreateCandidateAsync(e, "prod");

        Assert.NotNull(c);
        Assert.Equal(PromotionStatus.Approved, c!.Status);
        Assert.NotNull(c.ApprovedAt);
    }

    [Fact]
    public async Task Create_WithPolicy_Pending()
    {
        SeedPolicy();
        var e = SeedDeploy();

        var c = await _sut.CreateCandidateAsync(e, "prod");
        Assert.NotNull(c);
        Assert.Equal(PromotionStatus.Pending, c!.Status);
        Assert.Null(c.ApprovedAt);
    }

    [Fact]
    public async Task Create_SecondCandidateSupersedesFirst()
    {
        SeedPolicy();
        var e1 = SeedDeploy(version: "v1");
        var c1 = await _sut.CreateCandidateAsync(e1, "prod");

        var e2 = SeedDeploy(version: "v2");
        var c2 = await _sut.CreateCandidateAsync(e2, "prod");

        var reloaded1 = await _db.PromotionCandidates.FindAsync(c1!.Id);
        Assert.Equal(PromotionStatus.Superseded, reloaded1!.Status);
        Assert.Equal(c2!.Id, reloaded1.SupersededById);
        Assert.Equal(PromotionStatus.Pending, c2.Status);
    }

    [Fact]
    public async Task Create_CapturesDeployerEmail()
    {
        SeedPolicy();
        var e = SeedDeploy(deployerEmail: "deployer@example.com");
        var c = await _sut.CreateCandidateAsync(e, "prod");
        Assert.Equal("deployer@example.com", c!.SourceDeployerEmail);
    }

    // ---------------------------------------------------------------------
    // ApproveAsync
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Approve_AnyStrategy_OneApprovalFlipsToApproved()
    {
        SeedPolicy(strategy: PromotionStrategy.Any);
        var e = SeedDeploy(deployerEmail: "bob@example.com"); // not Alice
        var c = await _sut.CreateCandidateAsync(e, "prod");

        var updated = await _sut.ApproveAsync(c!.Id, "lgtm");

        Assert.Equal(PromotionStatus.Approved, updated.Status);
        Assert.NotNull(updated.ApprovedAt);
        Assert.Single(_db.PromotionApprovals);
    }

    [Fact]
    public async Task Approve_NOfM_NotEnoughApprovals_StaysPending()
    {
        SeedPolicy(strategy: PromotionStrategy.NOfM, minApprovers: 2);
        var e = SeedDeploy(deployerEmail: "bob@example.com");
        var c = await _sut.CreateCandidateAsync(e, "prod");

        var updated = await _sut.ApproveAsync(c!.Id, null);

        Assert.Equal(PromotionStatus.Pending, updated.Status);
        Assert.Single(_db.PromotionApprovals);
    }

    [Fact]
    public async Task Approve_Deployer_RejectedByExcludeDeployer()
    {
        SeedPolicy(excludeRole: "triggered-by");
        var e = SeedDeploy(deployerEmail: "alice@example.com"); // Alice is the deployer
        var c = await _sut.CreateCandidateAsync(e, "prod");

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => _sut.ApproveAsync(c!.Id, null));
    }

    [Fact]
    public async Task Approve_NotInGroup_Unauthorized()
    {
        SeedPolicy(approverGroup: "other-team");
        var e = SeedDeploy();
        var c = await _sut.CreateCandidateAsync(e, "prod");

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => _sut.ApproveAsync(c!.Id, null));
    }

    [Fact]
    public async Task Approve_SameUserTwice_Throws()
    {
        SeedPolicy(strategy: PromotionStrategy.NOfM, minApprovers: 5);
        var e = SeedDeploy();
        var c = await _sut.CreateCandidateAsync(e, "prod");

        await _sut.ApproveAsync(c!.Id, null);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _sut.ApproveAsync(c.Id, null));
    }

    [Fact]
    public async Task Approve_AdminAlwaysQualifies()
    {
        _currentUser.IsAdmin.Returns(true);
        SeedPolicy(approverGroup: "team-admins-never-heard-of");
        var e = SeedDeploy();
        var c = await _sut.CreateCandidateAsync(e, "prod");

        var updated = await _sut.ApproveAsync(c!.Id, null);
        Assert.Equal(PromotionStatus.Approved, updated.Status);
    }

    // ---------------------------------------------------------------------
    // RejectAsync
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Reject_SingleRejection_TerminatesCandidate()
    {
        SeedPolicy(strategy: PromotionStrategy.NOfM, minApprovers: 5);
        var e = SeedDeploy();
        var c = await _sut.CreateCandidateAsync(e, "prod");

        var updated = await _sut.RejectAsync(c!.Id, "no thanks");
        Assert.Equal(PromotionStatus.Rejected, updated.Status);
    }

    // ---------------------------------------------------------------------
    // State transitions
    // ---------------------------------------------------------------------

    [Fact]
    public async Task MarkDeploying_FromApproved_Works()
    {
        SeedPolicy(approverGroup: null!, minApprovers: 0, excludeRole: null); // auto-approve policy
        var e = SeedDeploy();
        var c = await _sut.CreateCandidateAsync(e, "prod");

        var updated = await _sut.MarkDeployingAsync(c!.Id, "https://ci/run/1");
        Assert.Equal(PromotionStatus.Deploying, updated.Status);
        Assert.Equal("https://ci/run/1", updated.ExternalRunUrl);
    }

    [Fact]
    public async Task MarkDeploying_FromPending_Throws()
    {
        SeedPolicy();
        var e = SeedDeploy();
        var c = await _sut.CreateCandidateAsync(e, "prod");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _sut.MarkDeployingAsync(c!.Id, null));
    }

    [Fact]
    public async Task MarkDeployed_FromDeploying_Works()
    {
        SeedPolicy(approverGroup: null!, minApprovers: 0, excludeRole: null);
        var e = SeedDeploy();
        var c = await _sut.CreateCandidateAsync(e, "prod");
        await _sut.MarkDeployingAsync(c!.Id, null);

        var updated = await _sut.MarkDeployedAsync(c.Id);
        Assert.Equal(PromotionStatus.Deployed, updated.Status);
        Assert.NotNull(updated.DeployedAt);
    }

    // ---------------------------------------------------------------------
    // Capability probes
    // ---------------------------------------------------------------------

    [Fact]
    public async Task CanApprove_Pending_InGroup_True()
    {
        SeedPolicy();
        var e = SeedDeploy(deployerEmail: "bob@example.com");
        var c = await _sut.CreateCandidateAsync(e, "prod");

        Assert.True(await _sut.CanUserApproveAsync(c!));
    }

    [Fact]
    public async Task CanApprove_AutoApprove_False()
    {
        SeedPolicy(approverGroup: null!, minApprovers: 0, excludeRole: null);
        var e = SeedDeploy();
        var c = await _sut.CreateCandidateAsync(e, "prod");
        Assert.False(await _sut.CanUserApproveAsync(c!));
    }

    [Fact]
    public async Task CanApprove_NotPending_False()
    {
        SeedPolicy(approverGroup: null!, minApprovers: 0, excludeRole: null);
        var e = SeedDeploy();
        var c = await _sut.CreateCandidateAsync(e, "prod");
        c!.Status = PromotionStatus.Rejected;
        await _db.SaveChangesAsync();
        Assert.False(await _sut.CanUserApproveAsync(c));
    }

    [Fact]
    public async Task CanApprove_AlreadyDecided_False()
    {
        SeedPolicy(strategy: PromotionStrategy.NOfM, minApprovers: 5);
        var e = SeedDeploy();
        var c = await _sut.CreateCandidateAsync(e, "prod");

        await _sut.ApproveAsync(c!.Id, null);
        var reloaded = await _db.PromotionCandidates.FindAsync(c.Id);
        Assert.False(await _sut.CanUserApproveAsync(reloaded!));
    }

    [Fact]
    public async Task CanApproveMany_BulkProbe_MatchesPerCandidateResult()
    {
        // Seed a product-level policy (Service=null) so it applies to all services.
        SeedPolicy();

        // Two different services so the second candidate doesn't supersede the first.
        var e1 = new DeployEvent
        {
            Id = Guid.NewGuid(), Product = "acme", Service = "api", Environment = "staging",
            Version = "v1", Status = "succeeded", Source = "ci", DeployedAt = DateTimeOffset.UtcNow,
            ParticipantsJson = JsonSerializer.Serialize(new[] { new { role = "triggered-by", email = "bob@example.com" } }),
            CreatedAt = DateTimeOffset.UtcNow,
        };
        var e2 = new DeployEvent
        {
            Id = Guid.NewGuid(), Product = "acme", Service = "web", Environment = "staging",
            Version = "v1", Status = "succeeded", Source = "ci", DeployedAt = DateTimeOffset.UtcNow,
            ParticipantsJson = JsonSerializer.Serialize(new[] { new { role = "triggered-by", email = "alice@example.com" } }),
            CreatedAt = DateTimeOffset.UtcNow,
        };
        _db.DeployEvents.AddRange(e1, e2);
        await _db.SaveChangesAsync();

        var c1 = await _sut.CreateCandidateAsync(e1, "prod");
        var c2 = await _sut.CreateCandidateAsync(e2, "prod"); // Alice is deployer → excluded

        var map = await _sut.CanUserApproveManyAsync(new[] { c1!, c2! });
        Assert.True(map[c1!.Id]);
        Assert.False(map[c2!.Id]);
    }
}
