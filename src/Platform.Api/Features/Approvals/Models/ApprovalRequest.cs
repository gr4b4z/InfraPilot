namespace Platform.Api.Features.Approvals.Models;

public class ApprovalRequest
{
    public Guid Id { get; set; }
    public Guid ServiceRequestId { get; set; }
    public ApprovalStrategy Strategy { get; set; } = ApprovalStrategy.Any;
    public int? QuorumCount { get; set; }
    public string Status { get; set; } = "Pending";
    public DateTimeOffset? TimeoutAt { get; set; }
    public string? EscalationGroup { get; set; }
    public bool Escalated { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    // Navigation
    public Requests.Models.ServiceRequest? ServiceRequest { get; set; }
    public List<ApprovalDecision> Decisions { get; set; } = [];
}
