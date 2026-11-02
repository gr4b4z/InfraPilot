using System.Security.Claims;
using Platform.Api.Features.Deployments;
using Platform.Api.Features.Migrations.Models;
using Platform.Api.Infrastructure.Auth;

namespace Platform.Api.Features.Migrations;

/// <summary>
/// Database migrations — <c>/api/migrations</c>. Read-only for people: the page lists what ran and
/// what is waiting, nothing here approves or starts a migration. Pipelines report through
/// <c>POST /runs</c> with their API key, exactly like deploy ingest (same rate limit, same product
/// scope).
/// </summary>
public static class MigrationEndpoints
{
    public static RouteGroupBuilder MapMigrationEndpoints(this RouteGroupBuilder group)
    {
        group.MapPost("/runs", async (MigrationService migrations, ClaimsPrincipal user, ReportMigrationRunDto dto, CancellationToken ct) =>
        {
            var errors = MigrationService.ValidationErrors(dto);
            if (errors.Count > 0) return Results.BadRequest(new { errors });

            // Checked against the product the key sent, as deploy ingest does.
            var allowedProducts = user.FindAll(ApiKeyAuthHandler.AllowedProductClaim).Select(c => c.Value).ToList();
            if (allowedProducts.Count > 0 && !allowedProducts.Contains(dto.Product, StringComparer.OrdinalIgnoreCase))
                return Results.Forbid();

            try
            {
                var result = await migrations.ReportAsync(dto, ct);
                var body = new
                {
                    result.Run.Id, result.Run.Status, result.Run.DeployEventId, result.Run.SupersededById,
                    result.Run.AppliedCount, result.Run.FailedCount, result.Run.TotalCount,
                };
                return result.Created
                    ? Results.Created($"/api/migrations/runs/{result.Run.Id}", body)
                    : Results.Ok(body);
            }
            catch (MigrationValidationException ex)
            {
                return Results.BadRequest(new { errors = new[] { ex.Message } });
            }
        })
        .RequireAuthorization(ApiKeyAuthHandler.PolicyName)
        .RequireRateLimiting(DeploymentIngestionRateLimit.PolicyName);

        // status: "attention" (pending, running, failed — the page's default), "all", or a
        // comma-separated list of statuses.
        group.MapGet("/runs", async (
            MigrationService migrations,
            string? product, string? service, string? environment, string? kind, string? status,
            string? version, Guid? deployEventId, DateTimeOffset? from, DateTimeOffset? to,
            int? limit, int? offset, CancellationToken ct) =>
        {
            if (from is not null && to is not null && from >= to)
                return Results.BadRequest(new { error = "'from' must be before 'to'" });
            if (!string.IsNullOrWhiteSpace(kind) && !MigrationKinds.All.Contains(kind.ToLowerInvariant()))
                return Results.BadRequest(new { error = $"'kind' must be one of: {string.Join(", ", MigrationKinds.All)}" });
            return Results.Ok(await migrations.ListAsync(
                product, service, environment, kind, status, version, deployEventId, from, to,
                Math.Clamp(limit ?? 100, 1, 500), Math.Max(offset ?? 0, 0), ct));
        });

        group.MapGet("/runs/{id:guid}", async (MigrationService migrations, Guid id, CancellationToken ct) =>
        {
            var detail = await migrations.GetAsync(id, ct);
            return detail is null ? Results.NotFound() : Results.Ok(detail);
        });

        group.MapGet("/runs/{id:guid}/logs/{logId:guid}", async (
            MigrationService migrations, Guid id, Guid logId, CancellationToken ct) =>
        {
            var log = await migrations.GetLogContentAsync(id, logId, ct);
            return log is null ? Results.NotFound() : Results.Ok(log);
        });

        // "Has this migration run on staging?" — partial and fuzzy over every migration name on record.
        group.MapGet("/search", async (
            MigrationService migrations, string? q, string? product, string? environment, string? kind,
            int? limit, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(q) || q.Trim().Length < 2)
                return Results.BadRequest(new { error = "'q' needs at least 2 characters" });
            if (!string.IsNullOrWhiteSpace(kind) && !MigrationKinds.All.Contains(kind.ToLowerInvariant()))
                return Results.BadRequest(new { error = $"'kind' must be one of: {string.Join(", ", MigrationKinds.All)}" });
            return Results.Ok(await migrations.SearchAsync(q, product, environment, kind, Math.Clamp(limit ?? 20, 1, 100), ct));
        });

        // What the deployment views flag: migrations that are pending, running or failed.
        group.MapGet("/flags", async (
            MigrationService migrations, string? product, string? service, string? environment, CancellationToken ct) =>
            Results.Ok(await migrations.GetFlagsAsync(product, service, environment, ct)));

        return group;
    }

    /// <summary>Admin maintenance — <c>/api/migrations/admin</c>, CatalogAdmin.</summary>
    public static RouteGroupBuilder MapMigrationAdminEndpoints(this RouteGroupBuilder group)
    {
        // Re-reads stored deploy logs for schema migrations. Run once after the feature ships so the
        // deployments recorded before it show their migrations; safe to repeat.
        group.MapPost("/derive", async (
            MigrationService migrations, DateTimeOffset? since, string? product, CancellationToken ct) =>
        {
            var from = since ?? DateTimeOffset.UtcNow.AddDays(-7);
            return Results.Ok(await migrations.DeriveSinceAsync(from, product, ct));
        });

        return group;
    }
}
