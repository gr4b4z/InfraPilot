namespace Platform.Api.Features.Webhooks.Models;

public class WebhookDelivery
{
    public Guid Id { get; set; }
    public Guid SubscriptionId { get; set; }
    public string EventType { get; set; } = "";
    /// <summary>Full JSON payload envelope.</summary>
    public string PayloadJson { get; set; } = "{}";
    /// <summary>pending | delivered | failed</summary>
    public string Status { get; set; } = "pending";
    public int Attempts { get; set; }
    public int? HttpStatus { get; set; }
    public string? ResponseBody { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? DeliveredAt { get; set; }
    public DateTimeOffset NextRetryAt { get; set; } = DateTimeOffset.UtcNow;

    public WebhookSubscription? Subscription { get; set; }
}
