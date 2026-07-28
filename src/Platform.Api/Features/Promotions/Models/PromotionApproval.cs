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

    /// <summary>
    /// Work-item only: the item is held back without vetoing the promotion. Unlike
    /// <see cref="Rejected"/> (a veto that terminates the candidate), a block leaves the
    /// candidate Pending and is reversible — the same user can later switch to Approved.
    /// The gate treats a blocked work item as unresolved. Never produced for a
    /// <see cref="PromotionApproval"/> (promotion-level decisions are Approved / Rejected only).
    /// </summary>
    Blocked,
}
