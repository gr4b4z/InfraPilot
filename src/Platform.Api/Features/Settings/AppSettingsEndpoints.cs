using Platform.Api.Features.Settings.Models;
using Platform.Api.Infrastructure.Auth;

namespace Platform.Api.Features.Settings;

public static class AppSettingsEndpoints
{
    public static RouteGroupBuilder MapAppSettingsEndpoints(this RouteGroupBuilder group)
    {
        // Shared UI config consumed by the deployment views. Readable by any authenticated
        // user (the group requires auth); writes are admin-only.
        group.MapGet("/", async (AppSettingsService settings, CancellationToken ct) =>
            Results.Ok(await settings.GetSettings(ct)));

        group.MapPut("/", async (AppSettingsDto body, AppSettingsService settings, CancellationToken ct) =>
        {
            if (body is null) return Results.BadRequest(new { error = "request body is required" });

            // Drop blank-key rows defensively (the editor allows adding empty rows) and
            // trim values so lookups stay deterministic.
            var cleaned = new AppSettingsDto(
                Environments: (body.Environments ?? [])
                    .Where(e => !string.IsNullOrWhiteSpace(e.Key))
                    .Select(e => new EnvironmentConfigDto(e.Key.Trim(), (e.DisplayName ?? "").Trim()))
                    .ToList(),
                Roles: (body.Roles ?? [])
                    .Where(r => !string.IsNullOrWhiteSpace(r.Key))
                    .Select(r => new RoleConfigDto(r.Key.Trim(), (r.DisplayName ?? "").Trim()))
                    .ToList(),
                ActivityTemplate: (body.ActivityTemplate ?? [])
                    .Where(l => !string.IsNullOrWhiteSpace(l.Template))
                    .Select(l => new ActivityTemplateLineDto(l.Template, string.IsNullOrWhiteSpace(l.Style) ? "secondary" : l.Style))
                    .ToList());

            await settings.SaveSettings(cleaned, ct);
            return Results.NoContent();
        }).RequireAuthorization(AuthorizationPolicies.CatalogAdmin);

        return group;
    }
}
