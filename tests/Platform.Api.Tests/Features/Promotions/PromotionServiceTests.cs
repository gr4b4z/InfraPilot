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
            TestOptions.Normalization(),
            TestUserPreferences.For(_db),
            TestProductOverrides.For(_db));
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
    public async Task Create_VersionAlreadyDeployedAndTargetMovedOn_ClosesAsDeployed()
    {
        // The stranded-promotion case, and the reason completion can't only live on the ingest path.
        // v1.0.0 went to prod, prod later moved to v2.0.0, and only then is v1.0.0 promoted. No further
        // deploy of v1.0.0 is ever going to arrive, so nothing would close this promotion — it sat in
        // "approved, awaiting deploy" forever. 53 real promotions were in this state.
        var t0 = DateTimeOffset.UtcNow.AddDays(-10);
        SeedPolicy();
        SeedDeploy(env: "staging", version: "v1.0.0", service: "api", status: "succeeded");
        SeedDeploy(env: "prod", version: "v1.0.0", service: "api", status: "succeeded", deployedAt: t0);
        SeedDeploy(env: "prod", version: "v2.0.0", service: "api", status: "succeeded", deployedAt: t0.AddDays(2));

        var c = await _sut.CreateExternalCandidateAsync(new CreatePromotionDto(
            Product: "acme", Service: "api", SourceEnv: "staging", TargetEnv: "prod",
            Version: "v1.0.0", FromRevision: null, ToRevision: null,
            References: null, Participants: null));

        Assert.NotNull(c);
        var reloaded = await _db.PromotionCandidates.FindAsync(c!.Id);
        Assert.Equal(PromotionStatus.Deployed, reloaded!.Status);
        // The date the version actually went live, not the moment we noticed.
        Assert.Equal(t0, reloaded.DeployedAt);
    }

    [Fact]
    public async Task Create_VersionOnlyFailedInTarget_StaysOpen()
    {
        // A failed attempt did not put the version live. The promotion is right to keep waiting, and
        // this is the guard that keeps the repair pass honest — 2 real promotions look like the
        // stranded ones but only ever had a failed deploy behind them.
        SeedPolicy();
        SeedDeploy(env: "staging", version: "v1.0.0", service: "api", status: "succeeded");
        SeedDeploy(env: "prod", version: "v1.0.0", service: "api", status: "failed");
        SeedDeploy(env: "prod", version: "v2.0.0", service: "api", status: "succeeded");

        var c = await _sut.CreateExternalCandidateAsync(new CreatePromotionDto(
            Product: "acme", Service: "api", SourceEnv: "staging", TargetEnv: "prod",
            Version: "v1.0.0", FromRevision: null, ToRevision: null,
            References: null, Participants: null));

        Assert.NotNull(c);
        Assert.NotEqual(PromotionStatus.Deployed, c!.Status);
    }

    [Fact]
    public async Task Reconcile_ClosesTheDeployedOnesAndLeavesTheRestAlone()
    {
        // The repair pass over promotions stranded before the create-time check existed. Three
        // promotions, one of which shipped — only that one may move.
        var t0 = DateTimeOffset.UtcNow.AddDays(-10);
        SeedPolicy(approverGroup: null); // auto-approve, so candidates land in Approved like the real ones
        SeedDeploy(env: "staging", version: "v1.0.0", service: "shipped", status: "succeeded");
        SeedDeploy(env: "prod", version: "v1.0.0", service: "shipped", status: "succeeded", deployedAt: t0);
        SeedDeploy(env: "prod", version: "v3.0.0", service: "shipped", status: "succeeded", deployedAt: t0.AddDays(1));

        var shipped = SeedStrandedCandidate("shipped", "v1.0.0");
        var neverDeployed = SeedStrandedCandidate("never", "v1.0.0");
        var failedOnly = SeedStrandedCandidate("failed", "v1.0.0");
        SeedDeploy(env: "prod", version: "v1.0.0", service: "failed", status: "failed");
        await _db.SaveChangesAsync();

        var dry = await _sut.ReconcileCompletionsAsync(dryRun: true);
        Assert.Equal(3, dry.Examined);
        Assert.Equal(1, dry.Closed);
        Assert.Equal(0, dry.Superseded); // the other two have no newer version in prod to overtake them
        Assert.Equal(PromotionStatus.Approved, (await _db.PromotionCandidates.FindAsync(shipped.Id))!.Status);

        var run = await _sut.ReconcileCompletionsAsync();
        Assert.Equal(1, run.Closed);
        var settled = Assert.Single(run.Candidates);
        Assert.Equal(shipped.Id, settled.Id);
        Assert.Equal("closed", settled.Action);

        Assert.Equal(PromotionStatus.Deployed, (await _db.PromotionCandidates.FindAsync(shipped.Id))!.Status);
        Assert.Equal(t0, (await _db.PromotionCandidates.FindAsync(shipped.Id))!.DeployedAt);
        Assert.Equal(PromotionStatus.Approved, (await _db.PromotionCandidates.FindAsync(neverDeployed.Id))!.Status);
        Assert.Equal(PromotionStatus.Approved, (await _db.PromotionCandidates.FindAsync(failedOnly.Id))!.Status);
    }

    [Fact]
    public async Task Reconcile_SupersedesTheOnesTheEnvironmentOvertook()
    {
        // The other stranded group: approved, then never deployed because a later version went instead.
        // 58 real promotions. Nothing retires them today, so they sit in "awaiting deploy" indefinitely.
        var t0 = DateTimeOffset.UtcNow.AddDays(-10);
        var overtaken = SeedStrandedCandidate("api", "v1.0.0", createdAt: t0);
        var rolledBackTarget = SeedStrandedCandidate("rolled", "v2.0.0", createdAt: t0);
        await _db.SaveChangesAsync();

        // prod moved past v1.0.0 without ever deploying it.
        SeedDeploy(env: "prod", version: "v2.0.0", service: "api", status: "succeeded", deployedAt: t0.AddDays(1));
        // The rolled-back service is on an OLDER version than its promotion carries — a live intent.
        SeedDeploy(env: "prod", version: "v1.0.0", service: "rolled", status: "succeeded", deployedAt: t0.AddDays(1));
        await _db.SaveChangesAsync();

        var result = await _sut.ReconcileCompletionsAsync();

        Assert.Equal(1, result.Superseded);
        Assert.Equal(0, result.Closed);
        var settled = Assert.Single(result.Candidates);
        Assert.Equal("superseded", settled.Action);
        Assert.Equal("v2.0.0", settled.LandedVersion);
        Assert.Equal(PromotionStatus.Superseded, (await _db.PromotionCandidates.FindAsync(overtaken.Id))!.Status);
        Assert.Equal(PromotionStatus.Approved, (await _db.PromotionCandidates.FindAsync(rolledBackTarget.Id))!.Status);
    }

    [Fact]
    public async Task Reconcile_TargetAlreadyAheadWhenPromotionWasCreated_LeavesItAlone()
    {
        // Promoting an older version into an environment that is already ahead is a deliberate act, not
        // a stranded promotion. Nothing here is ours to retire.
        var t0 = DateTimeOffset.UtcNow.AddDays(-10);
        SeedDeploy(env: "prod", version: "v2.0.0", service: "api", status: "succeeded", deployedAt: t0);
        var deliberate = SeedStrandedCandidate("api", "v1.0.0", createdAt: t0.AddDays(1));
        await _db.SaveChangesAsync();

        var result = await _sut.ReconcileCompletionsAsync();

        Assert.Equal(0, result.Closed);
        Assert.Equal(0, result.Superseded);
        Assert.Equal(PromotionStatus.Approved, (await _db.PromotionCandidates.FindAsync(deliberate.Id))!.Status);
    }

    [Fact]
    public async Task SupersedeOvertaken_ClosesApprovedPromotionsTheEnvironmentPassed()
    {
        // The 58 real promotions that were approved and then never went, because a later version went
        // instead. Nothing used to move them: the existing supersede rule only touches Pending.
        var landedAt = DateTimeOffset.UtcNow;
        var overtaken = SeedStrandedCandidate("api", "v1.0.0", createdAt: landedAt.AddDays(-1));
        var newerIntent = SeedStrandedCandidate("api", "v9.0.0", createdAt: landedAt.AddDays(-1));
        var createdAfter = SeedStrandedCandidate("api", "v1.5.0", createdAt: landedAt.AddHours(1));
        var unorderable = SeedStrandedCandidate("api", "release-candidate", createdAt: landedAt.AddDays(-1));
        await _db.SaveChangesAsync();

        var count = await _sut.SupersedeOvertakenByDeployAsync(
            "acme", "api", "prod", "v2.0.0", landedAt);

        Assert.Equal(1, count);
        Assert.Equal(PromotionStatus.Superseded, (await _db.PromotionCandidates.FindAsync(overtaken.Id))!.Status);
        // Newer than what landed — still a live intention.
        Assert.Equal(PromotionStatus.Approved, (await _db.PromotionCandidates.FindAsync(newerIntent.Id))!.Status);
        // Created after the deploy landed, so this deploy did not overtake it.
        Assert.Equal(PromotionStatus.Approved, (await _db.PromotionCandidates.FindAsync(createdAfter.Id))!.Status);
        // Unorderable version: leave it alone rather than guess.
        Assert.Equal(PromotionStatus.Approved, (await _db.PromotionCandidates.FindAsync(unorderable.Id))!.Status);
    }

    [Fact]
    public async Task SupersedeOvertaken_PromotionThatDidShipIsDeployedNotSuperseded()
    {
        // Being replaced later is the normal end of a promotion's life, not a supersede: if its own
        // version did land, the honest status is Deployed.
        var t0 = DateTimeOffset.UtcNow.AddDays(-2);
        SeedDeploy(env: "prod", version: "v1.0.0", service: "api", status: "succeeded", deployedAt: t0);
        SeedDeploy(env: "prod", version: "v2.0.0", service: "api", status: "succeeded", deployedAt: t0.AddDays(1));
        var shipped = SeedStrandedCandidate("api", "v1.0.0", createdAt: t0.AddHours(-1));
        await _db.SaveChangesAsync();

        var count = await _sut.SupersedeOvertakenByDeployAsync(
            "acme", "api", "prod", "v2.0.0", t0.AddDays(1));

        Assert.Equal(0, count);
        var reloaded = await _db.PromotionCandidates.FindAsync(shipped.Id);
        Assert.Equal(PromotionStatus.Deployed, reloaded!.Status);
    }

    /// <summary>
    /// An Approved candidate written straight to the database — the shape the stranded production rows
    /// are in, without going through the create path that would now close or reject them.
    /// </summary>
    private PromotionCandidate SeedStrandedCandidate(
        string service, string version, DateTimeOffset? createdAt = null)
    {
        var candidate = new PromotionCandidate
        {
            Id = Guid.NewGuid(),
            Product = "acme",
            Service = service,
            SourceEnv = "staging",
            TargetEnv = "prod",
            Version = version,
            Status = PromotionStatus.Approved,
            CreatedAt = createdAt ?? DateTimeOffset.UtcNow.AddDays(-1),
            ApprovedAt = createdAt ?? DateTimeOffset.UtcNow.AddDays(-1),
        };
        _db.PromotionCandidates.Add(candidate);
        return candidate;
    }

    // ---------------------------------------------------------------------
    // Duplicate-candidate maintenance
    // ---------------------------------------------------------------------

    private PromotionCandidate SeedCandidateRow(
        string service, string version, PromotionStatus status,
        DateTimeOffset createdAt, DateTimeOffset? deployedAt = null, Guid? supersededById = null)
    {
        var candidate = new PromotionCandidate
        {
            Id = Guid.NewGuid(),
            Product = "acme",
            Service = service,
            SourceEnv = "staging",
            TargetEnv = "prod",
            Version = version,
            Status = status,
            CreatedAt = createdAt,
            DeployedAt = deployedAt,
            SupersededById = supersededById,
        };
        _db.PromotionCandidates.Add(candidate);
        return candidate;
    }

    [Fact]
    public async Task Dedup_RemovesCopiesKeepsEarliestAndItsChildren()
    {
        // The production residue: several same-key Approved rows minted by the pre-D15 create path.
        var t0 = DateTimeOffset.UtcNow.AddDays(-5);
        var keeper = SeedCandidateRow("api", "v1.0.0", PromotionStatus.Approved, t0);
        var copy1 = SeedCandidateRow("api", "v1.0.0", PromotionStatus.Approved, t0.AddMinutes(7));
        var copy2 = SeedCandidateRow("api", "v1.0.0", PromotionStatus.Approved, t0.AddDays(3));
        _db.PromotionComments.Add(new PromotionComment
        {
            Id = Guid.NewGuid(), CandidateId = keeper.Id,
            AuthorEmail = "system", AuthorName = "System", Body = "kept", CreatedAt = t0,
        });
        _db.PromotionComments.Add(new PromotionComment
        {
            Id = Guid.NewGuid(), CandidateId = copy1.Id,
            AuthorEmail = "system", AuthorName = "System", Body = "removed with its row", CreatedAt = t0,
        });
        await _db.SaveChangesAsync();

        var (previewGroups, previewRows) = await _sut.CountDuplicateCandidatesAsync();
        Assert.Equal(1, previewGroups);
        Assert.Equal(2, previewRows);

        var (groups, rows) = await _sut.RemoveDuplicateCandidatesAsync();
        Assert.Equal(1, groups);
        Assert.Equal(2, rows);

        var remaining = await _db.PromotionCandidates.Select(c => c.Id).ToListAsync();
        Assert.Equal([keeper.Id], remaining);
        Assert.Null(await _db.PromotionCandidates.FindAsync(copy2.Id));
        // The keeper's thread survives; the copies' rows go with them.
        var comments = await _db.PromotionComments.ToListAsync();
        Assert.Equal("kept", Assert.Single(comments).Body);
    }

    [Fact]
    public async Task Dedup_RepointsSupersededByToTheKeeper()
    {
        var t0 = DateTimeOffset.UtcNow.AddDays(-5);
        var keeper = SeedCandidateRow("api", "v2.0.0", PromotionStatus.Pending, t0);
        var copy = SeedCandidateRow("api", "v2.0.0", PromotionStatus.Pending, t0.AddMinutes(1));
        // An older promotion that the COPY superseded — its pointer must not dangle.
        var older = SeedCandidateRow("api", "v1.0.0", PromotionStatus.Superseded, t0.AddDays(-1),
            supersededById: copy.Id);
        await _db.SaveChangesAsync();

        await _sut.RemoveDuplicateCandidatesAsync();

        var reloaded = await _db.PromotionCandidates.FindAsync(older.Id);
        Assert.Equal(keeper.Id, reloaded!.SupersededById);
    }

    [Fact]
    public async Task Dedup_LeavesLegitimateHistoryAlone()
    {
        var t0 = DateTimeOffset.UtcNow.AddDays(-30);

        // Deployed, rolled back, re-promoted, deployed again: same key, two distinct landings.
        SeedCandidateRow("api", "v1.0.0", PromotionStatus.Deployed, t0, deployedAt: t0.AddHours(1));
        SeedCandidateRow("api", "v1.0.0", PromotionStatus.Deployed, t0.AddDays(10), deployedAt: t0.AddDays(10).AddHours(1));

        // Superseded twice, each time by a different newer candidate.
        var winnerA = SeedCandidateRow("web", "v2.0.0", PromotionStatus.Deployed, t0, deployedAt: t0.AddHours(1));
        var winnerB = SeedCandidateRow("web", "v3.0.0", PromotionStatus.Pending, t0.AddDays(10));
        SeedCandidateRow("web", "v1.0.0", PromotionStatus.Superseded, t0, supersededById: winnerA.Id);
        SeedCandidateRow("web", "v1.0.0", PromotionStatus.Superseded, t0.AddDays(9), supersededById: winnerB.Id);

        // Rejected twice — nothing on the row distinguishes the rejections, so Rejected is excluded.
        SeedCandidateRow("job", "v1.0.0", PromotionStatus.Rejected, t0);
        SeedCandidateRow("job", "v1.0.0", PromotionStatus.Rejected, t0.AddDays(5));
        await _db.SaveChangesAsync();

        var before = await _db.PromotionCandidates.CountAsync();
        var (groups, rows) = await _sut.RemoveDuplicateCandidatesAsync();

        Assert.Equal(0, groups);
        Assert.Equal(0, rows);
        Assert.Equal(before, await _db.PromotionCandidates.CountAsync());
    }

    [Fact]
    public async Task Dedup_DeployedCopiesOfTheSameLanding_AreRemoved()
    {
        // The post-reconcile shape of the residue: every copy was closed against the same deploy
        // event, so they share DeployedAt — unlike a genuine re-promote (previous test).
        var t0 = DateTimeOffset.UtcNow.AddDays(-5);
        var landing = t0.AddHours(2);
        var keeper = SeedCandidateRow("api", "v1.0.0", PromotionStatus.Deployed, t0, deployedAt: landing);
        SeedCandidateRow("api", "v1.0.0", PromotionStatus.Deployed, t0.AddMinutes(9), deployedAt: landing);
        await _db.SaveChangesAsync();

        var (groups, rows) = await _sut.RemoveDuplicateCandidatesAsync();

        Assert.Equal(1, groups);
        Assert.Equal(1, rows);
        Assert.Equal([keeper.Id], await _db.PromotionCandidates.Select(c => c.Id).ToListAsync());
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
    public async Task Approve_SameUser_DifferentEmailCasing_IsDeduped()
    {
        // The identity claim can arrive with different casing across tokens (UPN vs Graph mail).
        // Authorization here is group-based (matched by Id via Graph), so casing doesn't affect it —
        // this isolates the dedup: the second attempt must still be rejected as "already decided",
        // and the stored ApproverEmail must be the canonical lower-invariant form.
        SeedPolicy(approverGroup: "ops", minApprovers: 5);
        var c = await CreateAsync();

        _currentUser.Email.Returns("Alice@Example.com");
        await _sut.ApproveAsync(c!.Id, null);

        _currentUser.Email.Returns("alice@example.com");
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _sut.ApproveAsync(c.Id, null));

        var row = Assert.Single(_db.PromotionApprovals);
        Assert.Equal("alice@example.com", row.ApproverEmail);
    }

    // ---------------------------------------------------------------------
    // UpsertReferenceParticipantAsync — the work-items queue "Assign" write path
    // ---------------------------------------------------------------------

    private PromotionCandidate SeedCandidateWithWorkItem(string key = "OBS-1")
    {
        var candidate = new PromotionCandidate
        {
            Id = Guid.NewGuid(),
            Product = "acme",
            Service = "api",
            SourceEnv = "staging",
            TargetEnv = "prod",
            Version = "v1",
            Status = PromotionStatus.Pending,
            ResolvedPolicyJson = "{}",
            References = new List<ReferenceDto> { new("work-item", Key: key, Title: "Some ticket") },
        };
        _db.PromotionCandidates.Add(candidate);
        _db.SaveChanges();
        return candidate;
    }

    [Fact]
    public async Task UpsertReferenceParticipant_AssignsOnTheWorkItemReference()
    {
        _currentUser.IsQA.Returns(true); // work-item assignment is the QA role's jurisdiction
        var c = SeedCandidateWithWorkItem("OBS-1");

        var participants = await _sut.UpsertReferenceParticipantAsync(
            c.Id, "OBS-1", "reviewer", new ParticipantDto("reviewer", "Sylwester Grabowski", "syl@softwareone.com"));

        Assert.Contains(participants, p => p.Role == "reviewer" && p.Email == "syl@softwareone.com");
        // Persisted onto the candidate's work-item reference (what GetWorkItemParticipants reads).
        var stored = _db.PromotionCandidates.Single(x => x.Id == c.Id)
            .References.Single(r => r.Key == "OBS-1").Participants;
        Assert.Contains(stored!, p => p.Role == "reviewer" && p.Email == "syl@softwareone.com");
    }

    [Fact]
    public async Task UpsertReferenceParticipant_NullAssignee_ClearsTheRole()
    {
        _currentUser.IsQA.Returns(true);
        var c = SeedCandidateWithWorkItem("OBS-1");
        await _sut.UpsertReferenceParticipantAsync(
            c.Id, "OBS-1", "reviewer", new ParticipantDto("reviewer", "X", "x@acme.com"));

        var after = await _sut.UpsertReferenceParticipantAsync(c.Id, "OBS-1", "reviewer", assignee: null);

        Assert.DoesNotContain(after, p => p.Role == "reviewer");
    }

    [Fact]
    public async Task UpsertReferenceParticipant_UnknownReference_Throws()
    {
        _currentUser.IsQA.Returns(true);
        var c = SeedCandidateWithWorkItem("OBS-1");
        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => _sut.UpsertReferenceParticipantAsync(
                c.Id, "DOES-NOT-EXIST", "reviewer", new ParticipantDto("reviewer", "X", "x@acme.com")));
    }

    [Fact]
    public async Task UpsertReferenceParticipant_NonQaNonAdmin_Unauthorized()
    {
        // Default alice is neither QA nor Admin — work-item assignment must be refused.
        var c = SeedCandidateWithWorkItem("OBS-1");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => _sut.UpsertReferenceParticipantAsync(
                c.Id, "OBS-1", "reviewer", new ParticipantDto("reviewer", "X", "x@acme.com")));
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

    [Fact]
    public async Task MarkDeployed_FromPending_Works_WithExplanatoryComment()
    {
        // Nobody approved it, but the version landed in prod anyway — the change is live, so the
        // candidate has to say Deployed.
        SeedPolicy(approverGroup: "ops", minApprovers: 1);
        var c = await CreateAsync();

        var updated = await _sut.MarkDeployedAsync(c!.Id, "shipped out-of-band");

        Assert.Equal(PromotionStatus.Deployed, updated.Status);
        Assert.NotNull(updated.DeployedAt);
        Assert.Contains(await _sut.GetCommentsAsync(c.Id), x => x.Body == "shipped out-of-band");
    }

    [Fact]
    public async Task MarkDeployed_FromRejected_Works()
    {
        // A rejection is a decision about whether the version SHOULD ship. Once it has shipped, the
        // candidate records that fact; the rejection stays in the approval trail.
        SeedPolicy(approverGroup: "ops", minApprovers: 1);
        var c = await CreateAsync();
        await _sut.RejectAsync(c!.Id, "not ready");

        var updated = await _sut.MarkDeployedAsync(c.Id, "shipped anyway");

        Assert.Equal(PromotionStatus.Deployed, updated.Status);
        var approvals = await _sut.GetApprovalsAsync(c.Id);
        Assert.Contains(approvals, a => a.Decision == PromotionDecision.Rejected);
    }

    [Fact]
    public async Task MarkDeployed_FromDeployed_Throws()
    {
        SeedPolicy(approverGroup: null);
        var c = await CreateAsync();
        await _sut.MarkDeployedAsync(c!.Id);

        await Assert.ThrowsAsync<InvalidOperationException>(() => _sut.MarkDeployedAsync(c.Id));
    }

    // ---------------------------------------------------------------------
    // Action comment trail
    // ---------------------------------------------------------------------

    [Fact]
    public async Task EveryAction_LeavesASystemComment()
    {
        SeedPolicy(approverGroup: "ops", minApprovers: 1);
        var c = await CreateAsync();

        // Creation alone already writes one.
        var afterCreate = await _sut.GetCommentsAsync(c!.Id);
        Assert.Single(afterCreate);
        Assert.All(afterCreate, x => Assert.Equal(PromotionComment.SystemAuthor, x.AuthorEmail));

        await _sut.UpsertParticipantAsync(c.Id, new PromotionParticipant("qa", "Quinn", "quinn@example.com"));
        await _sut.ApproveAsync(c.Id, "looks good");   // satisfies the gate → also logs the approval
        await _sut.MarkDeployingAsync(c.Id, "https://ci/run/9");
        await _sut.MarkDeployedAsync(c.Id);

        var bodies = (await _sut.GetCommentsAsync(c.Id)).Select(x => x.Body).ToList();
        Assert.Contains(bodies, b => b.Contains("Promotion created"));
        Assert.Contains(bodies, b => b.Contains("Quinn") && b.Contains("qa"));
        Assert.Contains(bodies, b => b.Contains("Alice") && b.Contains("approved") && b.Contains("looks good"));
        Assert.Contains(bodies, b => b.Contains("Approval gate satisfied"));
        Assert.Contains(bodies, b => b.Contains("Dispatched to the executor"));
        Assert.Contains(bodies, b => b.Contains("Deployed to prod"));
    }

    [Fact]
    public async Task Reject_LeavesASystemComment()
    {
        SeedPolicy(approverGroup: "ops", minApprovers: 1);
        var c = await CreateAsync();

        await _sut.RejectAsync(c!.Id, "not ready");

        var bodies = (await _sut.GetCommentsAsync(c.Id)).Select(x => x.Body).ToList();
        Assert.Contains(bodies, b => b.Contains("Alice") && b.Contains("rejected") && b.Contains("not ready"));
    }

    [Fact]
    public async Task SystemComment_CannotBeEditedOrDeleted_EvenByAdmin()
    {
        _currentUser.IsAdmin.Returns(true);
        SeedPolicy(approverGroup: "ops", minApprovers: 1);
        var c = await CreateAsync();
        var comment = Assert.Single(await _sut.GetCommentsAsync(c!.Id));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _sut.UpdateCommentAsync(comment.Id, "rewritten"));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _sut.DeleteCommentAsync(comment.Id));
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
