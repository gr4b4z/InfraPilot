namespace Platform.Api.Features.Webhooks.Models;

public class WebhookSubscription
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
    /// <summary>Secret encrypted via Data Protection API, used for HMAC-SHA256 signing.</summary>
    public string EncryptedSecret { get; set; } = "";
    /// <summary>Event types this subscription listens to (JSON array stored as text).</summary>
    public string EventsJson { get; set; } = "[]";
    /// <summary>Optional product filter for deployment events.</summary>
    public string? FilterProduct { get; set; }
    /// <summary>Optional environment filter for deployment events.</summary>
    public string? FilterEnvironment { get; set; }
    public bool Active { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public List<WebhookDelivery> Deliveries { get; set; } = [];
}
