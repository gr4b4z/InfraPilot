using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Platform.Api.Infrastructure.Notifications;

public class WebhookChannel : INotificationChannel
{
    private readonly WebhookChannelConfig _config;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<WebhookChannel> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public WebhookChannel(
        IHttpClientFactory httpClientFactory,
        IOptions<NotificationOptions> options,
        ILogger<WebhookChannel> logger)
    {
        _httpClientFactory = httpClientFactory;
        _config = options.Value.Channels.Webhook;
        _logger = logger;
    }

    public bool IsEnabled => _config.Enabled && !string.IsNullOrEmpty(_config.Url);

    public async Task SendAsync(NotificationMessage message, CancellationToken ct = default)
    {
        if (!IsEnabled) return;

        var payload = message.WebhookPayload ?? new
        {
            subject = message.Subject,
            body = message.BodyText ?? message.Subject,
            recipients = message.Recipients,
            actionUrl = message.ActionUrl,
            timestamp = DateTimeOffset.UtcNow,
        };

        var json = JsonSerializer.Serialize(payload, JsonOptions);

        using var request = new HttpRequestMessage(HttpMethod.Post, _config.Url);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        foreach (var (key, value) in _config.Headers)
        {
            request.Headers.TryAddWithoutValidation(key, value);
        }

        try
        {
            var client = _httpClientFactory.CreateClient("notification-webhook");
            using var response = await client.SendAsync(request, ct);

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                _logger.LogError("Webhook returned {StatusCode}: {Body}", (int)response.StatusCode, body);
            }
            else
            {
                _logger.LogInformation("Webhook notification sent: {Subject}", message.Subject);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send webhook notification: {Subject}", message.Subject);
        }
    }
}
