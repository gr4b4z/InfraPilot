using System.Text.Json;

namespace Platform.Api.Features.Promotions.Models;

/// <summary>
/// Configures who approves promotions to a given target environment, optionally narrowed to
/// a specific service. Resolution for a candidate: service-specific row wins, then product-level,
/// then implicit auto-approve (no policy row at all).
///
/// <para>Authorization is expressed as a bounded rule tree (<see cref="ApprovalSteps"/>): a list of
/// steps, each a list of requirements, each requirement satisfiable by a union of groups and users
/// (plan §8). An empty step list means the policy is an explicit "auto-approve this edge" record
/// (distinct from "no policy"). This replaces the legacy single
/// <c>ApproverGroup</c>/<c>Strategy</c>/<c>MinApprovers</c> trio (decisions D6–D12).</para>
/// </summary>
public class PromotionPolicy
{
    public Guid Id { get; set; }

    // Scope
    public string Product { get; set; } = "";
    public string? Service { get; set; }
    public string SourceEnv { get; set; } = "";
    public string TargetEnv { get; set; } = "";

    // ── Authorization (rule tree) ────────────────────────────────────────────

    /// <summary>
    /// JSON-serialised <see cref="ApprovalSteps"/>. Persisted as a plain string column; the
    /// computed <see cref="ApprovalSteps"/> mirrors the JSON computed-property pattern used on
    /// <see cref="PromotionCandidate.Participants"/>. Empty array (<c>"[]"</c>) ⇒ auto-approve.
    /// </summary>
    public string ApprovalStepsJson { get; set; } = "[]";

    /// <summary>
    /// The approval rule tree. A policy is satisfied when every requirement across every step has
    /// enough distinct eligible approvers (see <c>ApprovalMatcher</c>). No steps (or steps with no
    /// requirements) ⇒ no human gate ⇒ auto-approve.
    /// </summary>
    public List<ApprovalStep> ApprovalSteps
    {
        get => string.IsNullOrEmpty(ApprovalStepsJson)
            ? new()
            : JsonSerializer.Deserialize<List<ApprovalStep>>(ApprovalStepsJson, JsonOpts) ?? new();
        set => ApprovalStepsJson = JsonSerializer.Serialize(value, JsonOpts);
    }

    // ── Work-item-gate options ─────────────────────────────────────────────────
    // These three flags are independent and can be combined freely.

    /// <summary>
    /// When <c>true</c>, a human approver cannot approve the promotion until every work item
    /// in the bundle has at least one Approved WorkItemApproval row. Has no effect when the bundle
    /// contains no work items (nothing to wait for).
    /// </summary>
    public bool RequireAllWorkItemsApproved { get; set; } = false;

    /// <summary>
    /// When <c>true</c>, the candidate is automatically promoted the moment all work items
    /// in the bundle have been approved, regardless of any human approver requirements — the
    /// first path that satisfies the gate wins.
    /// </summary>
    public bool AutoApproveOnAllWorkItemsApproved { get; set; } = false;

    /// <summary>
    /// When <c>true</c>, a promotion candidate is auto-approved at creation time if its source
    /// deploy event has no work-item references. Useful for services where work items are expected
    /// on normal deploys but occasionally a purely-infrastructure change ships with none.
    /// </summary>
    public bool AutoApproveWhenNoWorkItems { get; set; } = false;

    // Timeouts / escalation
    public int TimeoutHours { get; set; } = 24;
    public string? EscalationGroup { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };
}
