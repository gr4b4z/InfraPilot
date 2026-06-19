namespace Platform.Api.Features.Rollbacks;

/// <summary>
/// Admin endpoints for per-product rollback enrollment (mounted at <c>/api/rollbacks/admin</c>,
/// gated by CatalogAdmin). A product can use rollbacks only when the global <c>features.rollbacks</c>
/// flag is on AND the product is in this enrolled set — i.e. promotion-enrolled products can
/// additionally opt into rollbacks.
/// </summary>
public static class RollbackAdminEndpoints
{
    public static RouteGroupBuilder MapRollbackAdminEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/enabled-products", async (RollbackService svc) =>
            Results.Ok(new { products = await svc.GetEnabledProductsAsync() }));

        group.MapPut("/enabled-products", async (RollbackService svc, SetEnabledProductsBody body) =>
        {
            await svc.SetEnabledProductsAsync(body.Products ?? new());
            return Results.Ok(new { products = await svc.GetEnabledProductsAsync() });
        });

        return group;
    }

    public record SetEnabledProductsBody(List<string>? Products);
}
