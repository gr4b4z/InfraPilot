namespace Platform.Api.Features.Catalog.Models;

public class CatalogValidation
{
    public string Id { get; set; } = "";
    public string Type { get; set; } = "";
    public string? Description { get; set; }
    public string? TargetField { get; set; }
    public string? Endpoint { get; set; }
    public string? Action { get; set; }
    public Dictionary<string, string>? Parameters { get; set; }
    public string? Expect { get; set; }
    public string? Source { get; set; }
    public string? OnFail { get; set; }
}
