using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Platform.Api.Features.Webhooks.Models;
using Platform.Api.Infrastructure.Persistence;

namespace Platform.Api.Features.Webhooks;

public class WebhookDispatcher : IWebhookDispatcher
{
    private readonly PlatformDbContext _db;
    private readonly ILogger<WebhookDispatcher> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public WebhookDispatcher(PlatformDbContext db, ILogger<WebhookDispatcher> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task DispatchAsync(string eventType, object payload, WebhookEventFilters? filters = null)
    {
        var subscriptions = await _db.WebhookSubscriptions
            .Where(s => s.Active)
            .ToListAsync();

        var matching = subscriptions.Where(s =>
        {
            // Check event type match
            var events = JsonSerializer.Deserialize<List<string>>(s.EventsJson) ?? [];
            if (!events.Contains(eventType)) return false;

            // Apply product/environment filters only when the event carries them
            if (filters is not null)
            {
                if (!string.IsNullOrEmpty(s.FilterProduct) && s.FilterProduct != filters.Product)
                    return false;
                if (!string.IsNullOrEmpty(s.FilterEnvironment) && s.FilterEnvironment != filters.Environment)
                    return false;
            }

            return true;
        }).ToList();

        if (matching.Count == 0) return;

        foreach (var sub in matching)
        {
            var deliveryId = Guid.NewGuid();
            var envelope = new
            {
                id = deliveryId,
                eventType,
                timestamp = DateTimeOffset.UtcNow,
                data = payload,
            };

            var delivery = new WebhookDelivery
            {
                Id = deliveryId,
                SubscriptionId = sub.Id,
                EventType = eventType,
                PayloadJson = JsonSerializer.Serialize(envelope, JsonOptions),
                Status = "pending",
                Attempts = 0,
                NextRetryAt = DateTimeOffset.UtcNow,
            };

            _db.WebhookDeliveries.Add(delivery);
        }

        await _db.SaveChangesAsync();
        _logger.LogInformation("Queued {Count} webhook deliveries for event {EventType}", matching.Count, eventType);
    }
}
