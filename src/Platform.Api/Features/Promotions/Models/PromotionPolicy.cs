namespace Platform.Api.Features.Promotions.Models;

/// <summary>
/// Configures who approves promotions to a given target environment, optionally narrowed to
/// a specific service. Resolution for a candidate: service-specific row wins, then product-level,
/// then implicit auto-approve (no policy row at all).
///
/// <para><c>ApproverGroup</c> can be an Entra group name / object ID or a role claim. When null,
/// the policy is an explicit "auto-approve this edge" record (distinct from "no policy").</para>
/// </summary>
public class PromotionPolicy
{
    public Guid Id { get; set; }

    // Scope
    public string Product { get; set; } = "";
    public string? Service { get; set; }
    public string TargetEnv { get; set; } = "";

    // Authorization
    public string? ApproverGroup { get; set; }
    public PromotionStrategy Strategy { get; set; } = PromotionStrategy.Any;
    public int MinApprovers { get; set; } = 1;

    // How approvals are evaluated for candidates resolved against this policy.
    // Default preserves legacy behaviour; ticket-level modes are read by the PR3 gate evaluator.
    public PromotionGate Gate { get; set; } = PromotionGate.PromotionOnly;

    // ── Ticket-gate options ──────────────────────────────────────────────────
    // These three flags are independent and can be combined freely.

    /// <summary>
    /// When <c>true</c>, a human approver cannot approve the promotion until every work-item ticket
    /// in the bundle has at least one Approved WorkItemApproval row. Has no effect when the bundle
    /// contains no tickets (nothing to wait for).
    /// </summary>
    public bool RequireAllTicketsApproved { get; set; } = false;

    /// <summary>
    /// When <c>true</c>, the candidate is automatically promoted the moment all work-item tickets
    /// in the bundle have been approved. Works alongside any Gate mode — the first path that
    /// satisfies the gate wins.
    /// </summary>
    public bool AutoApproveOnAllTicketsApproved { get; set; } = false;

    /// <summary>
    /// When <c>true</c>, a promotion candidate is auto-approved at creation time if its source
    /// deploy event has no work-item references. Useful for services where tickets are expected
    /// on normal deploys but occasionally a purely-infrastructure change ships with none.
    /// </summary>
    public bool AutoApproveWhenNoTickets { get; set; } = false;

    // When set to a non-empty role name, anyone listed on the source deploy event with that
    // role (compared after normalisation) cannot approve. Null/empty means no exclusion.
    // Replaces the old bool `ExcludeDeployer` — the role is now explicit so installations that
    // call the pipeline initiator something other than "triggered-by" can opt in without code
    // changes.
    public string? ExcludeRole { get; set; } = "triggered-by";

    // Timeouts / escalation
    public int TimeoutHours { get; set; } = 24;
    public string? EscalationGroup { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public enum PromotionStrategy
{
    /// <summary>Any one authorized approver is enough.</summary>
    Any,

    /// <summary>N distinct authorized approvers required, N = MinApprovers.</summary>
    NOfM,
}

/// <summary>
/// Controls how a promotion candidate's approval is evaluated.
/// PromotionOnly is the legacy/default behaviour and preserves the pre-PR3
/// flow; the other modes are read by the PR3 gate evaluator.
/// </summary>
public enum PromotionGate
{
    /// <summary>Today's behaviour: candidate's PromotionApproval rows count toward the strategy threshold.</summary>
    PromotionOnly,
    /// <summary>Auto-approve when every work-item in the bundle has an Approved WorkItemApproval row.</summary>
    TicketsOnly,
    /// <summary>Tickets approved AND a manual PromotionApproval from the approver group.</summary>
    TicketsAndManual,
}
