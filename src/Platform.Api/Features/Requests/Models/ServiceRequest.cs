using System.Text.Json;

namespace Platform.Api.Features.Requests.Models;

public class ServiceRequest
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public Guid Id { get; set; }
    public Guid CorrelationId { get; set; }
    public Guid CatalogItemId { get; set; }
    public Guid? SnapshotId { get; set; }
    public string RequesterId { get; set; } = "";
    public string RequesterName { get; set; } = "";
    public string RequesterEmail { get; set; } = "";
    public RequestStatus Status { get; set; } = RequestStatus.Draft;
    public string InputsJson { get; set; } = "{}";
    public string? ExternalTicketKey { get; set; }
    public string? ExternalTicketUrl { get; set; }

    /// <summary>
    /// JSON of <see cref="ApprovalOutcome"/>, set on submit. Null for requests never submitted and
    /// for those submitted before approval rules existed.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? ApprovalOutcomeJson { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Accessor over <see cref="ApprovalOutcomeJson"/> (not mapped).</summary>
    public ApprovalOutcome? ApprovalOutcome
    {
        get => string.IsNullOrEmpty(ApprovalOutcomeJson) ? null : JsonSerializer.Deserialize<ApprovalOutcome>(ApprovalOutcomeJson, JsonOptions);
        set => ApprovalOutcomeJson = value is null ? null : JsonSerializer.Serialize(value, JsonOptions);
    }

    // Navigation
    public Catalog.Models.CatalogItem? CatalogItem { get; set; }
    public Catalog.Models.CatalogItemVersion? Snapshot { get; set; }
    public List<FileAttachment> Attachments { get; set; } = [];
    public List<ExecutionResult> ExecutionResults { get; set; } = [];
    public Approvals.Models.ApprovalRequest? ApprovalRequest { get; set; }
}
