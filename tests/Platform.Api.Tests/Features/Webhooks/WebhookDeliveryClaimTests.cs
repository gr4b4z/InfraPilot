using System.Collections.Concurrent;
using System.Net;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Platform.Api.Features.Webhooks;
using Platform.Api.Features.Webhooks.Models;
using Platform.Api.Infrastructure.Persistence;
using Platform.Api.Infrastructure.Realtime;

namespace Platform.Api.Tests.Features.Webhooks;

/// <summary>
/// More than one replica runs the delivery worker, each polling the same table on its own timer.
/// Selecting a batch used to be all it took to start sending, and the status only changed after the
/// whole batch had gone out — so for the length of a batch a row still read as pending, and a second
/// replica polling inside that window POSTed the same delivery again. The receiver saw two requests
/// carrying one delivery id while the row recorded a single attempt, which is why the duplicate never
/// appeared in the delivery history.
/// <para>
/// Sending is now gated on claiming the row, and these cover the worker's half of that: a row it did
/// not claim is not sent, not written, and not announced. The claim's atomicity is the database's
/// half — a conditional UPDATE — and is not reachable from here: the InMemory provider cannot run
/// ExecuteUpdate, and SQLite cannot translate the DateTimeOffset comparison the worker's own query
/// depends on.
/// </para>
/// </summary>
public class WebhookDeliveryClaimTests : IDisposable
{
    private readonly PlatformDbContext _db;
    private readonly Guid _subscriptionId = Guid.NewGuid();

    public WebhookDeliveryClaimTests()
    {
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _db = new PlatformDbContext(options);

        _db.WebhookSubscriptions.Add(new WebhookSubscription
        {
            Id = _subscriptionId,
            Name = "receiver",
            Url = "https://example.invalid/hook",
            EncryptedSecret = "",
            EventsJson = "[\"ping\"]",
            TargetType = WebhookTargetTypes.Generic,
            Active = true,
        });
        _db.SaveChanges();
    }

    public void Dispose() => _db.Dispose();

    /// <summary>
    /// Hands the worker the one context these tests assert against. The real scope factory would
    /// dispose the context when the worker's per-pass scope ends, taking the InMemory store with it.
    /// </summary>
    private sealed class SharedContextScope(PlatformDbContext db)
        : IServiceScopeFactory, IServiceScope, IServiceProvider
    {
        public IServiceScope CreateScope() => this;
        public IServiceProvider ServiceProvider => this;
        public object? GetService(Type serviceType) =>
            serviceType == typeof(PlatformDbContext) ? db : null;
        public void Dispose() { }
    }

    private Guid AddPendingDelivery()
    {
        var id = Guid.NewGuid();
        _db.WebhookDeliveries.Add(new WebhookDelivery
        {
            Id = id,
            SubscriptionId = _subscriptionId,
            EventType = "ping",
            PayloadJson = "{}",
            Status = "pending",
            NextRetryAt = DateTimeOffset.UtcNow.AddSeconds(-1),
        });
        _db.SaveChanges();
        return id;
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public readonly ConcurrentBag<string> Sent = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Sent.Add(request.Headers.TryGetValues("X-Webhook-Delivery", out var v)
                ? string.Join(",", v)
                : "(none)");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(""),
            });
        }
    }

    /// <summary>
    /// Stands in for the database's conditional UPDATE. <see cref="Grant"/> decides which rows this
    /// worker wins, mirroring what a real claim does to the row it takes.
    /// </summary>
    private sealed class TestWorker : WebhookDeliveryWorker
    {
        public Func<WebhookDelivery, bool> Grant = _ => true;
        public readonly List<Guid> ClaimsAttempted = [];

        public TestWorker(PlatformDbContext db, HttpMessageHandler handler)
            : base(
                new SharedContextScope(db),
                new EphemeralDataProtectionProvider(),
                ClientFactory(handler),
                Substitute.For<IPlatformEventPublisher>(),
                new MessageTemplateRenderer(),
                NullLogger<WebhookDeliveryWorker>.Instance)
        {
        }

        private static IHttpClientFactory ClientFactory(HttpMessageHandler handler)
        {
            var factory = Substitute.For<IHttpClientFactory>();
            factory.CreateClient(Arg.Any<string>())
                .Returns(_ => new HttpClient(handler, disposeHandler: false));
            return factory;
        }

        protected override Task<bool> TryClaimAsync(
            PlatformDbContext db, WebhookDelivery delivery, DateTimeOffset now, CancellationToken ct)
        {
            ClaimsAttempted.Add(delivery.Id);
            if (!Grant(delivery)) return Task.FromResult(false);

            delivery.Attempts++;
            delivery.NextRetryAt = now.AddMinutes(2);
            return Task.FromResult(true);
        }
    }

    [Fact]
    public async Task A_row_this_worker_did_not_claim_is_never_sent()
    {
        AddPendingDelivery();
        var handler = new RecordingHandler();
        var worker = new TestWorker(_db, handler) { Grant = _ => false };

        await worker.ProcessPendingDeliveries(CancellationToken.None);

        Assert.Empty(handler.Sent);
    }

    [Fact]
    public async Task A_row_this_worker_did_not_claim_is_left_exactly_as_it_was()
    {
        var id = AddPendingDelivery();
        var worker = new TestWorker(_db, new RecordingHandler()) { Grant = _ => false };

        await worker.ProcessPendingDeliveries(CancellationToken.None);

        var row = await _db.WebhookDeliveries.SingleAsync(d => d.Id == id);
        Assert.Equal("pending", row.Status);   // still owed to the receiver, by whoever holds it
        Assert.Equal(0, row.Attempts);
        Assert.Null(row.DeliveredAt);
        Assert.Null(row.HttpStatus);
    }

    [Fact]
    public async Task A_claimed_row_is_sent_once_and_marked_delivered()
    {
        var id = AddPendingDelivery();
        var handler = new RecordingHandler();
        var worker = new TestWorker(_db, handler);

        await worker.ProcessPendingDeliveries(CancellationToken.None);

        Assert.Single(handler.Sent);
        var row = await _db.WebhookDeliveries.SingleAsync(d => d.Id == id);
        Assert.Equal("delivered", row.Status);
        Assert.Equal(1, row.Attempts);
    }

    [Fact]
    public async Task Only_the_claimed_rows_of_a_batch_are_sent()
    {
        var mine = AddPendingDelivery();
        var theirs = AddPendingDelivery();
        var handler = new RecordingHandler();
        var worker = new TestWorker(_db, handler) { Grant = d => d.Id == mine };

        await worker.ProcessPendingDeliveries(CancellationToken.None);

        Assert.Single(handler.Sent);
        Assert.Contains(mine.ToString(), handler.Sent);
        Assert.DoesNotContain(theirs.ToString(), handler.Sent);

        // Both were offered to the claim — the batch is not filtered before it.
        Assert.Equal(2, worker.ClaimsAttempted.Count);
        Assert.Equal("pending", (await _db.WebhookDeliveries.SingleAsync(d => d.Id == theirs)).Status);
    }

    [Fact]
    public async Task An_attempt_is_counted_once_per_send_not_twice()
    {
        // The claim owns the increment. Counting it in the send path too would skip backoff steps,
        // since the retry schedule is indexed by this number.
        AddPendingDelivery();
        var worker = new TestWorker(_db, new RecordingHandler());

        await worker.ProcessPendingDeliveries(CancellationToken.None);

        Assert.Equal(1, (await _db.WebhookDeliveries.SingleAsync()).Attempts);
    }

    [Fact]
    public async Task A_delivered_row_is_not_offered_to_the_claim_again()
    {
        AddPendingDelivery();
        var handler = new RecordingHandler();
        var worker = new TestWorker(_db, handler);

        await worker.ProcessPendingDeliveries(CancellationToken.None);
        var afterFirstPass = worker.ClaimsAttempted.Count;
        await worker.ProcessPendingDeliveries(CancellationToken.None);

        Assert.Single(handler.Sent);
        Assert.Equal(afterFirstPass, worker.ClaimsAttempted.Count);
    }

    [Fact]
    public async Task A_row_held_under_an_unexpired_lease_is_not_picked_up()
    {
        // What a worker leaves behind when it dies mid-send: still pending, but not yet due. The
        // lease is what makes that recover on its own instead of needing a sweeper.
        _db.WebhookDeliveries.Add(new WebhookDelivery
        {
            Id = Guid.NewGuid(),
            SubscriptionId = _subscriptionId,
            EventType = "ping",
            PayloadJson = "{}",
            Status = "pending",
            NextRetryAt = DateTimeOffset.UtcNow.AddMinutes(1),
        });
        await _db.SaveChangesAsync();

        var worker = new TestWorker(_db, new RecordingHandler());
        await worker.ProcessPendingDeliveries(CancellationToken.None);

        Assert.Empty(worker.ClaimsAttempted);
    }

    [Fact]
    public async Task An_inactive_subscription_fails_the_row_without_sending_it()
    {
        var sub = await _db.WebhookSubscriptions.SingleAsync(s => s.Id == _subscriptionId);
        sub.Active = false;
        await _db.SaveChangesAsync();

        var id = AddPendingDelivery();
        var handler = new RecordingHandler();
        var worker = new TestWorker(_db, handler);

        await worker.ProcessPendingDeliveries(CancellationToken.None);

        Assert.Empty(handler.Sent);
        Assert.Equal("failed", (await _db.WebhookDeliveries.SingleAsync(d => d.Id == id)).Status);
    }
}
