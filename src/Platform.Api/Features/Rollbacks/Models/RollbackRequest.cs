using System.Text.Json;

namespace Platform.Api.Features.Rollbacks.Models;

/// <summary>
/// An approvable request to revert one or more services in a single environment to an earlier,
/// previously-deployed version. The inverse of a promotion: the environment stays fixed and the
/// version moves backward.
///
/// <para>One request groups N <see cref="RollbackItem"/>s so the two real use cases share one
/// model: a single malfunctioning service (1 item) and "align this environment to a reference,
/// optionally excluding some services" (N items). Approval and tracking reuse the promotion
/// machinery (policy → approver group/strategy → gate), so rollbacks follow promotion rules.</para>
///
/// <para>Lifecycle: Pending → Approved → RollingBack → RolledBack, with Rejected / Cancelled as
/// terminal off-ramps. Completion is detected per-item from the deploy event the operator (or
/// Logic App executor) emits when the target version lands — there is no trusted callback.</para>
/// </summary>
public class RollbackRequest
{
    public Guid Id { get; set; }

    /// <summary>
    /// Product the rollback belongs to. Must have a <see cref="RollbackPolicy"/> covering the target
    /// environment, unless an admin is creating it.
    /// </summary>
    public string Product { get; set; } = "";

    /// <summary>The environment being rolled back. Rollback is in-place: source == target.</summary>
    public string TargetEnv { get; set; } = "";

    public RollbackStatus Status { get; set; } = RollbackStatus.Pending;

    /// <summary>How the item set was selected — for audit and re-resolution.</summary>
    public RollbackMode Mode { get; set; } = RollbackMode.Manual;

    /// <summary>For <see cref="RollbackMode.Align"/>: the environment whose versions we matched.</summary>
    public string? ReferenceEnv { get; set; }

    /// <summary>For <see cref="RollbackMode.Align"/>: services intentionally held back ("all except").</summary>
    public string ExclusionsJson { get; set; } = "[]";

    public List<string> Exclusions
    {
        get => string.IsNullOrEmpty(ExclusionsJson)
            ? new()
            : JsonSerializer.Deserialize<List<string>>(ExclusionsJson, JsonOpts) ?? new();
        set => ExclusionsJson = JsonSerializer.Serialize(value, JsonOpts);
    }

    /// <summary>Optional free-text reason (e.g. "prod incident INC-1234").</summary>
    public string? Reason { get; set; }

    // Snapshot of the resolved RollbackPolicy's approval tree at creation time, so a later policy edit
    // never changes the gate for an in-flight rollback. Stored in the same ResolvedPolicySnapshot shape
    // promotions use, so requests written before rollbacks had their own policy table still read back.
    //
    // A null PolicyId means no policy governed this scope: the request cannot be approved normally and
    // needs an admin override. (On requests created before the RollbackPolicy table existed, a non-null
    // PolicyId refers to the PromotionPolicy the gate was borrowed from.)
    public Guid? PolicyId { get; set; }
    public string? ResolvedPolicyJson { get; set; }

    /// <summary>
    /// True when an admin forced this request past its approval gate. Denormalised from the flagged
    /// <see cref="RollbackApproval"/> row so the queue list can mark overridden requests without
    /// loading approvals for every row; the who and why live on that row.
    /// </summary>
    public bool ApprovalOverridden { get; set; }

    public string CreatedBy { get; set; } = "";
    public string CreatedByName { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ApprovedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }

    public List<RollbackItem> Items { get; set; } = new();

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };
}

public enum RollbackStatus
{
    Pending,
    Approved,
    RollingBack,
    RolledBack,
    Rejected,
    Cancelled,
}

public enum RollbackMode
{
    /// <summary>Operator picked the specific service(s) and target version(s).</summary>
    Manual,

    /// <summary>Items derived from a diff against a reference environment (optionally minus exclusions).</summary>
    Align,
}
