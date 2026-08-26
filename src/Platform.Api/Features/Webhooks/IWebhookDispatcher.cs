namespace Platform.Api.Features.Webhooks;

public interface IWebhookDispatcher
{
    /// <summary>
    /// Queue webhook deliveries for all matching subscriptions.
    /// Returns immediately — actual delivery happens in background.
    /// </summary>
    Task DispatchAsync(
        string eventType, object payload, WebhookEventFilters? filters = null,
        WebhookDispatchOptions? options = null);

    /// <summary>
    /// Drops every still-unsent delivery queued under <paramref name="cancelKey"/> — the escape hatch
    /// for an event the platform announced and then took back inside its
    /// <see cref="WebhookDispatchOptions.Delay"/> window. Returns how many rows were stopped, so the
    /// caller can tell the user whether the news got out.
    /// </summary>
    Task<int> CancelPendingAsync(string cancelKey, CancellationToken ct = default);
}

public record WebhookEventFilters(string? Product = null, string? Environment = null);

/// <summary>
/// Per-dispatch delivery controls.
/// </summary>
/// <param name="Delay">
/// How long to hold the delivery before the worker may send it. A grace period for events the user
/// can still retract — see <c>PromotionService.ApprovedWebhookDelay</c>.
/// </param>
/// <param name="CancelKey">
/// Correlation handle for <see cref="IWebhookDispatcher.CancelPendingAsync"/>. Stamped on every row
/// this dispatch queues, across all matching subscriptions, so one cancel stops all of them.
/// </param>
public record WebhookDispatchOptions(TimeSpan? Delay = null, string? CancelKey = null);
