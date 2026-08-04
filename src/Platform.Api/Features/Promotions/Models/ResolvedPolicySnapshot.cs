namespace Platform.Api.Features.Promotions.Models;

/// <summary>
/// The resolved promotion policy a candidate is gated on, persisted as
/// <see cref="PromotionCandidate.ResolvedPolicyJson"/> so gate evaluation never depends on a live
/// join (and so a deleted policy leaves the candidate evaluable).
///
/// <para>The record itself is immutable, but the snapshot <b>on a Pending candidate</b> is replaced
/// wholesale when the policy is edited — see
/// <see cref="PromotionService.RefreshPolicySnapshotsAsync"/>. Once a candidate leaves Pending its
/// snapshot is frozen: the approval that already fired was judged under those rules.</para>
///
/// <para><c>PolicyId</c> is <c>null</c> when the resolution fell through to "auto-approve"
/// (no matching row at all). An empty <see cref="ApprovalSteps"/> (no requirements anywhere) is
/// also auto-approve.</para>
///
/// <para>All non-positional members are init-only with defaults so JSON written before a member
/// existed (i.e. older candidates) still deserialises. In particular <see cref="ApprovalSteps"/>
/// defaults to empty: a candidate created under the legacy single-group model deserialises to "no
/// requirements" — callers that need the legacy behaviour read the gate flags, which are preserved.
/// New candidates always carry a populated tree.</para>
/// </summary>
public record ResolvedPolicySnapshot(
    Guid? PolicyId,
    string? EscalationGroup)
{
    /// <summary>
    /// The approval rule tree captured at creation time. Empty ⇒ auto-approve (no human gate).
    /// Init-only so the snapshot stays effectively immutable; defaults to empty so old snapshot
    /// JSON (which had no such field) deserialises cleanly.
    /// </summary>
    public List<ApprovalStep> ApprovalSteps { get; init; } = new();

    /// <summary>True when no human approval is required for this edge (no requirements anywhere).</summary>
    public bool IsAutoApprove => ApprovalSteps.All(s => s.Requirements.Count == 0);

    /// <inheritdoc cref="PromotionPolicy.TracksWorkItems"/>
    /// <remarks>Defaults to <c>true</c> so snapshot JSON written before this field existed keeps the
    /// original behaviour (work items tracked).</remarks>
    public bool TracksWorkItems { get; init; } = true;

    /// <inheritdoc cref="PromotionPolicy.RequiredWorkItemRoles"/>
    /// <remarks>Defaults to empty so snapshot JSON written before this field existed reads as "no
    /// role requirement" rather than flagging every historical work item as incomplete.</remarks>
    public List<string> RequiredWorkItemRoles { get; init; } = new();

    // ── Work-item-gate options (default false so old snapshot JSON is backward-compatible) ──

    /// <inheritdoc cref="PromotionPolicy.RequireAllWorkItemsApproved"/>
    public bool RequireAllWorkItemsApproved { get; init; } = false;

    /// <inheritdoc cref="PromotionPolicy.AutoApproveOnAllWorkItemsApproved"/>
    public bool AutoApproveOnAllWorkItemsApproved { get; init; } = false;

    /// <inheritdoc cref="PromotionPolicy.AutoApproveWhenNoWorkItems"/>
    public bool AutoApproveWhenNoWorkItems { get; init; } = false;

    /// <inheritdoc cref="PromotionPolicy.SourceRequiresDeploy"/>
    /// <remarks>Defaults to <c>true</c> so snapshot JSON written before this field existed keeps
    /// the original behaviour (source deploy required, drift check active).</remarks>
    public bool SourceRequiresDeploy { get; init; } = true;

    /// <summary>Flattened requirement set across every step — the unit of gate satisfaction.</summary>
    public IReadOnlyList<ApproverRequirement> AllRequirements =>
        ApprovalSteps.SelectMany(s => s.Requirements).ToList();
}
