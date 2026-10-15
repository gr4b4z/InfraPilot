using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
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
/// Promotion Freeze (GLOSSARY.md, ADR-0001, ADR-0002): a freeze on (product, target env) holds the
/// approval gate — approvals are still recorded, auto-approve waits, lifting re-evaluates the pair,
/// and the Administrator Bypass still works (audited as frozen).
/// </summary>
public class PromotionFreezeTests : IDisposable
{
    private readonly PlatformDbContext _db;
    private readonly ICurrentUser _currentUser = Substitute.For<ICurrentUser>();
    private readonly IIdentityService _identity = Substitute.For<IIdentityService>();
    private readonly IAuditLogger _audit = Substitute.For<IAuditLogger>();
    private readonly IWebhookDispatcher _webhooks = Substitute.For<IWebhookDispatcher>();
    private readonly PromotionService _promotions;
    private readonly PromotionFreezeService _sut;

    public PromotionFreezeTests()
    {
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _db = new PlatformDbContext(options);

        // Admin: short-circuits approver-group membership so a single approval satisfies the gate.
        _currentUser.Id.Returns("admin-id");
        _currentUser.Name.Returns("Admin User");
        _currentUser.Email.Returns("admin@example.com");
        _currentUser.IsAdmin.Returns(true);
        _currentUser.Roles.Returns(new List<string>().AsReadOnly());
        _currentUser.Groups.Returns(new List<string>().AsReadOnly());
        _identity.GetGroupMembers(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new List<UserInfo>());

        var auth = new PromotionApprovalAuthorizer(
            _currentUser, _identity, Substitute.For<ILogger<PromotionApprovalAuthorizer>>());
        _promotions = new PromotionService(
            _db, new PromotionPolicyResolver(_db), auth, _currentUser, _audit,
            Substitute.For<ILogger<PromotionService>>(), _webhooks, TestOptions.Normalization());
        _sut = new PromotionFreezeService(
            _db, _promotions, _currentUser, _audit, Substitute.For<ILogger<PromotionFreezeService>>());
    }

    public void Dispose() => _db.Dispose();

    // ---------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------

    /// <summary>
    /// Product-level policy for (product, staging → prod). <paramref name="approverGroup"/> null ⇒
    /// empty step tree (auto-approve); otherwise one requirement needing one approver of that group.
    /// </summary>
    private void SeedPolicy(
        string product = "acme",
        string? approverGroup = "ops",
        bool autoApproveWhenNoWorkItems = false,
        bool autoApproveOnAllWorkItemsApproved = false)
    {
        var steps = approverGroup is null
            ? new List<ApprovalStep>()
            : new List<ApprovalStep>
            {
                new("Approval", new()
                {
                    new ApproverRequirement("Approvers", new() { new GroupRef(approverGroup, approverGroup) }, new(), 1),
                }),
            };
        _db.PromotionPolicies.Add(new PromotionPolicy
        {
            Id = Guid.NewGuid(),
            Product = product,
            Service = null,
            SourceEnv = "staging",
            TargetEnv = "prod",
            ApprovalSteps = steps,
            AutoApproveWhenNoWorkItems = autoApproveWhenNoWorkItems,
            AutoApproveOnAllWorkItemsApproved = autoApproveOnAllWorkItemsApproved,
        });
        _db.SaveChanges();
    }

    /// <summary>Seeds a succeeded staging deploy (so source validation passes) and creates the candidate.</summary>
    private async Task<PromotionCandidate> CreateAsync(
        string product = "acme", string version = "v1", List<ReferenceDto>? references = null)
    {
        _db.DeployEvents.Add(new DeployEvent
        {
            Id = Guid.NewGuid(),
            Product = product,
            Service = "api",
            Environment = "staging",
            Version = version,
            Status = "succeeded",
            Source = "ci",
            DeployedAt = DateTimeOffset.UtcNow,
            ParticipantsJson = "[]",
            CreatedAt = DateTimeOffset.UtcNow,
        });
        _db.SaveChanges();
        var candidate = await _promotions.CreateExternalCandidateAsync(new CreatePromotionDto(
            product, "api", "staging", "prod", version, null, null, references, null));
        return candidate!;
    }

    private void SeedFreeze(string product = "acme", string targetEnv = "prod", string reason = "Release window")
    {
        _db.PromotionFreezes.Add(new PromotionFreeze
        {
            Id = Guid.NewGuid(),
            Product = product,
            TargetEnv = targetEnv,
            Reason = reason,
            FrozenBy = "Dana Lee",
            FrozenByEmail = "dana@example.com",
            FrozenAt = DateTimeOffset.UtcNow,
        });
        _db.SaveChanges();
    }

    private PromotionStatus StatusOf(Guid id)
        => _db.PromotionCandidates.AsNoTracking().Single(c => c.Id == id).Status;

    private static bool JsonContains(object? o, string fragment) => JsonSerializer.Serialize(o).Contains(fragment);

    // ---------------------------------------------------------------------
    // Gate held while frozen
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Frozen_FullyApprovedCandidate_StaysPending_ApprovalStillRecorded()
    {
        SeedPolicy();
        var candidate = await CreateAsync();
        SeedFreeze();

        var result = await _promotions.ApproveAsync(candidate.Id, comment: "lgtm");

        Assert.Equal(PromotionStatus.Pending, result.Status);
        Assert.Single(_db.PromotionApprovals.Where(a => a.CandidateId == candidate.Id
                                                       && a.Decision == PromotionDecision.Approved));
        await _webhooks.DidNotReceive().DispatchAsync("promotion.approved", Arg.Any<object>(), Arg.Any<WebhookEventFilters>());
    }

    [Fact]
    public async Task Frozen_Gate_ReportsFreezeAsBlockingReason()
    {
        SeedPolicy();
        var candidate = await CreateAsync();
        SeedFreeze(reason: "Black Friday");
        await _promotions.ApproveAsync(candidate.Id, comment: null); // satisfies the only requirement
        _db.ChangeTracker.Clear();
        candidate = _db.PromotionCandidates.AsNoTracking().Single(c => c.Id == candidate.Id);
        var snapshot = JsonSerializer.Deserialize<ResolvedPolicySnapshot>(
            candidate.ResolvedPolicyJson!, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

        var gate = await _promotions.EvaluateGateAsync(candidate, snapshot, CancellationToken.None);

        Assert.False(gate.Satisfied);
        var blocker = Assert.Single(gate.Blockers);
        Assert.Contains("frozen", blocker);
        Assert.Contains("Black Friday", blocker);
    }

    [Fact]
    public async Task Frozen_WorkItemSignoff_IsRecorded_AutoApproveOnAllWorkItemsWaits()
    {
        SeedPolicy(autoApproveOnAllWorkItemsApproved: true);
        var candidate = await CreateAsync(references: new() { new ReferenceDto("work-item", Key: "ACME-1") });
        SeedFreeze();
        var workItems = new WorkItemApprovalService(
            _db, new PromotionApprovalAuthorizer(_currentUser, _identity, Substitute.For<ILogger<PromotionApprovalAuthorizer>>()),
            _currentUser, _audit, _webhooks, _promotions,
            new PromotionAssigneeRoleSettings(_db, Substitute.For<ILogger<PromotionAssigneeRoleSettings>>()),
            Substitute.For<ILogger<WorkItemApprovalService>>());

        await workItems.ApproveAsync("ACME-1", "acme", "prod", comment: null);

        Assert.Single(_db.WorkItemApprovals.Where(a => a.WorkItemKey == "ACME-1"));
        Assert.Equal(PromotionStatus.Pending, StatusOf(candidate.Id));
    }

    [Fact]
    public async Task Frozen_AutoApprovePolicy_CreatesPending_WithoutApprovedWebhook()
    {
        SeedPolicy(approverGroup: null);
        SeedFreeze();

        var candidate = await CreateAsync();

        Assert.Equal(PromotionStatus.Pending, candidate.Status);
        Assert.Null(candidate.ApprovedAt);
        await _webhooks.DidNotReceive().DispatchAsync("promotion.approved", Arg.Any<object>(), Arg.Any<WebhookEventFilters>());
    }

    [Fact]
    public async Task FreezeOnOtherProduct_DoesNotHoldAutoApprove()
    {
        SeedPolicy(approverGroup: null);
        SeedFreeze(product: "other");

        var candidate = await CreateAsync();

        Assert.Equal(PromotionStatus.Approved, candidate.Status);
    }

    [Fact]
    public async Task Frozen_Bypass_Succeeds_AuditAndWebhookCarryFrozenTrue()
    {
        SeedPolicy();
        var candidate = await CreateAsync();
        SeedFreeze();

        var result = await _promotions.BypassAsync(candidate.Id, "INC-1 hotfix");

        Assert.Equal(PromotionStatus.Approved, result.Status);
        await _audit.Received(1).Log(
            "promotions", "promotion.bypassed",
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Guid?>(),
            Arg.Any<object?>(), Arg.Is<object?>(o => JsonContains(o, "\"frozen\":true")), Arg.Any<object?>());
        await _webhooks.Received(1).DispatchAsync(
            "promotion.approved",
            Arg.Is<object>(o => JsonContains(o, "\"frozen\":true")),
            Arg.Any<WebhookEventFilters>());
    }

    // ---------------------------------------------------------------------
    // Freeze / list / lift
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Freeze_WithoutReason_Throws(string reason)
    {
        SeedPolicy();

        await Assert.ThrowsAsync<ArgumentException>(() => _sut.FreezeAsync("acme", "prod", reason));
        Assert.Empty(_db.PromotionFreezes);
    }

    [Fact]
    public async Task Freeze_ReasonLongerThanColumn_Throws()
    {
        SeedPolicy();

        await Assert.ThrowsAsync<ArgumentException>(() => _sut.FreezeAsync("acme", "prod", new string('x', 1001)));
    }

    [Fact]
    public async Task Freeze_AlreadyFrozenPair_Throws()
    {
        SeedPolicy();
        await _sut.FreezeAsync("acme", "prod", "first");

        await Assert.ThrowsAsync<FreezeAlreadyActiveException>(() => _sut.FreezeAsync("acme", "prod", "second"));
    }

    [Fact]
    public async Task Freeze_PairWithoutApprovalPolicy_Throws()
    {
        SeedPolicy(product: "acme");

        await Assert.ThrowsAsync<NoApprovalPolicyForPairException>(() => _sut.FreezeAsync("other", "prod", "why"));
        await Assert.ThrowsAsync<NoApprovalPolicyForPairException>(() => _sut.FreezeAsync("acme", "staging", "why"));
    }

    [Fact]
    public async Task Freeze_RecordsWhoAndWhy_AndWritesAudit()
    {
        SeedPolicy();

        var freeze = await _sut.FreezeAsync(" acme ", " prod ", " Release window ");

        Assert.Equal("acme", freeze.Product);
        Assert.Equal("prod", freeze.TargetEnv);
        Assert.Equal("Release window", freeze.Reason);
        Assert.Equal("Admin User", freeze.FrozenBy);
        var row = Assert.Single(_db.PromotionFreezes);
        Assert.Equal("admin@example.com", row.FrozenByEmail);
        await _audit.Received(1).Log(
            "promotions", "promotion.frozen",
            "admin-id", "Admin User", "user", "PromotionFreeze", row.Id,
            Arg.Any<object?>(), Arg.Is<object?>(o => JsonContains(o, "Release window")), Arg.Any<object?>());
    }

    [Fact]
    public async Task GetActive_ReportsHeldPendingCandidatesPerPair()
    {
        SeedPolicy(product: "acme");
        SeedPolicy(product: "other");
        var held1 = await CreateAsync(product: "acme", version: "v1");
        _db.ChangeTracker.Clear();
        // A different service so the second candidate does not supersede the first.
        _db.PromotionCandidates.Add(new PromotionCandidate
        {
            Id = Guid.NewGuid(), Product = "acme", Service = "web", SourceEnv = "staging", TargetEnv = "prod",
            Version = "v9", Status = PromotionStatus.Pending, ResolvedPolicyJson = held1.ResolvedPolicyJson,
        });
        _db.PromotionCandidates.Add(new PromotionCandidate
        {
            Id = Guid.NewGuid(), Product = "acme", Service = "worker", SourceEnv = "staging", TargetEnv = "prod",
            Version = "v9", Status = PromotionStatus.Approved, ResolvedPolicyJson = held1.ResolvedPolicyJson,
        });
        _db.SaveChanges();
        await CreateAsync(product: "other", version: "v1");
        await _sut.FreezeAsync("acme", "prod", "acme window");

        var active = await _sut.GetActiveAsync();

        var freeze = Assert.Single(active);
        Assert.Equal("acme", freeze.Product);
        Assert.Equal("prod", freeze.TargetEnv);
        Assert.Equal("acme window", freeze.Reason);
        Assert.Equal("Admin User", freeze.FrozenBy);
        Assert.Equal(2, freeze.HeldCandidates);
    }

    [Fact]
    public async Task Lift_ReevaluatesPendingCandidatesOfThePairOnly()
    {
        SeedPolicy(product: "acme");
        SeedPolicy(product: "other");
        var acme = await CreateAsync(product: "acme");
        var other = await CreateAsync(product: "other");
        await _sut.FreezeAsync("acme", "prod", "acme window");
        await _promotions.ApproveAsync(acme.Id, comment: null);
        // "other" (same env, NOT frozen) is gate-satisfiable but was never re-evaluated: its approval
        // row is seeded directly, bypassing ApproveAsync. Re-evaluating it would flip it to Approved,
        // so it stays Pending only if the lift sweep is scoped to the lifted pair.
        _db.PromotionApprovals.Add(new PromotionApproval
        {
            Id = Guid.NewGuid(),
            CandidateId = other.Id,
            ApproverEmail = "admin@example.com",
            ApproverName = "Admin User",
            Decision = PromotionDecision.Approved,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        _db.SaveChanges();
        Assert.Equal(PromotionStatus.Pending, StatusOf(acme.Id));
        Assert.Equal(PromotionStatus.Pending, StatusOf(other.Id));
        _webhooks.ClearReceivedCalls();

        var lifted = await _sut.LiftAsync("acme", "prod");

        Assert.True(lifted);
        Assert.Equal(PromotionStatus.Approved, StatusOf(acme.Id));
        Assert.Equal(PromotionStatus.Pending, StatusOf(other.Id));
        await _webhooks.Received(1).DispatchAsync(
            "promotion.approved", Arg.Any<object>(), Arg.Any<WebhookEventFilters>());
        await _webhooks.Received(1).DispatchAsync(
            "promotion.approved",
            Arg.Is<object>(o => JsonContains(o, acme.Id.ToString())),
            Arg.Any<WebhookEventFilters>());
        Assert.DoesNotContain(_db.PromotionFreezes, f => f.Product == "acme");
    }

    [Fact]
    public async Task Lift_ReleasesCandidateCreatedUnderAutoApproveWhenNoWorkItems()
    {
        SeedPolicy(autoApproveWhenNoWorkItems: true);
        await _sut.FreezeAsync("acme", "prod", "window");
        var candidate = await CreateAsync();
        Assert.Equal(PromotionStatus.Pending, candidate.Status);

        await _sut.LiftAsync("acme", "prod");

        Assert.Equal(PromotionStatus.Approved, StatusOf(candidate.Id));
    }

    [Fact]
    public async Task Lift_WritesAuditWithActorAndReason()
    {
        SeedPolicy();
        await _sut.FreezeAsync("acme", "prod", "Release window");

        await _sut.LiftAsync("acme", "prod");

        await _audit.Received(1).Log(
            "promotions", "promotion.unfrozen",
            "admin-id", "Admin User", "user", "PromotionFreeze", Arg.Any<Guid?>(),
            Arg.Any<object?>(), Arg.Is<object?>(o => JsonContains(o, "Release window")), Arg.Any<object?>());
    }

    [Fact]
    public async Task Lift_PairNotFrozen_ReturnsFalse()
    {
        SeedPolicy();

        Assert.False(await _sut.LiftAsync("acme", "prod"));
        await _audit.DidNotReceive().Log(
            Arg.Any<string>(), "promotion.unfrozen",
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Guid?>(),
            Arg.Any<object?>(), Arg.Any<object?>(), Arg.Any<object?>());
    }
}
