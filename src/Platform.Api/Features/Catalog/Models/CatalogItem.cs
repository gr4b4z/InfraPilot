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

    // ── github-issue ──────────────────────────────────────────────
    // An issue created through the API is plain title/body/labels: GitHub issue forms exist only
    // in the web UI. `fields` reproduces the markdown an issue form submission produces
    // (`### <label>` followed by the value, `_No response_` when empty), so the automation that
    // parses issues created from the template also understands issues created from here.

    /// <summary>Target repository as "owner/repo".</summary>
    public string? Repository { get; set; }

    /// <summary>Issue title template; supports the same {{input_id}} placeholders as parameters_map.</summary>
    public string? Title { get; set; }

    /// <summary>Labels applied on creation. The label is usually what the downstream workflow triggers on.</summary>
    public List<string> Labels { get; set; } = [];

    /// <summary>Issue-form sections in the order the template declares them.</summary>
    public List<IssueFormField> Fields { get; set; } = [];

    /// <summary>Free-form body template, used only when <see cref="Fields"/> is empty.</summary>
    public string? Body { get; set; }

    /// <summary>
    /// Optional comment posted right after the issue is created, e.g. to record who requested it:
    /// the issue author is the configured token, not the person who submitted the request.
    /// </summary>
    public string? Comment { get; set; }

    /// <summary>How the state of the issue maps back onto the request.</summary>
    public IssueCompletionConfig? Completion { get; set; }
}

/// <summary>One section of a GitHub issue form, rendered as <c>### Label</c> followed by the value.</summary>
public class IssueFormField
{
    /// <summary>Exact label from the issue form template; parsers match on it.</summary>
    public string Label { get; set; } = "";

    /// <summary>Value template, usually a single {{input_id}} placeholder.</summary>
    public string Value { get; set; } = "";
}

public class IssueCompletionConfig
{
    /// <summary>Request status when the issue is closed as completed (default Completed).</summary>
    public string ClosedCompleted { get; set; } = "Completed";

    /// <summary>Request status when the issue is closed as not planned or duplicate (default Failed).</summary>
    public string ClosedNotPlanned { get; set; } = "Failed";

    /// <summary>
    /// Case-insensitive text that marks a comment as a failure report from the downstream automation.
    /// While the issue is still open, the first comment containing it fails the request with that comment as the error.
    /// </summary>
    public string? FailureCommentMarker { get; set; }

    /// <summary>Hours after which an issue still open is treated as failed (default 168, one week).</summary>
    public int? TimeoutHours { get; set; }
}
