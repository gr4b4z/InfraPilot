using System.Text.Json;
using System.Text.Json.Serialization;
using YamlDotNet.Serialization;

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

    /// <summary>
    /// Ordered approval rules: the first whose <see cref="ApprovalRule.When"/> matches the
    /// request's inputs decides; with no match the fields above apply. Evaluated on submit
    /// (<see cref="ApprovalRules.Resolve"/>).
    /// </summary>
    public List<ApprovalRule>? Rules { get; set; }
}

/// <summary>
/// One entry of <c>approval.rules</c>. <see cref="Required"/> must be set; every other field left
/// out is taken from the item's top-level <c>approval</c> block.
/// </summary>
public class ApprovalRule
{
    /// <summary>Optional label shown on the request, e.g. "Staging needs approval".</summary>
    public string? Name { get; set; }
    public ApprovalCondition? When { get; set; }
    public bool? Required { get; set; }
    public string? Strategy { get; set; }
    public int? QuorumCount { get; set; }
    public string? ApproverGroup { get; set; }
    public int? TimeoutHours { get; set; }
    public string? EscalationGroup { get; set; }
}

/// <summary>
/// A test on the request's inputs, in the <c>visible_when</c> style: <c>field</c> with exactly one
/// of <c>equals</c>, <c>in</c> or <c>not_in</c>; or <c>all</c> / <c>any</c> over nested conditions.
/// Values compare as exact strings. A field that wasn't filled in never matches.
/// </summary>
public class ApprovalCondition
{
    public string? Field { get; set; }

    [YamlMember(Alias = "equals")]
    [JsonPropertyName("equals")]
    public string? EqualsValue { get; set; }

    public List<string>? In { get; set; }
    public List<string>? NotIn { get; set; }
    public List<ApprovalCondition>? All { get; set; }
    public List<ApprovalCondition>? Any { get; set; }
}

public class ExecutorConfig
{
    public string Type { get; set; } = "";
    public string? Connection { get; set; }
    public string? Project { get; set; }
    public int? PipelineId { get; set; }
    public Dictionary<string, string> ParametersMap { get; set; } = [];
}
