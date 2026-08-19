using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Platform.Api.Features.Webhooks.Models;
using Platform.Api.Infrastructure.Persistence;
using Platform.Api.Infrastructure.Realtime;

namespace Platform.Api.Features.Webhooks;

public class WebhookDeliveryWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IDataProtector _protector;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IPlatformEventPublisher _events;
    private readonly MessageTemplateRenderer _messages;
    private readonly ILogger<WebhookDeliveryWorker> _logger;

    private static readonly int[] RetryDelaysSeconds = [30, 120, 600, 3600, 14400]; // 30s, 2m, 10m, 1h, 4h

    /// <summary>
    /// How long a claimed delivery is held before another worker may take it. Comfortably longer
    /// than <see cref="SendTimeout"/> so an in-flight request is never raced, and short enough that
    /// a delivery owned by a replica that died is picked up again without operator involvement.
    /// </summary>
    private static readonly TimeSpan ClaimLease = TimeSpan.FromMinutes(2);

    private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(10);

    public WebhookDeliveryWorker(
        IServiceScopeFactory scopeFactory,
        IDataProtectionProvider dataProtection,
        IHttpClientFactory httpClientFactory,
        IPlatformEventPublisher events,
        MessageTemplateRenderer messages,
        ILogger<WebhookDeliveryWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _protector = dataProtection.CreateProtector("WebhookSecrets");
        _httpClientFactory = httpClientFactory;
        _events = events;
        _messages = messages;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessPendingDeliveries(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Error in webhook delivery worker");
            }

            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }
    }

    /// <summary>
    /// One pass of the delivery pump. Public so a test can drive a single pass instead of waiting on
    /// the five-second timer in <see cref="ExecuteAsync"/>.
    /// </summary>
    public async Task ProcessPendingDeliveries(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();

        var now = DateTimeOffset.UtcNow;
        var deliveries = await db.WebhookDeliveries
            .Include(d => d.Subscription)
            .Where(d => d.Status == "pending" && d.NextRetryAt <= now)
            .OrderBy(d => d.NextRetryAt)
            .Take(50)
            .ToListAsync(ct);

        if (deliveries.Count == 0) return;

        var client = _httpClientFactory.CreateClient("webhook-delivery");
        client.Timeout = SendTimeout;

        // Only the rows this pass actually owns. A row another worker claimed first is left alone:
        // not sent, not written, and not announced as changed.
        var owned = new List<Models.WebhookDelivery>(deliveries.Count);

        foreach (var delivery in deliveries)
        {
            // Claimed one at a time, immediately before its own send, and stamped with the time of
            // the claim rather than of the batch: a batch of slow or timing-out sends can run for
            // minutes, and a lease measured from when the batch started would already have expired
            // by the time the later rows go out — handing them to another replica mid-flight.
            if (!await TryClaimAsync(db, delivery, DateTimeOffset.UtcNow, ct)) continue;
            owned.Add(delivery);

            if (delivery.Subscription is null || !delivery.Subscription.Active)
            {
                delivery.Status = "failed";
                delivery.ErrorMessage = "Subscription inactive or deleted";
                continue;
            }

            await AttemptDelivery(client, delivery, ct);
        }

        if (owned.Count == 0) return;

        await db.SaveChangesAsync(ct);

        // Let the admin webhook screens replace their "wait a couple of seconds and refetch"
        // guesswork with an actual signal. One event per subscription touched this cycle.
        foreach (var subscriptionId in owned.Select(d => d.SubscriptionId).Distinct())
        {
            await _events.PublishEntityChanged(new EntityChangedEvent
            {
                Entity = "webhook-delivery",
                Action = "updated",
                Id = subscriptionId.ToString(),
            });
        }
    }

    /// <summary>
    /// Takes exclusive ownership of a delivery, or reports that another worker got there first.
    /// This is what stops a webhook going out twice.
    /// <para>
    /// More than one replica runs this worker — the container app scales on HTTP traffic — and each
    /// polls the same table on its own five-second timer. Selecting the batch is not enough to own
    /// it: the status used to change only after every send in the batch had gone out, so for the
    /// length of a batch the row still read as pending, and a second replica polling inside that
    /// window selected and POSTed the very same delivery. Both requests carried one delivery id, and
    /// whichever saved last recorded a single attempt — which is why the duplicate was invisible in
    /// the delivery history and only ever showed up at the receiver.
    /// </para>
    /// <para>
    /// The guard is the <c>WHERE</c> clause, evaluated by the database and not by us: of two
    /// concurrent UPDATEs only one can move a row out of "pending and due", so only one worker is
    /// told it changed a row. The claim doubles as the lease — pushing <c>NextRetryAt</c> out means a
    /// worker that dies mid-send leaves a row that simply falls due again later, so recovering it
    /// needs no sweeper, no extra status value, and no migration.
    /// </para>
    /// </summary>
    /// <remarks>
    /// Virtual only so a test can stand in for the database's half of the bargain: the guarantee
    /// here is a relational one, and neither the InMemory provider (no ExecuteUpdate) nor SQLite
    /// (no DateTimeOffset comparison) can host it. What a test can pin down is the worker's side —
    /// that a row it did not claim is never sent — which is the behaviour that regressed.
    /// </remarks>
    protected virtual async Task<bool> TryClaimAsync(
        PlatformDbContext db, Models.WebhookDelivery delivery, DateTimeOffset now, CancellationToken ct)
    {
        var leaseUntil = now + ClaimLease;

        var claimed = await db.WebhookDeliveries
            .Where(d => d.Id == delivery.Id && d.Status == "pending" && d.NextRetryAt <= now)
            .ExecuteUpdateAsync(
                s => s
                    .SetProperty(d => d.Attempts, d => d.Attempts + 1)
                    .SetProperty(d => d.NextRetryAt, leaseUntil),
                ct);

        if (claimed == 0) return false;

        // ExecuteUpdate writes straight past the change tracker, so mirror it onto the tracked
        // entity. Left unmirrored, the SaveChanges at the end of the batch would write the pre-claim
        // values back over the claim and hand the row to the next replica that polls.
        delivery.Attempts++;
        delivery.NextRetryAt = leaseUntil;
        return true;
    }

    private async Task AttemptDelivery(HttpClient client, Models.WebhookDelivery delivery, CancellationToken ct)
    {
        // Attempts is incremented by the claim, which is the only place that may touch it: the claim
        // is what the retry schedule below counts, and counting it here too would double every step.
        var sub = delivery.Subscription!;

        try
        {
            // The secret is the HMAC key for the generic and Azure DevOps targets, and the bearer
            // token for GitHub — which of those it is, the builder decides from the target type.
            // Messaging targets store none at all: their URL is the capability to post.
            var secret = string.IsNullOrEmpty(sub.EncryptedSecret)
                ? ""
                : _protector.Unprotect(sub.EncryptedSecret);

            // Rendered per attempt rather than stored on the delivery, so fixing a bad template takes
            // effect on retry instead of requiring the event to happen again.
            var message = WebhookTargetTypes.IsMessaging(sub.TargetType)
                ? _messages.Render(sub, delivery)
                : null;

            var framed = WebhookRequestBuilder.Build(sub, delivery, secret, message);

            using var request = new HttpRequestMessage(HttpMethod.Post, sub.Url);
            // Content type comes from the framing, not from here: every target but the Teams HTML one
            // sends JSON, and that one sends an HTML fragment.
            request.Content = new StringContent(framed.Body, Encoding.UTF8);
            request.Content.Headers.ContentType =
                System.Net.Http.Headers.MediaTypeHeaderValue.Parse(framed.ContentType);
            foreach (var (name, value) in framed.Headers)
                request.Headers.TryAddWithoutValidation(name, value);

            using var response = await client.SendAsync(request, ct);
            var responseBody = await response.Content.ReadAsStringAsync(ct);

            delivery.HttpStatus = (int)response.StatusCode;
            delivery.ResponseBody = responseBody.Length > 4000 ? responseBody[..4000] : responseBody;

            if (response.IsSuccessStatusCode)
            {
                delivery.Status = "delivered";
                delivery.DeliveredAt = DateTimeOffset.UtcNow;
                _logger.LogInformation("Webhook delivered: {DeliveryId} to {Url} ({Status})",
                    delivery.Id, sub.Url, response.StatusCode);
            }
            else
            {
                ScheduleRetryOrFail(delivery, $"HTTP {(int)response.StatusCode}");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            delivery.HttpStatus = null;
            ScheduleRetryOrFail(delivery, ex.Message);
            _logger.LogWarning(ex, "Webhook delivery failed: {DeliveryId} to {Url}",
                delivery.Id, sub.Url);
        }
    }

    private static void ScheduleRetryOrFail(Models.WebhookDelivery delivery, string error)
    {
        delivery.ErrorMessage = error;

        if (delivery.Attempts >= RetryDelaysSeconds.Length)
        {
            delivery.Status = "failed";
        }
        else
        {
            var delaySeconds = RetryDelaysSeconds[delivery.Attempts - 1];
            delivery.NextRetryAt = DateTimeOffset.UtcNow.AddSeconds(delaySeconds);
        }
    }
}
