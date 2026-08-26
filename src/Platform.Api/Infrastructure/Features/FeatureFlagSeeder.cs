using Microsoft.EntityFrameworkCore;
using Platform.Api.Infrastructure.Persistence;

namespace Platform.Api.Infrastructure.Features;

/// <summary>
/// Startup seeder that inserts default rows for well-known feature flags so admins can
/// toggle them from the UI without first having to create the row. Existing operator
/// values are never overwritten.
/// </summary>
public static class FeatureFlagSeeder
{
    /// <param name="enableAllByDefault">
    /// Turn every flag on where neither configuration nor an existing row says otherwise. Passed for
    /// Development: the install-time defaults below are deliberately conservative — Promotions,
    /// Rollbacks and Release notes ship off — which on a fresh dev database hides most of the product
    /// behind an admin toggle nobody knows to look for. Explicit configuration still wins, so an
    /// appsettings override that says <c>false</c> stays false.
    /// </param>
    public static async Task SeedDefaults(
        PlatformDbContext db, IConfiguration config, bool enableAllByDefault = false, CancellationToken ct = default)
    {
        // Map: flag key → configuration path supplying its install-time default.
        var defaults = new (string Key, string ConfigPath, bool FallbackDefault)[]
        {
            (FeatureFlagKeys.Promotions, "Features:Promotions:DefaultEnabled", false),
            (FeatureFlagKeys.Rollbacks, "Features:Rollbacks:DefaultEnabled", false),
            (FeatureFlagKeys.ServiceCatalog, "Features:ServiceCatalog:DefaultEnabled", true),
            (FeatureFlagKeys.Approvals, "Features:Approvals:DefaultEnabled", true),
            (FeatureFlagKeys.ReleaseNotes, "Features:ReleaseNotes:DefaultEnabled", false),
            (FeatureFlagKeys.Analytics, "Features:Analytics:DefaultEnabled", false),
        };

        var existingKeys = await db.PlatformSettings
            .Where(s => defaults.Select(d => d.Key).Contains(s.Key))
            .Select(s => s.Key)
            .ToListAsync(ct);

        var now = DateTimeOffset.UtcNow;
        var added = false;

        foreach (var (key, path, fallback) in defaults)
        {
            if (existingKeys.Contains(key)) continue;

            // bool? so "configured" and "configured false" stay distinguishable — with a plain bool
            // default, enableAllByDefault would silently override an operator's explicit false.
            var enabled = config.GetValue<bool?>(path) ?? (enableAllByDefault || fallback);
            db.PlatformSettings.Add(new PlatformSetting
            {
                Key = key,
                Value = enabled ? "true" : "false",
                UpdatedAt = now,
                UpdatedBy = "system",
            });
            added = true;
        }

        if (added)
            await db.SaveChangesAsync(ct);
    }
}
