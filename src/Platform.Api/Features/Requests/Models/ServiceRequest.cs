namespace Platform.Api.Features.Requests.Models;

public class ServiceRequest
{
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
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    // Navigation
    public Catalog.Models.CatalogItem? CatalogItem { get; set; }
    public Catalog.Models.CatalogItemVersion? Snapshot { get; set; }
    public List<FileAttachment> Attachments { get; set; } = [];
    public List<ExecutionResult> ExecutionResults { get; set; } = [];
    public Approvals.Models.ApprovalRequest? ApprovalRequest { get; set; }
}
