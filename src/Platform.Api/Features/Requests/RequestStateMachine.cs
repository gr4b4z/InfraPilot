using Platform.Api.Features.Requests.Models;
using Platform.Api.Features.Webhooks;
using Platform.Api.Infrastructure.Audit;
using Platform.Api.Infrastructure.Realtime;

namespace Platform.Api.Features.Requests;

public class RequestStateMachine
{
    private static readonly Dictionary<RequestStatus, HashSet<RequestStatus>> ValidTransitions = new()
    {
        [RequestStatus.Draft] = [RequestStatus.Validating, RequestStatus.Cancelled],
        [RequestStatus.Validating] = [RequestStatus.AwaitingApproval, RequestStatus.Executing, RequestStatus.ValidationFailed, RequestStatus.Cancelled],
        [RequestStatus.ValidationFailed] = [RequestStatus.Draft, RequestStatus.Cancelled],
        [RequestStatus.AwaitingApproval] = [RequestStatus.Executing, RequestStatus.Rejected, RequestStatus.ChangesRequested, RequestStatus.TimedOut, RequestStatus.Cancelled],
        [RequestStatus.ChangesRequested] = [RequestStatus.Draft, RequestStatus.Cancelled],
        [RequestStatus.Rejected] = [],
        [RequestStatus.Executing] = [RequestStatus.Completed, RequestStatus.Failed, RequestStatus.Cancelled],
        [RequestStatus.Failed] = [RequestStatus.Retrying, RequestStatus.ManuallyResolved, RequestStatus.Cancelled],
        [RequestStatus.Retrying] = [RequestStatus.Executing, RequestStatus.Cancelled],
        [RequestStatus.Completed] = [],
        [RequestStatus.ManuallyResolved] = [],
        [RequestStatus.TimedOut] = [],
        [RequestStatus.Cancelled] = [],
    };

    private readonly IAuditLogger _auditLogger;
    private readonly IPlatformEventPublisher _eventPublisher;
    private readonly IWebhookDispatcher _webhookDispatcher;

    public RequestStateMachine(IAuditLogger auditLogger, IPlatformEventPublisher eventPublisher, IWebhookDispatcher webhookDispatcher)
    {
        _auditLogger = auditLogger;
        _eventPublisher = eventPublisher;
        _webhookDispatcher = webhookDispatcher;
    }

    public async Task TransitionTo(ServiceRequest request, RequestStatus newStatus, string actorId, string actorName, string actorType)
    {
        var currentStatus = request.Status;

        if (!ValidTransitions.TryGetValue(currentStatus, out var allowed) || !allowed.Contains(newStatus))
        {
            throw new InvalidOperationException(
                $"Invalid state transition: {currentStatus} -> {newStatus}");
        }

        var beforeState = new { Status = currentStatus.ToString() };
        request.Status = newStatus;
        request.UpdatedAt = DateTimeOffset.UtcNow;
        var afterState = new { Status = newStatus.ToString() };

        await _auditLogger.Log(
            module: "requests",
            action: $"request.{newStatus.ToString().ToLowerInvariant()}",
            actorId: actorId,
            actorName: actorName,
            actorType: actorType,
            entityType: "ServiceRequest",
            entityId: request.Id,
            beforeState: beforeState,
            afterState: afterState);

        // Publish SSE event
        var serviceName = request.CatalogItem?.Name ?? "Request";
        await _eventPublisher.PublishRequestStatusChanged(
            request.Id, serviceName, currentStatus.ToString(), newStatus.ToString(), actorName);

        await _webhookDispatcher.DispatchAsync("request.status_changed", new
        {
            RequestId = request.Id,
            CatalogItemId = request.CatalogItemId,
            PreviousStatus = currentStatus.ToString(),
            NewStatus = newStatus.ToString(),
            ActorName = actorName,
        });
    }

    public static bool CanTransitionTo(RequestStatus from, RequestStatus to)
    {
        return ValidTransitions.TryGetValue(from, out var allowed) && allowed.Contains(to);
    }

    public static IReadOnlySet<RequestStatus> GetAllowedTransitions(RequestStatus from)
    {
        return ValidTransitions.TryGetValue(from, out var allowed) ? allowed : new HashSet<RequestStatus>();
    }

    public static bool IsTerminal(RequestStatus status)
    {
        return ValidTransitions.TryGetValue(status, out var allowed) && allowed.Count == 0;
    }
}
