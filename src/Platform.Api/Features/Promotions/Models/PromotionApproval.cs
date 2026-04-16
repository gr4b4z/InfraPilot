namespace Platform.Api.Features.Promotions.Models;

/// <summary>
/// One approver's decision on a candidate. The DB-level UNIQUE on (CandidateId, ApproverEmail)
/// is the belt-and-suspenders guard against double-approval races.
/// </summary>
public class PromotionApproval
{
    public Guid Id { get; set; }
    public Guid CandidateId { get; set; }
    public string ApproverEmail { get; set; } = "";
    public string ApproverName { get; set; } = "";
    public string? Comment { get; set; }
    public PromotionDecision Decision { get; set; } = PromotionDecision.Approved;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public enum PromotionDecision
{
    Approved,
    Rejected,
}
