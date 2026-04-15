using Microsoft.EntityFrameworkCore;
using Platform.Api.Infrastructure.Auth;
using Platform.Api.Infrastructure.Persistence;

namespace Platform.Api.Infrastructure.Features;

/// <summary>
/// Admin endpoints for inspecting and toggling feature flags. Read-only listing is allowed for
/// any authenticated user (so the UI can gate links and tabs consistently without extra roles);
/// mutation requires admin. Routes are grouped under <c>/api/features</c>.
/// </summary>
public static class FeatureFlagEndpoints
{
    public static RouteGroupBuilder MapFeatureFlagEndpoints(this RouteGroupBuilder group)
    {
        // List all known flags. Current behavior: return every row from platform_settings whose
        // key starts with "features." — keeps the endpoint generic as new flags are added.
        group.MapGet("/", async (PlatformDbContext db) =>
        {
            var rows = await db.PlatformSettings.AsNoTracking()
                .Where(s => s.Key.StartsWith("features."))
                .OrderBy(s => s.Key)
                .ToListAsync();

            return Results.Ok(new
            {
                flags = rows.Select(r => new
                {
                    key = r.Key,
                    enabled = string.Equals(r.Value, "true", StringComparison.OrdinalIgnoreCase),
                    updatedAt = r.UpdatedAt,
                    updatedBy = r.UpdatedBy,
                }),
            });
        }).RequireAuthorization();

        // Single flag read — anonymous-friendly so the login / shell components can decide
        // whether to even render a link without forcing a full list fetch.
        group.MapGet("/{key}", async (IFeatureFlags flags, string key) =>
        {
            var enabled = await flags.IsEnabled(key);
            return Results.Ok(new { key, enabled });
        }).RequireAuthorization();

        // Toggle — admin only.
        group.MapPut("/{key}", async (
            IFeatureFlags flags, ICurrentUser user, string key, SetFlagRequest request) =>
        {
            await flags.SetEnabled(key, request.Enabled, user.Email ?? user.Name);
            return Results.Ok(new { key, enabled = request.Enabled });
        }).RequireAuthorization(AuthorizationPolicies.CatalogAdmin);

        return group;
    }
}

public record SetFlagRequest(bool Enabled);
