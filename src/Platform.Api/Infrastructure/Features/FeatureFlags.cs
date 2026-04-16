using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Platform.Api.Infrastructure.Logging;
using Platform.Api.Infrastructure.Persistence;

namespace Platform.Api.Infrastructure.Features;

/// <summary>
/// Default <see cref="IFeatureFlags"/> implementation. Values live in <c>platform_settings</c>
/// (canonical serialisation: literal strings "true" / "false"). A 30-second per-flag cache
/// keeps hot-path checks from hitting the database; <see cref="SetEnabled"/> invalidates the
/// flag's cache entry synchronously so the change is visible immediately on this node.
/// </summary>
public class FeatureFlags : IFeatureFlags
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);

    private readonly PlatformDbContext _db;
    private readonly ILogger<FeatureFlags> _logger;

    // Process-wide cache shared across all scoped FeatureFlags instances.
    // Key = feature name. Value = (expiresAt, enabled).
    private static readonly ConcurrentDictionary<string, (DateTimeOffset ExpiresAt, bool Enabled)> _cache = new();

    public FeatureFlags(PlatformDbContext db, ILogger<FeatureFlags> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<bool> IsEnabled(string feature, CancellationToken ct = default)
    {
        if (_cache.TryGetValue(feature, out var cached) && cached.ExpiresAt > DateTimeOffset.UtcNow)
            return cached.Enabled;

        var row = await _db.PlatformSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Key == feature, ct);

        var enabled = row is not null
            && string.Equals(row.Value, "true", StringComparison.OrdinalIgnoreCase);

        _cache[feature] = (DateTimeOffset.UtcNow.Add(CacheTtl), enabled);
        return enabled;
    }

    public async Task SetEnabled(string feature, bool enabled, string updatedBy, CancellationToken ct = default)
    {
        var row = await _db.PlatformSettings.FirstOrDefaultAsync(s => s.Key == feature, ct);
        var value = enabled ? "true" : "false";
        var now = DateTimeOffset.UtcNow;

        if (row is null)
        {
            _db.PlatformSettings.Add(new PlatformSetting
            {
                Key = feature,
                Value = value,
                UpdatedAt = now,
                UpdatedBy = updatedBy,
            });
        }
        else
        {
            row.Value = value;
            row.UpdatedAt = now;
            row.UpdatedBy = updatedBy;
        }

        await _db.SaveChangesAsync(ct);

        // Invalidate on success so a failed save doesn't leak a stale cache update.
        _cache[feature] = (DateTimeOffset.UtcNow.Add(CacheTtl), enabled);
        _logger.LogInformation("Feature flag {Feature} set to {Enabled} by {UpdatedBy}", LogSanitizer.Sanitize(feature), enabled, LogSanitizer.Sanitize(updatedBy));
    }

    /// <summary>
    /// Test hook — clears the process-wide cache. Never call from production code paths.
    /// </summary>
    public static void ClearCacheForTesting() => _cache.Clear();
}
