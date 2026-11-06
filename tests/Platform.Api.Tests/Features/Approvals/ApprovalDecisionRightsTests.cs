using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Platform.Api.Features.Approvals;
using Platform.Api.Features.Approvals.Models;
using Platform.Api.Features.Catalog.Models;
using Platform.Api.Features.Promotions;
using Platform.Api.Features.Requests;
using Platform.Api.Features.Requests.Models;
using Platform.Api.Features.Webhooks;
using Platform.Api.Infrastructure.Audit;
using Platform.Api.Infrastructure.Auth;
using Platform.Api.Infrastructure.Identity;
using Platform.Api.Infrastructure.Notifications;
using Platform.Api.Infrastructure.Persistence;
using Platform.Api.Infrastructure.Realtime;

namespace Platform.Api.Tests.Features.Approvals;

/// <summary>
/// Who may decide on a catalog request's approval: never the requester, and only members of the
/// approver group stored on the request when it was submitted.
/// </summary>
public class ApprovalDecisionRightsTests : IDisposable
{
    private const string ApproverGroup = "fafaaeda-c84d-406e-be43-ff265c760758";

    private readonly PlatformDbContext _db;
    private readonly ICurrentUser _currentUser = Substitute.For<ICurrentUser>();
    private readonly IIdentityService _identity = Substitute.For<IIdentityService>();
    private readonly ApprovalService _sut;

    public ApprovalDecisionRightsTests()
    {
        _db = new PlatformDbContext(new DbContextOptionsBuilder<PlatformDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

        var stateMachine = new RequestStateMachine(
            Substitute.For<IAuditLogger>(), Substitute.For<IPlatformEventPublisher>(), Substitute.For<IWebhookDispatcher>());
        var authorizer = new PromotionApprovalAuthorizer(
            _currentUser, _identity, Substitute.For<ILogger<PromotionApprovalAuthorizer>>());

        _sut = new ApprovalService(_db, stateMachine, Substitute.For<IAuditLogger>(), _currentUser,
            Substitute.For<IPlatformEventPublisher>(), Substitute.For<INotificationService>(),
            Substitute.For<IWebhookDispatcher>(), authorizer);

        SignInAs("approver-1", "approver@example.com");
        _identity.GetGroupMembers(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new List<UserInfo>());
    }

    public void Dispose() => _db.Dispose();

    private void SignInAs(string id, string email, bool admin = false, params string[] roles)
    {
        _currentUser.Id.Returns(id);
        _currentUser.Name.Returns(id);
        _currentUser.Email.Returns(email);
        _currentUser.IsAdmin.Returns(admin);
        _currentUser.Roles.Returns(roles.ToList());
        _currentUser.Groups.Returns(new List<string>());
    }

    private void GroupHas(string email) =>
        _identity.GetGroupMembers(ApproverGroup, Arg.Any<CancellationToken>())
            .Returns(new List<UserInfo> { new("someone", "Someone", email) });

    private async Task<ApprovalRequest> Seed(ApprovalOutcome? outcome, string? catalogGroup = null)
    {
        var item = new CatalogItem
        {
            Id = Guid.NewGuid(),
            Slug = $"item-{Guid.NewGuid():N}",
            Name = "Export SQL database",
            Category = "data",
            CurrentYamlHash = "hash",
            Approval = new ApprovalConfig { Required = true, ApproverGroup = catalogGroup },
        };
        var request = new ServiceRequest
        {
            Id = Guid.NewGuid(),
            CorrelationId = Guid.NewGuid(),
            CatalogItemId = item.Id,
            CatalogItem = item,
            RequesterId = "requester-1",
            RequesterName = "Requester",
            RequesterEmail = "requester@example.com",
            Status = RequestStatus.AwaitingApproval,
            ApprovalOutcome = outcome,
        };
        var approval = new ApprovalRequest
        {
            Id = Guid.NewGuid(),
            ServiceRequestId = request.Id,
            ServiceRequest = request,
            Strategy = ApprovalStrategy.Any,
            Status = "Pending",
        };

        _db.CatalogItems.Add(item);
        _db.ServiceRequests.Add(request);
        _db.ApprovalRequests.Add(approval);
        await _db.SaveChangesAsync();
        return approval;
    }

    private static ApprovalOutcome Outcome(string? group) => new() { Required = true, ApproverGroup = group };

    private async Task AssertStillPending(ApprovalRequest approval)
    {
        var reloaded = await _db.ApprovalRequests.Include(a => a.Decisions).SingleAsync(a => a.Id == approval.Id);
        Assert.Equal("Pending", reloaded.Status);
        Assert.Empty(reloaded.Decisions);
    }

    [Theory]
    [InlineData("Approved")]
    [InlineData("Rejected")]
    [InlineData("ChangesRequested")]
    public async Task Requester_CannotDecideOnOwnRequest(string decision)
    {
        var approval = await Seed(Outcome(null));
        SignInAs("requester-1", "requester@example.com", admin: true);

        var ex = await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => _sut.RecordDecision(approval.Id, decision, "comment"));

        Assert.Contains("You raised this request", ex.Message);
        await AssertStillPending(approval);
    }

    [Fact]
    public async Task Requester_IsRecognisedByEmail_Too()
    {
        var approval = await Seed(Outcome(null));
        SignInAs("other-id", "REQUESTER@example.com");

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _sut.RecordDecision(approval.Id, "Approved", null));
    }

    [Fact]
    public async Task NoApproverGroup_AnyoneElseMayDecide()
    {
        var approval = await Seed(Outcome(null));

        await _sut.RecordDecision(approval.Id, "Approved", null);

        Assert.Equal("Approved", (await _db.ApprovalRequests.FindAsync(approval.Id))!.Status);
    }

    [Fact]
    public async Task NonMember_IsRefused()
    {
        var approval = await Seed(Outcome(ApproverGroup));

        var ex = await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => _sut.RecordDecision(approval.Id, "Approved", null));

        Assert.Contains("approver group", ex.Message);
        await AssertStillPending(approval);
    }

    [Fact]
    public async Task GroupMember_MayApprove()
    {
        var approval = await Seed(Outcome(ApproverGroup));
        GroupHas("approver@example.com");

        await _sut.RecordDecision(approval.Id, "Approved", null);

        var request = await _db.ServiceRequests.FindAsync(approval.ServiceRequestId);
        Assert.Equal(RequestStatus.Executing, request!.Status);
    }

    [Fact]
    public async Task RoleNamedAsGroup_MayApprove()
    {
        var approval = await Seed(Outcome("InfraPortal.Approver"));
        SignInAs("approver-1", "approver@example.com", admin: false, "InfraPortal.Approver");

        await _sut.RecordDecision(approval.Id, "Approved", null);

        Assert.Equal("Approved", (await _db.ApprovalRequests.FindAsync(approval.Id))!.Status);
    }

    [Fact]
    public async Task Admin_CountsAsMember_LikePromotions()
    {
        var approval = await Seed(Outcome(ApproverGroup));
        SignInAs("admin-1", "admin@example.com", admin: true);

        await _sut.RecordDecision(approval.Id, "Approved", null);

        Assert.Equal("Approved", (await _db.ApprovalRequests.FindAsync(approval.Id))!.Status);
    }

    [Fact]
    public async Task StoredOutcome_WinsOverTheItemsCurrentGroup()
    {
        // The item was edited after submit; the request keeps the group it was submitted with.
        var approval = await Seed(Outcome(ApproverGroup), catalogGroup: "some-other-group");
        GroupHas("approver@example.com");

        await _sut.RecordDecision(approval.Id, "Approved", null);

        Assert.Equal("Approved", (await _db.ApprovalRequests.FindAsync(approval.Id))!.Status);
    }

    [Fact]
    public async Task RequestSubmittedBeforeRules_FallsBackToTheItemsGroup()
    {
        var approval = await Seed(outcome: null, catalogGroup: ApproverGroup);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _sut.RecordDecision(approval.Id, "Approved", null));

        GroupHas("approver@example.com");
        await _sut.RecordDecision(approval.Id, "Approved", null);
        Assert.Equal("Approved", (await _db.ApprovalRequests.FindAsync(approval.Id))!.Status);
    }

    [Fact]
    public async Task CheckCanDecide_ExplainsWhy()
    {
        var approval = await Seed(Outcome(ApproverGroup));
        var loaded = await _sut.GetById(approval.Id);

        var nonMember = await _sut.CheckCanDecide(loaded!);
        Assert.False(nonMember.Allowed);
        Assert.Contains("approver group", nonMember.Reason);

        GroupHas("approver@example.com");
        var member = await _sut.CheckCanDecide(loaded!);
        Assert.True(member.Allowed);
        Assert.Null(member.Reason);
    }
}
