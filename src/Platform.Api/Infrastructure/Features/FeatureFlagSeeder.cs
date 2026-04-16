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
    public static async Task SeedDefaults(PlatformDbContext db, IConfiguration config, CancellationToken ct = default)
    {
        // Map: flag key → configuration path supplying its install-time default.
        var defaults = new (string Key, string ConfigPath)[]
        {
            (FeatureFlagKeys.Promotions, "Features:Promotions:DefaultEnabled"),
        };

        var existingKeys = await db.PlatformSettings
            .Where(s => defaults.Select(d => d.Key).Contains(s.Key))
            .Select(s => s.Key)
            .ToListAsync(ct);

        var now = DateTimeOffset.UtcNow;
        var added = false;

        foreach (var (key, path) in defaults)
        {
            if (existingKeys.Contains(key)) continue;

            var enabled = config.GetValue<bool>(path, defaultValue: false);
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
