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

    // ---------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------

    /// <summary>
    /// Seeds a staging deploy event for (acme, service, version). Used to control the source-drift
    /// invariant in <c>EvaluateGateAsync</c>: the source env must run the candidate's version (or
    /// have no history) for a candidate to be promotable. Seed a matching version to clear drift.
    /// </summary>
    private DeployEvent SeedDeploy(
        string env = "staging",
        string version = "v1.2.3",
        string service = "api",
        bool rollback = false,
        string status = "succeeded",
        string? deployerEmail = "bob@example.com",
        DateTimeOffset? deployedAt = null)
    {
        var participants = deployerEmail is null
            ? "[]"
            : JsonSerializer.Serialize(new[] { new { role = "triggered-by", email = deployerEmail } });

        var e = new DeployEvent
        {
            Id = Guid.NewGuid(),
            Product = "acme",
            Service = service,
            Environment = env,
            Version = version,
            Status = status,
            Source = "ci",
            IsRollback = rollback,
            DeployedAt = deployedAt ?? DateTimeOffset.UtcNow,
            ParticipantsJson = participants,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        _db.DeployEvents.Add(e);
        _db.SaveChanges();
        return e;
    }

    /// <summary>
    /// Seeds a promotion policy for (acme, service, prod). When <paramref name="approverGroup"/> is
    /// null the policy has no requirements ⇒ auto-approve. Otherwise it carries one step with one
    /// requirement satisfied by <paramref name="approverGroup"/> needing <paramref name="minApprovers"/>
    /// distinct approvers — the §8 tree equivalent of the legacy ApproverGroup/MinApprovers pair.
    /// </summary>
    private PromotionPolicy SeedPolicy(
        string? approverGroup = "ops",
        int minApprovers = 1,
        string? service = null)
    {
        var steps = approverGroup is null
            ? new List<ApprovalStep>()
            : new List<ApprovalStep>
            {
                new("Approval", new()
                {
                    new ApproverRequirement("Approvers", new() { new GroupRef(approverGroup, approverGroup) }, new(), minApprovers),
                }),
            };

        var p = new PromotionPolicy
        {
            Id = Guid.NewGuid(),
            Product = "acme",
            Service = service,
            SourceEnv = "staging",
            TargetEnv = "prod",
            ApprovalSteps = steps,
        };
        _db.PromotionPolicies.Add(p);
        _db.SaveChanges();
        return p;
    }

    /// <summary>
    /// Seeds a policy with one step ("Signoff") carrying TWO requirements ("ReleaseManager" and "QA"),
    /// both satisfiable by group "ops" (which Alice is in via Graph) and each needing one approver.
    /// Used to exercise the multi-eligible "approve as" choice path.
    /// </summary>
    private PromotionPolicy SeedMultiReqPolicy()
    {
        var steps = new List<ApprovalStep>
        {
            new("Signoff", new()
            {
                new ApproverRequirement("ReleaseManager", new() { new GroupRef("ops", "ops") }, new(), 1),
                new ApproverRequirement("QA", new() { new GroupRef("ops", "ops") }, new(), 1),
            }),
        };
        var p = new PromotionPolicy
        {
            Id = Guid.NewGuid(),
            Product = "acme",
            Service = null,
            SourceEnv = "staging",
            TargetEnv = "prod",
            ApprovalSteps = steps,
        };
        _db.PromotionPolicies.Add(p);
        _db.SaveChanges();
        return p;
    }

    /// <summary>
    /// Builds a <see cref="CreatePromotionDto"/> for the (acme, api, staging→prod) edge and calls the
    /// external create path — the only way candidates are born now (the old DeployEvent-driven
    /// <c>CreateCandidateAsync</c> was removed). Seeds a matching succeeded source deploy first so the
    /// source-validation invariant passes (tests that exercise the missing-source path call the DTO
    /// path directly instead).
    /// </summary>
    private Task<PromotionCandidate?> CreateAsync(
        string version = "v1.2.3", string service = "api")
    {
        SeedDeploy(env: "staging", version: version, service: service, status: "succeeded");
        return _sut.CreateExternalCandidateAsync(new CreatePromotionDto(
            Product: "acme",
            Service: service,
            SourceEnv: "staging",
            TargetEnv: "prod",
            Version: version,
            FromRevision: null,
            ToRevision: null,
            References: null,
            Participants: null));
    }

    // ---------------------------------------------------------------------
    // CreateExternalCandidateAsync
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Create_NoPolicy_Skipped()
    {
        // No policy resolves for the edge → external create returns null (→ 422 at the endpoint).
        var c = await CreateAsync();

        Assert.Null(c);
        Assert.Empty(_db.PromotionCandidates);
    }

    [Fact]
    public async Task Create_AutoApprovePolicy_ApprovedImmediately()
    {
        SeedPolicy(approverGroup: null); // empty ApprovalSteps ⇒ auto-approve
        var c = await CreateAsync();

        Assert.NotNull(c);
        Assert.Equal(PromotionStatus.Approved, c!.Status);
        Assert.NotNull(c.ApprovedAt);
    }

    [Fact]
    public async Task Create_WithPolicy_Pending()
    {
        SeedPolicy();
        var c = await CreateAsync();

        Assert.NotNull(c);
        Assert.Equal(PromotionStatus.Pending, c!.Status);
        Assert.Null(c.ApprovedAt);
    }

    [Fact]
    public async Task Create_NoSucceededSourceDeploy_Throws()
    {
        // Policy exists but no succeeded staging deploy of v1.2.3 → source validation blocks create.
        SeedPolicy();

        await Assert.ThrowsAsync<SourceDeploymentNotFoundException>(
            () => _sut.CreateExternalCandidateAsync(new CreatePromotionDto(
                Product: "acme",
                Service: "api",
                SourceEnv: "staging",
                TargetEnv: "prod",
                Version: "v1.2.3",
                FromRevision: null,
                ToRevision: null,
                References: null,
                Participants: null)));
    }

    [Fact]
    public async Task Create_WithSucceededSourceDeploy_Succeeds()
    {
        SeedPolicy();
        SeedDeploy(env: "staging", version: "v1.2.3", service: "api", status: "succeeded");

        var c = await _sut.CreateExternalCandidateAsync(new CreatePromotionDto(
            Product: "acme",
            Service: "api",
            SourceEnv: "staging",
            TargetEnv: "prod",
            Version: "v1.2.3",
            FromRevision: null,
            ToRevision: null,
            References: null,
            Participants: null));

        Assert.NotNull(c);
        Assert.Equal(PromotionStatus.Pending, c!.Status);
    }

    [Fact]
    public async Task Create_TargetAlreadyAtVersion_Throws()
    {
        // Source has v1.2.3 AND the target env's current version is already v1.2.3 → redundant
        // promotion; reject with the target-already-at-version 422.
        SeedPolicy();
        SeedDeploy(env: "staging", version: "v1.2.3", service: "api", status: "succeeded");
        SeedDeploy(env: "prod", version: "v1.2.3", service: "api", status: "succeeded");

        await Assert.ThrowsAsync<TargetAlreadyAtVersionException>(
            () => _sut.CreateExternalCandidateAsync(new CreatePromotionDto(
                Product: "acme",
                Service: "api",
                SourceEnv: "staging",
                TargetEnv: "prod",
                Version: "v1.2.3",
                FromRevision: null,
                ToRevision: null,
                References: null,
                Participants: null)));

        Assert.Empty(_db.PromotionCandidates);
    }

    [Fact]
    public async Task Create_TargetRolledBackFromVersion_Succeeds()
    {
        // Target ran v1.2.3, then rolled back to v1.0.0 (its CURRENT version). Re-promoting v1.2.3
        // must be allowed — the check compares current target version, not history.
        var t0 = DateTimeOffset.UtcNow;
        SeedPolicy();
        SeedDeploy(env: "staging", version: "v1.2.3", service: "api", status: "succeeded");
        SeedDeploy(env: "prod", version: "v1.2.3", service: "api", status: "succeeded", deployedAt: t0);
        SeedDeploy(env: "prod", version: "v1.0.0", service: "api", status: "succeeded", rollback: true, deployedAt: t0.AddMinutes(5));

        var c = await _sut.CreateExternalCandidateAsync(new CreatePromotionDto(
            Product: "acme",
            Service: "api",
            SourceEnv: "staging",
            TargetEnv: "prod",
            Version: "v1.2.3",
            FromRevision: null,
            ToRevision: null,
            References: null,
            Participants: null));

        Assert.NotNull(c);
        Assert.Equal(PromotionStatus.Pending, c!.Status);
    }

    [Fact]
    public async Task Create_SecondCandidateSupersedesFirst()
    {
        SeedPolicy();
        var c1 = await CreateAsync(version: "v1");
        var c2 = await CreateAsync(version: "v2");

        var reloaded1 = await _db.PromotionCandidates.FindAsync(c1!.Id);
        Assert.Equal(PromotionStatus.Superseded, reloaded1!.Status);
        Assert.Equal(c2!.Id, reloaded1.SupersededById);
        Assert.Equal(PromotionStatus.Pending, c2.Status);
    }

    // ---------------------------------------------------------------------
    // ApproveAsync
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Approve_SingleRequirement_OneApprovalFlipsToApproved()
    {
        // One requirement (group "ops", MinApprovers:1). Alice is in "ops" via Graph; one approval
        // satisfies the gate. No conflicting staging deploy → no source drift to block.
        SeedPolicy(approverGroup: "ops", minApprovers: 1);
        var c = await CreateAsync();

        var updated = await _sut.ApproveAsync(c!.Id, "lgtm");

        Assert.Equal(PromotionStatus.Approved, updated.Status);
        Assert.NotNull(updated.ApprovedAt);
        Assert.Single(_db.PromotionApprovals);
    }

    [Fact]
    public async Task Approve_NotEnoughApprovals_StaysPending()
    {
        // MinApprovers:2 but only one distinct approver → requirement unmet → stays Pending.
        SeedPolicy(approverGroup: "ops", minApprovers: 2);
        var c = await CreateAsync();

        var updated = await _sut.ApproveAsync(c!.Id, null);

        Assert.Equal(PromotionStatus.Pending, updated.Status);
        Assert.Single(_db.PromotionApprovals);
    }

    [Fact]
    public async Task Approve_NotInGroup_Unauthorized()
    {
        // Requirement is satisfiable only by "other-team", which Alice is not in (no Graph members).
        SeedPolicy(approverGroup: "other-team");
        var c = await CreateAsync();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => _sut.ApproveAsync(c!.Id, null));
    }

    [Fact]
    public async Task Approve_SameUserTwice_Throws()
    {
        SeedPolicy(approverGroup: "ops", minApprovers: 5);
        var c = await CreateAsync();

        await _sut.ApproveAsync(c!.Id, null);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _sut.ApproveAsync(c.Id, null));
    }

    [Fact]
    public async Task Approve_AdminAlwaysQualifies()
    {
        // Admin bypasses group checks (IsInApproverGroupAsync honours IsAdmin).
        _currentUser.IsAdmin.Returns(true);
        SeedPolicy(approverGroup: "team-admins-never-heard-of");
        var c = await CreateAsync();

        var updated = await _sut.ApproveAsync(c!.Id, null);
        Assert.Equal(PromotionStatus.Approved, updated.Status);
    }

    [Fact]
    public async Task Approve_SingleEligible_AutoPicksAndRecordsAttribution()
    {
        // One requirement Alice is eligible for → no choice needed; attribution auto-recorded.
        SeedPolicy(approverGroup: "ops", minApprovers: 1);
        var c = await CreateAsync();

        var updated = await _sut.ApproveAsync(c!.Id, "lgtm");

        Assert.Equal(PromotionStatus.Approved, updated.Status);
        var row = _db.PromotionApprovals.Single();
        Assert.Equal("Approval", row.StepName);
        Assert.Equal("Approvers", row.RequirementName);
    }

    [Fact]
    public async Task Approve_MultiEligible_WithoutChoice_Throws_WithOptions()
    {
        // Two requirements, both satisfiable by Alice (group "ops"). With no explicit choice she's
        // eligible for >1 open requirement → service asks the caller to choose.
        SeedMultiReqPolicy();
        var c = await CreateAsync();

        var ex = await Assert.ThrowsAsync<MultipleEligibleRequirementsException>(
            () => _sut.ApproveAsync(c!.Id, null));

        Assert.Equal(2, ex.Options.Count);
        Assert.Empty(_db.PromotionApprovals); // nothing recorded when we bail for a choice
    }

    [Fact]
    public async Task Approve_MultiEligible_WithChoice_RecordsPinnedRequirement()
    {
        SeedMultiReqPolicy();
        var c = await CreateAsync();

        // Pin to the QA requirement explicitly.
        var updated = await _sut.ApproveAsync(c!.Id, "as qa", stepName: "Signoff", requirementName: "QA");

        var row = _db.PromotionApprovals.Single();
        Assert.Equal("Signoff", row.StepName);
        Assert.Equal("QA", row.RequirementName);
        // Two requirements each need 1; Alice covers only one → still Pending.
        Assert.Equal(PromotionStatus.Pending, updated.Status);
    }

    [Fact]
    public async Task Approve_ChoiceNotEligible_Throws()
    {
        SeedMultiReqPolicy();
        var c = await CreateAsync();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => _sut.ApproveAsync(c!.Id, null, stepName: "Signoff", requirementName: "NoSuchReq"));
    }

    // ---------------------------------------------------------------------
    // RejectAsync
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Reject_SingleRejection_TerminatesCandidate()
    {
        SeedPolicy(approverGroup: "ops", minApprovers: 5);
        var c = await CreateAsync();

        var updated = await _sut.RejectAsync(c!.Id, "no thanks");
        Assert.Equal(PromotionStatus.Rejected, updated.Status);
    }

    // ---------------------------------------------------------------------
    // GetApprovalProgressAsync
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Progress_AutoApprove_RequiresApprovalFalse()
    {
        // No requirements ⇒ auto-approve ⇒ panel hidden.
        SeedPolicy(approverGroup: null);
        var c = await CreateAsync();

        var progress = await _sut.GetApprovalProgressAsync(c!);

        Assert.False(progress.RequiresApproval);
        Assert.True(progress.AllSatisfied);
        Assert.Empty(progress.Steps);
        Assert.Equal(0, progress.TotalRequired);
    }

    [Fact]
    public async Task Progress_TwoOfTwo_PartialCountsReflectMatcher()
    {
        // One requirement needing 2 distinct approvers; only Alice has approved → 1 of 2, unsatisfied.
        SeedPolicy(approverGroup: "ops", minApprovers: 2);
        var c = await CreateAsync();
        await _sut.ApproveAsync(c!.Id, null); // Alice approves; stays Pending (needs 2)
        var reloaded = await _db.PromotionCandidates.FindAsync(c.Id);

        var progress = await _sut.GetApprovalProgressAsync(reloaded!);

        Assert.True(progress.RequiresApproval);
        Assert.False(progress.AllSatisfied);
        Assert.Equal(2, progress.TotalRequired);
        Assert.Equal(1, progress.TotalApproved);
        var step = Assert.Single(progress.Steps);
        Assert.False(step.Satisfied);
        var req = Assert.Single(step.Requirements);
        Assert.Equal(2, req.Required);
        Assert.Equal(1, req.Approved);
        Assert.False(req.Satisfied);
    }

    [Fact]
    public async Task Progress_Satisfied_WhenRequirementMet()
    {
        // MinApprovers:1, Alice (in "ops") approves → requirement satisfied, AllSatisfied true.
        SeedPolicy(approverGroup: "ops", minApprovers: 1);
        var c = await CreateAsync();
        await _sut.ApproveAsync(c!.Id, null);
        var reloaded = await _db.PromotionCandidates.FindAsync(c.Id);

        var progress = await _sut.GetApprovalProgressAsync(reloaded!);

        Assert.True(progress.RequiresApproval);
        Assert.True(progress.AllSatisfied);
        Assert.Equal(1, progress.TotalRequired);
        Assert.Equal(1, progress.TotalApproved);
        Assert.True(Assert.Single(progress.Steps).Satisfied);
    }

    // ---------------------------------------------------------------------
    // State transitions
    // ---------------------------------------------------------------------

    [Fact]
    public async Task MarkDeploying_FromApproved_Works()
    {
        SeedPolicy(approverGroup: null); // auto-approve policy
        var c = await CreateAsync();

        var updated = await _sut.MarkDeployingAsync(c!.Id, "https://ci/run/1");
        Assert.Equal(PromotionStatus.Deploying, updated.Status);
        Assert.Equal("https://ci/run/1", updated.ExternalRunUrl);
    }

    [Fact]
    public async Task MarkDeploying_FromPending_Throws()
    {
        SeedPolicy();
        var c = await CreateAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _sut.MarkDeployingAsync(c!.Id, null));
    }

    [Fact]
    public async Task MarkDeployed_FromDeploying_Works()
    {
        SeedPolicy(approverGroup: null);
        var c = await CreateAsync();
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
        var c = await CreateAsync();

        Assert.True(await _sut.CanUserApproveAsync(c!));
    }

    [Fact]
    public async Task CanApprove_AutoApprove_False()
    {
        SeedPolicy(approverGroup: null);
        var c = await CreateAsync();
        Assert.False(await _sut.CanUserApproveAsync(c!));
    }

    [Fact]
    public async Task CanApprove_NotPending_False()
    {
        SeedPolicy(approverGroup: null);
        var c = await CreateAsync();
        c!.Status = PromotionStatus.Rejected;
        await _db.SaveChangesAsync();
        Assert.False(await _sut.CanUserApproveAsync(c));
    }

    [Fact]
    public async Task CanApprove_AlreadyDecided_False()
    {
        SeedPolicy(approverGroup: "ops", minApprovers: 5);
        var c = await CreateAsync();

        await _sut.ApproveAsync(c!.Id, null);
        var reloaded = await _db.PromotionCandidates.FindAsync(c.Id);
        Assert.False(await _sut.CanUserApproveAsync(reloaded!));
    }

    [Fact]
    public async Task CanApproveMany_BulkProbe_MatchesPerCandidateResult()
    {
        // Product-level policy (Service=null) applies to all services.
        SeedPolicy();

        // Two different services so the second candidate doesn't supersede the first. The probe is a
        // pure capability check (group membership), so both are approvable by Alice (in "ops").
        var c1 = await CreateAsync(service: "api");
        var c2 = await CreateAsync(service: "web");

        var map = await _sut.CanUserApproveManyAsync(new[] { c1!, c2! });
        Assert.True(map[c1!.Id]);
        Assert.True(map[c2!.Id]);
    }
}
