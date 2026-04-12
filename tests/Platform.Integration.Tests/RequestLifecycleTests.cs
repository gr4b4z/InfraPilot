using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Platform.Api.Features.Requests;
using Platform.Api.Features.Requests.Models;
using Platform.Api.Infrastructure.Audit;
using Platform.Api.Infrastructure.Persistence;
using Platform.Api.Infrastructure.Realtime;

namespace Platform.Integration.Tests;

public class RequestLifecycleTests : IDisposable
{
    private readonly PlatformDbContext _db;
    private readonly IAuditLogger _auditLogger = Substitute.For<IAuditLogger>();
    private readonly IPlatformEventPublisher _eventPublisher = Substitute.For<IPlatformEventPublisher>();
    private readonly RequestStateMachine _stateMachine;

    public RequestLifecycleTests()
    {
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _db = new PlatformDbContext(options);
        _stateMachine = new RequestStateMachine(_auditLogger, _eventPublisher);
    }

    public void Dispose()
    {
        _db.Dispose();
    }

    private ServiceRequest CreateDraftRequest()
    {
        var request = new ServiceRequest
        {
            Id = Guid.NewGuid(),
            CorrelationId = Guid.NewGuid(),
            CatalogItemId = Guid.NewGuid(),
            RequesterId = "user1",
            RequesterName = "Test User",
            Status = RequestStatus.Draft,
        };

        _db.ServiceRequests.Add(request);
        _db.SaveChanges();

        return request;
    }

    [Fact]
    public async Task FullLifecycle_DraftToCompleted_Succeeds()
    {
        var request = CreateDraftRequest();

        // Draft -> Validating
        await _stateMachine.TransitionTo(request, RequestStatus.Validating, "system", "System", "system");
        Assert.Equal(RequestStatus.Validating, request.Status);

        // Validating -> AwaitingApproval
        await _stateMachine.TransitionTo(request, RequestStatus.AwaitingApproval, "system", "System", "system");
        Assert.Equal(RequestStatus.AwaitingApproval, request.Status);

        // AwaitingApproval -> Executing
        await _stateMachine.TransitionTo(request, RequestStatus.Executing, "system", "System", "system");
        Assert.Equal(RequestStatus.Executing, request.Status);

        // Executing -> Completed
        await _stateMachine.TransitionTo(request, RequestStatus.Completed, "system", "System", "system");
        Assert.Equal(RequestStatus.Completed, request.Status);

        // Verify each transition was audited
        await _auditLogger.Received(4).Log(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<Guid?>(),
            Arg.Any<object>(),
            Arg.Any<object>(),
            Arg.Any<object>());
    }

    [Fact]
    public async Task FullLifecycle_SkipApproval_DraftToCompleted()
    {
        var request = CreateDraftRequest();

        // Draft -> Validating -> Executing (skip approval) -> Completed
        await _stateMachine.TransitionTo(request, RequestStatus.Validating, "system", "System", "system");
        await _stateMachine.TransitionTo(request, RequestStatus.Executing, "system", "System", "system");
        await _stateMachine.TransitionTo(request, RequestStatus.Completed, "system", "System", "system");

        Assert.Equal(RequestStatus.Completed, request.Status);
    }

    [Fact]
    public async Task FullLifecycle_RejectionPath()
    {
        var request = CreateDraftRequest();

        await _stateMachine.TransitionTo(request, RequestStatus.Validating, "system", "System", "system");
        await _stateMachine.TransitionTo(request, RequestStatus.AwaitingApproval, "system", "System", "system");
        await _stateMachine.TransitionTo(request, RequestStatus.Rejected, "system", "System", "system");

        Assert.Equal(RequestStatus.Rejected, request.Status);
        Assert.True(RequestStateMachine.IsTerminal(request.Status));
    }

    [Fact]
    public async Task FullLifecycle_ChangesRequestedResubmit()
    {
        var request = CreateDraftRequest();

        // Submit and get changes requested
        await _stateMachine.TransitionTo(request, RequestStatus.Validating, "system", "System", "system");
        await _stateMachine.TransitionTo(request, RequestStatus.AwaitingApproval, "system", "System", "system");
        await _stateMachine.TransitionTo(request, RequestStatus.ChangesRequested, "system", "System", "system");

        Assert.Equal(RequestStatus.ChangesRequested, request.Status);

        // Go back to draft and resubmit
        await _stateMachine.TransitionTo(request, RequestStatus.Draft, "system", "System", "system");
        await _stateMachine.TransitionTo(request, RequestStatus.Validating, "system", "System", "system");
        await _stateMachine.TransitionTo(request, RequestStatus.AwaitingApproval, "system", "System", "system");
        await _stateMachine.TransitionTo(request, RequestStatus.Executing, "system", "System", "system");
        await _stateMachine.TransitionTo(request, RequestStatus.Completed, "system", "System", "system");

        Assert.Equal(RequestStatus.Completed, request.Status);
    }

    [Fact]
    public async Task FullLifecycle_FailureAndRetry()
    {
        var request = CreateDraftRequest();

        await _stateMachine.TransitionTo(request, RequestStatus.Validating, "system", "System", "system");
        await _stateMachine.TransitionTo(request, RequestStatus.Executing, "system", "System", "system");
        await _stateMachine.TransitionTo(request, RequestStatus.Failed, "system", "System", "system");

        Assert.Equal(RequestStatus.Failed, request.Status);

        // Retry and succeed
        await _stateMachine.TransitionTo(request, RequestStatus.Retrying, "system", "System", "system");
        await _stateMachine.TransitionTo(request, RequestStatus.Executing, "system", "System", "system");
        await _stateMachine.TransitionTo(request, RequestStatus.Completed, "system", "System", "system");

        Assert.Equal(RequestStatus.Completed, request.Status);
    }

    [Fact]
    public async Task InvalidTransition_DraftToCompleted_Throws()
    {
        var request = CreateDraftRequest();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _stateMachine.TransitionTo(request, RequestStatus.Completed, "system", "System", "system"));

        Assert.Equal(RequestStatus.Draft, request.Status);
    }

    [Fact]
    public async Task InvalidTransition_DraftToExecuting_Throws()
    {
        var request = CreateDraftRequest();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _stateMachine.TransitionTo(request, RequestStatus.Executing, "system", "System", "system"));

        Assert.Equal(RequestStatus.Draft, request.Status);
    }

    [Fact]
    public async Task InvalidTransition_CompletedToAnything_Throws()
    {
        var request = CreateDraftRequest();

        // Get to completed state
        await _stateMachine.TransitionTo(request, RequestStatus.Validating, "system", "System", "system");
        await _stateMachine.TransitionTo(request, RequestStatus.Executing, "system", "System", "system");
        await _stateMachine.TransitionTo(request, RequestStatus.Completed, "system", "System", "system");

        // Terminal state: cannot transition anywhere
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _stateMachine.TransitionTo(request, RequestStatus.Draft, "system", "System", "system"));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _stateMachine.TransitionTo(request, RequestStatus.Executing, "system", "System", "system"));

        Assert.Equal(RequestStatus.Completed, request.Status);
    }

    [Fact]
    public async Task InvalidTransition_SkipValidating_Throws()
    {
        var request = CreateDraftRequest();

        // Cannot go directly from Draft to AwaitingApproval
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _stateMachine.TransitionTo(request, RequestStatus.AwaitingApproval, "system", "System", "system"));

        Assert.Equal(RequestStatus.Draft, request.Status);
    }

    [Fact]
    public async Task RequestPersistedInDatabase_AfterTransitions()
    {
        var request = CreateDraftRequest();

        await _stateMachine.TransitionTo(request, RequestStatus.Validating, "system", "System", "system");
        await _db.SaveChangesAsync();

        var loaded = await _db.ServiceRequests.FindAsync(request.Id);
        Assert.NotNull(loaded);
        Assert.Equal(RequestStatus.Validating, loaded!.Status);
    }
}
