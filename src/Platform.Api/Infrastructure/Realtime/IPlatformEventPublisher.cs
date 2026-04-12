namespace Platform.Api.Infrastructure.Realtime;

public interface IPlatformEventPublisher
{
    Task PublishRequestStatusChanged(Guid requestId, string serviceName, string oldStatus, string newStatus, string actorName);
    Task PublishApprovalDecision(Guid requestId, string serviceName, string decision, string approverName, string? comment);
}

public class SsePlatformEventPublisher : IPlatformEventPublisher
{
    private readonly SseConnectionManager _connectionManager;

    public SsePlatformEventPublisher(SseConnectionManager connectionManager)
    {
        _connectionManager = connectionManager;
    }

    public async Task PublishRequestStatusChanged(Guid requestId, string serviceName, string oldStatus, string newStatus, string actorName)
    {
        var evt = new PlatformEvent
        {
            Type = "request-status-changed",
            RequestId = requestId.ToString(),
            ServiceName = serviceName,
            OldStatus = oldStatus,
            NewStatus = newStatus,
            ActorName = actorName,
            Message = $"{serviceName} request moved from {oldStatus} to {newStatus}",
        };

        await _connectionManager.BroadcastEvent(evt);
    }

    public async Task PublishApprovalDecision(Guid requestId, string serviceName, string decision, string approverName, string? comment)
    {
        var evt = new PlatformEvent
        {
            Type = "approval-decision",
            RequestId = requestId.ToString(),
            ServiceName = serviceName,
            NewStatus = decision,
            ActorName = approverName,
            Message = $"{approverName} {decision.ToLowerInvariant()} the {serviceName} request" + (comment != null ? $": {comment}" : ""),
        };

        await _connectionManager.BroadcastEvent(evt);
    }
}
