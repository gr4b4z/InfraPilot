namespace Platform.Api.Features.Catalog.Models;

public class CatalogInput
{
    public string Id { get; set; } = "";
    public string Component { get; set; } = "";
    public string Label { get; set; } = "";
    public string? Placeholder { get; set; }
    public string? Validation { get; set; }
    public bool Required { get; set; }
    public object? Default { get; set; }
    public string? Source { get; set; }
    public List<CatalogInputOption>? Options { get; set; }
    public VisibilityCondition? VisibleWhen { get; set; }
    public string? Accept { get; set; }
    public int? MaxSizeMb { get; set; }
    public int? MaxFiles { get; set; }
    public double? Min { get; set; }
    public double? Max { get; set; }
    public double? Step { get; set; }
    public string? Language { get; set; }
}

public class CatalogInputOption
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
}

public class VisibilityCondition
{
    public string Field { get; set; } = "";
    public new object Equals { get; set; } = "";
}
