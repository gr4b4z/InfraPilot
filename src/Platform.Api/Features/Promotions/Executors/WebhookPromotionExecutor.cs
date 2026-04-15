using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Platform.Api.Features.Promotions.Models;

namespace Platform.Api.Features.Promotions.Executors;

/// <summary>
/// Generic webhook executor — POSTs a JSON payload describing the approved candidate to a URL
/// configured on the policy. This is the lowest-friction integration path for CI systems that
/// already expose an incoming webhook (GitHub Actions repository-dispatch, Jenkins remote
/// trigger, custom relay services).
///
/// <para><b>Config shape (<c>ExecutorConfigJson</c>):</b>
/// <code>
/// {
///   "url": "https://ci.example/hooks/promote",
///   "method": "POST",                 // optional, defaults to POST
///   "headers": { "X-Token": "..." },  // optional static headers
///   "runUrlTemplate": "https://ci.example/runs/{candidateId}"  // optional
/// }
/// </code>
/// </para>
///
/// <para>The outgoing body is a <see cref="PromotionWebhookPayload"/>. Non-2xx responses are
/// reported as failures; transport errors are caught and returned as <see cref="PromotionDispatchResult.Fail"/>.</para>
/// </summary>
public class WebhookPromotionExecutor : IPromotionExecutor
{
    public const string KindName = "webhook";

    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<WebhookPromotionExecutor> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public WebhookPromotionExecutor(
        IHttpClientFactory httpFactory, ILogger<WebhookPromotionExecutor> logger)
    {
        _httpFactory = httpFactory;
        _logger = logger;
    }

    public string Kind => KindName;

    public async Task<PromotionDispatchResult> DispatchAsync(
        PromotionCandidate candidate, string? configJson, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(configJson))
            return PromotionDispatchResult.Fail("Webhook executor requires a config JSON blob");

        WebhookConfig? config;
        try
        {
            config = JsonSerializer.Deserialize<WebhookConfig>(configJson, JsonOptions);
        }
        catch (JsonException ex)
        {
            return PromotionDispatchResult.Fail($"Invalid webhook config JSON: {ex.Message}");
        }

        if (config is null || string.IsNullOrWhiteSpace(config.Url))
            return PromotionDispatchResult.Fail("Webhook config missing 'url'");

        var payload = new PromotionWebhookPayload(
            CandidateId: candidate.Id,
            Product: candidate.Product,
            Service: candidate.Service,
            SourceEnv: candidate.SourceEnv,
            TargetEnv: candidate.TargetEnv,
            Version: candidate.Version,
            SourceDeployEventId: candidate.SourceDeployEventId,
            ApprovedAt: candidate.ApprovedAt ?? DateTimeOffset.UtcNow);

        var method = string.IsNullOrWhiteSpace(config.Method) ? HttpMethod.Post : new HttpMethod(config.Method!);
        using var req = new HttpRequestMessage(method, config.Url);
        req.Content = new StringContent(
            JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json");
        if (config.Headers is not null)
        {
            foreach (var (k, v) in config.Headers)
            {
                // TryAddWithoutValidation tolerates custom headers like X-Token that may otherwise
                // be rejected by the stricter typed collection.
                req.Headers.TryAddWithoutValidation(k, v);
            }
        }

        try
        {
            var client = _httpFactory.CreateClient("promotion-webhook");
            using var response = await client.SendAsync(req, ct);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                _logger.LogWarning(
                    "Promotion webhook for candidate {Id} returned {Status}: {Body}",
                    candidate.Id, (int)response.StatusCode, Truncate(body, 500));
                return PromotionDispatchResult.Fail(
                    $"Webhook responded {(int)response.StatusCode} {response.ReasonPhrase}");
            }

            var runUrl = RenderRunUrl(config.RunUrlTemplate, candidate);
            _logger.LogInformation(
                "Promotion webhook dispatched for candidate {Id} ({Status}); runUrl={RunUrl}",
                candidate.Id, (int)response.StatusCode, runUrl ?? "<none>");
            return PromotionDispatchResult.Ok(runUrl);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Promotion webhook transport error for candidate {Id}", candidate.Id);
            return PromotionDispatchResult.Fail($"Transport error: {ex.Message}");
        }
        catch (TaskCanceledException ex)
        {
            _logger.LogError(ex, "Promotion webhook timed out for candidate {Id}", candidate.Id);
            return PromotionDispatchResult.Fail("Webhook request timed out");
        }
    }

    private static string? RenderRunUrl(string? template, PromotionCandidate c)
    {
        if (string.IsNullOrWhiteSpace(template)) return null;
        return template
            .Replace("{candidateId}", c.Id.ToString())
            .Replace("{product}", c.Product)
            .Replace("{service}", c.Service)
            .Replace("{targetEnv}", c.TargetEnv)
            .Replace("{version}", c.Version);
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

    private record WebhookConfig(
        string Url,
        string? Method = null,
        Dictionary<string, string>? Headers = null,
        string? RunUrlTemplate = null);
}

/// <summary>
/// Shape of the JSON body POSTed to the configured webhook URL. External systems can rely on
/// these fields being present; additional fields may be added in the future but names are stable.
/// </summary>
public record PromotionWebhookPayload(
    Guid CandidateId,
    string Product,
    string Service,
    string SourceEnv,
    string TargetEnv,
    string Version,
    Guid SourceDeployEventId,
    DateTimeOffset ApprovedAt);
