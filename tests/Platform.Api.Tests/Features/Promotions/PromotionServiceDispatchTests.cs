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
        _currentUser.Roles.Returns(new List<string>().AsReadOnly());
        _currentUser.Groups.Returns(new List<string>().AsReadOnly());
        _identity.GetGroupMembers(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new List<UserInfo>());

        var resolver = new PromotionPolicyResolver(_db);
        _sut = new PromotionService(
            _db, resolver, _identity, _currentUser, _audit,
            Substitute.For<ILogger<PromotionService>>(),
            _webhookDispatcher);
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

    private void SeedPolicy(string? approverGroup)
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
        });
        _db.SaveChanges();
    }

    [Fact]
    public async Task AutoApprove_DispatchesPromotionApprovedWebhook()
    {
        SeedPolicy(approverGroup: null);

        var e = SeedDeploy();
        var candidate = await _sut.CreateCandidateAsync(e, "prod");

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

        var e = SeedDeploy();
        var candidate = await _sut.CreateCandidateAsync(e, "prod");

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

        var e = SeedDeploy();
        var candidate = await _sut.CreateCandidateAsync(e, "prod");
        Assert.Equal(PromotionStatus.Pending, candidate!.Status);

        var approved = await _sut.ApproveAsync(candidate.Id, comment: null);

        await _webhookDispatcher.Received(1).DispatchAsync(
            "promotion.approved",
            Arg.Any<object>(),
            Arg.Any<WebhookEventFilters>());
    }

    [Fact]
    public async Task Reject_DispatchesPromotionRejectedWebhook()
    {
        SeedPolicy(approverGroup: "ops");

        var e = SeedDeploy();
        var candidate = await _sut.CreateCandidateAsync(e, "prod");
        Assert.Equal(PromotionStatus.Pending, candidate!.Status);

        await _sut.RejectAsync(candidate.Id, comment: "not ready");

        await _webhookDispatcher.Received(1).DispatchAsync(
            "promotion.rejected",
            Arg.Any<object>(),
            Arg.Any<WebhookEventFilters>());
    }
}
