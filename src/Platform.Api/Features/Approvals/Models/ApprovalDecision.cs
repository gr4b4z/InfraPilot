namespace Platform.Api.Features.Approvals.Models;

public class ApprovalDecision
{
    public Guid Id { get; set; }
    public Guid ApprovalRequestId { get; set; }
    public string ApproverId { get; set; } = "";
    public string ApproverName { get; set; } = "";
    public string Decision { get; set; } = ""; // Approved, Rejected, ChangesRequested
    public string? Comment { get; set; }
    public DateTimeOffset DecidedAt { get; set; } = DateTimeOffset.UtcNow;

    public ApprovalRequest? ApprovalRequest { get; set; }
}
