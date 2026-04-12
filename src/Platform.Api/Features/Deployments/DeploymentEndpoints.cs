using Platform.Api.Features.Deployments.Models;
using Platform.Api.Infrastructure.Auth;

namespace Platform.Api.Features.Deployments;

public static class DeploymentEndpoints
{
    public static RouteGroupBuilder MapDeploymentEndpoints(this RouteGroupBuilder group)
    {
        // Ingestion — called by pipelines, secured with API key
        group.MapPost("/events", async (DeploymentService service, CreateDeployEventDto dto, CancellationToken ct) =>
        {
            var errors = Validate(dto);
            if (errors.Count > 0)
                return Results.BadRequest(new { errors });

            var result = await service.IngestEvent(dto, ct);
            return Results.Created($"/api/deployments/events/{result.Id}", new { result.Id, result.Version, result.PreviousVersion });
        }).RequireAuthorization(ApiKeyAuthHandler.PolicyName);

        // Product overview
        group.MapGet("/products", async (DeploymentService service, CancellationToken ct) =>
        {
            return Results.Ok(await service.GetProductSummaries(ct));
        });

        // Current state matrix
        group.MapGet("/state", async (DeploymentService service, string? product, string? environment, CancellationToken ct) =>
        {
            return Results.Ok(await service.GetState(product, environment, ct));
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

        return group;
    }

    private static List<string> Validate(CreateDeployEventDto dto)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(dto.Product)) errors.Add("'product' is required");
        if (string.IsNullOrWhiteSpace(dto.Service)) errors.Add("'service' is required");
        if (string.IsNullOrWhiteSpace(dto.Environment)) errors.Add("'environment' is required");
        if (string.IsNullOrWhiteSpace(dto.Version)) errors.Add("'version' is required");
        if (string.IsNullOrWhiteSpace(dto.Source)) errors.Add("'source' is required");
        if (dto.DeployedAt == default) errors.Add("'deployedAt' is required");
        return errors;
    }
}
