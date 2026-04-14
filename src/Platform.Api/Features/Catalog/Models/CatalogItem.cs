using System.Text.Json;

namespace Platform.Api.Features.Catalog.Models;

public class CatalogItem
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

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

    // JSON storage columns
    public string InputsJson { get; set; } = "[]";
    public string ValidationsJson { get; set; } = "[]";
    public string? ApprovalJson { get; set; }
    public string? ExecutorJson { get; set; }

    // Navigation
    public List<CatalogItemVersion> Versions { get; set; } = [];

    // Convenience accessors (not mapped to DB)
    public List<CatalogInput> Inputs
    {
        get => JsonSerializer.Deserialize<List<CatalogInput>>(InputsJson, JsonOptions) ?? [];
        set => InputsJson = JsonSerializer.Serialize(value, JsonOptions);
    }

    public List<CatalogValidation> Validations
    {
        get => JsonSerializer.Deserialize<List<CatalogValidation>>(ValidationsJson, JsonOptions) ?? [];
        set => ValidationsJson = JsonSerializer.Serialize(value, JsonOptions);
    }

    public ApprovalConfig? Approval
    {
        get => string.IsNullOrEmpty(ApprovalJson) ? null : JsonSerializer.Deserialize<ApprovalConfig>(ApprovalJson, JsonOptions);
        set => ApprovalJson = value is null ? null : JsonSerializer.Serialize(value, JsonOptions);
    }

    public ExecutorConfig? Executor
    {
        get => string.IsNullOrEmpty(ExecutorJson) ? null : JsonSerializer.Deserialize<ExecutorConfig>(ExecutorJson, JsonOptions);
        set => ExecutorJson = value is null ? null : JsonSerializer.Serialize(value, JsonOptions);
    }
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
