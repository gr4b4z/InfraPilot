using Microsoft.EntityFrameworkCore;
using Platform.Api.Features.Requests.Models;
using Platform.Api.Infrastructure.Auth;
using Platform.Api.Infrastructure.Persistence;

namespace Platform.Api.Features.Requests;

public class RetryHandler
{
    private readonly PlatformDbContext _db;
    private readonly RequestStateMachine _stateMachine;
    private readonly ICurrentUser _currentUser;
    private readonly ILogger<RetryHandler> _logger;

    public RetryHandler(
        PlatformDbContext db,
        RequestStateMachine stateMachine,
        ICurrentUser currentUser,
        ILogger<RetryHandler> logger)
    {
        _db = db;
        _stateMachine = stateMachine;
        _currentUser = currentUser;
        _logger = logger;
    }

    public async Task RetryExecution(Guid requestId, CancellationToken ct)
    {
        var request = await _db.ServiceRequests
            .Include(r => r.CatalogItem)
            .FirstOrDefaultAsync(r => r.Id == requestId, ct);

        if (request is null)
            throw new InvalidOperationException($"ServiceRequest {requestId} not found.");

        if (request.Status != RequestStatus.Failed)
            throw new InvalidOperationException(
                $"Cannot retry request {requestId}: current status is {request.Status}, expected Failed.");

        // Transition: Failed -> Retrying -> Executing
        // The ExecutorWorkerService will pick it up and dispatch the executor
        await _stateMachine.TransitionTo(
            request, RequestStatus.Retrying,
            _currentUser.Id, _currentUser.Name, "user");

        await _stateMachine.TransitionTo(
            request, RequestStatus.Executing,
            "system", "System", "system");

        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Request {RequestId} queued for retry execution", requestId);
    }
}
