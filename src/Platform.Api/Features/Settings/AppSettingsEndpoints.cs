using Platform.Api.Features.Settings.Models;
using Platform.Api.Infrastructure.Audit;
using Platform.Api.Infrastructure.Auth;
using Platform.Api.Infrastructure.Realtime;

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
                    .Select(e => new EnvironmentConfigDto(
                        e.Key.Trim(),
                        (e.DisplayName ?? "").Trim(),
                        AppSettingsService.NormalizeHexColor(e.Color),
                        e.IsProduction,
                        EnvironmentAliasValidator.CleanAliases(e.Key, e.Aliases)))
                    .ToList(),
                Roles: (body.Roles ?? [])
                    .Where(r => !string.IsNullOrWhiteSpace(r.Key))
                    .Select(r => new RoleConfigDto(r.Key.Trim(), (r.DisplayName ?? "").Trim()))
                    .ToList(),
                ActivityTemplate: (body.ActivityTemplate ?? [])
                    .Where(l => !string.IsNullOrWhiteSpace(l.Template))
                    .Select(l => new ActivityTemplateLineDto(l.Template, string.IsNullOrWhiteSpace(l.Style) ? "secondary" : l.Style))
                    .ToList());

            // Aliases are the one part of this payload that can be internally inconsistent rather
            // than merely untidy — an alias claimed by two environments, or one that is also an
            // environment of its own, has no single answer at resolution time. Rejected whole: a
            // partial save would leave the admin looking at a list that isn't what they submitted.
            var errors = EnvironmentAliasValidator.Validate(cleaned.Environments);
            if (errors.Count > 0) return Results.BadRequest(new { error = errors[0], errors });

            await settings.SaveSettings(cleaned, ct);
            return Results.NoContent();
        }).RequireAuthorization(AuthorizationPolicies.CatalogAdmin);

        // ── Environment consolidation ────────────────────────────────────────
        // Aliases (above) fix what arrives next; the merge below is how the history that arrived
        // under the old names follows. See EnvironmentMergeService for what moves and what is
        // deliberately left in place.

        // What environment names the data actually uses, as opposed to the ones an admin curated.
        // Read-only and admin-only: it is a maintenance view, and the row counts are the thing an
        // admin needs before deciding what to fold into what.
        group.MapGet("/environments/usage", async (EnvironmentMergeService merge, CancellationToken ct) =>
                Results.Ok(await merge.UsageAsync(ct)))
            .RequireAuthorization(AuthorizationPolicies.CatalogAdmin);

        // Preview, then apply — the same GET/POST shape as the other maintenance repairs. A merge
        // rewrites years of deploy history across eleven tables, so the counts come first.
        group.MapPost("/environments/merge/preview", async (
                MergeEnvironmentsRequest body, EnvironmentMergeService merge, CancellationToken ct) =>
            {
                if (body is null) return Results.BadRequest(new { error = "request body is required" });
                try
                {
                    return Results.Ok(ToDto(await merge.PreviewAsync(ToRequest(body), ct)));
                }
                catch (ArgumentException ex)
                {
                    return Results.BadRequest(new { error = ex.Message });
                }
            })
            .RequireAuthorization(AuthorizationPolicies.CatalogAdmin);

        group.MapPost("/environments/merge", async (
                MergeEnvironmentsRequest body, EnvironmentMergeService merge, ICurrentUser user,
                IAuditLogger audit, IPlatformEventPublisher events, CancellationToken ct) =>
            {
                if (body is null) return Results.BadRequest(new { error = "request body is required" });

                EnvironmentMergeService.MergePlan plan;
                try
                {
                    plan = await merge.ApplyAsync(ToRequest(body), ct);
                }
                catch (ArgumentException ex)
                {
                    return Results.BadRequest(new { error = ex.Message });
                }

                await audit.Log(
                    "settings", "settings.environments.merged",
                    user.Id, user.Name, "user",
                    "AppSettings", null, null,
                    new
                    {
                        into = plan.Into,
                        from = plan.Sources,
                        plan.AliasesRecorded,
                        plan.RemovedEnvironments,
                        plan.Counts.Deployments,
                        plan.Counts.PromotionCandidates,
                        plan.Counts.PromotionPolicies,
                        plan.Counts.WorkItemApprovals,
                        plan.Counts.ReleaseNotes,
                        leftBehind = plan.Counts.LeftBehind,
                    });

                // Every deployment view is keyed on environment, so all of them are now looking at a
                // stale list — not just the ones filtered to the merged names.
                await events.PublishEntityChanged(new EntityChangedEvent
                {
                    Entity = "deployment", Action = "updated", Environment = plan.Into,
                });

                return Results.Ok(ToDto(plan));
            })
            .RequireAuthorization(AuthorizationPolicies.CatalogAdmin);

        return group;
    }

    private static EnvironmentMergeService.MergeRequest ToRequest(MergeEnvironmentsRequest body)
        => new(body.Into ?? "", body.From ?? [], body.RecordAliases);

    private static EnvironmentMergePlanDto ToDto(EnvironmentMergeService.MergePlan plan)
        => new(plan.Into, plan.Sources, plan.AliasesRecorded, plan.RemovedEnvironments,
               plan.Applied, plan.Counts, plan.Counts.Moved, plan.Counts.LeftBehind);
}
