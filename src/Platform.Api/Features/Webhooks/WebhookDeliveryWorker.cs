using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Platform.Api.Infrastructure.Persistence;

namespace Platform.Api.Features.Webhooks;

public class WebhookDeliveryWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IDataProtector _protector;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<WebhookDeliveryWorker> _logger;

    private static readonly int[] RetryDelaysSeconds = [30, 120, 600, 3600, 14400]; // 30s, 2m, 10m, 1h, 4h

    public WebhookDeliveryWorker(
        IServiceScopeFactory scopeFactory,
        IDataProtectionProvider dataProtection,
        IHttpClientFactory httpClientFactory,
        ILogger<WebhookDeliveryWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _protector = dataProtection.CreateProtector("WebhookSecrets");
        _httpClientFactory = httpClientFactory;
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
    }

    private async Task AttemptDelivery(HttpClient client, Models.WebhookDelivery delivery, CancellationToken ct)
    {
        delivery.Attempts++;
        var sub = delivery.Subscription!;

        try
        {
            // Decrypt secret and compute HMAC-SHA256 signature
            var secret = _protector.Unprotect(sub.EncryptedSecret);
            var payloadBytes = Encoding.UTF8.GetBytes(delivery.PayloadJson);
            var signature = ComputeSignature(payloadBytes, secret);

            using var request = new HttpRequestMessage(HttpMethod.Post, sub.Url);
            request.Content = new StringContent(delivery.PayloadJson, Encoding.UTF8, "application/json");
            request.Headers.Add("X-Hub-Signature-256", $"sha256={signature}");
            request.Headers.Add("X-Webhook-Event", delivery.EventType);
            request.Headers.Add("X-Webhook-Delivery", delivery.Id.ToString());
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

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

    private static string ComputeSignature(byte[] payload, string secret)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var hash = hmac.ComputeHash(payload);
        return Convert.ToHexStringLower(hash);
    }
}
