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
/// Focused tests for how <see cref="PromotionService"/> dispatches webhook events
/// when a candidate transitions to Approved or Rejected.
/// </summary>
public class PromotionServiceDispatchTests : IDisposable
{
    private readonly PlatformDbContext _db;
    private readonly ICurrentUser _currentUser = Substitute.For<ICurrentUser>();
    private readonly IIdentityService _identity = Substitute.For<IIdentityService>();
    private readonly IAuditLogger _audit = Substitute.For<IAuditLogger>();
    private readonly IWebhookDispatcher _webhookDispatcher = Substitute.For<IWebhookDispatcher>();
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
        _currentUser.IsQA.Returns(false);
        _currentUser.Roles.Returns(new List<string>().AsReadOnly());
        _currentUser.Groups.Returns(new List<string>().AsReadOnly());
        _identity.GetGroupMembers(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new List<UserInfo>());

        var resolver = new PromotionPolicyResolver(_db);
        var auth = new PromotionApprovalAuthorizer(
            _currentUser, _identity,
            Substitute.For<ILogger<PromotionApprovalAuthorizer>>());
        _sut = new PromotionService(
            _db, resolver, auth, _currentUser, _audit,
            Substitute.For<ILogger<PromotionService>>(),
            _webhookDispatcher,
            TestOptions.Normalization());
    }

    public void Dispose() => _db.Dispose();

    /// <summary>
    /// Builds a <see cref="CreatePromotionDto"/> for the (acme, api, staging→prod) edge and calls the
    /// external create path. No staging DeployEvent is seeded, so the source-drift invariant never
    /// blocks (no source history ⇒ can't conclude drift).
    /// </summary>
    private Task<PromotionCandidate?> CreateAsync(string version = "v1")
        => _sut.CreateExternalCandidateAsync(new CreatePromotionDto(
            Product: "acme",
            Service: "api",
            SourceEnv: "staging",
            TargetEnv: "prod",
            Version: version,
            FromRevision: null,
            ToRevision: null,
            References: null,
            Participants: null));

    /// <summary>
    /// Seeds a product-level policy (Service=null) for prod. A null <paramref name="approverGroup"/>
    /// means no requirements ⇒ auto-approve; otherwise one requirement satisfied by the group with
    /// MinApprovers:1.
    /// </summary>
    private void SeedPolicy(string? approverGroup)
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
            TargetEnv = "prod",
            ApprovalSteps = steps,
        });
        _db.SaveChanges();
    }

    [Fact]
    public async Task AutoApprove_DispatchesPromotionApprovedWebhook()
    {
        SeedPolicy(approverGroup: null);

        var candidate = await CreateAsync();

        Assert.NotNull(candidate);
        await _webhookDispatcher.Received(1).DispatchAsync(
            "promotion.approved",
            Arg.Any<object>(),
            Arg.Any<WebhookEventFilters>());
    }

    [Fact]
    public async Task NoAutoApprove_DoesNotDispatchWebhook()
    {
        SeedPolicy(approverGroup: "ops");

        var candidate = await CreateAsync();

        Assert.NotNull(candidate);
        Assert.Equal(PromotionStatus.Pending, candidate!.Status);
        await _webhookDispatcher.DidNotReceive().DispatchAsync(
            Arg.Any<string>(),
            Arg.Any<object>(),
            Arg.Any<WebhookEventFilters>());
    }

    [Fact]
    public async Task ThresholdMet_OnApprove_DispatchesWebhook()
    {
        SeedPolicy(approverGroup: "ops");

        var candidate = await CreateAsync();
        Assert.Equal(PromotionStatus.Pending, candidate!.Status);

        // Admin satisfies the single MinApprovers:1 requirement (group bypass) → gate flips Approved.
        var approved = await _sut.ApproveAsync(candidate.Id, comment: null);

        Assert.Equal(PromotionStatus.Approved, approved.Status);
        await _webhookDispatcher.Received(1).DispatchAsync(
            "promotion.approved",
            Arg.Any<object>(),
            Arg.Any<WebhookEventFilters>());
    }

    [Fact]
    public async Task Reject_DispatchesPromotionRejectedWebhook()
    {
        SeedPolicy(approverGroup: "ops");

        var candidate = await CreateAsync();
        Assert.Equal(PromotionStatus.Pending, candidate!.Status);

        await _sut.RejectAsync(candidate.Id, comment: "not ready");

        await _webhookDispatcher.Received(1).DispatchAsync(
            "promotion.rejected",
            Arg.Any<object>(),
            Arg.Any<WebhookEventFilters>());
    }
}
