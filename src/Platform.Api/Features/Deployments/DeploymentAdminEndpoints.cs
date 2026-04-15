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

        return group;
    }
}
