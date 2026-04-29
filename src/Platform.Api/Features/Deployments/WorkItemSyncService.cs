using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Platform.Api.Features.Deployments.Models;
using Platform.Api.Infrastructure.Persistence;

namespace Platform.Api.Features.Deployments;

/// <summary>
/// Maintains the <see cref="DeployEventWorkItem"/> relational projection of
/// <c>work-item</c> references for a deploy event. Owned by the promotion
/// subsystem's concerns — the deployment service has no reason to know about
/// ticket approvals.
///
/// <para>Called from two sites:</para>
/// <list type="bullet">
///   <item><see cref="Promotions.PromotionIngestHook"/> — after a new deploy event
///         lands, so the promotion gate evaluator can find tickets without scanning JSON.</item>
///   <item><see cref="BackgroundServices.DeploymentEnrichmentService"/> — after Jira
///         enrichment fills in ticket titles on existing events.</item>
/// </list>
///
/// <para>Idempotent: re-running with the same references is a no-op; re-running
/// after enrichment updates titles in place; references removed from the event are
/// also removed from the projection.</para>
/// </summary>
public class WorkItemSyncService
{
    private readonly PlatformDbContext _db;
    private readonly ILogger<WorkItemSyncService> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public WorkItemSyncService(PlatformDbContext db, ILogger<WorkItemSyncService> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>
    /// Reconciles <see cref="DeployEventWorkItem"/> rows for <paramref name="ev"/> to match its
    /// current <c>work-item</c> references. Caller is responsible for the surrounding
    /// <c>SaveChangesAsync</c> — this method only stages adds / updates / removes on the tracked
    /// <see cref="PlatformDbContext"/>.
    /// </summary>
    public async Task SyncAsync(DeployEvent ev, CancellationToken ct = default)
    {
        var rawRefs = Deserialize<List<ReferenceDto>>(ev.ReferencesJson) ?? [];

        var refs = rawRefs
            .Where(r => string.Equals(r.Type, "work-item", StringComparison.OrdinalIgnoreCase)
                     && !string.IsNullOrWhiteSpace(r.Key))
            .GroupBy(r => r.Key!, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

        var existing = await _db.DeployEventWorkItems
            .Where(w => w.DeployEventId == ev.Id)
            .ToListAsync(ct);

        var existingByKey = existing.ToDictionary(w => w.WorkItemKey, StringComparer.OrdinalIgnoreCase);

        var changed = 0;

        var freshKeys = refs.Select(r => r.Key!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var stale in existing.Where(w => !freshKeys.Contains(w.WorkItemKey)))
        {
            _db.DeployEventWorkItems.Remove(stale);
            changed++;
        }

        // Title: prefer per-reference Title; fall back to enrichment label when there is
        // exactly one work-item (the enrichment shape is flat so applying a single label to
        // multiple tickets would be misleading).
        var enrichment = string.IsNullOrEmpty(ev.EnrichmentJson)
            ? null
            : Deserialize<EnrichmentDto>(ev.EnrichmentJson);
        string? singleEnrichedTitle = null;
        if (refs.Count == 1 && enrichment?.Labels != null
            && enrichment.Labels.TryGetValue("workItemTitle", out var t)
            && !string.IsNullOrWhiteSpace(t))
        {
            singleEnrichedTitle = t;
        }

        foreach (var r in refs)
        {
            var title = !string.IsNullOrWhiteSpace(r.Title) ? r.Title : singleEnrichedTitle;

            if (existingByKey.TryGetValue(r.Key!, out var row))
            {
                var before = (row.Provider, row.Url, row.Title, row.Revision, row.Product);
                row.Provider = r.Provider;
                row.Url = r.Url;
                row.Title = title;
                row.Revision = r.Revision;
                row.Product = ev.Product;
                if (before != (row.Provider, row.Url, row.Title, row.Revision, row.Product))
                    changed++;
            }
            else
            {
                _db.DeployEventWorkItems.Add(new DeployEventWorkItem
                {
                    Id = Guid.NewGuid(),
                    DeployEventId = ev.Id,
                    WorkItemKey = r.Key!,
                    Product = ev.Product,
                    Provider = r.Provider,
                    Url = r.Url,
                    Title = title,
                    Revision = r.Revision,
                    CreatedAt = DateTimeOffset.UtcNow,
                });
                changed++;
            }
        }

        if (refs.Count > 0 || changed > 0)
            _logger.LogInformation("Synced {Count} work-items from deploy event {EventId}", refs.Count, ev.Id);
    }

    private static T? Deserialize<T>(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return default;
        try { return JsonSerializer.Deserialize<T>(json, JsonOptions); }
        catch { return default; }
    }
}
