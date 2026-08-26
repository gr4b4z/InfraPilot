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

    // ── Work-item completeness ────────────────────────────────────────────────

    /// <summary>
    /// When <c>false</c>, promotions on this edge carry no work items at all: no
    /// <see cref="PromotionWorkItem"/> rows are created, so the tickets never reach the work-items
    /// queue, never need a sign-off, and are never flagged for a missing
    /// <see cref="RequiredWorkItemRoles"/> entry. The change set itself is untouched — the candidate
    /// still records its work-item references, so the promotion page and downstream integrations keep
    /// showing what shipped.
    ///
    /// <para>For edges whose target isn't ready for QA — a developer integration environment, a CI
    /// test ring — where a work item arriving in the queue is noise, not work. Defaults to <c>true</c>
    /// so existing edges keep tracking.</para>
    ///
    /// <para>Every other work-item setting on this policy (the required roles and the three gate
    /// flags) is inert while this is off: they all describe work items, and there are none.</para>
    /// </summary>
    public bool TracksWorkItems { get; set; } = true;

    /// <summary>
    /// JSON-serialised <see cref="RequiredWorkItemRoles"/>. Persisted as a plain string column,
    /// mirroring <see cref="ApprovalStepsJson"/>. Empty array (<c>"[]"</c>) ⇒ no role requirement.
    /// </summary>
    public string RequiredWorkItemRolesJson { get; set; } = "[]";

    /// <summary>
    /// Participant roles every work item on a candidate gated by this policy must have somebody in —
    /// e.g. <c>qa-owner</c>. A work item with no named person in one of these roles is
    /// <b>incomplete</b>: the platform flags it everywhere it renders and asks for someone to be put
    /// on the role. Stored canonicalised (<see cref="Infrastructure.RoleNormalizer"/>).
    ///
    /// <para>Deliberately not part of the approval gate: this records who is <i>answerable</i> for a
    /// work item, which is a data-completeness question, not an authorisation one. The blocking
    /// condition remains <see cref="RequireAllWorkItemsApproved"/>.</para>
    /// </summary>
    public List<string> RequiredWorkItemRoles
    {
        get => string.IsNullOrEmpty(RequiredWorkItemRolesJson)
            ? new()
            : JsonSerializer.Deserialize<List<string>>(RequiredWorkItemRolesJson, JsonOpts) ?? new();
        set => RequiredWorkItemRolesJson = JsonSerializer.Serialize(value, JsonOpts);
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

    /// <summary>
    /// When <c>true</c> (the default), an external candidate is rejected unless the exact version
    /// has a succeeded deploy event in <see cref="SourceEnv"/>. Set to <c>false</c> for edges whose
    /// source is not a real runtime environment — e.g. a CI landing zone / release track directory
    /// ("stable") that versions pass through without ever being deployed there. Disabling it also
    /// disables the source-drift gate check, which is meaningless without source deploy history.
    /// </summary>
    public bool SourceRequiresDeploy { get; set; } = true;

    // ── Build-registry auto-create ────────────────────────────────────────────

    /// <summary>
    /// JSON-serialised <see cref="AutoCreateFromBranches"/>. Persisted as a plain string column,
    /// mirroring <see cref="ApprovalStepsJson"/>. Null/empty ⇒ no auto-create.
    /// </summary>
    public string? AutoCreateFromBranchesJson { get; set; }

    /// <summary>
    /// Branch patterns (full git refs, <c>*</c> wildcards allowed — e.g.
    /// <c>refs/heads/main</c>, <c>refs/heads/release/*</c>) for which a registered build
    /// automatically opens a candidate on this edge. Meaningful only on edges whose
    /// <see cref="SourceEnv"/> is the synthetic <c>build</c> source; inert elsewhere — the
    /// <c>BuildIngestHook</c> is the only reader. Empty ⇒ builds never auto-create here; a person
    /// (or the promotions API) has to ask. This is what makes main → dev automatic while feature
    /// branches stay strictly on-demand (plan: feature-branch-builds D5).
    /// </summary>
    public List<string> AutoCreateFromBranches
    {
        get => string.IsNullOrEmpty(AutoCreateFromBranchesJson)
            ? new()
            : JsonSerializer.Deserialize<List<string>>(AutoCreateFromBranchesJson, JsonOpts) ?? new();
        set => AutoCreateFromBranchesJson = value.Count == 0 ? null : JsonSerializer.Serialize(value, JsonOpts);
    }

    /// <summary>
    /// Per-edge override of the pause between an approval and the <c>promotion.approved</c>
    /// delivery (the "undo window"). Null ⇒ the global default
    /// (<see cref="PromotionService.ApprovedWebhookDelay"/>). Set to 0 on auto-approved edges like
    /// <c>build → dev</c>, where a cancellation window on an automatic deploy is pure latency
    /// (plan: feature-branch-builds D12).
    /// </summary>
    public int? ApprovedWebhookDelaySeconds { get; set; }

    // Escalation
    public string? EscalationGroup { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };
}
