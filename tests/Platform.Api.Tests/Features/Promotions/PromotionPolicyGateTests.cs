using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Platform.Api.Features.Promotions;
using Platform.Api.Features.Promotions.Models;
using Platform.Api.Features.Webhooks;
using Platform.Api.Infrastructure.Audit;
using Platform.Api.Infrastructure.Auth;
using Platform.Api.Infrastructure.Identity;
using Platform.Api.Infrastructure.Persistence;

namespace Platform.Api.Tests.Features.Promotions;

/// <summary>
/// Tests for the §8 extended approval policy: multi-requirement gate (parallel AND), groups-∪-users
/// authorization, and backward-compatible deserialization of legacy snapshot JSON.
/// </summary>
public class PromotionPolicyGateTests : IDisposable
{
    private readonly PlatformDbContext _db;
    private readonly IIdentityService _identity = Substitute.For<IIdentityService>();
    private readonly ICurrentUser _currentUser = Substitute.For<ICurrentUser>();
    private readonly IAuditLogger _audit = Substitute.For<IAuditLogger>();
    private readonly PromotionService _sut;

    private static readonly JsonSerializerOptions JsonOpts =
        new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };

    public PromotionPolicyGateTests()
    {
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _db = new PlatformDbContext(options);

        _currentUser.Id.Returns("alice-id");
        _currentUser.Name.Returns("Alice");
        _currentUser.Email.Returns("alice@example.com");
        _currentUser.IsAdmin.Returns(false);
        _currentUser.IsQA.Returns(false);
        _currentUser.Roles.Returns(new List<string>().AsReadOnly());
        _currentUser.Groups.Returns(new List<string>().AsReadOnly());
        _currentUser.IsInGroup(Arg.Any<string>()).Returns(false);
        _identity.GetGroupMembers(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new List<UserInfo>());

        var resolver = new PromotionPolicyResolver(_db);
        var auth = new PromotionApprovalAuthorizer(
            _currentUser, _identity,
            Substitute.For<ILogger<PromotionApprovalAuthorizer>>());
        _sut = new PromotionService(
            _db, resolver, auth, _currentUser, _audit,
            Substitute.For<ILogger<PromotionService>>(),
            Substitute.For<IWebhookDispatcher>(),
            TestOptions.Normalization());
    }

    public void Dispose() => _db.Dispose();

    private PromotionCandidate SeedCandidate(List<ApprovalStep> steps)
    {
        var snapshot = new ResolvedPolicySnapshot(PolicyId: Guid.NewGuid(), TimeoutHours: 0, EscalationGroup: null)
        {
            ApprovalSteps = steps,
        };
        return SeedCandidateRaw(JsonSerializer.Serialize(snapshot, JsonOpts));
    }

    private PromotionCandidate SeedCandidateRaw(string resolvedPolicyJson)
    {
        var candidate = new PromotionCandidate
        {
            Id = Guid.NewGuid(),
            Product = "acme",
            Service = "api",
            SourceEnv = "staging",
            TargetEnv = "prod",
            Version = $"v{Guid.NewGuid():N}"[..10],
            Status = PromotionStatus.Pending,
            PolicyId = Guid.NewGuid(),
            ResolvedPolicyJson = resolvedPolicyJson,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        _db.PromotionCandidates.Add(candidate);
        _db.SaveChanges();
        return candidate;
    }

    private void AsUser(string id, string email, string name)
    {
        _currentUser.Id.Returns(id);
        _currentUser.Email.Returns(email);
        _currentUser.Name.Returns(name);
    }

    // ── Multi-requirement gate: parallel AND across requirements ───────────────

    [Fact]
    public async Task TwoRequirements_BothMustBeSatisfied_ByDistinctUsers()
    {
        // Requirement A satisfiable by Alice (user list); requirement B by Bob.
        var steps = new List<ApprovalStep>
        {
            new("Step", new()
            {
                new ApproverRequirement("Eng", new(), new() { "alice@example.com" }, 1),
                new ApproverRequirement("Security", new(), new() { "bob@example.com" }, 1),
            }),
        };
        var candidate = SeedCandidate(steps);

        // Alice approves → only requirement A satisfied → still Pending.
        AsUser("alice-id", "alice@example.com", "Alice");
        var afterAlice = await _sut.ApproveAsync(candidate.Id, null);
        Assert.Equal(PromotionStatus.Pending, afterAlice.Status);

        // Bob approves → both satisfied → Approved.
        AsUser("bob-id", "bob@example.com", "Bob");
        var afterBob = await _sut.ApproveAsync(candidate.Id, null);
        Assert.Equal(PromotionStatus.Approved, afterBob.Status);
    }

    [Fact]
    public async Task RequirementsAcrossMultipleSteps_AreFlattenedAndAnded()
    {
        var steps = new List<ApprovalStep>
        {
            new("Step 1", new() { new ApproverRequirement("A", new(), new() { "alice@example.com" }, 1) }),
            new("Step 2", new() { new ApproverRequirement("B", new(), new() { "bob@example.com" }, 1) }),
        };
        var candidate = SeedCandidate(steps);

        AsUser("alice-id", "alice@example.com", "Alice");
        Assert.Equal(PromotionStatus.Pending, (await _sut.ApproveAsync(candidate.Id, null)).Status);

        AsUser("bob-id", "bob@example.com", "Bob");
        Assert.Equal(PromotionStatus.Approved, (await _sut.ApproveAsync(candidate.Id, null)).Status);
    }

    // ── Authorization: groups ∪ users ─────────────────────────────────────────

    [Fact]
    public async Task User_In_RequirementUserList_IsAuthorized()
    {
        var steps = new List<ApprovalStep>
        {
            new("Step", new() { new ApproverRequirement("R", new() { new GroupRef("some-group", "some-group") }, new() { "alice@example.com" }, 1) }),
        };
        var candidate = SeedCandidate(steps);

        // Alice is not in "some-group" (no role/group/Graph), but is in the user list.
        AsUser("alice-id", "alice@example.com", "Alice");
        Assert.True(await _sut.CanUserApproveAsync(candidate));
        Assert.Equal(PromotionStatus.Approved, (await _sut.ApproveAsync(candidate.Id, null)).Status);
    }

    [Fact]
    public async Task User_In_RequirementGroup_ViaGraph_IsAuthorized()
    {
        _identity.GetGroupMembers("release", Arg.Any<CancellationToken>())
            .Returns(new List<UserInfo> { new("alice-id", "Alice", "alice@example.com") });

        var steps = new List<ApprovalStep>
        {
            new("Step", new() { new ApproverRequirement("R", new() { new GroupRef("release", "release") }, new(), 1) }),
        };
        var candidate = SeedCandidate(steps);

        AsUser("alice-id", "alice@example.com", "Alice");
        Assert.True(await _sut.CanUserApproveAsync(candidate));
    }

    [Fact]
    public async Task User_In_Neither_Group_Nor_UserList_IsUnauthorized()
    {
        var steps = new List<ApprovalStep>
        {
            new("Step", new() { new ApproverRequirement("R", new() { new GroupRef("release", "release") }, new() { "bob@example.com" }, 1) }),
        };
        var candidate = SeedCandidate(steps);

        AsUser("alice-id", "alice@example.com", "Alice");
        Assert.False(await _sut.CanUserApproveAsync(candidate));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _sut.ApproveAsync(candidate.Id, null));
    }

    // ── Backward-compatible deserialization of legacy snapshot JSON ────────────

    [Fact]
    public void LegacySnapshotJson_WithoutApprovalSteps_DeserializesAsAutoApprove()
    {
        // JSON shape written before the §8 refactor (single ApproverGroup/Strategy/MinApprovers,
        // no approvalSteps array) and before the gate enum was dropped (it carried a "gate" field).
        // Both the legacy single-group fields and the removed "gate" field must be ignored as
        // unknown members. Tolerant init defaults must apply.
        const string legacy = """
            {"policyId":"3f1d4b6e-0000-0000-0000-000000000001","approverGroup":"ops",
             "strategy":1,"minApprovers":2,"excludeRole":"triggered-by",
             "timeoutHours":48,"escalationGroup":"leads","gate":0}
            """;

        var snap = JsonSerializer.Deserialize<ResolvedPolicySnapshot>(legacy, JsonOpts);

        Assert.NotNull(snap);
        Assert.Empty(snap!.ApprovalSteps);       // unknown legacy fields ignored, steps default empty
        Assert.True(snap.IsAutoApprove);          // no requirements ⇒ no human gate
        Assert.Equal(48, snap.TimeoutHours);
        Assert.Equal("leads", snap.EscalationGroup);
    }

    [Fact]
    public async Task LegacyAutoApproveCandidate_StillDeserializesAndCannotBeApproved()
    {
        var candidate = SeedCandidateRaw(
            """{"policyId":null,"approverGroup":null,"strategy":"Any","minApprovers":0,"timeoutHours":0}""");

        Assert.False(await _sut.CanUserApproveAsync(candidate));
    }

    [Fact]
    public void NewSnapshotJson_RoundTrips()
    {
        var steps = new List<ApprovalStep>
        {
            new("Release", new() { new ApproverRequirement("R", new() { new GroupRef("ops", "ops") }, new() { "x@y" }, 2) }),
        };
        var snap = new ResolvedPolicySnapshot(Guid.NewGuid(), 24, "leads") { ApprovalSteps = steps };

        var json = JsonSerializer.Serialize(snap, JsonOpts);
        var back = JsonSerializer.Deserialize<ResolvedPolicySnapshot>(json, JsonOpts)!;

        var req = back.AllRequirements.Single();
        Assert.Equal("ops", req.Groups.Single().Id);
        Assert.Equal("x@y", req.Users.Single());
        Assert.Equal(2, req.MinApprovers);
        Assert.False(back.IsAutoApprove);
    }
}
