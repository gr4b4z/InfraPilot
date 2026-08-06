namespace Platform.Api.Features.Webhooks.Models;

public class WebhookDelivery
{
    public Guid Id { get; set; }
    public Guid SubscriptionId { get; set; }
    public string EventType { get; set; } = "";
    /// <summary>Full JSON payload envelope.</summary>
    public string PayloadJson { get; set; } = "{}";
    /// <summary>pending | delivered | failed | cancelled</summary>
    public string Status { get; set; } = "pending";
    public int Attempts { get; set; }
    public int? HttpStatus { get; set; }
    public string? ResponseBody { get; set; }
    public string? ErrorMessage { get; set; }
    /// <summary>
    /// Correlation handle set by the dispatcher when the caller passes a
    /// <see cref="WebhookDispatchOptions.CancelKey"/>. Lets a source event that is later retracted
    /// (a cancelled promotion approval) find and stop its own not-yet-sent rows without matching on
    /// payload contents. Null for the vast majority of deliveries, which are never retractable.
    /// </summary>
    public string? CancelKey { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? DeliveredAt { get; set; }
    /// <summary>
    /// When the worker may next pick this row up. Doubles as the initial send time: a dispatch with
    /// a <see cref="WebhookDispatchOptions.Delay"/> lands here in the future, so the delay costs no
    /// scheduling machinery of its own and survives a restart.
    /// </summary>
    public DateTimeOffset NextRetryAt { get; set; } = DateTimeOffset.UtcNow;

    public WebhookSubscription? Subscription { get; set; }
}
