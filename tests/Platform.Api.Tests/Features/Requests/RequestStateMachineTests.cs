using NSubstitute;
using Platform.Api.Features.Requests;
using Platform.Api.Features.Requests.Models;
using Platform.Api.Features.Webhooks;
using Platform.Api.Infrastructure.Audit;
using Platform.Api.Infrastructure.Realtime;

namespace Platform.Api.Tests.Features.Requests;

public class RequestStateMachineTests
{
    private readonly IAuditLogger _auditLogger = Substitute.For<IAuditLogger>();
    private readonly IPlatformEventPublisher _eventPublisher = Substitute.For<IPlatformEventPublisher>();
    private readonly IWebhookDispatcher _webhookDispatcher = Substitute.For<IWebhookDispatcher>();
    private readonly RequestStateMachine _sut;

    public RequestStateMachineTests()
    {
        _sut = new RequestStateMachine(_auditLogger, _eventPublisher, _webhookDispatcher);
    }

    private static ServiceRequest CreateRequest(RequestStatus status = RequestStatus.Draft) =>
        new()
        {
            Id = Guid.NewGuid(),
            CorrelationId = Guid.NewGuid(),
            CatalogItemId = Guid.NewGuid(),
            RequesterId = "user1",
            RequesterName = "Test User",
            Status = status
        };

    [Theory]
    [InlineData(RequestStatus.Draft, RequestStatus.Validating)]
    [InlineData(RequestStatus.Validating, RequestStatus.AwaitingApproval)]
    [InlineData(RequestStatus.Validating, RequestStatus.Executing)]
    [InlineData(RequestStatus.Validating, RequestStatus.ValidationFailed)]
    [InlineData(RequestStatus.ValidationFailed, RequestStatus.Draft)]
    [InlineData(RequestStatus.AwaitingApproval, RequestStatus.Executing)]
    [InlineData(RequestStatus.AwaitingApproval, RequestStatus.Rejected)]
    [InlineData(RequestStatus.AwaitingApproval, RequestStatus.ChangesRequested)]
    [InlineData(RequestStatus.AwaitingApproval, RequestStatus.TimedOut)]
    [InlineData(RequestStatus.ChangesRequested, RequestStatus.Draft)]
    [InlineData(RequestStatus.Executing, RequestStatus.Completed)]
    [InlineData(RequestStatus.Executing, RequestStatus.Failed)]
    [InlineData(RequestStatus.Failed, RequestStatus.Retrying)]
    [InlineData(RequestStatus.Failed, RequestStatus.ManuallyResolved)]
    [InlineData(RequestStatus.Retrying, RequestStatus.Executing)]
    public async Task TransitionTo_ValidTransition_UpdatesStatusAndAudits(RequestStatus from, RequestStatus to)
    {
        var request = CreateRequest(from);

        await _sut.TransitionTo(request, to, "actor1", "Actor", "user");

        Assert.Equal(to, request.Status);
        await _auditLogger.Received(1).Log(
            Arg.Is("requests"),
            Arg.Any<string>(),
            Arg.Is("actor1"),
            Arg.Is("Actor"),
            Arg.Is("user"),
            Arg.Is("ServiceRequest"),
            Arg.Is(request.Id),
            Arg.Any<object>(),
            Arg.Any<object>(),
            Arg.Any<object>());
    }

    [Theory]
    [InlineData(RequestStatus.Draft, RequestStatus.Completed)]
    [InlineData(RequestStatus.Draft, RequestStatus.Executing)]
    [InlineData(RequestStatus.Draft, RequestStatus.Rejected)]
    [InlineData(RequestStatus.Validating, RequestStatus.Draft)]
    [InlineData(RequestStatus.Completed, RequestStatus.Draft)]
    [InlineData(RequestStatus.Completed, RequestStatus.Executing)]
    [InlineData(RequestStatus.Rejected, RequestStatus.Draft)]
    [InlineData(RequestStatus.Rejected, RequestStatus.Executing)]
    [InlineData(RequestStatus.TimedOut, RequestStatus.Draft)]
    [InlineData(RequestStatus.ManuallyResolved, RequestStatus.Executing)]
    public async Task TransitionTo_InvalidTransition_Throws(RequestStatus from, RequestStatus to)
    {
        var request = CreateRequest(from);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _sut.TransitionTo(request, to, "actor1", "Actor", "user"));

        Assert.Equal(from, request.Status);
    }

    [Theory]
    [InlineData(RequestStatus.Completed)]
    [InlineData(RequestStatus.Rejected)]
    [InlineData(RequestStatus.ManuallyResolved)]
    [InlineData(RequestStatus.TimedOut)]
    public void IsTerminal_TerminalStates_ReturnsTrue(RequestStatus status)
    {
        Assert.True(RequestStateMachine.IsTerminal(status));
    }

    [Theory]
    [InlineData(RequestStatus.Draft)]
    [InlineData(RequestStatus.Validating)]
    [InlineData(RequestStatus.Executing)]
    [InlineData(RequestStatus.AwaitingApproval)]
    public void IsTerminal_NonTerminalStates_ReturnsFalse(RequestStatus status)
    {
        Assert.False(RequestStateMachine.IsTerminal(status));
    }

    [Fact]
    public void CanTransitionTo_ValidPair_ReturnsTrue()
    {
        Assert.True(RequestStateMachine.CanTransitionTo(RequestStatus.Draft, RequestStatus.Validating));
    }

    [Fact]
    public void CanTransitionTo_InvalidPair_ReturnsFalse()
    {
        Assert.False(RequestStateMachine.CanTransitionTo(RequestStatus.Draft, RequestStatus.Completed));
    }
}
