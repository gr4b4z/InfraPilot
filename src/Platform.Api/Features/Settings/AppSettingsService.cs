using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Platform.Api.Features.Settings.Models;
using Platform.Api.Infrastructure.Auth;
using Platform.Api.Infrastructure.Features;
using Platform.Api.Infrastructure.Persistence;

namespace Platform.Api.Features.Settings;

/// <summary>
/// Reads/writes the shared UI configuration (environments, roles, activity template)
/// from the generic <c>platform_settings</c> table under a single JSON row. Server is
/// the source of truth; the web client hydrates from here on load and writes through
/// on save, so the config no longer depends on per-browser localStorage.
/// </summary>
public class AppSettingsService
{
    public const string SettingsKey = "ui.app-settings";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    // Built-in defaults — mirror the web client's former DEFAULT_* constants so a fresh
    // install (no saved row) behaves identically to the old localStorage-seeded store.
    public static readonly AppSettingsDto Defaults = new(
        Environments:
        [
            new("development", "Development"),
            new("staging", "Staging"),
            new("production", "Production"),
        ],
        Roles:
        [
            new("triggered-by", "Triggered by"),
            new("author", "Author"),
            new("reviewer", "Reviewer"),
            new("qa", "QA"),
        ],
        ActivityTemplate:
        [
            new("{ref:work-item:key} — {label:workItemTitle}", "secondary"),
            new("PR: {participant:PR Author}  ·  QA: {participant:QA}  ·  {time}", "muted"),
        ]);

    private readonly PlatformDbContext _db;
    private readonly ICurrentUser _user;

    public AppSettingsService(PlatformDbContext db, ICurrentUser user)
    {
        _db = db;
        _user = user;
    }

    public async Task<AppSettingsDto> GetSettings(CancellationToken ct = default)
    {
        var row = await _db.PlatformSettings.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Key == SettingsKey, ct);
        if (row is null || string.IsNullOrWhiteSpace(row.Value)) return Defaults;

        try
        {
            return JsonSerializer.Deserialize<AppSettingsDto>(row.Value, JsonOptions) ?? Defaults;
        }
        catch (JsonException)
        {
            // A malformed row should never strip the UI of its config — fall back to defaults.
            return Defaults;
        }
    }

    public async Task SaveSettings(AppSettingsDto settings, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(settings, JsonOptions);
        var existing = await _db.PlatformSettings.FirstOrDefaultAsync(s => s.Key == SettingsKey, ct);
        var actor = !string.IsNullOrEmpty(_user.Email) ? _user.Email : _user.Name;

        if (existing is null)
        {
            _db.PlatformSettings.Add(new PlatformSetting
            {
                Key = SettingsKey,
                Value = json,
                UpdatedAt = DateTimeOffset.UtcNow,
                UpdatedBy = actor,
            });
        }
        else
        {
            existing.Value = json;
            existing.UpdatedAt = DateTimeOffset.UtcNow;
            existing.UpdatedBy = actor;
        }
        await _db.SaveChangesAsync(ct);
    }
}
