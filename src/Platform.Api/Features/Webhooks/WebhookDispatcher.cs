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

    public async Task<int> DispatchAsync(
        string eventType, object payload, WebhookEventFilters? filters = null,
        WebhookDispatchOptions? options = null)
    {
        var subscriptions = await _db.WebhookSubscriptions
            .Where(s => s.Active)
            .ToListAsync();

        var matching = subscriptions.Where(s =>
        {
            // Check event type match
            var events = JsonSerializer.Deserialize<List<string>>(s.EventsJson) ?? [];
            if (!events.Contains(eventType)) return false;

            // Product / service / environment filters, each a set and each applied only when the
            // event carries that dimension.
            return WebhookSubscriptionFilters.Matches(s, filters);
        }).ToList();

        if (matching.Count == 0) return 0;

        // A delay is expressed as a future NextRetryAt — the worker already refuses to touch a row
        // before that moment, so the hold needs no timer, no in-memory state, and survives a restart.
        var sendAt = DateTimeOffset.UtcNow + (options?.Delay ?? TimeSpan.Zero);

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
                CancelKey = options?.CancelKey,
                NextRetryAt = sendAt,
            };

            _db.WebhookDeliveries.Add(delivery);
        }

        await _db.SaveChangesAsync();
        if (options?.Delay is { } delay && delay > TimeSpan.Zero)
        {
            _logger.LogInformation(
                "Queued {Count} webhook deliveries for event {EventType}, held until {SendAt:o}",
                matching.Count, eventType, sendAt);
        }
        else
        {
            _logger.LogInformation("Queued {Count} webhook deliveries for event {EventType}", matching.Count, eventType);
        }

        return matching.Count;
    }

    /// <summary>
    /// Cancels the deliveries queued under <paramref name="cancelKey"/> that have not gone out yet.
    /// Deliberately limited to rows with zero attempts: once a request has left the building the
    /// receiver has the news, and killing its retries would only strand a half-delivered event.
    /// </summary>
    public async Task<int> CancelPendingAsync(string cancelKey, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(cancelKey)) return 0;

        var pending = await _db.WebhookDeliveries
            .Where(d => d.CancelKey == cancelKey && d.Status == "pending" && d.Attempts == 0)
            .ToListAsync(ct);
        if (pending.Count == 0) return 0;

        foreach (var delivery in pending)
        {
            delivery.Status = "cancelled";
            delivery.ErrorMessage = "Cancelled before delivery — the source event was retracted";
        }

        await _db.SaveChangesAsync(ct);
        _logger.LogInformation(
            "Cancelled {Count} undelivered webhook deliveries for key {CancelKey}", pending.Count, cancelKey);
        return pending.Count;
    }
}
