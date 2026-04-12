namespace Platform.Api.Features.Webhooks;

public interface IWebhookDispatcher
{
    /// <summary>
    /// Queue webhook deliveries for all matching subscriptions.
    /// Returns immediately — actual delivery happens in background.
    /// </summary>
    Task DispatchAsync(string eventType, object payload, WebhookEventFilters? filters = null);
}

public record WebhookEventFilters(string? Product = null, string? Environment = null);
