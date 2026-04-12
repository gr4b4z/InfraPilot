using Platform.Api.Features.Catalog;
using Platform.Api.Infrastructure.Auth;

namespace Platform.Api.Features.Requests;

public static class RequestEndpoints
{
    public static RouteGroupBuilder MapRequestEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/", async (RequestService service, ICurrentUser user, string? status, string? requesterId, string? scope) =>
        {
            List<Platform.Api.Features.Requests.Models.ServiceRequest> items;
            if (scope == "all")
            {
                items = await service.GetAll(status);
            }
            else
            {
                var effectiveRequesterId = requesterId ?? user.Id;
                items = await service.GetByRequester(effectiveRequesterId, status);
            }
            return Results.Ok(new { items, total = items.Count });
        });

        group.MapGet("/{id:guid}", async (RequestService service, Guid id) =>
        {
            var request = await service.GetById(id);
            if (request is null) return Results.NotFound(new { error = "Request not found" });
            return Results.Ok(new { request });
        });

        group.MapPost("/", async (RequestService service, CatalogService catalog, CreateRequestDto dto) =>
        {
            // Accept either a GUID or a slug
            Guid catalogItemId;
            if (Guid.TryParse(dto.CatalogItemId, out var parsed))
            {
                catalogItemId = parsed;
            }
            else
            {
                var item = await catalog.GetBySlug(dto.CatalogItemId);
                if (item is null)
                    return Results.NotFound(new { error = $"Catalog item '{dto.CatalogItemId}' not found" });
                catalogItemId = item.Id;
            }

            var request = await service.Create(catalogItemId, dto.Inputs);
            return Results.Created($"/api/requests/{request.Id}", new { id = request.Id });
        });

        group.MapPost("/{id:guid}/submit", async (RequestService service, Guid id) =>
        {
            await service.Submit(id);
            return Results.Ok(new { message = "Request submitted for validation" });
        });

        group.MapPost("/{id:guid}/retry", async (RetryHandler handler, Guid id, CancellationToken ct) =>
        {
            await handler.RetryExecution(id, ct);
            return Results.Ok(new { message = "Request retried" });
        });

        group.MapPost("/{id:guid}/cancel", async (RequestService service, Guid id) =>
        {
            await service.Cancel(id);
            return Results.Ok(new { message = "Request cancelled" });
        });

        return group;
    }
}

public record CreateRequestDto(string CatalogItemId, Dictionary<string, object> Inputs);
