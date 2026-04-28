using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Platform.Api.Features.Deployments.Models;
using Platform.Api.Infrastructure.Features;
using Platform.Api.Infrastructure.Persistence;

namespace Platform.Api.BackgroundServices;

/// <summary>
/// One-shot startup hosted service that scans existing <see cref="DeployEvent"/> rows
/// for work-item references and populates missing <see cref="DeployEventWorkItem"/>
/// projection rows. Idempotent: records a flag in <c>PlatformSettings</c> on success
/// and no-ops on subsequent restarts.
///
/// Scope: events from the last 90 days only — older events won't drive any active
/// promotions, so backfilling them isn't worth the I/O.
///
/// This service intentionally does NOT remove or update existing
/// <see cref="DeployEventWorkItem"/> rows; the live ingest path owns reconciliation,
/// and we don't want to clobber any titles or metadata it has filled in.
/// </summary>
public class DeployEventWorkItemBackfillService : BackgroundService
{
    private const string FlagKey = "deploy_event_work_items_backfilled_at";
    private const int BatchSize = 200;
    private const int LookbackDays = 90;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<DeployEventWorkItemBackfillService> _logger;

    public DeployEventWorkItemBackfillService(
        IServiceScopeFactory scopeFactory,
        ILogger<DeployEventWorkItemBackfillService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // Small initial delay so backfill doesn't compete with first-request handling.
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);

            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();

            var existingFlag = await db.PlatformSettings
                .FindAsync(new object[] { FlagKey }, stoppingToken);

            if (existingFlag is not null)
            {
                _logger.LogInformation(
                    "DeployEventWorkItem backfill already complete (at {Value}); skipping",
                    existingFlag.Value);
                return;
            }

            _logger.LogInformation(
                "DeployEventWorkItem backfill starting (lookback={LookbackDays}d, batch={BatchSize})",
                LookbackDays, BatchSize);

            var inserted = await RunBackfillAsync(db, stoppingToken);

            // Record completion flag so subsequent restarts no-op.
            db.PlatformSettings.Add(new PlatformSetting
            {
                Key = FlagKey,
                Value = DateTimeOffset.UtcNow.ToString("O"),
                UpdatedAt = DateTimeOffset.UtcNow,
                UpdatedBy = "system:backfill",
            });
            await db.SaveChangesAsync(stoppingToken);

            _logger.LogInformation(
                "DeployEventWorkItem backfill complete: inserted {Total} work-item rows",
                inserted);
        }
        catch (OperationCanceledException)
        {
            // Host is shutting down — surface as info, don't write the flag.
            _logger.LogInformation("DeployEventWorkItem backfill cancelled during shutdown");
        }
        catch (Exception ex)
        {
            // Don't crash the app — let the next restart retry. Flag is intentionally not
            // written so the next start picks the work back up.
            _logger.LogError(ex, "DeployEventWorkItem backfill failed; will retry on next startup");
        }
    }

    private async Task<int> RunBackfillAsync(PlatformDbContext db, CancellationToken ct)
    {
        var cutoff = DateTimeOffset.UtcNow.AddDays(-LookbackDays);

        // Snapshot the list of in-scope event IDs up front. We only run when the flag
        // is unset (i.e. once per cluster lifetime) so a single pass over the snapshot
        // is the simplest correct strategy; new events that arrive while we're walking
        // will be handled by live ingest. Storing Guids only keeps memory tiny even
        // with hundreds of thousands of events.
        var eventIds = await db.DeployEvents
            .Where(e => e.DeployedAt >= cutoff)
            .OrderBy(e => e.DeployedAt)
            .Select(e => e.Id)
            .ToListAsync(ct);

        var total = eventIds.Count;
        if (total == 0)
        {
            _logger.LogInformation("Backfill: no deploy events in the last {Days} days", LookbackDays);
            return 0;
        }

        _logger.LogInformation("Backfill: scanning {Total} deploy events", total);

        var processed = 0;
        var inserted = 0;

        for (var offset = 0; offset < total; offset += BatchSize)
        {
            ct.ThrowIfCancellationRequested();

            var batchIds = eventIds.GetRange(offset, Math.Min(BatchSize, total - offset));

            var batch = await db.DeployEvents
                .Where(e => batchIds.Contains(e.Id))
                .ToListAsync(ct);

            // Pre-load existing projection rows for this batch so we can check
            // (DeployEventId, WorkItemKey) uniqueness without per-row round-trips.
            var existingPairs = await db.DeployEventWorkItems
                .Where(w => batchIds.Contains(w.DeployEventId))
                .Select(w => new { w.DeployEventId, w.WorkItemKey })
                .ToListAsync(ct);

            var existingSet = new HashSet<(Guid, string)>(
                existingPairs.Select(p => (p.DeployEventId, p.WorkItemKey.ToLowerInvariant())));

            foreach (var ev in batch)
            {
                inserted += AddMissingWorkItemRows(db, ev, existingSet);
            }

            await db.SaveChangesAsync(ct);

            processed += batch.Count;
            _logger.LogInformation(
                "Backfill: processed {Processed}/{Total} events, inserted {Inserted} work-item rows so far",
                processed, total, inserted);
        }

        return inserted;
    }

    /// <summary>
    /// Stage <see cref="DeployEventWorkItem"/> inserts for any work-item references on
    /// <paramref name="ev"/> that don't already have a projection row. Never updates
    /// or removes existing rows — live ingest owns reconciliation.
    /// </summary>
    private static int AddMissingWorkItemRows(
        PlatformDbContext db,
        DeployEvent ev,
        HashSet<(Guid, string)> existingSet)
    {
        var rawRefs = Deserialize<List<ReferenceDto>>(ev.ReferencesJson) ?? new();

        var refs = rawRefs
            .Where(r => string.Equals(r.Type, "work-item", StringComparison.OrdinalIgnoreCase)
                     && !string.IsNullOrWhiteSpace(r.Key))
            .GroupBy(r => r.Key!, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

        if (refs.Count == 0) return 0;

        // Title source: mirrors the live ingest path (DeploymentService.SyncWorkItemsAsync).
        // Prefer caller-supplied per-reference Title; otherwise fall back to a single
        // enrichment.Labels["workItemTitle"] when there's exactly one work-item on the
        // event (the Enrichment shape is flat, so applying one label to multiple
        // work-items would be misleading). Otherwise leave Title null.
        var enrichment = string.IsNullOrEmpty(ev.EnrichmentJson)
            ? null
            : Deserialize<EnrichmentDto>(ev.EnrichmentJson);

        string? singleEnrichedTitle = null;
        if (refs.Count == 1 && enrichment?.Labels is not null
            && enrichment.Labels.TryGetValue("workItemTitle", out var t)
            && !string.IsNullOrWhiteSpace(t))
        {
            singleEnrichedTitle = t;
        }

        var addedHere = 0;
        foreach (var r in refs)
        {
            var lookupKey = (ev.Id, r.Key!.ToLowerInvariant());
            if (existingSet.Contains(lookupKey))
                continue;

            var title = !string.IsNullOrWhiteSpace(r.Title) ? r.Title : singleEnrichedTitle;

            db.DeployEventWorkItems.Add(new DeployEventWorkItem
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

            // Track inside the working set so duplicate references within the same event
            // (rare but possible across batches) don't cause unique-index collisions.
            existingSet.Add(lookupKey);
            addedHere++;
        }

        return addedHere;
    }

    private static T? Deserialize<T>(string? json)
    {
        if (string.IsNullOrEmpty(json)) return default;
        return JsonSerializer.Deserialize<T>(json, JsonOptions);
    }
}
