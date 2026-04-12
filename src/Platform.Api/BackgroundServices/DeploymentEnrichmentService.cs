using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Platform.Api.Features.Deployments.Models;
using Platform.Api.Infrastructure.Jira;
using Platform.Api.Infrastructure.Persistence;

namespace Platform.Api.BackgroundServices;

public class DeploymentEnrichmentService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _config;
    private readonly ILogger<DeploymentEnrichmentService> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public DeploymentEnrichmentService(
        IServiceScopeFactory scopeFactory,
        IConfiguration config,
        ILogger<DeploymentEnrichmentService> logger)
    {
        _scopeFactory = scopeFactory;
        _config = config;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var enabled = _config.GetValue("Deployments:Enrichment:Enabled", true);
        if (!enabled)
        {
            _logger.LogInformation("DeploymentEnrichmentService is disabled");
            return;
        }

        var intervalSeconds = _config.GetValue("Deployments:Enrichment:IntervalSeconds", 30);
        var maxPerCycle = _config.GetValue("Deployments:Enrichment:MaxEventsPerCycle", 10);
        var lookbackHours = _config.GetValue("Deployments:Enrichment:LookbackHours", 24);

        _logger.LogInformation("DeploymentEnrichmentService started (interval={Interval}s, max={Max}/cycle)", intervalSeconds, maxPerCycle);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessUnenrichedEvents(maxPerCycle, lookbackHours, stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in deployment enrichment cycle");
            }

            await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), stoppingToken);
        }
    }

    private async Task ProcessUnenrichedEvents(int maxPerCycle, int lookbackHours, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var jira = scope.ServiceProvider.GetRequiredService<JiraClient>();

        var cutoff = DateTimeOffset.UtcNow.AddHours(-lookbackHours);

        var events = await db.DeployEvents
            .Where(e => e.EnrichmentJson == null && e.CreatedAt > cutoff)
            .OrderBy(e => e.CreatedAt)
            .Take(maxPerCycle)
            .ToListAsync(ct);

        if (events.Count == 0) return;

        _logger.LogDebug("Enriching {Count} deploy events", events.Count);

        foreach (var evt in events)
        {
            try
            {
                await EnrichEvent(evt, jira, ct);
                await db.SaveChangesAsync(ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to enrich deploy event {EventId}, will retry next cycle", evt.Id);
            }
        }
    }

    private async Task EnrichEvent(DeployEvent evt, JiraClient jira, CancellationToken ct)
    {
        var references = Deserialize<List<ReferenceDto>>(evt.ReferencesJson) ?? [];
        var labels = new Dictionary<string, string>();
        var discoveredParticipants = new List<ParticipantDto>();

        foreach (var reference in references)
        {
            try
            {
                if (reference.Type == "work-item" && reference.Provider == "jira" && !string.IsNullOrEmpty(reference.Key))
                {
                    await EnrichFromJira(jira, reference.Key, labels, ct);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to enrich reference {Type}:{Key}", reference.Type, reference.Key);
            }
        }

        if (labels.Count > 0 || discoveredParticipants.Count > 0)
        {
            var enrichment = new EnrichmentDto(labels, discoveredParticipants, DateTimeOffset.UtcNow);
            evt.EnrichmentJson = JsonSerializer.Serialize(enrichment, JsonOptions);
        }
        else
        {
            // Mark as enriched (empty) so we don't retry
            var enrichment = new EnrichmentDto(labels, discoveredParticipants, DateTimeOffset.UtcNow);
            evt.EnrichmentJson = JsonSerializer.Serialize(enrichment, JsonOptions);
        }

        _logger.LogDebug("Enriched event {EventId} with {LabelCount} labels", evt.Id, labels.Count);
    }

    private async Task EnrichFromJira(JiraClient jira, string issueKey, Dictionary<string, string> labels, CancellationToken ct)
    {
        var conn = jira.ResolveConnection(null);
        var url = $"{conn.BaseUrl.TrimEnd('/')}/rest/api/3/issue/{Uri.EscapeDataString(issueKey)}?fields=summary,status";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        var credentials = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{conn.Email}:{conn.ApiToken}"));
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", credentials);

        using var httpClient = new HttpClient();
        using var response = await httpClient.SendAsync(request, ct);

        if (!response.IsSuccessStatusCode) return;

        var body = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);
        var fields = doc.RootElement.GetProperty("fields");

        if (fields.TryGetProperty("summary", out var summary))
            labels["workItemTitle"] = summary.GetString() ?? "";

        if (fields.TryGetProperty("status", out var status) && status.TryGetProperty("name", out var statusName))
            labels["workItemStatus"] = statusName.GetString() ?? "";
    }

    private static T? Deserialize<T>(string? json)
    {
        if (string.IsNullOrEmpty(json)) return default;
        return JsonSerializer.Deserialize<T>(json, JsonOptions);
    }
}
