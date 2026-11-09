namespace Platform.Api.Features.Rollbacks.Models;

/// <summary>Request body for creating a rollback. Two selection modes share one shape.</summary>
public record CreateRollbackRequestDto(
    string Product,
    string TargetEnv,
    string Mode,                          // "manual" | "align"
    string? ReferenceEnv = null,          // align: env whose versions we match
    List<string>? Exclude = null,         // align: services to hold back ("all except")
    List<RollbackItemInput>? Items = null,// manual: explicit (service, toVersion) list
    string? Reason = null);

/// <summary>
/// One manual item. An empty <paramref name="ToVersion"/> uninstalls the service: it is removed from
/// the environment, which is allowed for any service that is there (running, or with a failed deploy).
/// </summary>
public record RollbackItemInput(string Service, string ToVersion);

/// <summary>
/// One resolved item in a create/preview result, with why it was included or skipped.
/// <c>Note</c> reports a failed deploy newer than the running version. The failed version is not the
/// current one, but whoever saw it in deploy history needs to know why the preview disagrees.
/// </summary>
public record ResolvedRollbackItem(
    string Service,
    string FromVersion,
    string ToVersion,
    bool Eligible,
    string? SkipReason,
    string? Note = null);

/// <summary>Dry-run result so the UI can show "will roll back N, skip M" before submitting.</summary>
public record RollbackPreview(
    string Product,
    string TargetEnv,
    string Mode,
    string? ReferenceEnv,
    List<ResolvedRollbackItem> Items);

/// <summary>A failed deploy of an open rollback item's target version, since the request was approved.</summary>
public record FailedDeployAttempt(Guid DeployEventId, DateTimeOffset DeployedAt);

/// <summary>
/// The "roll back to the last known good version" offer for one failed deploy event, produced by
/// <c>RollbackService.GetLastKnownGoodAsync</c>. <see cref="Available"/> false ⇒ <see cref="Reason"/>
/// explains why and the version fields are null.
/// </summary>
/// <param name="FailedVersion">The service's newest failed deploy: the event itself, or a later failed one.</param>
/// <param name="LastGoodVersion">The version the rollback would restore: the last succeeded deploy.</param>
/// <param name="Approval">"auto", "required" or "unconfigured" (no rollback policy: an admin must override).</param>
/// <param name="Approvers">The approval steps a "required" rollback waits on.</param>
/// <param name="OpenRollback">An open request that already carries this service, if any.</param>
/// <param name="ExecutorConfigured">Whether any webhook subscription delivers <c>rollback.approved</c> here.</param>
/// <param name="PromotionsToReopen">Promotions of the failed version that approving the rollback reopens.</param>
/// <param name="Action">
/// <see cref="LastKnownGoodActions.Restore"/> back to <paramref name="LastGoodVersion"/>, or
/// <see cref="LastKnownGoodActions.Remove"/> the service from the environment when no version of it
/// ever deployed successfully there (<paramref name="LastGoodVersion"/> is then null). Null when not
/// available.
/// </param>
public record LastKnownGoodOffer(
    Guid EventId,
    string Product,
    string Service,
    string TargetEnv,
    bool Available,
    string? Reason,
    string? FailedVersion,
    Guid? FailedEventId,
    DateTimeOffset? FailedAt,
    string? LastGoodVersion,
    Guid? LastGoodEventId,
    DateTimeOffset? LastGoodAt,
    bool CanCreate,
    string? CreateBlockedReason,
    string? Approval,
    IReadOnlyList<string> Approvers,
    OpenRollbackRef? OpenRollback,
    bool ExecutorConfigured,
    IReadOnlyList<PromotionToReopen> PromotionsToReopen,
    string? Action);

/// <summary>The two things "back to the last known good state" can mean.</summary>
public static class LastKnownGoodActions
{
    /// <summary>Redeploy the last version that deployed successfully.</summary>
    public const string Restore = "restore";

    /// <summary>
    /// Take the service out of the environment: nothing of it ever deployed successfully there. The
    /// rollback item has an empty target version.
    /// </summary>
    public const string Remove = "remove";
}

public record OpenRollbackRef(Guid Id, string Status, DateTimeOffset CreatedAt);

public record PromotionToReopen(Guid Id, string SourceEnv, string Version, string Status);

/// <summary>Body for <c>POST /api/rollbacks/last-known-good/{eventId}</c>.</summary>
public record LastKnownGoodBody(string? Reason);

/// <summary>
/// Thrown when a rollback is asked for a service that an open request already carries. Mapped to 409
/// with the open request's id, so the caller can point at it instead of raising a second one.
/// </summary>
public sealed class RollbackAlreadyOpenException(Guid requestId, string status)
    : InvalidOperationException($"Rollback {requestId} is already {status} for this service")
{
    public Guid RequestId { get; } = requestId;
}

public record RollbackQuery(
    RollbackStatus? Status = null,
    string? Product = null,
    string? TargetEnv = null,
    int Limit = 200);
