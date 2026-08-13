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

    /// <summary>
    /// The built-in participant-role vocabulary. Separate from <see cref="Defaults"/> because
    /// <see cref="ParticipantRoleSeeder"/> also merges it into an <i>existing</i> settings row: a
    /// role the platform never learned about can't be assigned to or filtered on, and installs
    /// predate the roles their producers now send.
    /// </summary>
    public static readonly List<RoleConfigDto> DefaultRoles =
    [
        new("triggered-by", "Triggered by"),
        new("author", "Author"),
        new("reviewer", "Reviewer"),
        new("qa", "QA"),
        new("qa-owner", "QA owner"),
        new("assignee", "Assignee"),
        new("reporter", "Reporter"),
    ];

    // Built-in defaults — mirror the web client's former DEFAULT_* constants so a fresh
    // install (no saved row) behaves identically to the old localStorage-seeded store.
    public static readonly AppSettingsDto Defaults = new(
        Environments:
        [
            new("development", "Development", "#2563eb"),
            new("staging", "Staging", "#d97706"),
            new("production", "Production", "#dc2626", IsProduction: true),
        ],
        // The roles producers actually send. Anything missing here reads as "not a configured role"
        // in the UI and can't be assigned to, so the defaults cover the whole vocabulary the deploy
        // ingest path emits (triggered-by, author, reviewer) plus what Jira contributes on a work
        // item (assignee, reporter, qa-owner).
        Roles: DefaultRoles,
        ActivityTemplate:
        [
            new("{ref:work-item:key} — {label:workItemTitle}", "secondary"),
            new("PR: {participant:PR Author}  ·  QA: {participant:QA}  ·  {time}", "muted"),
        ]);

    /// <summary>
    /// Normalise an environment colour to <c>#rrggbb</c>. Accepts <c>#rgb</c> shorthand and any
    /// casing, with or without the leading hash. Anything else (including blank) returns null,
    /// which the client reads as "derive a colour from the key" rather than as an error — a
    /// bad swatch should never block saving the rest of the settings.
    /// </summary>
    public static string? NormalizeHexColor(string? value)
    {
        var hex = (value ?? "").Trim().TrimStart('#');
        if (hex.Length is not (3 or 6)) return null;
        foreach (var c in hex)
        {
            if (!Uri.IsHexDigit(c)) return null;
        }
        if (hex.Length == 3) hex = string.Concat(hex[0], hex[0], hex[1], hex[1], hex[2], hex[2]);
        return "#" + hex.ToLowerInvariant();
    }

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
