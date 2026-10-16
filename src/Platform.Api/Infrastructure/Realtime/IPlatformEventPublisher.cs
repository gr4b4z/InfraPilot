using Microsoft.AspNetCore.SignalR;

namespace Platform.Api.Infrastructure.Realtime;

public interface IPlatformEventPublisher
{
    Task PublishRequestStatusChanged(Guid requestId, string serviceName, string oldStatus, string newStatus, string actorName);
    Task PublishApprovalDecision(Guid requestId, string serviceName, string decision, string approverName, string? comment);

    /// <summary>
    /// Broadcast a "this entity changed" signal so open pages can refresh the lists and details
    /// showing it. Fire-and-forget semantics: implementations must not throw into callers whose
    /// state change has already been persisted.
    /// </summary>
    Task PublishEntityChanged(EntityChangedEvent evt);
}

/// <summary>
/// Publishes over the SignalR <see cref="EventsHub"/>. Two message kinds: "notification" carries a
/// human-readable message the chat sidebar surfaces as-is; "entityChanged" is the machine-readable
/// refresh signal pages subscribe to.
/// </summary>
public class SignalRPlatformEventPublisher : IPlatformEventPublisher
{
    private readonly IHubContext<EventsHub> _hub;
    private readonly ILogger<SignalRPlatformEventPublisher> _logger;

    public SignalRPlatformEventPublisher(IHubContext<EventsHub> hub, ILogger<SignalRPlatformEventPublisher> logger)
    {
        _hub = hub;
        _logger = logger;
    }

    public async Task PublishRequestStatusChanged(Guid requestId, string serviceName, string oldStatus, string newStatus, string actorName)
    {
        await SendSafe("notification", new PlatformEvent
        {
            Type = "request-status-changed",
            RequestId = requestId.ToString(),
            ServiceName = serviceName,
            OldStatus = oldStatus,
            NewStatus = newStatus,
            ActorName = actorName,
            Message = $"{serviceName} request moved from {oldStatus} to {newStatus}",
        });
    }

    public async Task PublishApprovalDecision(Guid requestId, string serviceName, string decision, string approverName, string? comment)
    {
        await SendSafe("notification", new PlatformEvent
        {
            Type = "approval-decision",
            RequestId = requestId.ToString(),
            ServiceName = serviceName,
            NewStatus = decision,
            ActorName = approverName,
            Message = $"{approverName} {decision.ToLowerInvariant()} the {serviceName} request" + (comment != null ? $": {comment}" : ""),
        });
    }

    public async Task PublishEntityChanged(EntityChangedEvent evt)
    {
        await SendSafe("entityChanged", evt);
    }

    private async Task SendSafe(string method, object payload)
    {
        try
        {
            await _hub.Clients.All.SendAsync(method, payload);
        }
        catch (Exception ex)
        {
            // The mutation this event describes is already persisted — a broadcast failure must
            // never surface as a request failure. Clients recover on their next reconnect refresh.
            _logger.LogWarning(ex, "Realtime broadcast '{Method}' failed", method);
        }
    }
}

public class PlatformEvent
{
    public string Type { get; set; } = "";
    public string? RequestId { get; set; }
    public string? ServiceName { get; set; }
    public string? OldStatus { get; set; }
    public string? NewStatus { get; set; }
    public string? ActorName { get; set; }
    public string? Message { get; set; }
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;
}
