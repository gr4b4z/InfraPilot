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
