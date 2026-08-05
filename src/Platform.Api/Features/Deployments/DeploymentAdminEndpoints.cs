namespace Platform.Api.Features.Deployments;

/// <summary>
/// Admin-only deployment maintenance endpoints.
/// Gated on <see cref="Platform.Api.Infrastructure.Auth.AuthorizationPolicies.CatalogAdmin"/>
/// when registered (see Program.cs).
/// </summary>
public static class DeploymentAdminEndpoints
{
    public static RouteGroupBuilder MapDeploymentAdminEndpoints(this RouteGroupBuilder group)
    {
        // Preview — count duplicate DeployEvent rows without deleting.
        group.MapGet("/duplicates", async (DeploymentService service, CancellationToken ct) =>
        {
            var (groups, rows) = await service.CountDuplicates(ct);
            return Results.Ok(new { groups, rows });
        });

        // Execute — delete duplicates (keeps earliest CreatedAt per natural-key group).
        group.MapDelete("/duplicates", async (DeploymentService service, CancellationToken ct) =>
        {
            var (groups, rows) = await service.RemoveDuplicates(ct);
            return Results.Ok(new { groups, rows });
        });

        // ── Log retention (Settings → Maintenance) ───────────────────────────
        // Captured pipeline output is the largest thing stored per deploy and ages fast. This purges
        // log rows for deploy events older than the cutoff; the events themselves stay. Preview via
        // GET, then DELETE — same contract as the duplicates pair above.
        group.MapGet("/logs", async (DeploymentService service, int? olderThanDays, CancellationToken ct) =>
        {
            if (olderThanDays is null or < 1 or > 3650)
                return Results.BadRequest(new { error = "olderThanDays is required (1–3650)" });
            var (logs, bytes) = await service.CountOldLogs(olderThanDays.Value, ct);
            return Results.Ok(new { logs, bytes });
        });

        group.MapDelete("/logs", async (DeploymentService service, int? olderThanDays, CancellationToken ct) =>
        {
            if (olderThanDays is null or < 1 or > 3650)
                return Results.BadRequest(new { error = "olderThanDays is required (1–3650)" });
            var (logs, bytes) = await service.RemoveOldLogs(olderThanDays.Value, ct);
            return Results.Ok(new { logs, bytes });
        });

        return group;
    }
}
