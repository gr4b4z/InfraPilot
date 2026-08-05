using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Platform.Api.Features.Webhooks.Models;
using Platform.Api.Infrastructure.Auth;
using Platform.Api.Infrastructure.Persistence;

namespace Platform.Api.Features.Webhooks;

public static class WebhookEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static RouteGroupBuilder MapWebhookEndpoints(this RouteGroupBuilder group)
    {
        group.MapPost("/", CreateSubscription);
        group.MapGet("/", ListSubscriptions);
        group.MapGet("/{id:guid}", GetSubscription);
        group.MapPut("/{id:guid}", UpdateSubscription);
        group.MapDelete("/{id:guid}", DeleteSubscription);
        group.MapGet("/{id:guid}/deliveries", GetDeliveries);
        group.MapPost("/deliveries/{id:guid}/retry", RetryDelivery);
        group.MapPost("/{id:guid}/test", TestSubscription);

        // ── Delivery maintenance (Settings → Maintenance) ────────────────────
        // Bulk counterparts to the per-delivery retry above, plus retention. The per-row button is
        // fine for one flaky call; after a receiver outage there are hundreds of exhausted rows and
        // nothing else re-queues them — and delivered/failed rows otherwise accumulate forever.
        group.MapGet("/maintenance/deliveries", async (
            PlatformDbContext db, int? olderThanDays, CancellationToken ct) =>
        {
            if (olderThanDays is < 1 or > 3650)
                return Results.BadRequest(new { error = "olderThanDays must be between 1 and 3650" });
            var stats = await GetDeliveryMaintenanceStatsAsync(db, olderThanDays ?? 30, ct);
            return Results.Ok(stats);
        });

        group.MapPost("/maintenance/deliveries/retry-failed", async (
            PlatformDbContext db, CancellationToken ct) =>
        {
            var retried = await RetryAllFailedDeliveriesAsync(db, ct);
            return Results.Ok(new { retried });
        });

        group.MapDelete("/maintenance/deliveries", async (
            PlatformDbContext db, int? olderThanDays, CancellationToken ct) =>
        {
            if (olderThanDays is null or < 1 or > 3650)
                return Results.BadRequest(new { error = "olderThanDays is required (1–3650)" });
            var removed = await PurgeSettledDeliveriesAsync(db, olderThanDays.Value, ct);
            return Results.Ok(new { removed });
        });

        return group;
    }

    // ── Delivery maintenance internals ──────────────────────────────────────
    // Static and endpoint-independent so the rules are unit-testable without HTTP plumbing.

    public record DeliveryMaintenanceStats(int Failed, int Purgeable, DateTimeOffset? OldestFailedAt);

    public static async Task<DeliveryMaintenanceStats> GetDeliveryMaintenanceStatsAsync(
        PlatformDbContext db, int olderThanDays, CancellationToken ct = default)
    {
        var cutoff = DateTimeOffset.UtcNow.AddDays(-olderThanDays);
        var failed = await db.WebhookDeliveries.CountAsync(d => d.Status == "failed", ct);
        var purgeable = await db.WebhookDeliveries
            .CountAsync(d => d.Status != "pending" && d.CreatedAt < cutoff, ct);
        var oldestFailed = await db.WebhookDeliveries
            .Where(d => d.Status == "failed")
            .OrderBy(d => d.CreatedAt)
            .Select(d => (DateTimeOffset?)d.CreatedAt)
            .FirstOrDefaultAsync(ct);
        return new DeliveryMaintenanceStats(failed, purgeable, oldestFailed);
    }

    /// <summary>
    /// Re-queues every failed delivery, the same reset the per-delivery retry performs: back to
    /// pending, attempts zeroed, eligible for the worker immediately. Payloads are kept verbatim —
    /// a retry re-sends what the receiver was owed, it does not rebuild it.
    /// </summary>
    public static async Task<int> RetryAllFailedDeliveriesAsync(
        PlatformDbContext db, CancellationToken ct = default)
    {
        var failed = await db.WebhookDeliveries.Where(d => d.Status == "failed").ToListAsync(ct);
        foreach (var delivery in failed)
        {
            delivery.Status = "pending";
            delivery.NextRetryAt = DateTimeOffset.UtcNow;
            delivery.Attempts = 0;
            delivery.ErrorMessage = null;
        }
        if (failed.Count > 0) await db.SaveChangesAsync(ct);
        return failed.Count;
    }

    /// <summary>
    /// Deletes settled (delivered or failed) delivery rows older than the cutoff. Pending rows are
    /// never touched, whatever their age — they are still owed to a receiver, and deleting one
    /// silently drops an event a subscriber asked for.
    /// </summary>
    public static async Task<int> PurgeSettledDeliveriesAsync(
        PlatformDbContext db, int olderThanDays, CancellationToken ct = default)
    {
        var cutoff = DateTimeOffset.UtcNow.AddDays(-olderThanDays);
        var stale = await db.WebhookDeliveries
            .Where(d => d.Status != "pending" && d.CreatedAt < cutoff)
            .ToListAsync(ct);
        if (stale.Count == 0) return 0;
        db.WebhookDeliveries.RemoveRange(stale);
        await db.SaveChangesAsync(ct);
        return stale.Count;
    }

    private static async Task<IResult> CreateSubscription(
        PlatformDbContext db,
        IDataProtectionProvider dataProtection,
        CreateWebhookRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.Url))
            return Results.BadRequest(new { error = "Name and URL are required" });
        if (request.Events is null || request.Events.Length == 0)
            return Results.BadRequest(new { error = "At least one event type is required" });

        var rawSecret = GenerateSecret();
        var protector = dataProtection.CreateProtector("WebhookSecrets");

        var sub = new WebhookSubscription
        {
            Id = Guid.NewGuid(),
            Name = request.Name,
            Url = request.Url,
            EncryptedSecret = protector.Protect(rawSecret),
            EventsJson = JsonSerializer.Serialize(request.Events),
            FilterProduct = request.Filters?.Product,
            FilterEnvironment = request.Filters?.Environment,
            Active = true,
        };

        db.WebhookSubscriptions.Add(sub);
        await db.SaveChangesAsync();

        return Results.Created($"/api/webhooks/{sub.Id}", new
        {
            sub.Id,
            sub.Name,
            sub.Url,
            secret = rawSecret, // shown only once
            events = request.Events,
            filters = new { product = sub.FilterProduct, environment = sub.FilterEnvironment },
            sub.Active,
            sub.CreatedAt,
        });
    }

    private static async Task<IResult> ListSubscriptions(PlatformDbContext db)
    {
        var subs = await db.WebhookSubscriptions
            .OrderByDescending(s => s.CreatedAt)
            .Select(s => new
            {
                s.Id,
                s.Name,
                s.Url,
                events = s.EventsJson,
                filters = new { product = s.FilterProduct, environment = s.FilterEnvironment },
                s.Active,
                s.CreatedAt,
                s.UpdatedAt,
                deliveryStats = new
                {
                    total = s.Deliveries.Count,
                    delivered = s.Deliveries.Count(d => d.Status == "delivered"),
                    failed = s.Deliveries.Count(d => d.Status == "failed"),
                    pending = s.Deliveries.Count(d => d.Status == "pending"),
                    lastDeliveryAt = s.Deliveries
                        .Where(d => d.DeliveredAt != null)
                        .OrderByDescending(d => d.DeliveredAt)
                        .Select(d => (DateTimeOffset?)d.DeliveredAt)
                        .FirstOrDefault(),
                    lastStatus = s.Deliveries
                        .OrderByDescending(d => d.CreatedAt)
                        .Select(d => d.Status)
                        .FirstOrDefault(),
                },
            })
            .ToListAsync();

        // Parse events JSON for cleaner output
        var result = subs.Select(s => new
        {
            s.Id,
            s.Name,
            s.Url,
            events = JsonSerializer.Deserialize<string[]>(s.events) ?? [],
            s.filters,
            s.Active,
            s.CreatedAt,
            s.UpdatedAt,
            s.deliveryStats,
        });

        return Results.Ok(result);
    }

    private static async Task<IResult> GetSubscription(PlatformDbContext db, Guid id)
    {
        var sub = await db.WebhookSubscriptions
            .FirstOrDefaultAsync(s => s.Id == id);

        if (sub is null) return Results.NotFound();

        var recentDeliveries = await db.WebhookDeliveries
            .Where(d => d.SubscriptionId == id)
            .OrderByDescending(d => d.CreatedAt)
            .Take(50)
            .Select(d => new
            {
                d.Id,
                d.EventType,
                d.Status,
                d.Attempts,
                d.HttpStatus,
                d.ResponseBody,
                d.ErrorMessage,
                d.CreatedAt,
                d.DeliveredAt,
                d.NextRetryAt,
            })
            .ToListAsync();

        return Results.Ok(new
        {
            sub.Id,
            sub.Name,
            sub.Url,
            events = JsonSerializer.Deserialize<string[]>(sub.EventsJson) ?? [],
            filters = new { product = sub.FilterProduct, environment = sub.FilterEnvironment },
            sub.Active,
            sub.CreatedAt,
            sub.UpdatedAt,
            recentDeliveries,
        });
    }

    private static async Task<IResult> UpdateSubscription(
        PlatformDbContext db, Guid id, UpdateWebhookRequest request)
    {
        var sub = await db.WebhookSubscriptions.FindAsync(id);
        if (sub is null) return Results.NotFound();

        if (request.Name is not null) sub.Name = request.Name;
        if (request.Url is not null) sub.Url = request.Url;
        if (request.Events is not null) sub.EventsJson = JsonSerializer.Serialize(request.Events);
        if (request.Filters is not null)
        {
            sub.FilterProduct = request.Filters.Product;
            sub.FilterEnvironment = request.Filters.Environment;
        }
        if (request.Active.HasValue) sub.Active = request.Active.Value;
        sub.UpdatedAt = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync();

        return Results.Ok(new
        {
            sub.Id,
            sub.Name,
            sub.Url,
            events = JsonSerializer.Deserialize<string[]>(sub.EventsJson) ?? [],
            filters = new { product = sub.FilterProduct, environment = sub.FilterEnvironment },
            sub.Active,
            sub.UpdatedAt,
        });
    }

    private static async Task<IResult> DeleteSubscription(PlatformDbContext db, Guid id)
    {
        var sub = await db.WebhookSubscriptions
            .Include(s => s.Deliveries)
            .FirstOrDefaultAsync(s => s.Id == id);
        if (sub is null) return Results.NotFound();

        db.WebhookDeliveries.RemoveRange(sub.Deliveries);
        db.WebhookSubscriptions.Remove(sub);
        await db.SaveChangesAsync();

        return Results.NoContent();
    }

    private static async Task<IResult> GetDeliveries(
        PlatformDbContext db, Guid id, int? limit, int? offset)
    {
        var exists = await db.WebhookSubscriptions.AnyAsync(s => s.Id == id);
        if (!exists) return Results.NotFound();

        var query = db.WebhookDeliveries
            .Where(d => d.SubscriptionId == id)
            .OrderByDescending(d => d.CreatedAt);

        var total = await query.CountAsync();
        var items = await query
            .Skip(offset ?? 0)
            .Take(limit ?? 50)
            .Select(d => new
            {
                d.Id,
                d.EventType,
                d.Status,
                d.Attempts,
                d.HttpStatus,
                d.ResponseBody,
                d.ErrorMessage,
                d.PayloadJson,
                d.CreatedAt,
                d.DeliveredAt,
                d.NextRetryAt,
            })
            .ToListAsync();

        return Results.Ok(new { items, total });
    }

    private static async Task<IResult> RetryDelivery(PlatformDbContext db, Guid id)
    {
        var delivery = await db.WebhookDeliveries.FindAsync(id);
        if (delivery is null) return Results.NotFound();
        if (delivery.Status != "failed")
            return Results.BadRequest(new { error = "Only failed deliveries can be retried" });

        delivery.Status = "pending";
        delivery.NextRetryAt = DateTimeOffset.UtcNow;
        delivery.Attempts = 0;
        delivery.ErrorMessage = null;
        await db.SaveChangesAsync();

        return Results.Ok(new { message = "Delivery queued for retry" });
    }

    private static async Task<IResult> TestSubscription(
        PlatformDbContext db, IWebhookDispatcher dispatcher, Guid id)
    {
        var sub = await db.WebhookSubscriptions.FindAsync(id);
        if (sub is null) return Results.NotFound();

        // Create a ping delivery directly for this subscription
        var deliveryId = Guid.NewGuid();
        var envelope = new
        {
            id = deliveryId,
            eventType = "ping",
            timestamp = DateTimeOffset.UtcNow,
            data = new { message = "Test webhook delivery", subscriptionId = id },
        };

        var delivery = new WebhookDelivery
        {
            Id = deliveryId,
            SubscriptionId = sub.Id,
            EventType = "ping",
            PayloadJson = JsonSerializer.Serialize(envelope, JsonOptions),
            Status = "pending",
            NextRetryAt = DateTimeOffset.UtcNow,
        };

        db.WebhookDeliveries.Add(delivery);
        await db.SaveChangesAsync();

        return Results.Ok(new { message = "Test delivery queued", deliveryId });
    }

    private static string GenerateSecret()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return $"whsec_{Convert.ToBase64String(bytes).TrimEnd('=')}";
    }
}

// ── Request DTOs ──

public record CreateWebhookRequest(
    string Name,
    string Url,
    string[] Events,
    WebhookFilterDto? Filters = null);

public record UpdateWebhookRequest(
    string? Name = null,
    string? Url = null,
    string[]? Events = null,
    WebhookFilterDto? Filters = null,
    bool? Active = null);

public record WebhookFilterDto(string? Product = null, string? Environment = null);
