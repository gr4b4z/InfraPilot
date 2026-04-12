namespace Platform.Api.Features.Catalog;

public static class CatalogEndpoints
{
    public static RouteGroupBuilder MapCatalogEndpoints(this RouteGroupBuilder group)
    {
        // List catalog — reads from YAML directly (works without DB)
        group.MapGet("/", (CatalogYamlLoader loader, string? category, string? search) =>
        {
            var definitions = loader.LoadAll();

            if (!string.IsNullOrWhiteSpace(category))
                definitions = definitions.Where(d => d.Category == category).ToList();

            if (!string.IsNullOrWhiteSpace(search))
                definitions = definitions.Where(d =>
                    d.Name.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                    d.Description.Contains(search, StringComparison.OrdinalIgnoreCase)).ToList();

            var items = definitions.Select(d => new
            {
                id = d.Id,
                slug = d.Id,
                name = d.Name,
                description = d.Description,
                category = d.Category,
                icon = d.Icon,
                isActive = true,
            });

            return Results.Ok(new { items });
        });

        // Get single item with full definition
        group.MapGet("/{slug}", (CatalogYamlLoader loader, string slug) =>
        {
            var definition = loader.LoadAll().FirstOrDefault(d => d.Id == slug);
            if (definition is null)
                return Results.NotFound(new { error = $"Service '{slug}' not found" });

            return Results.Ok(new
            {
                item = new
                {
                    id = definition.Id,
                    slug = definition.Id,
                    name = definition.Name,
                    description = definition.Description,
                    category = definition.Category,
                    icon = definition.Icon,
                    isActive = true,
                },
                inputs = definition.Inputs,
                validations = definition.Validations,
                approval = definition.Approval,
                executor = definition.Executor != null ? new { definition.Executor.Type } : null,
            });
        });

        // Sync YAML to DB (requires DB + admin auth)
        group.MapPost("/sync", async (CatalogService service) =>
        {
            await service.SyncFromYaml();
            return Results.Ok(new { message = "Catalog synced from YAML definitions" });
        }).RequireAuthorization("CatalogAdmin");

        return group;
    }
}
