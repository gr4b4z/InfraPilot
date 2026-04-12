namespace Platform.Api.Features.Catalog.Models;

public class CatalogItemVersion
{
    public Guid Id { get; set; }
    public Guid CatalogItemId { get; set; }
    public string YamlContent { get; set; } = "";
    public string YamlHash { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    // Navigation
    public CatalogItem? CatalogItem { get; set; }
}
