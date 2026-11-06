using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Platform.Api.Features.Approvals;
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

namespace Platform.Api.Tests.Features.Requests;

/// <summary>Submit evaluates <c>approval.rules</c> and stores the outcome on the request.</summary>
public class RequestSubmitApprovalRulesTests : IDisposable
{
    private const string Group = "fafaaeda-c84d-406e-be43-ff265c760758";

    private readonly PlatformDbContext _db;
    private readonly IIdentityService _identity = Substitute.For<IIdentityService>();
    private readonly INotificationService _notifications = Substitute.For<INotificationService>();
    private readonly RequestService _sut;

    public RequestSubmitApprovalRulesTests()
    {
        _db = new PlatformDbContext(new DbContextOptionsBuilder<PlatformDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

        var currentUser = Substitute.For<ICurrentUser>();
        currentUser.Id.Returns("requester-1");
        currentUser.Name.Returns("Requester");
        currentUser.Email.Returns("requester@example.com");

        var stateMachine = new RequestStateMachine(
            Substitute.For<IAuditLogger>(), Substitute.For<IPlatformEventPublisher>(), Substitute.For<IWebhookDispatcher>());
        var approvals = new ApprovalService(_db, stateMachine, Substitute.For<IAuditLogger>(), currentUser,
            Substitute.For<IPlatformEventPublisher>(), _notifications, Substitute.For<IWebhookDispatcher>(),
            new PromotionApprovalAuthorizer(currentUser, _identity, Substitute.For<ILogger<PromotionApprovalAuthorizer>>()));

        _identity.GetGroupMembers(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new List<UserInfo> { new("a1", "Approver", "approver@example.com") });

        _sut = new RequestService(_db, stateMachine, currentUser, approvals, new ApproverResolver(_identity), _notifications);
    }

    public void Dispose() => _db.Dispose();

    private async Task<Guid> CreateRequest(ApprovalConfig? approval, string environment)
    {
        var item = new CatalogItem
        {
            Id = Guid.NewGuid(),
            Slug = $"export-{Guid.NewGuid():N}",
            Name = "Export SQL database",
            Category = "data",
            CurrentYamlHash = "hash",
            Approval = approval,
            Executor = new ExecutorConfig { Type = "manual" },
        };
        _db.CatalogItems.Add(item);
        await _db.SaveChangesAsync();

        var request = await _sut.Create(item.Id, new Dictionary<string, object> { ["environment"] = environment });
        return request.Id;
    }

    private static ApprovalConfig FailClosed() => new()
    {
        Required = true,
        ApproverGroup = Group,
        TimeoutHours = 72,
        Rules =
        [
            new ApprovalRule
            {
                Name = "Dev and test need no approval",
                When = new ApprovalCondition { Field = "environment", In = ["dev", "test"] },
                Required = false,
            },
        ],
    };

    [Fact]
    public async Task RuleWaivesApproval_GoesStraightToExecuting_AndRecordsTheRule()
    {
        var id = await CreateRequest(FailClosed(), "dev");

        await _sut.Submit(id);

        var request = await _sut.GetById(id);
        Assert.Equal(RequestStatus.Executing, request!.Status);
        Assert.Null(request.ApprovalRequest);
        Assert.False(request.ApprovalOutcome!.Required);
        Assert.Equal(0, request.ApprovalOutcome.RuleIndex);
        Assert.Equal("Dev and test need no approval", request.ApprovalOutcome.RuleName);
    }

    [Fact]
    public async Task NoRuleMatches_WaitsForApproval_FromTheTopLevelGroup()
    {
        var id = await CreateRequest(FailClosed(), "staging");

        await _sut.Submit(id);

        var request = await _sut.GetById(id);
        Assert.Equal(RequestStatus.AwaitingApproval, request!.Status);
        Assert.NotNull(request.ApprovalRequest);
        Assert.NotNull(request.ApprovalRequest!.TimeoutAt);
        Assert.True(request.ApprovalOutcome!.Required);
        Assert.Equal(Group, request.ApprovalOutcome.ApproverGroup);
        Assert.Null(request.ApprovalOutcome.RuleIndex);
        await _notifications.Received(1).SendApprovalNotification(
            Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Is<IReadOnlyList<string>>(l => l.Contains("approver@example.com")));
    }

    [Fact]
    public async Task ItemWithoutRules_BehavesAsBefore()
    {
        var withApproval = await CreateRequest(new ApprovalConfig { Required = true }, "dev");
        var without = await CreateRequest(null, "dev");

        await _sut.Submit(withApproval);
        await _sut.Submit(without);

        Assert.Equal(RequestStatus.AwaitingApproval, (await _sut.GetById(withApproval))!.Status);
        Assert.Equal(RequestStatus.Executing, (await _sut.GetById(without))!.Status);
    }
}
