namespace Platform.Api.Features.Promotions.Models;

/// <summary>
/// A QA / approver decision on a specific work-item for a specific
/// (product, target environment). Approvals carry across superseded builds —
/// they're attached to the work item, not the candidate. The promotion gate
/// evaluator (PR3) reads this table to decide whether the active candidate
/// can transition to Approved under WorkItemsOnly / WorkItemsAndManual modes.
///
/// One signoff per work item is enough (MVP). The unique index on
/// (WorkItemKey, Product, TargetEnv, ApproverEmail) prevents the same user
/// from recording two decisions on the same work item; multi-approver-per-work-item
/// is a future policy.
/// </summary>
public class WorkItemApproval
{
    public Guid Id { get; set; }
    public string WorkItemKey { get; set; } = "";
    public string Product { get; set; } = "";
    public string TargetEnv { get; set; } = "";
    public string ApproverEmail { get; set; } = "";
    public string ApproverName { get; set; } = "";
    public PromotionDecision Decision { get; set; }
    public string? Comment { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
