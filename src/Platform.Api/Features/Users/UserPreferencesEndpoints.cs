using Microsoft.EntityFrameworkCore;
using Platform.Api.Infrastructure.Persistence;

namespace Platform.Api.Features.Users;

/// <summary>
/// The signed-in user's own preferences, under <c>/api/me</c>. Deliberately not part of
/// <c>/api/settings</c>: that is a single global row whose write path is admin-only, whereas these
/// are per-person and every authenticated user must be able to change their own.
/// </summary>
public static class UserPreferencesEndpoints
{
    public static RouteGroupBuilder MapUserPreferencesEndpoints(this RouteGroupBuilder group)
    {
        // The whole preference bag. One call at app bootstrap.
        group.MapGet("/preferences", async (
            UserPreferencesService prefs, CancellationToken ct) =>
        {
            var hidden = await prefs.GetHiddenProductsAsync(ct);
            return Results.Ok(new { hiddenProducts = hidden.OrderBy(p => p, StringComparer.OrdinalIgnoreCase) });
        });

        // Everything needed to render the hidden-products control.
        //
        // The product list here is deliberately UNFILTERED — it is the one place that has to show
        // hidden products, otherwise there is no way to un-hide one. Sourced from deploy history
        // rather than the (filtered) products endpoint for the same reason.
        group.MapGet("/preferences/products", async (
            PlatformDbContext db, UserPreferencesService prefs, CancellationToken ct) =>
        {
            var products = await db.DeployEvents.AsNoTracking()
                .Select(e => e.Product)
                .Distinct()
                .OrderBy(p => p)
                .ToListAsync(ct);

            var hidden = await prefs.GetHiddenProductsAsync(ct);

            // A product can be hidden and no longer present in deploy history (retired, or renamed).
            // Union it in so the control can still show it — otherwise the entry is stuck in the
            // user's preference with no way to clear it.
            var all = products
                .Concat(hidden.Where(h => !products.Contains(h, StringComparer.OrdinalIgnoreCase)))
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToList();

            return Results.Ok(new
            {
                products = all,
                hiddenProducts = hidden.OrderBy(p => p, StringComparer.OrdinalIgnoreCase),
            });
        });

        group.MapPut("/preferences/hidden-products", async (
            UserPreferencesService prefs, HiddenProductsRequest? request, CancellationToken ct) =>
        {
            try
            {
                var saved = await prefs.SetHiddenProductsAsync(request?.Products, ct);
                return Results.Ok(new { hiddenProducts = saved });
            }
            catch (UnauthorizedAccessException ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status403Forbidden);
            }
        });

        return group;
    }
}

/// <summary>Body for the hidden-products write. Null or empty clears the filter.</summary>
public record HiddenProductsRequest(List<string>? Products);
