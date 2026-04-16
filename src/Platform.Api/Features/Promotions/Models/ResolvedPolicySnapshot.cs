namespace Platform.Api.Features.Promotions.Models;

/// <summary>
/// Immutable snapshot of the promotion policy at the moment a candidate was created.
/// Persisted as <see cref="PromotionCandidate.ResolvedPolicyJson"/> so subsequent policy
/// edits never change the rules mid-flight for an already-pending promotion.
///
/// <para><c>PolicyId</c> is <c>null</c> when the resolution fell through to "auto-approve"
/// (no matching row at all). <c>ApproverGroup</c> is <c>null</c> when the policy exists but
/// intentionally has no approver group — also treated as auto-approve.</para>
///
/// </summary>
public record ResolvedPolicySnapshot(
    Guid? PolicyId,
    string? ApproverGroup,
    PromotionStrategy Strategy,
    int MinApprovers,
    bool ExcludeDeployer,
    int TimeoutHours,
    string? EscalationGroup)
{
    /// <summary>True when no human approval is required for this edge.</summary>
    public bool IsAutoApprove => string.IsNullOrEmpty(ApproverGroup);
}
