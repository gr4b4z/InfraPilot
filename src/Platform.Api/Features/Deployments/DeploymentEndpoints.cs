using System.Security.Claims;
using Platform.Api.Features.Deployments.Models;
using Platform.Api.Infrastructure.Auth;

namespace Platform.Api.Features.Deployments;

public static class DeploymentEndpoints
{
    public static RouteGroupBuilder MapDeploymentEndpoints(this RouteGroupBuilder group)
    {
        // Ingestion — called by pipelines, secured with API key + per-key rate limit + optional product scope
        group.MapPost("/events", async (DeploymentService service, ClaimsPrincipal user, CreateDeployEventDto dto, CancellationToken ct) =>
        {
            var errors = Validate(dto);
            if (errors.Count > 0)
                return Results.BadRequest(new { errors });

            // Enforce product scope when the key restricts which products it can post for.
            var allowedProducts = user.FindAll(ApiKeyAuthHandler.AllowedProductClaim).Select(c => c.Value).ToList();
            if (allowedProducts.Count > 0 &&
                !allowedProducts.Contains(dto.Product, StringComparer.OrdinalIgnoreCase))
            {
                return Results.Forbid();
            }

            var result = await service.IngestEvent(dto, ct);
            return Results.Created($"/api/deployments/events/{result.Id}", new { result.Id, result.Version, result.PreviousVersion });
        })
        .RequireAuthorization(ApiKeyAuthHandler.PolicyName)
        .RequireRateLimiting(DeploymentIngestionRateLimit.PolicyName);

        // Product overview
        group.MapGet("/products", async (DeploymentService service, CancellationToken ct) =>
        {
            return Results.Ok(await service.GetProductSummaries(ct));
        });

        // Current state matrix
        group.MapGet("/state", async (DeploymentService service, string? product, string? environment, string? serviceName, CancellationToken ct) =>
        {
            return Results.Ok(await service.GetState(product, environment, serviceName, ct));
        });

        // Deployment history for a specific service
        group.MapGet("/history/{product}/{serviceName}", async (
            DeploymentService service, string product, string serviceName,
            string? environment, int? limit, CancellationToken ct) =>
        {
            return Results.Ok(await service.GetHistory(product, serviceName, environment, limit ?? 50, ct));
        });

        // Recent deployments across all environments for a product
        group.MapGet("/recent/{product}", async (
            DeploymentService service, string product,
            DateTimeOffset? since, int? limit, CancellationToken ct) =>
        {
            var sinceDate = since ?? DateTimeOffset.UtcNow.Date;
            return Results.Ok(await service.GetRecentByProduct(product, sinceDate, limit ?? 200, ct));
        });

        // Recent deployments for an environment
        group.MapGet("/recent/{product}/{environment}", async (
            DeploymentService service, string product, string environment,
            DateTimeOffset? since, CancellationToken ct) =>
        {
            var sinceDate = since ?? DateTimeOffset.UtcNow.Date;
            return Results.Ok(await service.GetRecentByEnvironment(product, environment, sinceDate, ct));
        });

        // Versions deployed to a given (product, environment[, service]) — powers the rollback
        // picker's "source: deployments/versions" catalog input.
        group.MapGet("/versions", async (
            DeploymentService service,
            string product, string environment, string? serviceName, int? limit,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(product) || string.IsNullOrWhiteSpace(environment))
                return Results.BadRequest(new { error = "'product' and 'environment' are required" });

            var versions = await service.GetVersions(product, environment, serviceName, limit ?? 50, ct);
            return Results.Ok(new { versions });
        });

        return group;
    }

    private static readonly HashSet<string> ValidStatuses = new(StringComparer.OrdinalIgnoreCase)
        { "succeeded", "failed", "in_progress" };

    private static List<string> Validate(CreateDeployEventDto dto)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(dto.Product)) errors.Add("'product' is required");
        if (string.IsNullOrWhiteSpace(dto.Service)) errors.Add("'service' is required");
        if (string.IsNullOrWhiteSpace(dto.Environment)) errors.Add("'environment' is required");
        if (string.IsNullOrWhiteSpace(dto.Version)) errors.Add("'version' is required");
        if (string.IsNullOrWhiteSpace(dto.Source)) errors.Add("'source' is required");
        if (dto.DeployedAt == default) errors.Add("'deployedAt' is required");
        if (dto.Status is not null && !ValidStatuses.Contains(dto.Status))
            errors.Add($"'status' must be one of: {string.Join(", ", ValidStatuses)}");

        // Reference-level participants: same shape as event-level — Role is required.
        if (dto.References is not null)
        {
            for (var i = 0; i < dto.References.Count; i++)
            {
                var nested = dto.References[i].Participants;
                if (nested is null) continue;
                for (var j = 0; j < nested.Count; j++)
                {
                    if (string.IsNullOrWhiteSpace(nested[j].Role))
                        errors.Add($"'references[{i}].participants[{j}].role' is required");
                }
            }
        }
        return errors;
    }
}
