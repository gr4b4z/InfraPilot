namespace Platform.Api.Features.Rollbacks.Models;

/// <summary>
/// One service's revert within a <see cref="RollbackRequest"/>: take <c>Service</c> in the
/// request's environment from <see cref="FromVersion"/> (what's running now) to
/// <see cref="ToVersion"/> (a version that previously ran successfully in that environment).
///
/// <para>Items complete independently as their target version lands as a deploy event, so a bulk
/// "align" request settles item-by-item. An item can also close without its own deploy: it was
/// already running at approval (RolledBack), the environment moved on (Superseded), or an admin
/// skipped it (Skipped). <see cref="Note"/> says which.</para>
/// </summary>
public class RollbackItem
{
    public Guid Id { get; set; }
    public Guid RequestId { get; set; }

    public string Service { get; set; } = "";

    /// <summary>Version running in the env when the request was created (for display/audit).</summary>
    public string FromVersion { get; set; } = "";

    /// <summary>Target version — validated to have previously run in this env.</summary>
    public string ToVersion { get; set; } = "";

    public RollbackItemStatus Status { get; set; } = RollbackItemStatus.Pending;

    /// <summary>
    /// The deploy event that closed this item: the revert landing, the deploy that was already running
    /// it at approval, or the later deploy that superseded it. Null when an admin skipped it.
    /// </summary>
    public Guid? CompletedDeployEventId { get; set; }

    /// <summary>
    /// Why the item closed, when its status alone doesn't say: what superseded it, that it was already
    /// running at approval, or the admin's skip reason. Null for an ordinary landing.
    /// </summary>
    public string? Note { get; set; }

    /// <summary>Optional CI run URL if the executor reports one.</summary>
    public string? ExternalRunUrl { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }
}

public enum RollbackItemStatus
{
    Pending,
    RollingBack,
    RolledBack,
    Failed,
    Skipped,

    /// <summary>
    /// A different version was deployed after the rollback was approved, so the revert no longer
    /// describes the environment and must not go out.
    /// </summary>
    Superseded,
}
