using Platform.Api.Features.Deployments.Models;
using Platform.Api.Infrastructure.Audit;
using Platform.Api.Infrastructure.Auth;
using Platform.Api.Infrastructure.Realtime;

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

        // ── Retired services (soft delete) ───────────────────────────────────
        // A service that a migration made obsolete stops appearing in the deployment matrix and in
        // promotions, without anything being erased. See ServiceDeletionService for the rules; the
        // one worth repeating here is that a new deployment un-retires the service by itself, so an
        // admin who retires something prematurely does not have to remember to undo it.

        group.MapGet("/deleted-services", async (
            ServiceDeletionService service, string? product, CancellationToken ct) =>
        {
            var rows = await service.ListAsync(product, ct);
            return Results.Ok(rows.Select(ToDto).ToList());
        });

        group.MapPost("/deleted-services", async (
            ServiceDeletionService service, ICurrentUser user, IAuditLogger audit,
            IPlatformEventPublisher events, DeleteServiceRequest req, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Product) || string.IsNullOrWhiteSpace(req.Service))
                return Results.BadRequest(new { error = "product and service are required" });

            try
            {
                var (row, impact) = await service.DeleteAsync(req.Product, req.Service, req.Reason, ct);

                await audit.Log(
                    "deployments", "deployment.service.deleted",
                    user.Id, user.Name, "user",
                    "DeletedService", row.Id, null,
                    new { row.Product, row.Service, row.Reason, impact.Deployments, impact.OpenPromotions });

                await events.PublishEntityChanged(new EntityChangedEvent
                {
                    Entity = "deployment", Action = "updated", Product = row.Product,
                });

                return Results.Ok(new DeleteServiceResultDto(
                    ToDto(row), impact.Deployments, impact.OpenPromotions));
            }
            catch (KeyNotFoundException ex)
            {
                return Results.NotFound(new { error = ex.Message });
            }
        });

        // Query parameters rather than route segments: service names carry dots and slashes often
        // enough that a path segment would need escaping the caller keeps getting wrong.
        group.MapDelete("/deleted-services", async (
            ServiceDeletionService service, ICurrentUser user, IAuditLogger audit,
            IPlatformEventPublisher events, string? product, string? serviceName, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(product) || string.IsNullOrWhiteSpace(serviceName))
                return Results.BadRequest(new { error = "product and serviceName are required" });

            if (!await service.RestoreAsync(product, serviceName, ct))
                return Results.NotFound(new { error = $"{product}/{serviceName} is not retired." });

            await audit.Log(
                "deployments", "deployment.service.restored",
                user.Id, user.Name, "user",
                "DeletedService", null,
                new { product, service = serviceName }, null);

            await events.PublishEntityChanged(new EntityChangedEvent
            {
                Entity = "deployment", Action = "updated", Product = product,
            });

            return Results.NoContent();
        });

        // ── Service product overrides ────────────────────────────────────────
        // Which product a service's entities belong to, when the product on the payload can't be
        // trusted. See ServiceProductOverrideService for the resolution rules; the two worth repeating
        // here are that the most specific row wins (a row naming the sending product beats the
        // catch-all), and that writing a row changes nothing that is already stored — the remap pair
        // below is how history follows.

        group.MapGet("/product-overrides", async (
            ServiceProductOverrideService overrides, CancellationToken ct) =>
        {
            var rows = await overrides.ListWithCountsAsync(ct);
            return Results.Ok(rows.Select(ToDto).ToList());
        });

        group.MapPost("/product-overrides", async (
            ServiceProductOverrideService overrides, ICurrentUser user, IAuditLogger audit,
            IPlatformEventPublisher events, SaveServiceProductOverrideRequest req, CancellationToken ct) =>
        {
            ServiceProductOverride row;
            try
            {
                row = await overrides.UpsertAsync(req.Service, req.FromProduct, req.Product, req.Reason, ct);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }

            await audit.Log(
                "deployments", "deployment.product-override.saved",
                user.Id, user.Name, "user",
                "ServiceProductOverride", row.Id, null,
                new { row.Service, row.FromProduct, row.Product, row.Reason });

            // Both products change shape: the service starts appearing under the target and stops
            // growing under the source, so a client filtered to either one is now looking at a stale
            // list.
            await events.PublishEntityChanged(new EntityChangedEvent
            {
                Entity = "deployment", Action = "updated", Product = row.Product,
            });
            if (row.FromProduct is not null)
            {
                await events.PublishEntityChanged(new EntityChangedEvent
                {
                    Entity = "deployment", Action = "updated", Product = row.FromProduct,
                });
            }

            return Results.Ok(ToDto(new ServiceProductOverrideService.OverrideWithCounts(row, 0, 0)));
        });

        group.MapDelete("/product-overrides/{id:guid}", async (
            ServiceProductOverrideService overrides, ICurrentUser user, IAuditLogger audit,
            IPlatformEventPublisher events, Guid id, CancellationToken ct) =>
        {
            var row = await overrides.DeleteAsync(id, ct);
            if (row is null) return Results.NotFound(new { error = $"No service product override with id {id}." });

            await audit.Log(
                "deployments", "deployment.product-override.removed",
                user.Id, user.Name, "user",
                "ServiceProductOverride", row.Id,
                new { row.Service, row.FromProduct, row.Product }, null);

            await events.PublishEntityChanged(new EntityChangedEvent
            {
                Entity = "deployment", Action = "updated", Product = row.Product,
            });

            return Results.NoContent();
        });

        // Preview, then apply — the same GET/POST shape as the duplicates and log-retention pairs above.
        // A remap moves years of deploy history between products, so the counts come first.
        group.MapGet("/product-overrides/{id:guid}/remap", async (
            ServiceProductRemapService remap, Guid id, CancellationToken ct) =>
        {
            try
            {
                return Results.Ok(ToDto(await remap.PreviewAsync(id, ct)));
            }
            catch (KeyNotFoundException ex)
            {
                return Results.NotFound(new { error = ex.Message });
            }
        });

        group.MapPost("/product-overrides/{id:guid}/remap", async (
            ServiceProductRemapService remap, ICurrentUser user, IAuditLogger audit,
            IPlatformEventPublisher events, Guid id, CancellationToken ct) =>
        {
            ServiceProductRemapService.RemapPlan plan;
            try
            {
                plan = await remap.ApplyAsync(id, ct);
            }
            catch (KeyNotFoundException ex)
            {
                return Results.NotFound(new { error = ex.Message });
            }

            await audit.Log(
                "deployments", "deployment.product-override.remapped",
                user.Id, user.Name, "user",
                "ServiceProductOverride", plan.OverrideId, null,
                new
                {
                    plan.Service,
                    plan.TargetProduct,
                    fromProducts = plan.FromProducts,
                    plan.Counts.Deployments,
                    plan.Counts.Builds,
                    plan.Counts.BuildConflicts,
                    plan.Counts.Promotions,
                    plan.Counts.StrandedTicketApprovals,
                });

            await events.PublishEntityChanged(new EntityChangedEvent
            {
                Entity = "deployment", Action = "updated", Product = plan.TargetProduct,
            });
            foreach (var from in plan.FromProducts)
            {
                await events.PublishEntityChanged(new EntityChangedEvent
                {
                    Entity = "deployment", Action = "updated", Product = from,
                });
            }

            return Results.Ok(ToDto(plan));
        });

        return group;
    }

    private static DeletedServiceDto ToDto(DeletedService d) =>
        new(d.Id, d.Product, d.Service, d.DeletedAt, d.DeletedByName, d.Reason);

    private static ServiceProductOverrideDto ToDto(ServiceProductOverrideService.OverrideWithCounts row) =>
        new(row.Override.Id, row.Override.Service, row.Override.FromProduct, row.Override.Product,
            row.Override.Reason, row.Override.CreatedAt, row.Override.UpdatedAt, row.Override.UpdatedByName,
            row.Stored, row.Stranded);

    private static ServiceProductRemapDto ToDto(ServiceProductRemapService.RemapPlan plan) =>
        new(plan.OverrideId, plan.Service, plan.TargetProduct, plan.FromProducts, plan.Applied,
            plan.Counts.Deployments, plan.Counts.DeployWorkItems, plan.Counts.Builds,
            plan.Counts.BuildConflicts, plan.Counts.Promotions, plan.Counts.OpenPromotions,
            plan.Counts.PromotionWorkItems, plan.Counts.Retirements, plan.Counts.RetirementMerges,
            plan.Counts.StrandedTicketApprovals);
}
