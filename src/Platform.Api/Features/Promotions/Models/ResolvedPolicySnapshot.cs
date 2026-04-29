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
/// <para><c>Gate</c> defaults to <see cref="PromotionGate.PromotionOnly"/>. Old candidates
/// whose serialised JSON predates this field deserialise with that default — preserving the
/// legacy promotion-only flow until PR3 starts populating it from the policy.</para>
/// </summary>
public record ResolvedPolicySnapshot(
    Guid? PolicyId,
    string? ApproverGroup,
    PromotionStrategy Strategy,
    int MinApprovers,
    string? ExcludeRole,
    int TimeoutHours,
    string? EscalationGroup)
{
    /// <summary>True when no human approval is required for this edge.</summary>
    public bool IsAutoApprove => string.IsNullOrEmpty(ApproverGroup);

    /// <summary>
    /// How the candidate's approval is evaluated. Defaults to <see cref="PromotionGate.PromotionOnly"/>
    /// so JSON payloads written before this field existed (i.e. old candidates) deserialise to the
    /// pre-PR3 behaviour. Init-only so the snapshot remains effectively immutable.
    /// </summary>
    public PromotionGate Gate { get; init; } = PromotionGate.PromotionOnly;

    // ── Ticket-gate options (default false so old snapshot JSON is backward-compatible) ──

    /// <inheritdoc cref="PromotionPolicy.RequireAllTicketsApproved"/>
    public bool RequireAllTicketsApproved { get; init; } = false;

    /// <inheritdoc cref="PromotionPolicy.AutoApproveOnAllTicketsApproved"/>
    public bool AutoApproveOnAllTicketsApproved { get; init; } = false;

    /// <inheritdoc cref="PromotionPolicy.AutoApproveWhenNoTickets"/>
    public bool AutoApproveWhenNoTickets { get; init; } = false;
}
