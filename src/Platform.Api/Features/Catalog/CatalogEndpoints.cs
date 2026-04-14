namespace Platform.Api.Features.Catalog;

public static class CatalogEndpoints
{
    public static RouteGroupBuilder MapCatalogEndpoints(this RouteGroupBuilder group)
    {
        // Public: list active catalog items
        group.MapGet("/", async (CatalogService service, string? category, string? search) =>
        {
            var items = await service.GetAll(category, search);
            return Results.Ok(new
            {
                items = items.Select(i => new
                {
                    id = i.Slug,
                    slug = i.Slug,
                    name = i.Name,
                    description = i.Description,
                    category = i.Category,
                    icon = i.Icon,
                    isActive = i.IsActive,
                })
            });
        });

        // Public: get single item with full definition
        group.MapGet("/{slug}", async (CatalogService service, string slug) =>
        {
            var item = await service.GetBySlug(slug);
            if (item is null)
                return Results.NotFound(new { error = $"Service '{slug}' not found" });

            return Results.Ok(new
            {
                item = new
                {
                    id = item.Slug,
                    slug = item.Slug,
                    name = item.Name,
                    description = item.Description,
                    category = item.Category,
                    icon = item.Icon,
                    isActive = item.IsActive,
                },
                inputs = item.Inputs,
                validations = item.Validations,
                approval = item.Approval,
                executor = item.Executor != null ? new { item.Executor.Type } : null,
            });
        });

        // Admin: list all items including inactive
        group.MapGet("/admin", async (CatalogService service) =>
        {
            var items = await service.GetAll(includeInactive: true);
            return Results.Ok(new
            {
                items = items.Select(i => new
                {
                    id = i.Id,
                    slug = i.Slug,
                    name = i.Name,
                    description = i.Description,
                    category = i.Category,
                    icon = i.Icon,
                    isActive = i.IsActive,
                    createdAt = i.CreatedAt,
                    updatedAt = i.UpdatedAt,
                })
            });
        }).RequireAuthorization("CatalogAdmin");

        // Admin: create new item
        group.MapPost("/", async (CatalogService service, CreateCatalogRequest request) =>
        {
            var (item, errors) = await service.Create(request.YamlContent);
            if (item is null)
                return Results.BadRequest(new { errors });

            return Results.Created($"/api/catalog/{item.Slug}", new { item = new { item.Id, item.Slug, item.Name } });
        }).RequireAuthorization("CatalogAdmin");

        // Admin: update item
        group.MapPut("/{slug}", async (CatalogService service, string slug, UpdateCatalogRequest request) =>
        {
            var (item, errors) = await service.Update(slug, request.YamlContent);
            if (item is null)
                return Results.BadRequest(new { errors });

            return Results.Ok(new { item = new { item.Id, item.Slug, item.Name } });
        }).RequireAuthorization("CatalogAdmin");

        // Admin: delete item
        group.MapDelete("/{slug}", async (CatalogService service, string slug) =>
        {
            var deleted = await service.Delete(slug);
            return deleted ? Results.NoContent() : Results.NotFound();
        }).RequireAuthorization("CatalogAdmin");

        // Admin: toggle active state
        group.MapPatch("/{slug}/active", async (CatalogService service, string slug, ToggleActiveRequest request) =>
        {
            var item = await service.ToggleActive(slug, request.IsActive);
            return item is not null
                ? Results.Ok(new { item.Slug, item.IsActive })
                : Results.NotFound();
        }).RequireAuthorization("CatalogAdmin");

        // Admin: validate YAML
        group.MapPost("/validate", (CatalogService service, ValidateYamlRequest request) =>
        {
            var result = service.ValidateYaml(request.YamlContent);
            return Results.Ok(result);
        }).RequireAuthorization("CatalogAdmin");

        return group;
    }
}

public record CreateCatalogRequest(string YamlContent);
public record UpdateCatalogRequest(string YamlContent);
public record ToggleActiveRequest(bool IsActive);
public record ValidateYamlRequest(string YamlContent);
