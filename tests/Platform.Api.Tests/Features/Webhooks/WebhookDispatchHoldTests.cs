using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Platform.Api.Features.Webhooks;
using Platform.Api.Features.Webhooks.Models;
using Platform.Api.Infrastructure.Persistence;

namespace Platform.Api.Tests.Features.Webhooks;

/// <summary>
/// The held-delivery mechanics behind cancellable events: a dispatch can ask for its rows to sit
/// unsent for a while, and the source that queued them can drop the ones that have not gone out.
/// Both are expressed in the delivery row itself — no timers, no in-memory state — so a restart
/// mid-hold changes nothing.
/// </summary>
public class WebhookDispatchHoldTests : IDisposable
{
    private readonly PlatformDbContext _db;
    private readonly WebhookDispatcher _sut;

    public WebhookDispatchHoldTests()
    {
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _db = new PlatformDbContext(options);
        _sut = new WebhookDispatcher(_db, Substitute.For<ILogger<WebhookDispatcher>>());
    }

    public void Dispose() => _db.Dispose();

    private Guid SeedSubscription(params string[] events)
    {
        var id = Guid.NewGuid();
        _db.WebhookSubscriptions.Add(new WebhookSubscription
        {
            Id = id,
            Name = "hook",
            Url = "https://example.com/hook",
            EncryptedSecret = "x",
            EventsJson = System.Text.Json.JsonSerializer.Serialize(events),
            Active = true,
        });
        _db.SaveChanges();
        return id;
    }

    [Fact]
    public async Task Dispatch_WithoutOptions_IsSendableImmediately_AndCarriesNoCancelKey()
    {
        SeedSubscription("promotion.rejected");

        await _sut.DispatchAsync("promotion.rejected", new { candidateId = Guid.NewGuid() });

        var delivery = _db.WebhookDeliveries.Single();
        Assert.True(delivery.NextRetryAt <= DateTimeOffset.UtcNow);
        Assert.Null(delivery.CancelKey);
    }

    [Fact]
    public async Task Dispatch_WithADelay_HoldsTheRowUntilItIsDue()
    {
        SeedSubscription("promotion.approved");
        var before = DateTimeOffset.UtcNow;

        await _sut.DispatchAsync(
            "promotion.approved", new { candidateId = Guid.NewGuid() }, null,
            new WebhookDispatchOptions(Delay: TimeSpan.FromSeconds(10), CancelKey: "key-1"));

        var delivery = _db.WebhookDeliveries.Single();
        // The worker's own filter is NextRetryAt <= now, so a future stamp IS the hold.
        Assert.True(delivery.NextRetryAt >= before.AddSeconds(10));
        Assert.Equal("pending", delivery.Status);
        Assert.Equal("key-1", delivery.CancelKey);
    }

    [Fact]
    public async Task CancelPending_DropsEveryUnsentRowUnderTheKey()
    {
        // Two subscribers on the same event ⇒ two rows from one dispatch. One cancel stops both,
        // or a fan-out would leak the retracted event to whichever subscriber it missed.
        SeedSubscription("promotion.approved");
        SeedSubscription("promotion.approved");
        await _sut.DispatchAsync(
            "promotion.approved", new { candidateId = Guid.NewGuid() }, null,
            new WebhookDispatchOptions(Delay: TimeSpan.FromSeconds(10), CancelKey: "key-1"));
        Assert.Equal(2, _db.WebhookDeliveries.Count());

        var cancelled = await _sut.CancelPendingAsync("key-1");

        Assert.Equal(2, cancelled);
        Assert.All(_db.WebhookDeliveries.ToList(), d => Assert.Equal("cancelled", d.Status));
    }

    [Fact]
    public async Task CancelPending_LeavesOtherKeysAlone()
    {
        SeedSubscription("promotion.approved");
        await _sut.DispatchAsync("promotion.approved", new { n = 1 }, null,
            new WebhookDispatchOptions(Delay: TimeSpan.FromSeconds(10), CancelKey: "candidate-a"));
        await _sut.DispatchAsync("promotion.approved", new { n = 2 }, null,
            new WebhookDispatchOptions(Delay: TimeSpan.FromSeconds(10), CancelKey: "candidate-b"));

        var cancelled = await _sut.CancelPendingAsync("candidate-a");

        Assert.Equal(1, cancelled);
        Assert.Single(_db.WebhookDeliveries.Where(d => d.Status == "pending"));
    }

    [Fact]
    public async Task CancelPending_WillNotUnsendAnAttemptedDelivery()
    {
        // Once a request has left, the receiver has the news. Cancelling its retries would strand a
        // half-delivered event instead of undoing anything, so an attempted row is off limits.
        SeedSubscription("promotion.approved");
        await _sut.DispatchAsync("promotion.approved", new { n = 1 }, null,
            new WebhookDispatchOptions(Delay: TimeSpan.FromSeconds(10), CancelKey: "key-1"));
        var delivery = _db.WebhookDeliveries.Single();
        delivery.Attempts = 1;
        delivery.ErrorMessage = "HTTP 503";
        await _db.SaveChangesAsync();

        var cancelled = await _sut.CancelPendingAsync("key-1");

        Assert.Equal(0, cancelled);
        Assert.Equal("pending", _db.WebhookDeliveries.Single().Status);
    }

    [Fact]
    public async Task CancelPending_WithNothingQueued_IsANoOp()
    {
        Assert.Equal(0, await _sut.CancelPendingAsync("key-nobody-used"));
        Assert.Equal(0, await _sut.CancelPendingAsync(""));
    }
}
