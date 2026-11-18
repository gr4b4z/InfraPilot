using System.Security.Claims;
using Platform.Api.Features.Deployments;
using Platform.Api.Infrastructure.Auth;

namespace Platform.Api.Features.Integrations.Flux;

/// <summary>
/// <c>POST /api/integrations/flux/{client}/events</c> — Flux notification-controller reports the
/// outcome of a Helm install. Authenticated by <see cref="FluxSignatureAuthHandler"/>; see
/// <c>notes/integrations/flux.md</c>.
/// </summary>
public static class FluxEndpoints
{
    public static RouteGroupBuilder MapFluxEndpoints(this RouteGroupBuilder group)
    {
        group.MapPost("/{client}/events", async (
            string client,
            FluxEventDto evt,
            ClaimsPrincipal user,
            FluxIngestService service,
            CancellationToken ct) =>
        {
            var mapped = FluxEventMapper.Map(evt, client);
            switch (mapped)
            {
                case FluxEventMapper.Ignored ignored:
                    // Accepted so Flux does not retry: an event we do not record is not an error.
                    return Results.Accepted(value: new { ignored = true, reason = ignored.Reason });
                case FluxEventMapper.Invalid invalid:
                    return Results.BadRequest(new { errors = invalid.Errors });
            }

            var dto = ((FluxEventMapper.Mapped)mapped).Event;
            var errors = DeploymentEndpoints.Validate(dto);
            if (errors.Count > 0)
                return Results.BadRequest(new { errors });

            // Same rule as deploy ingest: checked against the product the client sent, not the one a
            // service→product override redirects it to.
            var allowedProducts = user.FindAll(ApiKeyAuthHandler.AllowedProductClaim).Select(c => c.Value).ToList();
            if (allowedProducts.Count > 0 &&
                !allowedProducts.Contains(dto.Product, StringComparer.OrdinalIgnoreCase))
            {
                return Results.Forbid();
            }

            var result = await service.IngestAsync(dto, ct);
            var stored = result.Ingest.Event;
            var body = new
            {
                stored.Id,
                stored.Version,
                stored.PreviousVersion,
                stored.Status,
                result.PromotionId,
                result.Ingest.Replayed,
            };
            return result.Ingest.Replayed
                ? Results.Ok(body)
                : Results.Created($"/api/deployments/events/{stored.Id}", body);
        })
        .RequireAuthorization(AuthorizationPolicies.FluxIntegration)
        .RequireRateLimiting(DeploymentIngestionRateLimit.PolicyName);

        return group;
    }
}
