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

    /// <summary>
    /// Optional attribution: which <see cref="ApprovalStep"/> / <see cref="ApproverRequirement"/>
    /// the approver was recorded against. Informational only — the gate evaluator re-derives
    /// requirement satisfaction from group/user membership via the matcher, so correctness does
    /// not depend on these being set. Null on auto-approve rows and on legacy data.
    /// </summary>
    public string? StepName { get; set; }

    /// <inheritdoc cref="StepName"/>
    public string? RequirementName { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public enum PromotionDecision
{
    Approved,
    Rejected,
}
