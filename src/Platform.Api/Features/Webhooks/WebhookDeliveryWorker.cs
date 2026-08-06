using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Platform.Api.Infrastructure.Persistence;
using Platform.Api.Infrastructure.Realtime;

namespace Platform.Api.Features.Webhooks;

public class WebhookDeliveryWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IDataProtector _protector;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IPlatformEventPublisher _events;
    private readonly ILogger<WebhookDeliveryWorker> _logger;

    private static readonly int[] RetryDelaysSeconds = [30, 120, 600, 3600, 14400]; // 30s, 2m, 10m, 1h, 4h

    public WebhookDeliveryWorker(
        IServiceScopeFactory scopeFactory,
        IDataProtectionProvider dataProtection,
        IHttpClientFactory httpClientFactory,
        IPlatformEventPublisher events,
        ILogger<WebhookDeliveryWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _protector = dataProtection.CreateProtector("WebhookSecrets");
        _httpClientFactory = httpClientFactory;
        _events = events;
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

    private async Task ProcessPendingDeliveries(CancellationToken ct)
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
        client.Timeout = TimeSpan.FromSeconds(10);

        foreach (var delivery in deliveries)
        {
            if (delivery.Subscription is null || !delivery.Subscription.Active)
            {
                delivery.Status = "failed";
                delivery.ErrorMessage = "Subscription inactive or deleted";
                continue;
            }

            await AttemptDelivery(client, delivery, ct);
        }

        await db.SaveChangesAsync(ct);

        // Let the admin webhook screens replace their "wait a couple of seconds and refetch"
        // guesswork with an actual signal. One event per subscription touched this cycle.
        foreach (var subscriptionId in deliveries.Select(d => d.SubscriptionId).Distinct())
        {
            await _events.PublishEntityChanged(new EntityChangedEvent
            {
                Entity = "webhook-delivery",
                Action = "updated",
                Id = subscriptionId.ToString(),
            });
        }
    }

    private async Task AttemptDelivery(HttpClient client, Models.WebhookDelivery delivery, CancellationToken ct)
    {
        delivery.Attempts++;
        var sub = delivery.Subscription!;

        try
        {
            // The secret is the HMAC key for the generic and Azure DevOps targets, and the bearer
            // token for GitHub — which of those it is, the builder decides from the target type.
            var secret = _protector.Unprotect(sub.EncryptedSecret);
            var framed = WebhookRequestBuilder.Build(sub, delivery, secret);

            using var request = new HttpRequestMessage(HttpMethod.Post, sub.Url);
            request.Content = new StringContent(framed.Body, Encoding.UTF8, "application/json");
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
