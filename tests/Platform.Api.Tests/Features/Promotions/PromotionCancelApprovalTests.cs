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
/// Undoing an approval (<see cref="PromotionService.CancelApprovalAsync"/>): who may do it, how long
/// the window stays open, and what an undo actually has to unwind for the promotion to be genuinely
/// back to Pending rather than cosmetically so.
/// </summary>
public class PromotionCancelApprovalTests : IDisposable
{
    private readonly PlatformDbContext _db;
    private readonly ICurrentUser _currentUser = Substitute.For<ICurrentUser>();
    private readonly IIdentityService _identity = Substitute.For<IIdentityService>();
    private readonly IAuditLogger _audit = Substitute.For<IAuditLogger>();
    private readonly IWebhookDispatcher _webhooks = Substitute.For<IWebhookDispatcher>();
    private readonly PromotionService _sut;

    public PromotionCancelApprovalTests()
    {
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _db = new PlatformDbContext(options);

        _currentUser.Id.Returns("alice-id");
        _currentUser.Name.Returns("Alice");
        _currentUser.Email.Returns("alice@example.com");
        // Admin, which the authorizer treats as a member of any approver group — enough to both
        // approve and undo without wiring up Graph. Tests that care about authorization flip it off.
        _currentUser.IsAdmin.Returns(true);
        _currentUser.IsQA.Returns(false);
        _currentUser.Roles.Returns(new List<string>().AsReadOnly());
        _currentUser.Groups.Returns(new List<string>().AsReadOnly());
        _currentUser.IsInGroup(Arg.Any<string>()).Returns(false);
        _identity.GetGroupMembers(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new List<UserInfo>());

        var auth = new PromotionApprovalAuthorizer(
            _currentUser, _identity, Substitute.For<ILogger<PromotionApprovalAuthorizer>>());
        _sut = new PromotionService(
            _db, new PromotionPolicyResolver(_db), auth, _currentUser, _audit,
            Substitute.For<ILogger<PromotionService>>(),
            _webhooks,
            TestOptions.Normalization(),
            TestUserPreferences.For(_db),
            TestProductOverrides.For(_db));
    }

    public void Dispose() => _db.Dispose();

    /// <summary>
    /// Seeds the (acme, api, staging→prod) policy. A null <paramref name="approverGroup"/> leaves the
    /// requirement tree empty, which is the auto-approve case.
    /// </summary>
    private void SeedPolicy(string? approverGroup = "ops")
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
            Product = "acme",
            Service = null,
            SourceEnv = "staging",
            TargetEnv = "prod",
            ApprovalSteps = steps,
        });
        _db.SaveChanges();
    }

    private async Task<PromotionCandidate> CreateAsync(string version = "v1")
    {
        _db.DeployEvents.Add(new DeployEvent
        {
            Id = Guid.NewGuid(),
            Product = "acme",
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

        var candidate = await _sut.CreateExternalCandidateAsync(new CreatePromotionDto(
            Product: "acme", Service: "api", SourceEnv: "staging", TargetEnv: "prod",
            Version: version, FromRevision: null, ToRevision: null,
            References: null, Participants: null));
        return candidate!;
    }

    /// <summary>Creates a gated candidate and approves it, so it sits in Approved with one sign-off.</summary>
    private async Task<PromotionCandidate> ApprovedCandidateAsync()
    {
        SeedPolicy();
        var candidate = await CreateAsync();
        var approved = await _sut.ApproveAsync(candidate.Id, comment: null);
        Assert.Equal(PromotionStatus.Approved, approved.Status);
        return approved;
    }

    // ---------------------------------------------------------------------
    // The happy path
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Cancel_ReturnsCandidateToPending_AndClearsApprovedAt()
    {
        var candidate = await ApprovedCandidateAsync();

        var result = await _sut.CancelApprovalAsync(candidate.Id, comment: "wrong service");

        Assert.Equal(PromotionStatus.Pending, result.Candidate.Status);
        Assert.Null(result.Candidate.ApprovedAt);
        var reloaded = _db.PromotionCandidates.Single();
        Assert.Equal(PromotionStatus.Pending, reloaded.Status);
        Assert.Null(reloaded.ApprovedAt);
    }

    [Fact]
    public async Task Cancel_ClearsTheApprovalRows()
    {
        // The rows have to go, or the very next gate evaluation re-approves the candidate and the
        // undo is cosmetic. This is the assertion that keeps the two halves honest.
        var candidate = await ApprovedCandidateAsync();
        Assert.Single(_db.PromotionApprovals);

        var result = await _sut.CancelApprovalAsync(candidate.Id, comment: null);

        Assert.Equal(1, result.ClearedApprovals);
        Assert.Empty(_db.PromotionApprovals);

        // And the candidate genuinely stays Pending under a fresh evaluation.
        var reevaluated = await _sut.ReevaluateAsync(candidate.Id);
        Assert.Equal(PromotionStatus.Pending, reevaluated.Status);
    }

    [Fact]
    public async Task Cancel_LetsTheSameApproverApproveAgain()
    {
        // One decision per person is enforced on the approval rows; clearing them is what makes the
        // mistake recoverable by the person who made it.
        var candidate = await ApprovedCandidateAsync();
        await _sut.CancelApprovalAsync(candidate.Id, comment: null);

        var reapproved = await _sut.ApproveAsync(candidate.Id, comment: "meant this one");

        Assert.Equal(PromotionStatus.Approved, reapproved.Status);
    }

    [Fact]
    public async Task Cancel_WritesASystemCommentNamingTheClearedSignOff()
    {
        var candidate = await ApprovedCandidateAsync();

        await _sut.CancelApprovalAsync(candidate.Id, comment: "picked the wrong row");

        var comment = _db.PromotionComments
            .Where(c => c.CandidateId == candidate.Id)
            .AsEnumerable()
            .Last(c => c.Body.Contains("cancelled the approval"));
        Assert.Contains("Alice", comment.Body);
        Assert.Contains("picked the wrong row", comment.Body);
    }

    // ---------------------------------------------------------------------
    // Webhooks
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Cancel_StopsTheHeldApprovedDelivery_AndReportsIt()
    {
        var candidate = await ApprovedCandidateAsync();
        _webhooks.CancelPendingAsync(
            PromotionService.ApprovedWebhookCancelKey(candidate.Id), Arg.Any<CancellationToken>())
            .Returns(2);

        var result = await _sut.CancelApprovalAsync(candidate.Id, comment: null);

        Assert.True(result.ApprovedWebhookStopped);
        await _webhooks.Received(1).CancelPendingAsync(
            PromotionService.ApprovedWebhookCancelKey(candidate.Id), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Cancel_WhenApprovedWebhookAlreadyWentOut_SaysSo()
    {
        var candidate = await ApprovedCandidateAsync();
        _webhooks.CancelPendingAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(0);

        var result = await _sut.CancelApprovalAsync(candidate.Id, comment: null);

        // Nothing was stopped ⇒ subscribers already have the approval, and the cancellation event
        // below is the retraction. Reporting this honestly is the whole point of the flag.
        Assert.False(result.ApprovedWebhookStopped);
    }

    [Fact]
    public async Task Cancel_DispatchesTheCancellationEvent()
    {
        var candidate = await ApprovedCandidateAsync();

        await _sut.CancelApprovalAsync(candidate.Id, comment: null);

        await _webhooks.Received(1).DispatchAsync(
            "promotion.approval.cancelled",
            Arg.Any<object>(),
            Arg.Any<WebhookEventFilters>(),
            // Not held — a retraction is only useful the moment it happens.
            Arg.Is<WebhookDispatchOptions?>(o => o == null || o.Delay == null));
    }

    [Fact]
    public async Task Cancel_SurvivesAFailingWebhookCancel()
    {
        // Delivery bookkeeping must never be able to strand a state transition the user asked for.
        var candidate = await ApprovedCandidateAsync();
        _webhooks.CancelPendingAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<int>(_ => throw new InvalidOperationException("delivery store down"));

        var result = await _sut.CancelApprovalAsync(candidate.Id, comment: null);

        Assert.Equal(PromotionStatus.Pending, result.Candidate.Status);
        Assert.False(result.ApprovedWebhookStopped);
    }

    // ---------------------------------------------------------------------
    // The window
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData(PromotionStatus.Deploying)]
    [InlineData(PromotionStatus.Deployed)]
    [InlineData(PromotionStatus.Rejected)]
    [InlineData(PromotionStatus.Superseded)]
    [InlineData(PromotionStatus.Pending)]
    public async Task Cancel_RefusedOnEveryStatusButApproved(PromotionStatus status)
    {
        var candidate = await ApprovedCandidateAsync();
        candidate.Status = status;
        await _db.SaveChangesAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _sut.CancelApprovalAsync(candidate.Id, comment: null));

        // And nothing was unwound on the way to refusing.
        Assert.Single(_db.PromotionApprovals);
    }

    [Fact]
    public async Task Cancel_RefusedOnAnAutoApprovePolicy()
    {
        // No human decided anything, and clearing rows there would only re-approve on the next
        // evaluation. Refuse with an explanation instead of flapping.
        SeedPolicy(approverGroup: null);
        var candidate = await CreateAsync();
        Assert.Equal(PromotionStatus.Approved, candidate.Status);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _sut.CancelApprovalAsync(candidate.Id, comment: null));
        Assert.Contains("without human sign-off", ex.Message);
        Assert.Equal(PromotionStatus.Approved, _db.PromotionCandidates.Single().Status);
    }

    [Fact]
    public async Task Cancel_UnknownCandidate_Throws()
    {
        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => _sut.CancelApprovalAsync(Guid.NewGuid(), comment: null));
    }

    // ---------------------------------------------------------------------
    // Who may cancel
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Cancel_RefusedForSomeoneOutsideTheApproverTree()
    {
        var candidate = await ApprovedCandidateAsync();
        // Drop out of every group: no longer admin, not in "ops", Graph knows nobody.
        _currentUser.IsAdmin.Returns(false);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => _sut.CancelApprovalAsync(candidate.Id, comment: null));
        Assert.Equal(PromotionStatus.Approved, _db.PromotionCandidates.Single().Status);
    }

    [Fact]
    public async Task Cancel_AllowedForADifferentEligibleApprover()
    {
        // The mistake is often spotted by a colleague, so eligibility — not authorship of the
        // original sign-off — is what the check asks about.
        var candidate = await ApprovedCandidateAsync();
        _currentUser.Email.Returns("bob@example.com");
        _currentUser.Name.Returns("Bob");

        var result = await _sut.CancelApprovalAsync(candidate.Id, comment: null);

        Assert.Equal(PromotionStatus.Pending, result.Candidate.Status);
        Assert.Equal(1, result.ClearedApprovals); // Alice's sign-off, cleared by Bob
    }

    [Fact]
    public async Task Cancel_UndoesAnAdminBypass_WhichRecordedNoApprovalRow()
    {
        SeedPolicy();
        var candidate = await CreateAsync();
        await _sut.BypassAsync(candidate.Id, reason: "hotfix INC-42");

        var result = await _sut.CancelApprovalAsync(candidate.Id, comment: "not needed after all");

        Assert.Equal(PromotionStatus.Pending, result.Candidate.Status);
        Assert.Equal(0, result.ClearedApprovals);
    }

    // ---------------------------------------------------------------------
    // Capability probe (drives the UI's button)
    // ---------------------------------------------------------------------

    [Fact]
    public async Task CanUserCancelApproval_TracksTheSameRules()
    {
        SeedPolicy();
        var candidate = await CreateAsync();
        Assert.False(await _sut.CanUserCancelApprovalAsync(candidate)); // Pending — nothing to undo

        var approved = await _sut.ApproveAsync(candidate.Id, comment: null);
        Assert.True(await _sut.CanUserCancelApprovalAsync(approved));

        _currentUser.IsAdmin.Returns(false); // out of the approver tree
        Assert.False(await _sut.CanUserCancelApprovalAsync(approved));
    }
}
