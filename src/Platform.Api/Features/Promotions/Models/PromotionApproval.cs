namespace Platform.Api.Features.Promotions.Models;

/// <summary>
/// One approver's decision on a candidate, recorded against one approval requirement (gate). A
/// person eligible for several gates approves each of them separately and gets one row per gate.
/// The DB-level UNIQUE on (CandidateId, ApproverEmail, StepName, RequirementName) is the
/// belt-and-suspenders guard against approving the same gate twice in a race.
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
    /// Which <see cref="ApprovalStep"/> / <see cref="ApproverRequirement"/> the approval was
    /// recorded against. Every approval recorded through <c>ApproveAsync</c> carries it; the gate
    /// evaluator counts the row toward exactly that requirement. Null on Rejected rows, on
    /// auto-approve rows and on legacy data recorded before attribution existed — those the matcher
    /// attributes itself (most-constrained requirement first, at most one requirement per person).
    /// </summary>
    public string? StepName { get; set; }

    /// <inheritdoc cref="StepName"/>
    public string? RequirementName { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// A decision on a <i>promotion</i> (or a rollback request). Rejection here is a veto: it
/// terminates the candidate. Work items have their own vocabulary — see
/// <see cref="WorkItemDecision"/> — because nothing an approver does to a single ticket
/// terminates anything.
/// </summary>
public enum PromotionDecision
{
    Approved,
    Rejected,
}
