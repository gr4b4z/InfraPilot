using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Platform.Api.Features.Deployments.Models;
using Platform.Api.Features.Promotions;
using Platform.Api.Features.Promotions.Models;
using Platform.Api.Features.Webhooks;
using Platform.Api.Infrastructure.Audit;
using Platform.Api.Infrastructure.Auth;
using Platform.Api.Infrastructure.Identity;
using Platform.Api.Infrastructure.Persistence;

namespace Platform.Api.Tests.Features.Promotions;

/// <summary>
/// Focused tests for the approver group membership logic in <see cref="PromotionService"/>.
/// Covers every path in <c>IsInApproverGroupAsync</c>: admin bypass, role claim, group claim,
/// Graph membership (email match, ID match, case-insensitive), Graph failure fallback,
/// and multi-user NOfM approval scenarios.
/// </summary>
public class PromotionApproverGroupTests : IDisposable
{
    private readonly PlatformDbContext _db;
    private readonly IIdentityService _identity = Substitute.For<IIdentityService>();
    private readonly ICurrentUser _currentUser = Substitute.For<ICurrentUser>();
    private readonly IAuditLogger _audit = Substitute.For<IAuditLogger>();
    private readonly PromotionService _sut;

    public PromotionApproverGroupTests()
    {
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _db = new PlatformDbContext(options);

        // Defaults: non-admin user with no roles, no groups, no Graph membership.
        _currentUser.Id.Returns("user-id");
        _currentUser.Name.Returns("TestUser");
        _currentUser.Email.Returns("test@example.com");
        _currentUser.IsAdmin.Returns(false);
        _currentUser.IsQA.Returns(false);
        _currentUser.Roles.Returns(new List<string>().AsReadOnly());
        _currentUser.Groups.Returns(new List<string>().AsReadOnly());
        _currentUser.IsInGroup(Arg.Any<string>()).Returns(false);
        _identity.GetGroupMembers(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new List<UserInfo>());

        var resolver = new PromotionPolicyResolver(_db);
        _sut = new PromotionService(
            _db, resolver, _identity, _currentUser, _audit,
            Substitute.For<ILogger<PromotionService>>(),
            Substitute.For<IWebhookDispatcher>());
    }

    public void Dispose() => _db.Dispose();

    // ── Helpers ──────────────────────────────────────────────────────────────

    private PromotionCandidate SeedPendingCandidate(string approverGroup = "release-approvers")
    {
        var policy = new PromotionPolicy
        {
            Id = Guid.NewGuid(),
            Product = "acme",
            Service = null,
            TargetEnv = "prod",
            ApproverGroup = approverGroup,
            Strategy = PromotionStrategy.Any,
            MinApprovers = 1,
            ExcludeDeployer = false,
        };
        _db.PromotionPolicies.Add(policy);

        var snapshot = new ResolvedPolicySnapshot(
            PolicyId: policy.Id,
            ApproverGroup: approverGroup,
            Strategy: PromotionStrategy.Any,
            MinApprovers: 1,
            ExcludeDeployer: false,
            TimeoutHours: 0,
            EscalationGroup: null);

        var candidate = new PromotionCandidate
        {
            Id = Guid.NewGuid(),
            Product = "acme",
            Service = "api",
            SourceEnv = "staging",
            TargetEnv = "prod",
            Version = $"v{Guid.NewGuid():N}"[..10],
            SourceDeployEventId = Guid.NewGuid(),
            Status = PromotionStatus.Pending,
            PolicyId = policy.Id,
            ResolvedPolicyJson = JsonSerializer.Serialize(snapshot,
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }),
            CreatedAt = DateTimeOffset.UtcNow,
        };
        _db.PromotionCandidates.Add(candidate);
        _db.SaveChanges();
        return candidate;
    }

    private PromotionCandidate SeedNOfMCandidate(
        string approverGroup = "release-approvers", int minApprovers = 2)
    {
        var policy = new PromotionPolicy
        {
            Id = Guid.NewGuid(),
            Product = "acme",
            Service = $"svc-{Guid.NewGuid():N}"[..12],
            TargetEnv = "prod",
            ApproverGroup = approverGroup,
            Strategy = PromotionStrategy.NOfM,
            MinApprovers = minApprovers,
            ExcludeDeployer = false,
        };
        _db.PromotionPolicies.Add(policy);

        var snapshot = new ResolvedPolicySnapshot(
            PolicyId: policy.Id,
            ApproverGroup: approverGroup,
            Strategy: PromotionStrategy.NOfM,
            MinApprovers: minApprovers,
            ExcludeDeployer: false,
            TimeoutHours: 0,
            EscalationGroup: null);

        var candidate = new PromotionCandidate
        {
            Id = Guid.NewGuid(),
            Product = "acme",
            Service = policy.Service,
            SourceEnv = "staging",
            TargetEnv = "prod",
            Version = $"v{Guid.NewGuid():N}"[..10],
            SourceDeployEventId = Guid.NewGuid(),
            Status = PromotionStatus.Pending,
            PolicyId = policy.Id,
            ResolvedPolicyJson = JsonSerializer.Serialize(snapshot,
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }),
            CreatedAt = DateTimeOffset.UtcNow,
        };
        _db.PromotionCandidates.Add(candidate);
        _db.SaveChanges();
        return candidate;
    }

    // ── IsInApproverGroupAsync paths ─────────────────────────────────────────

    [Fact]
    public async Task Approve_ViaRoleClaim_Succeeds()
    {
        // User has the approver group as a role claim.
        _currentUser.Roles.Returns(new List<string> { "release-approvers" }.AsReadOnly());

        var candidate = SeedPendingCandidate("release-approvers");
        var result = await _sut.ApproveAsync(candidate.Id, "approved via role");

        Assert.Equal(PromotionStatus.Approved, result.Status);
    }

    [Fact]
    public async Task Approve_ViaRoleClaim_CaseInsensitive()
    {
        // Role claim is uppercase; policy group is lowercase.
        _currentUser.Roles.Returns(new List<string> { "RELEASE-APPROVERS" }.AsReadOnly());

        var candidate = SeedPendingCandidate("release-approvers");
        var result = await _sut.ApproveAsync(candidate.Id, null);

        Assert.Equal(PromotionStatus.Approved, result.Status);
    }

    [Fact]
    public async Task Approve_ViaGroupClaim_Succeeds()
    {
        // User has the approver group in their group claims (e.g. Entra group object ID).
        _currentUser.IsInGroup("release-approvers").Returns(true);

        var candidate = SeedPendingCandidate("release-approvers");
        var result = await _sut.ApproveAsync(candidate.Id, "approved via group claim");

        Assert.Equal(PromotionStatus.Approved, result.Status);
    }

    [Fact]
    public async Task Approve_ViaGraphMembership_EmailMatch()
    {
        // User is found in the group via Graph API email lookup.
        _identity.GetGroupMembers("release-approvers", Arg.Any<CancellationToken>())
            .Returns(new List<UserInfo>
            {
                new("other-id", "Other User", "other@example.com"),
                new("user-id", "TestUser", "test@example.com"),
            });

        var candidate = SeedPendingCandidate("release-approvers");
        var result = await _sut.ApproveAsync(candidate.Id, null);

        Assert.Equal(PromotionStatus.Approved, result.Status);
    }

    [Fact]
    public async Task Approve_ViaGraphMembership_EmailCaseInsensitive()
    {
        // Graph returns uppercase email; current user has lowercase.
        _identity.GetGroupMembers("release-approvers", Arg.Any<CancellationToken>())
            .Returns(new List<UserInfo>
            {
                new("someone", "Someone", "TEST@EXAMPLE.COM"),
            });

        var candidate = SeedPendingCandidate("release-approvers");
        var result = await _sut.ApproveAsync(candidate.Id, null);

        Assert.Equal(PromotionStatus.Approved, result.Status);
    }

    [Fact]
    public async Task Approve_ViaGraphMembership_IdMatch()
    {
        // Graph returns matching user ID but different email.
        _identity.GetGroupMembers("release-approvers", Arg.Any<CancellationToken>())
            .Returns(new List<UserInfo>
            {
                new("user-id", "TestUser", "different-email@example.com"),
            });

        var candidate = SeedPendingCandidate("release-approvers");
        var result = await _sut.ApproveAsync(candidate.Id, null);

        Assert.Equal(PromotionStatus.Approved, result.Status);
    }

    [Fact]
    public async Task Approve_GraphFails_ReturnsFalse_ThrowsUnauthorized()
    {
        // Graph API throws — should catch and deny, not crash.
        _identity.GetGroupMembers("release-approvers", Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("Graph API unavailable"));

        var candidate = SeedPendingCandidate("release-approvers");

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => _sut.ApproveAsync(candidate.Id, null));
    }

    [Fact]
    public async Task Approve_NoMatchInAnyPath_ThrowsUnauthorized()
    {
        // User has no roles, no group claims, not in Graph results.
        var candidate = SeedPendingCandidate("release-approvers");

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => _sut.ApproveAsync(candidate.Id, null));
    }

    [Fact]
    public async Task CanApprove_ViaRoleClaim_ReturnsTrue()
    {
        _currentUser.Roles.Returns(new List<string> { "release-approvers" }.AsReadOnly());

        var candidate = SeedPendingCandidate("release-approvers");
        Assert.True(await _sut.CanUserApproveAsync(candidate));
    }

    [Fact]
    public async Task CanApprove_ViaGroupClaim_ReturnsTrue()
    {
        _currentUser.IsInGroup("release-approvers").Returns(true);

        var candidate = SeedPendingCandidate("release-approvers");
        Assert.True(await _sut.CanUserApproveAsync(candidate));
    }

    [Fact]
    public async Task CanApprove_ViaGraphMembership_ReturnsTrue()
    {
        _identity.GetGroupMembers("release-approvers", Arg.Any<CancellationToken>())
            .Returns(new List<UserInfo> { new("user-id", "TestUser", "test@example.com") });

        var candidate = SeedPendingCandidate("release-approvers");
        Assert.True(await _sut.CanUserApproveAsync(candidate));
    }

    [Fact]
    public async Task CanApprove_GraphFails_ReturnsFalse()
    {
        _identity.GetGroupMembers("release-approvers", Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("Graph API unavailable"));

        var candidate = SeedPendingCandidate("release-approvers");
        Assert.False(await _sut.CanUserApproveAsync(candidate));
    }

    [Fact]
    public async Task CanApprove_NotInAnyPath_ReturnsFalse()
    {
        var candidate = SeedPendingCandidate("release-approvers");
        Assert.False(await _sut.CanUserApproveAsync(candidate));
    }

    // ── Priority: role claim wins before Graph is called ─────────────────────

    [Fact]
    public async Task Approve_RoleClaim_GraphNeverCalled()
    {
        _currentUser.Roles.Returns(new List<string> { "release-approvers" }.AsReadOnly());

        var candidate = SeedPendingCandidate("release-approvers");
        await _sut.ApproveAsync(candidate.Id, null);

        // Graph should never be consulted since the role claim was sufficient.
        await _identity.DidNotReceive()
            .GetGroupMembers(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Approve_GroupClaim_GraphNeverCalled()
    {
        _currentUser.IsInGroup("release-approvers").Returns(true);

        var candidate = SeedPendingCandidate("release-approvers");
        await _sut.ApproveAsync(candidate.Id, null);

        await _identity.DidNotReceive()
            .GetGroupMembers(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // ── NOfM with two different users ────────────────────────────────────────

    [Fact]
    public async Task NOfM_TwoUsers_ViaGraphMembership_MeetsThreshold()
    {
        // Both users are in the group via Graph.
        _identity.GetGroupMembers("release-approvers", Arg.Any<CancellationToken>())
            .Returns(new List<UserInfo>
            {
                new("alice-id", "Alice", "alice@example.com"),
                new("bob-id", "Bob", "bob@example.com"),
            });

        var candidate = SeedNOfMCandidate("release-approvers", minApprovers: 2);

        // First approval: Alice.
        _currentUser.Id.Returns("alice-id");
        _currentUser.Email.Returns("alice@example.com");
        _currentUser.Name.Returns("Alice");

        var afterFirst = await _sut.ApproveAsync(candidate.Id, "first approval");
        Assert.Equal(PromotionStatus.Pending, afterFirst.Status); // 1 of 2

        // Second approval: Bob.
        _currentUser.Id.Returns("bob-id");
        _currentUser.Email.Returns("bob@example.com");
        _currentUser.Name.Returns("Bob");

        var afterSecond = await _sut.ApproveAsync(candidate.Id, "second approval");
        Assert.Equal(PromotionStatus.Approved, afterSecond.Status); // 2 of 2
        Assert.NotNull(afterSecond.ApprovedAt);

        // Verify 2 approval records exist.
        var approvals = await _db.PromotionApprovals
            .Where(a => a.CandidateId == candidate.Id)
            .ToListAsync();
        Assert.Equal(2, approvals.Count);
    }

    [Fact]
    public async Task NOfM_MixedPaths_RoleClaimAndGraph()
    {
        // Alice qualifies via role claim, Bob via Graph.
        _identity.GetGroupMembers("release-approvers", Arg.Any<CancellationToken>())
            .Returns(new List<UserInfo>
            {
                new("bob-id", "Bob", "bob@example.com"),
            });

        var candidate = SeedNOfMCandidate("release-approvers", minApprovers: 2);

        // Alice: has role claim.
        _currentUser.Id.Returns("alice-id");
        _currentUser.Email.Returns("alice@example.com");
        _currentUser.Name.Returns("Alice");
        _currentUser.Roles.Returns(new List<string> { "release-approvers" }.AsReadOnly());

        var afterFirst = await _sut.ApproveAsync(candidate.Id, null);
        Assert.Equal(PromotionStatus.Pending, afterFirst.Status);

        // Bob: qualifies via Graph. Clear Alice's role claim.
        _currentUser.Id.Returns("bob-id");
        _currentUser.Email.Returns("bob@example.com");
        _currentUser.Name.Returns("Bob");
        _currentUser.Roles.Returns(new List<string>().AsReadOnly());

        var afterSecond = await _sut.ApproveAsync(candidate.Id, null);
        Assert.Equal(PromotionStatus.Approved, afterSecond.Status);
    }

    // ── QA role bypass ──────────────────────────────────────────────────────

    [Fact]
    public async Task Approve_ViaQARole_Succeeds()
    {
        // QA role qualifies for any approver group, like admin.
        _currentUser.IsQA.Returns(true);

        var candidate = SeedPendingCandidate("release-approvers");
        var result = await _sut.ApproveAsync(candidate.Id, "qa approved");

        Assert.Equal(PromotionStatus.Approved, result.Status);
    }

    [Fact]
    public async Task Approve_QARole_GraphNeverCalled()
    {
        _currentUser.IsQA.Returns(true);

        var candidate = SeedPendingCandidate("release-approvers");
        await _sut.ApproveAsync(candidate.Id, null);

        await _identity.DidNotReceive()
            .GetGroupMembers(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CanApprove_ViaQARole_ReturnsTrue()
    {
        _currentUser.IsQA.Returns(true);

        var candidate = SeedPendingCandidate("release-approvers");
        Assert.True(await _sut.CanUserApproveAsync(candidate));
    }
}
