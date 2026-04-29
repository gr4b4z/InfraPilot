using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Platform.Api.Infrastructure;
using Platform.Api.Infrastructure.Persistence;

namespace Platform.Api.Features.Promotions;

/// <summary>
/// Reads the configurable "assignee role set" stored in the <c>platform_settings</c> table
/// under the key <c>promotions.assignee_roles</c>. The set controls which participant roles
/// count as "assigned to" for the My-queue assignee filter — e.g. a participant with role
/// <c>qa</c> or <c>reviewer</c> is treated as an assignee, but <c>triggered-by</c> isn't
/// (that's pipeline metadata, not routing).
///
/// <para>Default when no row exists or the JSON is malformed: <c>["qa", "reviewer", "assignee"]</c>
/// (canonicalised via <see cref="RoleNormalizer.Normalize"/>).</para>
///
/// <para>There is intentionally no save / admin endpoint for this setting in the current PR.
/// Operators override it via SQL, e.g.:</para>
/// <code>
/// INSERT INTO platform_settings (key, value, updated_at, updated_by)
/// VALUES ('promotions.assignee_roles', '["qa","reviewer"]', now(), 'operator');
/// </code>
/// <para>or with an UPDATE if the row already exists. The reader re-fetches on every call —
/// the queue endpoint hits this once per request so there's no caching layer.</para>
/// </summary>
public class PromotionAssigneeRoleSettings
{
    public const string SettingKey = "promotions.assignee_roles";

    private static readonly IReadOnlyList<string> DefaultRoles = new[]
    {
        RoleNormalizer.Normalize("qa"),
        RoleNormalizer.Normalize("reviewer"),
        RoleNormalizer.Normalize("assignee"),
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly PlatformDbContext _db;
    private readonly ILogger<PromotionAssigneeRoleSettings> _logger;

    public PromotionAssigneeRoleSettings(
        PlatformDbContext db, ILogger<PromotionAssigneeRoleSettings> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>
    /// Returns the canonicalised role set the operator has configured (or the default
    /// <c>["qa", "reviewer", "assignee"]</c> if no row exists or the JSON is malformed).
    /// Each call hits the DB once — the queue endpoint only invokes this when the assignee
    /// filter is set, so there's no need for a request-scoped cache.
    /// </summary>
    public async Task<IReadOnlyList<string>> GetAsync(CancellationToken ct = default)
    {
        var row = await _db.PlatformSettings.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Key == SettingKey, ct);
        if (row is null || string.IsNullOrWhiteSpace(row.Value)) return DefaultRoles;

        try
        {
            var parsed = JsonSerializer.Deserialize<List<string>>(row.Value, JsonOptions);
            if (parsed is null || parsed.Count == 0) return DefaultRoles;

            var canonical = parsed
                .Select(RoleNormalizer.Normalize)
                .Where(s => !string.IsNullOrEmpty(s))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            return canonical.Count == 0 ? DefaultRoles : canonical;
        }
        catch (JsonException ex)
        {
            // Malformed setting shouldn't cripple the queue endpoint — log and fall back to
            // the documented default so the filter still works.
            _logger.LogError(ex,
                "Promotion assignee-role JSON is malformed; falling back to default role set");
            return DefaultRoles;
        }
    }
}
