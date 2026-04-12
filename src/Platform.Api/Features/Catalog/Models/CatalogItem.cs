namespace Platform.Api.Features.Catalog.Models;

public class CatalogItem
{
    public Guid Id { get; set; }
    public string Slug { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public string Category { get; set; } = "";
    public string? Icon { get; set; }
    public string CurrentYamlHash { get; set; } = "";
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    // Navigation
    public List<CatalogItemVersion> Versions { get; set; } = [];

    // Not persisted — loaded from YAML
    public List<CatalogInput> Inputs { get; set; } = [];
    public List<CatalogValidation> Validations { get; set; } = [];
    public ApprovalConfig? Approval { get; set; }
    public ExecutorConfig? Executor { get; set; }
}

public class ApprovalConfig
{
    public bool Required { get; set; }
    public string Strategy { get; set; } = "any";
    public int? QuorumCount { get; set; }
    public string? ApproverGroup { get; set; }
    public int? TimeoutHours { get; set; }
    public string? EscalationGroup { get; set; }
}

public class ExecutorConfig
{
    public string Type { get; set; } = "";
    public string? Connection { get; set; }
    public string? Project { get; set; }
    public int? PipelineId { get; set; }
    public Dictionary<string, string> ParametersMap { get; set; } = [];
}
