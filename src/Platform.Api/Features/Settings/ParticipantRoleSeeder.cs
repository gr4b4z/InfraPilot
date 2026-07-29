using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Platform.Api.Features.Settings.Models;
using Platform.Api.Infrastructure;
using Platform.Api.Infrastructure.Features;
using Platform.Api.Infrastructure.Persistence;

namespace Platform.Api.Features.Settings;

/// <summary>
/// One-time startup merge of <see cref="AppSettingsService.DefaultRoles"/> into an existing
/// <c>ui.app-settings</c> row.
///
/// <para><see cref="AppSettingsService.Defaults"/> only applies when no row exists, so an install
/// that saved its settings before a role was added to the defaults would never learn about it — and
/// a role the platform doesn't know about can't be assigned to or filtered on, which is how tickets
/// end up with permanently unroutable slots (the "not a configured role" warning). This backfills
/// the gap.</para>
///
/// <para>Runs at most once, guarded by a marker row. That matters: an admin who deliberately removes
/// a role must be able to make it stay removed, so this cannot be a merge on every startup. Only
/// missing keys are appended — configured order, labels, and anything the admin added are untouched.</para>
/// </summary>
public static class ParticipantRoleSeeder
{
    /// <summary>
    /// Marker recording that the backfill has run. Versioned: a future addition to the default
    /// vocabulary gets its own marker rather than re-running this one.
    /// </summary>
    public const string MarkerKey = "ui.app-settings.default-roles-merged.v1";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public static async Task MergeDefaults(PlatformDbContext db, ILogger? logger = null, CancellationToken ct = default)
    {
        var alreadyRun = await db.PlatformSettings.AsNoTracking()
            .AnyAsync(s => s.Key == MarkerKey, ct);
        if (alreadyRun) return;

        var now = DateTimeOffset.UtcNow;
        var settingsRow = await db.PlatformSettings
            .FirstOrDefaultAsync(s => s.Key == AppSettingsService.SettingsKey, ct);

        // No row means the install is already reading Defaults, which include every default role —
        // nothing to merge. The marker still goes down so this never reconsiders.
        if (settingsRow is not null && !string.IsNullOrWhiteSpace(settingsRow.Value))
        {
            AppSettingsDto? settings = null;
            try
            {
                settings = JsonSerializer.Deserialize<AppSettingsDto>(settingsRow.Value, JsonOptions);
            }
            catch (JsonException ex)
            {
                // A malformed row is already being served as Defaults by AppSettingsService; rewriting
                // it here would be guesswork about what the admin meant. Leave it be.
                logger?.LogWarning(ex,
                    "Participant-role backfill skipped: {Key} is not valid JSON", AppSettingsService.SettingsKey);
            }

            if (settings is not null)
            {
                var existing = settings.Roles ?? [];
                var present = existing
                    .Select(r => RoleNormalizer.Normalize(r.Key))
                    .Where(k => k.Length > 0)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                var missing = AppSettingsService.DefaultRoles
                    .Where(r => !present.Contains(RoleNormalizer.Normalize(r.Key)))
                    .ToList();

                if (missing.Count > 0)
                {
                    // Appended, not merged in place: the configured order is the order the UI's
                    // pickers used to render, and reordering an admin's list is not this seeder's call.
                    var merged = settings with { Roles = [.. existing, .. missing] };
                    settingsRow.Value = JsonSerializer.Serialize(merged, JsonOptions);
                    settingsRow.UpdatedAt = now;
                    settingsRow.UpdatedBy = "system";

                    logger?.LogInformation(
                        "Added {Count} default participant role(s) to the configured vocabulary: {Roles}",
                        missing.Count, string.Join(", ", missing.Select(r => r.Key)));
                }
            }
        }

        db.PlatformSettings.Add(new PlatformSetting
        {
            Key = MarkerKey,
            Value = "true",
            UpdatedAt = now,
            UpdatedBy = "system",
        });
        await db.SaveChangesAsync(ct);
    }
}
