using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Platform.Api.Features.Deployments.Models;
using Microsoft.Extensions.Options;
using Platform.Api.Features.Promotions;
using Platform.Api.Features.Webhooks;
using Platform.Api.Infrastructure;
using Platform.Api.Infrastructure.Persistence;

namespace Platform.Api.Features.Deployments;

public class DeploymentService
{
    private readonly PlatformDbContext _db;
    private readonly IWebhookDispatcher _webhookDispatcher;
    private readonly IPromotionIngestHook _promotionHook;
    private readonly IOptionsMonitor<NormalizationOptions> _normalization;
    private readonly ILogger<DeploymentService> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public DeploymentService(
        PlatformDbContext db,
        IWebhookDispatcher webhookDispatcher,
        IPromotionIngestHook promotionHook,
        IOptionsMonitor<NormalizationOptions> normalization,
        ILogger<DeploymentService> logger)
    {
        _db = db;
        _webhookDispatcher = webhookDispatcher;
        _promotionHook = promotionHook;
        _normalization = normalization;
        _logger = logger;
    }

    public async Task<DeployEvent> IngestEvent(CreateDeployEventDto dto, CancellationToken ct = default)
    {
        var norm = _normalization.CurrentValue;

        // Optional canonicalisation — controlled by appsettings `Normalization:*`. Off by
        // default, so senders' original casing is preserved unless an admin opts in.
        var environment = norm.ApplyEnvironment(dto.Environment);

        // Use caller-supplied previousVersion when present (lets integrators assert the
        // predecessor they observed and detect drift vs. the server's history). Otherwise
        // derive it from the most recent event for the same product+service+environment.
        string? previousVersion = dto.PreviousVersion;
        if (previousVersion is null)
        {
            var previousEvent = await _db.DeployEvents
                .Where(e => e.Product == dto.Product && e.Service == dto.Service && e.Environment == environment)
                .OrderByDescending(e => e.DeployedAt)
                .Select(e => new { e.Version })
                .FirstOrDefaultAsync(ct);
            previousVersion = previousEvent?.Version;
        }

        var status = dto.Status ?? "succeeded";
        var deployEvent = new DeployEvent
        {
            Id = Guid.NewGuid(),
            Product = dto.Product,
            Service = dto.Service,
            Environment = environment,
            Version = dto.Version,
            PreviousVersion = previousVersion,
            IsRollback = dto.IsRollback,
            Status = status,
            Source = dto.Source,
            DeployedAt = dto.DeployedAt,
            ReferencesJson = JsonSerializer.Serialize(
                (dto.References ?? []).Select(r => new ReferenceDto(
                    Type: r.Type,
                    Url: r.Url,
                    Provider: r.Provider,
                    Key: r.Key,
                    Revision: r.Revision,
                    Title: r.Title,
                    // Apply the same role canonicalisation to nested participants so
                    // reference-level roles are stored in the same shape as event-level.
                    Participants: r.Participants is null
                        ? null
                        : r.Participants.Select(p => new ParticipantDto(
                            Role: norm.ApplyRole(p.Role),
                            DisplayName: p.DisplayName,
                            Email: p.Email)).ToList())).ToList(),
                JsonOptions),
            ParticipantsJson = JsonSerializer.Serialize(
                (dto.Participants ?? []).Select(p => new ParticipantDto(
                    Role: norm.ApplyRole(p.Role),
                    DisplayName: p.DisplayName,
                    Email: p.Email)).ToList(),
                JsonOptions),
            MetadataJson = JsonSerializer.Serialize(dto.Metadata ?? new Dictionary<string, object>(), JsonOptions),
            CreatedAt = DateTimeOffset.UtcNow,
        };

        _db.DeployEvents.Add(deployEvent);
        await SyncWorkItemsAsync(deployEvent, ct);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Ingested deploy event {Id}: {Product}/{Service} → {Environment} v{Version} (prev: {PreviousVersion})",
            deployEvent.Id, deployEvent.Product, deployEvent.Service, deployEvent.Environment,
            deployEvent.Version, deployEvent.PreviousVersion ?? "none");

        await _webhookDispatcher.DispatchAsync("deployment.created", new
        {
            deployEvent.Id,
            deployEvent.Product,
            deployEvent.Service,
            deployEvent.Environment,
            deployEvent.Version,
            deployEvent.PreviousVersion,
            deployEvent.IsRollback,
            deployEvent.Status,
            deployEvent.Source,
            deployEvent.DeployedAt,
        }, new WebhookEventFilters(deployEvent.Product, deployEvent.Environment));

        // Fire-and-observe: generate promotion candidates / close in-flight ones. The hook is
        // feature-flag gated internally and swallows its own failures so ingestion stays
        // resilient even when the promotion machinery misbehaves.
        await _promotionHook.OnIngestedAsync(deployEvent, ct);

        return deployEvent;
    }

    /// <summary>
    /// Returns the distinct versions that have been deployed to the given (product, service, environment),
    /// most-recent-first. Intended as the backing data source for a rollback picker in the UI:
    /// each item carries the deploy id, version, deployer, and timestamp so the UI can show a
    /// meaningful label ("v1.2.3 — deployed 2 days ago by alice").
    ///
    /// <para><c>product</c> and <c>environment</c> are required; <c>service</c> is optional and
    /// when omitted returns versions across all services for the product/environment. Results
    /// are capped by <paramref name="limit"/> (default 50).</para>
    /// </summary>
    public async Task<List<DeploymentVersionDto>> GetVersions(
        string product, string environment, string? serviceName,
        int limit = 50, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(product) || string.IsNullOrWhiteSpace(environment))
            return new List<DeploymentVersionDto>();

        var query = _db.DeployEvents.AsNoTracking()
            .Where(e => e.Product == product && e.Environment == environment);
        if (!string.IsNullOrWhiteSpace(serviceName))
            query = query.Where(e => e.Service == serviceName);

        // Only successful deploys are rollback candidates; failed events don't represent a
        // real deployed version to go back to.
        query = query.Where(e => e.Status == "succeeded");

        // DeployedAt-desc with a DeployEventId tiebreak (LINQ `.First()` inside GroupBy would
        // be the natural shape but the in-memory provider doesn't translate it cleanly, so we
        // project, order, and then distinct-by version client-side.)
        var raw = await query
            .OrderByDescending(e => e.DeployedAt)
            .Select(e => new
            {
                e.Id,
                e.Service,
                e.Version,
                e.DeployedAt,
                e.IsRollback,
                e.ParticipantsJson,
            })
            .Take(Math.Max(1, limit) * 4) // oversample so distinct-by-version still hits the limit
            .ToListAsync(ct);

        var versions = new List<DeploymentVersionDto>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in raw)
        {
            // Unique key includes service — same version number across two services is not a duplicate.
            var key = $"{e.Service}\0{e.Version}";
            if (!seen.Add(key)) continue;

            string? deployer = null;
            if (!string.IsNullOrWhiteSpace(e.ParticipantsJson))
            {
                try
                {
                    var parts = JsonSerializer.Deserialize<List<ParticipantDto>>(e.ParticipantsJson, JsonOptions);
                    // Match after normalization so this works whether or not ingest-time
                    // canonicalisation is enabled.
                    deployer = parts?.FirstOrDefault(p =>
                        RoleNormalizer.Normalize(p.Role) == "triggered-by")?.Email;
                }
                catch { /* best-effort */ }
            }

            versions.Add(new DeploymentVersionDto(
                Id: e.Id,
                Service: e.Service,
                Version: e.Version,
                DeployedAt: e.DeployedAt,
                DeployerEmail: deployer,
                IsRollback: e.IsRollback));

            if (versions.Count >= limit) break;
        }

        return versions;
    }

    public async Task<List<DeploymentStateDto>> GetState(string? product, string? environment, string? serviceName, CancellationToken ct = default)
    {
        var query = _db.DeployEvents.AsQueryable();
        if (!string.IsNullOrEmpty(product)) query = query.Where(e => e.Product == product);
        if (!string.IsNullOrEmpty(environment)) query = query.Where(e => e.Environment == environment);
        if (!string.IsNullOrEmpty(serviceName)) query = query.Where(e => e.Service == serviceName);

        // Latest event per (product, service, environment) using a window-function approach
        var latest = await query
            .GroupBy(e => new { e.Product, e.Service, e.Environment })
            .Select(g => g.OrderByDescending(e => e.DeployedAt).First())
            .ToListAsync(ct);

        return latest.Select(MapToStateDto).ToList();
    }

    public async Task<List<ProductSummaryDto>> GetProductSummaries(CancellationToken ct = default)
    {
        var latest = await _db.DeployEvents
            .GroupBy(e => new { e.Product, e.Service, e.Environment })
            .Select(g => g.OrderByDescending(e => e.DeployedAt).First())
            .ToListAsync(ct);

        var grouped = latest
            .GroupBy(e => e.Product)
            .Select(pg =>
            {
                var environments = pg
                    .GroupBy(e => e.Environment)
                    .ToDictionary(
                        eg => eg.Key,
                        eg => new EnvironmentSummaryDto(
                            TotalServices: eg.Count(),
                            DeployedServices: eg.Count(),
                            LastDeployedAt: eg.Max(e => e.DeployedAt)));

                return new ProductSummaryDto(pg.Key, environments);
            })
            .ToList();

        return grouped;
    }

    public async Task<List<DeployEventResponseDto>> GetHistory(
        string product, string service, string? environment, int limit = 50, CancellationToken ct = default)
    {
        var query = _db.DeployEvents
            .Where(e => e.Product == product && e.Service == service);

        if (!string.IsNullOrEmpty(environment))
            query = query.Where(e => e.Environment == environment);

        var events = await query
            .OrderByDescending(e => e.DeployedAt)
            .Take(limit)
            .ToListAsync(ct);

        return events.Select(MapToResponseDto).ToList();
    }

    public async Task<List<DeployEventResponseDto>> GetRecentByEnvironment(
        string product, string environment, DateTimeOffset since, CancellationToken ct = default)
    {
        var events = await _db.DeployEvents
            .Where(e => e.Product == product && e.Environment == environment && e.DeployedAt >= since)
            .OrderByDescending(e => e.DeployedAt)
            .ToListAsync(ct);

        return events.Select(MapToResponseDto).ToList();
    }

    public async Task<List<DeployEventResponseDto>> GetRecentByProduct(
        string product, DateTimeOffset since, int limit = 200, CancellationToken ct = default)
    {
        var events = await _db.DeployEvents
            .Where(e => e.Product == product && e.DeployedAt >= since)
            .OrderByDescending(e => e.DeployedAt)
            .Take(limit)
            .ToListAsync(ct);

        return events.Select(MapToResponseDto).ToList();
    }

    // --- Admin: duplicate cleanup ---

    /// <summary>
    /// Natural key used to detect a DeployEvent that was ingested twice.
    /// Rows matching on every field here are duplicates; the earliest-created one is kept.
    /// </summary>
    private readonly record struct DuplicateKey(
        string Product, string Service, string Environment, string Version, DateTimeOffset DeployedAt, string Source);

    /// <summary>Count of duplicate groups and total rows that would be removed by <see cref="RemoveDuplicates"/>.</summary>
    public async Task<(int Groups, int Rows)> CountDuplicates(CancellationToken ct = default)
    {
        // Pull only the natural-key fields to keep the query light.
        var keys = await _db.DeployEvents
            .Select(e => new { e.Product, e.Service, e.Environment, e.Version, e.DeployedAt, e.Source })
            .ToListAsync(ct);

        var grouped = keys
            .GroupBy(k => new DuplicateKey(k.Product, k.Service, k.Environment, k.Version, k.DeployedAt, k.Source))
            .Where(g => g.Count() > 1)
            .ToList();

        var groups = grouped.Count;
        var rows = grouped.Sum(g => g.Count() - 1);
        return (groups, rows);
    }

    /// <summary>
    /// Deletes duplicate DeployEvent rows, keeping the one with the earliest <c>CreatedAt</c> per natural-key group.
    /// Returns the number of distinct groups touched and total rows removed.
    /// </summary>
    public async Task<(int Groups, int Rows)> RemoveDuplicates(CancellationToken ct = default)
    {
        // Fetch just what we need to partition client-side. We can't delete directly in SQL
        // because the "keep earliest" rule is easier to express in memory and keeps this
        // provider-agnostic across Postgres + SqlServer.
        var rows = await _db.DeployEvents
            .Select(e => new { e.Id, e.Product, e.Service, e.Environment, e.Version, e.DeployedAt, e.Source, e.CreatedAt })
            .ToListAsync(ct);

        var toDelete = rows
            .GroupBy(r => new DuplicateKey(r.Product, r.Service, r.Environment, r.Version, r.DeployedAt, r.Source))
            .Where(g => g.Count() > 1)
            .SelectMany(g => g.OrderBy(r => r.CreatedAt).Skip(1)) // keep earliest, drop the rest
            .Select(r => r.Id)
            .ToList();

        if (toDelete.Count == 0) return (0, 0);

        var groupCount = rows
            .GroupBy(r => new DuplicateKey(r.Product, r.Service, r.Environment, r.Version, r.DeployedAt, r.Source))
            .Count(g => g.Count() > 1);

        // Single SaveChanges is atomic at the EF level (one DB transaction under the hood).
        var idSet = toDelete.ToHashSet();
        var stale = await _db.DeployEvents.Where(e => idSet.Contains(e.Id)).ToListAsync(ct);
        _db.DeployEvents.RemoveRange(stale);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Deploy-event dedup removed {Rows} rows across {Groups} groups",
            stale.Count, groupCount);

        return (groupCount, stale.Count);
    }

    // --- Work-item extraction ---

    /// <summary>
    /// Extract work-item references from <paramref name="ev"/> and reconcile the
    /// <see cref="DeployEventWorkItem"/> rows for that event so they match the references
    /// list 1:1. Idempotent: re-running with the same references is a no-op; re-running
    /// after enrichment fills in titles will update existing rows in place; references that
    /// disappeared from the event are removed.
    ///
    /// Caller is responsible for the surrounding <c>SaveChangesAsync</c> — this method only
    /// stages adds/updates/removes on the tracked <see cref="PlatformDbContext"/>.
    /// </summary>
    public async Task SyncWorkItemsAsync(DeployEvent ev, CancellationToken ct = default)
    {
        // Parse via ReferenceDto so any caller-supplied Title in the input JSON survives
        // — Reference (the simple model) intentionally omits Title.
        var rawRefs = Deserialize<List<ReferenceDto>>(ev.ReferencesJson) ?? [];

        var refs = rawRefs
            .Where(r => string.Equals(r.Type, "work-item", StringComparison.OrdinalIgnoreCase)
                     && !string.IsNullOrWhiteSpace(r.Key))
            .GroupBy(r => r.Key!, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First()) // dedupe within the same event
            .ToList();

        var existing = await _db.DeployEventWorkItems
            .Where(w => w.DeployEventId == ev.Id)
            .ToListAsync(ct);

        var existingByKey = existing.ToDictionary(
            w => w.WorkItemKey, StringComparer.OrdinalIgnoreCase);

        var changed = 0;

        // Remove rows whose key is no longer in the references list — re-ingest treats
        // the references list as authoritative.
        var freshKeys = refs.Select(r => r.Key!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var stale in existing.Where(w => !freshKeys.Contains(w.WorkItemKey)))
        {
            _db.DeployEventWorkItems.Remove(stale);
            changed++;
        }

        // Title source: best-effort. Prefer caller-supplied per-reference Title; otherwise
        // fall back to the single enrichment.Labels["workItemTitle"] when there's exactly
        // one work-item on the event (the Enrichment shape is flat — no per-key titles —
        // so applying the same label to multiple work-items would be misleading).
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
                // Update mutable fields in case the source event was edited or enriched.
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
        {
            _logger.LogInformation(
                "Extracted {Count} work-items from deploy event {EventId}",
                refs.Count, ev.Id);
        }
    }

    // --- Mapping helpers ---

    private static DeploymentStateDto MapToStateDto(DeployEvent e)
    {
        var refs = Deserialize<List<ReferenceDto>>(e.ReferencesJson) ?? [];
        var parts = Deserialize<List<ParticipantDto>>(e.ParticipantsJson) ?? [];
        var enrichment = string.IsNullOrEmpty(e.EnrichmentJson)
            ? null
            : Deserialize<EnrichmentDto>(e.EnrichmentJson);

        return new DeploymentStateDto(
            e.Product, e.Service, e.Environment, e.Version, e.PreviousVersion,
            e.IsRollback, e.Status, e.Source, e.DeployedAt, refs, parts, enrichment);
    }

    private static DeployEventResponseDto MapToResponseDto(DeployEvent e)
    {
        var refs = Deserialize<List<ReferenceDto>>(e.ReferencesJson) ?? [];
        var parts = Deserialize<List<ParticipantDto>>(e.ParticipantsJson) ?? [];
        var enrichment = string.IsNullOrEmpty(e.EnrichmentJson)
            ? null
            : Deserialize<EnrichmentDto>(e.EnrichmentJson);
        var metadata = Deserialize<Dictionary<string, object>>(e.MetadataJson);

        return new DeployEventResponseDto(
            e.Id, e.Product, e.Service, e.Environment, e.Version, e.PreviousVersion,
            e.IsRollback, e.Status, e.Source, e.DeployedAt, refs, parts, enrichment, metadata);
    }

    private static T? Deserialize<T>(string? json)
    {
        if (string.IsNullOrEmpty(json)) return default;
        return JsonSerializer.Deserialize<T>(json, JsonOptions);
    }
}
