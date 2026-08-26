using Microsoft.EntityFrameworkCore;
using Platform.Api.Features.Webhooks;
using Platform.Api.Features.Webhooks.Models;
using Platform.Api.Infrastructure.Persistence;

namespace Platform.Api.Tests.Features.Webhooks;

/// <summary>
/// The bulk delivery-maintenance rules behind Settings → Maintenance: retry-all re-queues exactly the
/// failed set, and the purge deletes only settled rows past the cutoff. The invariant both must hold
/// is that a pending row is never touched — it is still owed to a receiver.
/// </summary>
public class WebhookDeliveryMaintenanceTests : IDisposable
{
    private readonly PlatformDbContext _db;

    public WebhookDeliveryMaintenanceTests()
    {
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _db = new PlatformDbContext(options);
    }

    public void Dispose() => _db.Dispose();

    private WebhookDelivery Seed(string status, DateTimeOffset createdAt, int attempts = 5)
    {
        var delivery = new WebhookDelivery
        {
            Id = Guid.NewGuid(),
            SubscriptionId = Guid.NewGuid(),
            EventType = "deployment.created",
            Status = status,
            Attempts = attempts,
            ErrorMessage = status == "failed" ? "receiver returned 503" : null,
            CreatedAt = createdAt,
            NextRetryAt = createdAt,
        };
        _db.WebhookDeliveries.Add(delivery);
        return delivery;
    }

    [Fact]
    public async Task RetryAllFailed_RequeuesFailedOnly_AndResetsTheirCounters()
    {
        var now = DateTimeOffset.UtcNow;
        var failed = Seed("failed", now.AddDays(-2));
        var delivered = Seed("delivered", now.AddDays(-2));
        var pending = Seed("pending", now.AddDays(-2), attempts: 1);
        await _db.SaveChangesAsync();

        var retried = await WebhookEndpoints.RetryAllFailedDeliveriesAsync(_db);

        Assert.Equal(1, retried);
        var reloaded = await _db.WebhookDeliveries.FindAsync(failed.Id);
        Assert.Equal("pending", reloaded!.Status);
        Assert.Equal(0, reloaded.Attempts);
        Assert.Null(reloaded.ErrorMessage);
        Assert.True(reloaded.NextRetryAt <= DateTimeOffset.UtcNow); // eligible for the worker now
        // The others keep their state — a retry-all is not a reset-all.
        Assert.Equal("delivered", (await _db.WebhookDeliveries.FindAsync(delivered.Id))!.Status);
        Assert.Equal(1, (await _db.WebhookDeliveries.FindAsync(pending.Id))!.Attempts);
    }

    [Fact]
    public async Task Purge_DeletesSettledRowsPastTheCutoff_NeverPending()
    {
        var now = DateTimeOffset.UtcNow;
        Seed("delivered", now.AddDays(-60));
        Seed("failed", now.AddDays(-45));
        var recentSettled = Seed("delivered", now.AddDays(-3));
        // Pending and ancient — still owed to its receiver, so age must not condemn it.
        var stalePending = Seed("pending", now.AddDays(-90));
        await _db.SaveChangesAsync();

        var stats = await WebhookEndpoints.GetDeliveryMaintenanceStatsAsync(_db, olderThanDays: 30);
        Assert.Equal(1, stats.Failed);
        Assert.Equal(2, stats.Purgeable);

        var removed = await WebhookEndpoints.PurgeSettledDeliveriesAsync(_db, olderThanDays: 30);

        Assert.Equal(2, removed);
        var remainingIds = await _db.WebhookDeliveries.Select(d => d.Id).ToListAsync();
        Assert.Contains(recentSettled.Id, remainingIds);
        Assert.Contains(stalePending.Id, remainingIds);
        Assert.Equal(2, remainingIds.Count);
    }
}
