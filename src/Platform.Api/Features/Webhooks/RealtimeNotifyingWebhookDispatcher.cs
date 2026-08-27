using System.Text.Json;
using Platform.Api.Infrastructure.Realtime;

namespace Platform.Api.Features.Webhooks;

/// <summary>
/// Decorates the real dispatcher so every webhook event also reaches connected browsers as an
/// entity-changed broadcast. The webhook call sites are the one place every mutating subsystem
/// already announces "something happened" after saving, so piggybacking here gives the UI live
/// coverage of promotions, rollbacks, requests, approvals, deployments, work-item sign-offs and
/// release notes without touching each service.
///
/// Broadcasting happens before delegating: the inner dispatcher returns early when no webhook
/// subscription matches an event, and the UI must refresh regardless of whether anyone subscribed.
/// </summary>
public class RealtimeNotifyingWebhookDispatcher : IWebhookDispatcher
{
    private readonly WebhookDispatcher _inner;
    private readonly IPlatformEventPublisher _events;
    private readonly ILogger<RealtimeNotifyingWebhookDispatcher> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public RealtimeNotifyingWebhookDispatcher(
        WebhookDispatcher inner,
        IPlatformEventPublisher events,
        ILogger<RealtimeNotifyingWebhookDispatcher> logger)
    {
        _inner = inner;
        _events = events;
        _logger = logger;
    }

    public async Task<int> DispatchAsync(
        string eventType, object payload, WebhookEventFilters? filters = null,
        WebhookDispatchOptions? options = null)
    {
        try
        {
            foreach (var evt in MapToEntityEvents(eventType, payload, filters))
                await _events.PublishEntityChanged(evt);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Realtime broadcast for '{EventType}' failed", eventType);
        }

        // The realtime broadcast is NOT delayed even when the delivery is: the browser is showing
        // the state the transition just wrote, and holding the UI back for a webhook grace period
        // would make the page lie about what the database says.
        return await _inner.DispatchAsync(eventType, payload, filters, options);
    }

    public Task<int> CancelPendingAsync(string cancelKey, CancellationToken ct = default)
        => _inner.CancelPendingAsync(cancelKey, ct);

    /// <summary>
    /// Translates a webhook event name into the entity-changed signal(s) it implies. The payloads
    /// are shaped per external consumer, so identifiers are probed generically from the serialized
    /// form rather than binding to each shape.
    /// </summary>
    internal static IReadOnlyList<EntityChangedEvent> MapToEntityEvents(
        string eventType, object payload, WebhookEventFilters? filters)
    {
        if (eventType == "ping") return [];

        var doc = JsonSerializer.SerializeToElement(payload, JsonOptions);
        string? Prop(params string[] names)
        {
            if (doc.ValueKind != JsonValueKind.Object) return null;
            foreach (var name in names)
            {
                if (doc.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null)
                    return v.ValueKind == JsonValueKind.String ? v.GetString() : v.GetRawText();
            }
            return null;
        }

        var product = filters?.Product ?? Prop("product");
        var environment = filters?.Environment ?? Prop("environment", "targetEnv");
        var id = Prop("id", "candidateId", "requestId", "approvalId", "rollbackId");
        var key = Prop("workItemKey");

        EntityChangedEvent Evt(string entity, string action) => new()
        {
            Entity = entity,
            Action = action,
            Id = id,
            Key = key,
            Product = product,
            Environment = environment,
        };

        const string ticketPrefix = "promotion.ticket.";
        if (eventType.StartsWith(ticketPrefix, StringComparison.Ordinal))
        {
            // A ticket decision changes the work item itself and the readiness of any promotion
            // carrying it, so both entity streams fire.
            return [Evt("work-item", eventType[ticketPrefix.Length..]), Evt("promotion", "updated")];
        }

        var dot = eventType.IndexOf('.');
        if (dot <= 0) return [];
        var suffix = eventType[(dot + 1)..];

        return eventType[..dot] switch
        {
            "promotion" => [Evt("promotion", suffix)],
            "deployment" => [Evt("deployment", suffix)],
            "request" => [Evt("request", suffix)],
            "approval" => [Evt("approval", suffix)],
            "rollback" => [Evt("rollback", suffix)],
            "release_note" => [Evt("release-note", "created")],
            _ => [],
        };
    }
}
