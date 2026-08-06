using System.Text.Json;
using Platform.Api.Features.Promotions.Models;

namespace Platform.Api.Features.Rollbacks.Models;

/// <summary>
/// A set of principals — the union of AD/role <see cref="Groups"/> and explicit user
/// <see cref="Users"/> (matched case-insensitively on email). The same shape as the group/user half of
/// an <see cref="ApproverRequirement"/>, without the <c>MinApprovers</c> count: a capability check is
/// "is this one person in the set", not "have N distinct people acted".
///
/// <para>An <b>empty</b> set grants nothing. It never means "everyone" — see
/// <see cref="RollbackPolicy.Creators"/>.</para>
/// </summary>
public record PrincipalSet(List<GroupRef> Groups, List<string> Users)
{
    public PrincipalSet() : this(new(), new()) { }

    public bool IsEmpty => Groups.Count == 0 && Users.Count == 0;
}

/// <summary>
/// Per-product rollback configuration: <b>who may create</b> a rollback (<see cref="Creators"/>) and
/// <b>who must approve</b> it (<see cref="ApprovalSteps"/>). One row per (product, target env), where
/// a null <see cref="TargetEnv"/> is the product default — the same specific-then-default resolution
/// idiom <see cref="PromotionPolicy"/> uses for its nullable <c>Service</c>.
///
/// <para>The existence of a row is also the <b>enrollment</b> signal: a product with no row at all is
/// not configured for rollbacks, which is deliberately not the same thing as a row with an empty
/// approval tree. See <see cref="RollbackPolicyResolver"/> for what each case does.</para>
///
/// <para>This replaces the previous arrangement, where the <c>rollback.enabledProducts</c> platform
/// setting carried enrollment and the approval gate was borrowed from whichever
/// <see cref="PromotionPolicy"/> happened to guard the target environment. Borrowing meant rollback
/// approvers could not be configured independently of promotion approvers, and — because a missing
/// promotion policy projects to auto-approve — that an enrolled product with no policy for its prod
/// environment could roll back prod with no human gate at all.</para>
/// </summary>
public class RollbackPolicy
{
    public Guid Id { get; set; }

    // ── Scope ────────────────────────────────────────────────────────────────

    public string Product { get; set; } = "";

    /// <summary>
    /// The environment this policy governs, or <c>null</c> for the product default that applies to
    /// every environment without its own row. Lets "two approvers for prod, none for dev" coexist
    /// under one product.
    /// </summary>
    public string? TargetEnv { get; set; }

    // ── Who may create ───────────────────────────────────────────────────────

    /// <summary>JSON-serialised <see cref="Creators"/>. Persisted as a plain string column.</summary>
    public string CreatorsJson { get; set; } = "{}";

    /// <summary>
    /// Who may raise a rollback request in this scope. <b>An empty set grants nobody</b> — admins
    /// remain able to create (they can override the gate anyway, so denying them the request would be
    /// theatre), but no one else can. Empty is therefore the safe default for a half-filled policy,
    /// not an accidental open door.
    /// </summary>
    public PrincipalSet Creators
    {
        get => string.IsNullOrEmpty(CreatorsJson)
            ? new()
            : JsonSerializer.Deserialize<PrincipalSet>(CreatorsJson, JsonOpts) ?? new();
        set => CreatorsJson = JsonSerializer.Serialize(value, JsonOpts);
    }

    // ── Who must approve ─────────────────────────────────────────────────────

    /// <summary>
    /// JSON-serialised <see cref="ApprovalSteps"/>, mirroring
    /// <see cref="PromotionPolicy.ApprovalStepsJson"/>. Empty array (<c>"[]"</c>) ⇒ no gate.
    /// </summary>
    public string ApprovalStepsJson { get; set; } = "[]";

    /// <summary>
    /// The approval rule tree, reusing the promotion model wholesale (steps → requirements → groups ∪
    /// users, satisfied by <c>MinApprovers</c> distinct people, ANDed across the flattened set and
    /// evaluated by <c>ApprovalMatcher</c>). Rollbacks and promotions share the vocabulary of
    /// approval; what this entity adds is the ability to point it somewhere different.
    ///
    /// <para>No steps (or steps with no requirements) ⇒ this scope is an explicit <b>auto-approve</b>
    /// record: rollbacks here need no human decision. That is distinct from having no policy row,
    /// which requires an admin override instead.</para>
    /// </summary>
    public List<ApprovalStep> ApprovalSteps
    {
        get => string.IsNullOrEmpty(ApprovalStepsJson)
            ? new()
            : JsonSerializer.Deserialize<List<ApprovalStep>>(ApprovalStepsJson, JsonOpts) ?? new();
        set => ApprovalStepsJson = JsonSerializer.Serialize(value, JsonOpts);
    }

    public string? EscalationGroup { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public string? UpdatedBy { get; set; }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };
}
