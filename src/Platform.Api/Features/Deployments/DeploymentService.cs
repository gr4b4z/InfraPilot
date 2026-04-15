using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Platform.Api.Features.Deployments.Models;
using Platform.Api.Features.Webhooks;
using Platform.Api.Infrastructure.Persistence;

namespace Platform.Api.Features.Deployments;

public class DeploymentService
{
    private readonly PlatformDbContext _db;
    private readonly IWebhookDispatcher _webhookDispatcher;
    private readonly ILogger<DeploymentService> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public DeploymentService(PlatformDbContext db, IWebhookDispatcher webhookDispatcher, ILogger<DeploymentService> logger)
    {
        _db = db;
        _webhookDispatcher = webhookDispatcher;
        _logger = logger;
    }

    public async Task<DeployEvent> IngestEvent(CreateDeployEventDto dto, CancellationToken ct = default)
    {
        // Look up previous version for same product+service+environment
        var previousEvent = await _db.DeployEvents
            .Where(e => e.Product == dto.Product && e.Service == dto.Service && e.Environment == dto.Environment)
            .OrderByDescending(e => e.DeployedAt)
            .Select(e => new { e.Version })
            .FirstOrDefaultAsync(ct);

        var status = dto.Status ?? "succeeded";
        var deployEvent = new DeployEvent
        {
            Id = Guid.NewGuid(),
            Product = dto.Product,
            Service = dto.Service,
            Environment = dto.Environment,
            Version = dto.Version,
            PreviousVersion = previousEvent?.Version,
            IsRollback = dto.IsRollback,
            Status = status,
            Source = dto.Source,
            DeployedAt = dto.DeployedAt,
            ReferencesJson = JsonSerializer.Serialize(dto.References ?? [], JsonOptions),
            ParticipantsJson = JsonSerializer.Serialize(dto.Participants ?? [], JsonOptions),
            MetadataJson = JsonSerializer.Serialize(dto.Metadata ?? new Dictionary<string, object>(), JsonOptions),
            CreatedAt = DateTimeOffset.UtcNow,
        };

        _db.DeployEvents.Add(deployEvent);
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

        return deployEvent;
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
