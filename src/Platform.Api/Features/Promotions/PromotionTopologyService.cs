using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Platform.Api.Features.Promotions.Models;
using Platform.Api.Infrastructure.Audit;
using Platform.Api.Infrastructure.Features;
using Platform.Api.Infrastructure.Persistence;

namespace Platform.Api.Features.Promotions;

/// <summary>
/// Reads and writes the <see cref="PromotionTopology"/> stored in the <c>platform_settings</c>
/// table under the key <c>promotions.topology</c>.
///
/// <para>The topology is the source of truth for "which environments can promote to which" —
/// the ingest hook calls <see cref="GetNextEnvironmentsAsync"/> to decide how many candidates
/// to generate for a new deploy event.</para>
/// </summary>
public class PromotionTopologyService
{
    public const string SettingKey = "promotions.topology";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly PlatformDbContext _db;
    private readonly IAuditLogger _audit;
    private readonly ILogger<PromotionTopologyService> _logger;

    public PromotionTopologyService(
        PlatformDbContext db, IAuditLogger audit, ILogger<PromotionTopologyService> logger)
    {
        _db = db;
        _audit = audit;
        _logger = logger;
    }

    public async Task<PromotionTopology> GetAsync(CancellationToken ct = default)
    {
        var row = await _db.PlatformSettings.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Key == SettingKey, ct);
        if (row is null || string.IsNullOrWhiteSpace(row.Value)) return PromotionTopology.Empty;

        try
        {
            return JsonSerializer.Deserialize<PromotionTopology>(row.Value, JsonOptions)
                ?? PromotionTopology.Empty;
        }
        catch (JsonException ex)
        {
            // Don't cripple ingest if the operator saved malformed JSON — log loudly and fall back
            // to Empty so the system fails closed (no candidates) rather than open (random ones).
            _logger.LogError(ex, "Promotion topology JSON is malformed; treating as empty");
            return PromotionTopology.Empty;
        }
    }

    /// <summary>
    /// Convenience wrapper: returns the list of environments reachable from <paramref name="sourceEnv"/>.
    /// Used by the ingest hook to decide how many candidates to create.
    /// </summary>
    public async Task<IReadOnlyList<string>> GetNextEnvironmentsAsync(
        string sourceEnv, CancellationToken ct = default)
    {
        var topology = await GetAsync(ct);
        return topology.NextFrom(sourceEnv).ToList();
    }

    public async Task SaveAsync(PromotionTopology topology, string updatedBy, CancellationToken ct = default)
    {
        Validate(topology);

        var json = JsonSerializer.Serialize(topology, JsonOptions);
        var now = DateTimeOffset.UtcNow;

        var row = await _db.PlatformSettings.FirstOrDefaultAsync(s => s.Key == SettingKey, ct);
        if (row is null)
        {
            _db.PlatformSettings.Add(new PlatformSetting
            {
                Key = SettingKey,
                Value = json,
                UpdatedAt = now,
                UpdatedBy = updatedBy,
            });
        }
        else
        {
            row.Value = json;
            row.UpdatedAt = now;
            row.UpdatedBy = updatedBy;
        }

        await _db.SaveChangesAsync(ct);

        await _audit.Log(
            "promotions", "topology.updated",
            updatedBy, updatedBy, "user",
            "PromotionTopology", Guid.Empty, null,
            new { envCount = topology.Environments.Count, edgeCount = topology.Edges.Count });

        _logger.LogInformation(
            "Promotion topology updated by {UpdatedBy}: {EnvCount} envs, {EdgeCount} edges",
            updatedBy, topology.Environments.Count, topology.Edges.Count);
    }

    private static void Validate(PromotionTopology topology)
    {
        // Every edge must reference environments that are in the node list — catches typos
        // at save time instead of silently dropping candidates at ingest time.
        var envSet = topology.Environments
            .Select(e => e.Trim())
            .Where(e => !string.IsNullOrEmpty(e))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var edge in topology.Edges)
        {
            if (string.IsNullOrWhiteSpace(edge.From) || string.IsNullOrWhiteSpace(edge.To))
                throw new ArgumentException("Edge endpoints must be non-empty");
            if (string.Equals(edge.From, edge.To, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException($"Edge cannot loop on itself: {edge.From} → {edge.To}");
            if (!envSet.Contains(edge.From))
                throw new ArgumentException($"Edge source '{edge.From}' is not in the environments list");
            if (!envSet.Contains(edge.To))
                throw new ArgumentException($"Edge target '{edge.To}' is not in the environments list");
        }

        // Reject duplicate edges so the UI round-trips cleanly.
        var duplicates = topology.Edges
            .GroupBy(e => (e.From.ToLowerInvariant(), e.To.ToLowerInvariant()))
            .Where(g => g.Count() > 1)
            .Select(g => $"{g.Key.Item1} → {g.Key.Item2}")
            .ToList();
        if (duplicates.Count > 0)
            throw new ArgumentException("Duplicate edges: " + string.Join(", ", duplicates));
    }
}
