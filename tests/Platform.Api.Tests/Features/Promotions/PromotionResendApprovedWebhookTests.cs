using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Platform.Api.Features.Promotions;
using Platform.Api.Features.Promotions.Models;
using Platform.Api.Features.Webhooks;
using Platform.Api.Features.Webhooks.Models;
using Platform.Api.Infrastructure.Audit;
using Platform.Api.Infrastructure.Auth;
using Platform.Api.Infrastructure.Identity;
using Platform.Api.Infrastructure.Persistence;

namespace Platform.Api.Tests.Features.Promotions;

/// <summary>
/// The Settings → Maintenance action that re-announces <c>promotion.approved</c> for promotions
/// stuck at Approved with no deploy behind them. Two rules carry the whole feature: only Approved
/// promotions are re-announced (everything else either already got its message or was never cleared
/// to send one), and a promotion whose delivery is still queued is skipped rather than fired twice.
/// </summary>
public class PromotionResendApprovedWebhookTests : IDisposable
{
    private readonly PlatformDbContext _db;
    private readonly ICurrentUser _currentUser = Substitute.For<ICurrentUser>();
    private readonly IAuditLogger _audit = Substitute.For<IAuditLogger>();
    private readonly IWebhookDispatcher _webhooks = Substitute.For<IWebhookDispatcher>();
    private readonly PromotionService _sut;

    public PromotionResendApprovedWebhookTests()
    {
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _db = new PlatformDbContext(options);

        _currentUser.Id.Returns("alice-id");
        _currentUser.Name.Returns("Alice");
        _currentUser.Email.Returns("alice@example.com");
        _currentUser.IsAdmin.Returns(true);
        _currentUser.Roles.Returns(new List<string>().AsReadOnly());
        _currentUser.Groups.Returns(new List<string>().AsReadOnly());

        // Two receivers listen, so a resend that reaches the dispatcher queues two rows — enough to
        // tell "queued something" from the zero-subscription case apart in the assertions.
        _webhooks.DispatchAsync(
                Arg.Any<string>(), Arg.Any<object>(), Arg.Any<WebhookEventFilters?>(),
                Arg.Any<WebhookDispatchOptions?>())
            .Returns(2);

        var identity = Substitute.For<IIdentityService>();
        identity.GetGroupMembers(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new List<UserInfo>());

        _sut = new PromotionService(
            _db, new PromotionPolicyResolver(_db),
            new PromotionApprovalAuthorizer(
                _currentUser, identity, Substitute.For<ILogger<PromotionApprovalAuthorizer>>()),
            _currentUser, _audit,
            Substitute.For<ILogger<PromotionService>>(),
            _webhooks,
            TestOptions.Normalization(),
            TestEnvironmentAliases.For(_db),
            TestUserPreferences.For(_db),
            TestProductOverrides.For(_db));
    }

    public void Dispose() => _db.Dispose();

    private PromotionCandidate SeedCandidate(
        PromotionStatus status, string version = "v1", string product = "acme",
        string targetEnv = "prod", DateTimeOffset? approvedAt = null)
    {
        var candidate = new PromotionCandidate
        {
            Id = Guid.NewGuid(),
            Product = product,
            Service = "api",
            SourceEnv = "staging",
            TargetEnv = targetEnv,
            Version = version,
            Status = status,
            ApprovedAt = status == PromotionStatus.Pending
                ? null
                : approvedAt ?? DateTimeOffset.UtcNow.AddDays(-3),
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-4),
        };
        _db.PromotionCandidates.Add(candidate);
        _db.SaveChanges();
        return candidate;
    }

    /// <summary>Seeds an approval delivery for <paramref name="candidateId"/> in the given state.</summary>
    private void SeedDelivery(
        Guid candidateId, string status, DateTimeOffset? createdAt = null, bool withCancelKey = true)
    {
        var at = createdAt ?? DateTimeOffset.UtcNow.AddDays(-3);
        _db.WebhookDeliveries.Add(new WebhookDelivery
        {
            Id = Guid.NewGuid(),
            SubscriptionId = Guid.NewGuid(),
            EventType = "promotion.approved",
            Status = status,
            PayloadJson =
                "{\"eventType\":\"promotion.approved\",\"data\":{\"candidateId\":\"" + candidateId + "\"}}",
            CancelKey = withCancelKey ? PromotionService.ApprovedWebhookCancelKey(candidateId) : null,
            CreatedAt = at,
            DeliveredAt = status == "delivered" ? at : null,
            NextRetryAt = at,
        });
        _db.SaveChanges();
    }

    [Fact]
    public async Task ResendsApprovedOnly_LeavingEveryOtherStatusAlone()
    {
        var approved = SeedCandidate(PromotionStatus.Approved);
        SeedCandidate(PromotionStatus.Pending, "v2");
        SeedCandidate(PromotionStatus.Deploying, "v3");
        SeedCandidate(PromotionStatus.Deployed, "v4");
        SeedCandidate(PromotionStatus.Superseded, "v5");
        SeedCandidate(PromotionStatus.Rejected, "v6");

        var result = await _sut.ResendApprovedWebhooksAsync();

        Assert.Equal(1, result.Examined);
        Assert.Equal(1, result.Resent);
        Assert.Equal(0, result.Skipped);
        Assert.Equal(2, result.Deliveries); // one promotion × two listening subscriptions
        Assert.Equal(approved.Id, Assert.Single(result.Promotions).Id);

        await _webhooks.Received(1).DispatchAsync(
            "promotion.approved", Arg.Any<object>(), Arg.Any<WebhookEventFilters?>(),
            Arg.Any<WebhookDispatchOptions?>());
    }

    [Fact]
    public async Task ResendGoesOutImmediately_ButStaysCancellable()
    {
        var approved = SeedCandidate(PromotionStatus.Approved);

        await _sut.ResendApprovedWebhooksAsync();

        // No undo window: the decision being re-announced was made days ago, so holding the delivery
        // would buy nothing. The cancel key survives all the same, so cancelling the approval still
        // stops a resend that has not left yet.
        await _webhooks.Received(1).DispatchAsync(
            "promotion.approved", Arg.Any<object>(), Arg.Any<WebhookEventFilters?>(),
            Arg.Is<WebhookDispatchOptions?>(o =>
                o != null
                && o.Delay == TimeSpan.Zero
                && o.CancelKey == PromotionService.ApprovedWebhookCancelKey(approved.Id)));
    }

    [Fact]
    public async Task SkipsAPromotionWhoseDeliveryIsStillQueued()
    {
        var stillQueued = SeedCandidate(PromotionStatus.Approved, "v1");
        SeedDelivery(stillQueued.Id, "pending");
        var lost = SeedCandidate(PromotionStatus.Approved, "v2");
        SeedDelivery(lost.Id, "failed");

        var result = await _sut.ResendApprovedWebhooksAsync();

        Assert.Equal(2, result.Examined);
        Assert.Equal(1, result.Resent);
        Assert.Equal(1, result.Skipped);

        var skipped = result.Promotions.Single(p => p.Id == stillQueued.Id);
        Assert.NotNull(skipped.SkippedReason);
        Assert.Equal(0, skipped.Deliveries);

        var resent = result.Promotions.Single(p => p.Id == lost.Id);
        Assert.Null(resent.SkippedReason);
        Assert.Equal("failed", resent.LastDeliveryStatus);
        Assert.Equal(2, resent.Deliveries);

        await _webhooks.Received(1).DispatchAsync(
            "promotion.approved", Arg.Any<object>(), Arg.Any<WebhookEventFilters?>(),
            Arg.Any<WebhookDispatchOptions?>());
    }

    /// <summary>
    /// The skip guard reads the pending queue by payload, not by cancel key, so an approval queued
    /// before cancel keys existed still protects its promotion from a double fire.
    /// </summary>
    [Fact]
    public async Task SkipsAQueuedDeliveryThatCarriesNoCancelKey()
    {
        var candidate = SeedCandidate(PromotionStatus.Approved);
        SeedDelivery(candidate.Id, "pending", withCancelKey: false);

        var result = await _sut.ResendApprovedWebhooksAsync();

        Assert.Equal(0, result.Resent);
        Assert.Equal(1, result.Skipped);
        // Unattributable, so the history column says so rather than guessing.
        Assert.Equal("none", Assert.Single(result.Promotions).LastDeliveryStatus);
        await _webhooks.DidNotReceive().DispatchAsync(
            Arg.Any<string>(), Arg.Any<object>(), Arg.Any<WebhookEventFilters?>(),
            Arg.Any<WebhookDispatchOptions?>());
    }

    [Fact]
    public async Task DryRun_ReportsTheListWithoutQueuingAnything()
    {
        var candidate = SeedCandidate(PromotionStatus.Approved);
        SeedDelivery(candidate.Id, "delivered");

        var result = await _sut.ResendApprovedWebhooksAsync(dryRun: true);

        Assert.True(result.DryRun);
        Assert.Equal(1, result.Examined);
        Assert.Equal(1, result.Resent);
        Assert.Equal(0, result.Deliveries);
        Assert.Equal("delivered", Assert.Single(result.Promotions).LastDeliveryStatus);

        await _webhooks.DidNotReceive().DispatchAsync(
            Arg.Any<string>(), Arg.Any<object>(), Arg.Any<WebhookEventFilters?>(),
            Arg.Any<WebhookDispatchOptions?>());
        Assert.Empty(_db.PromotionComments);
    }

    /// <summary>
    /// Resending with nothing subscribed is a real outcome an admin must be told about, so it comes
    /// back as "re-sent, zero deliveries" rather than as a plain success.
    /// </summary>
    [Fact]
    public async Task ReportsZeroDeliveries_WhenNoSubscriptionListens()
    {
        _webhooks.DispatchAsync(
                Arg.Any<string>(), Arg.Any<object>(), Arg.Any<WebhookEventFilters?>(),
                Arg.Any<WebhookDispatchOptions?>())
            .Returns(0);
        SeedCandidate(PromotionStatus.Approved);

        var result = await _sut.ResendApprovedWebhooksAsync();

        Assert.Equal(1, result.Resent);
        Assert.Equal(0, result.Deliveries);
    }

    [Fact]
    public async Task ScopesToTheRequestedProductAndTargetEnvironment()
    {
        var wanted = SeedCandidate(PromotionStatus.Approved, "v1", product: "acme", targetEnv: "prod");
        SeedCandidate(PromotionStatus.Approved, "v2", product: "acme", targetEnv: "staging");
        SeedCandidate(PromotionStatus.Approved, "v3", product: "other", targetEnv: "prod");

        var result = await _sut.ResendApprovedWebhooksAsync(product: "acme", targetEnv: "prod");

        Assert.Equal(1, result.Examined);
        Assert.Equal(wanted.Id, Assert.Single(result.Promotions).Id);
    }

    [Fact]
    public async Task RecordsTheResendOnThePromotionThreadAndInTheAuditLog()
    {
        var candidate = SeedCandidate(PromotionStatus.Approved);

        await _sut.ResendApprovedWebhooksAsync();

        var comment = Assert.Single(_db.PromotionComments.Where(c => c.CandidateId == candidate.Id));
        Assert.Equal(PromotionComment.SystemAuthor, comment.AuthorEmail);
        Assert.Contains("Alice", comment.Body);
        Assert.Contains("re-sent the approval webhook", comment.Body);

        await _audit.Received(1).Log(
            "promotions", "promotion.approved.webhook.resent",
            "alice-id", "Alice", "user",
            "PromotionCandidate", candidate.Id, null, Arg.Any<object>(), Arg.Any<object>());
    }

    /// <summary>Nothing approved is the ordinary healthy state, and must not look like a failure.</summary>
    [Fact]
    public async Task ReportsAnEmptySweepWhenNothingIsApproved()
    {
        SeedCandidate(PromotionStatus.Deployed);

        var result = await _sut.ResendApprovedWebhooksAsync(dryRun: true);

        Assert.Equal(0, result.Examined);
        Assert.Equal(0, result.Resent);
        Assert.Empty(result.Promotions);
    }
}
