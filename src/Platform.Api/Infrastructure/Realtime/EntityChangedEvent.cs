namespace Platform.Api.Infrastructure.Realtime;

/// <summary>
/// Compact broadcast telling connected clients that a server entity changed, so any open page
/// showing it can refetch its data. Deliberately not the entity payload: clients re-query the API
/// through the usual authorized endpoints instead of trusting a pushed snapshot, which keeps the
/// broadcast safe to send to every connection regardless of what each user may see.
/// </summary>
public sealed record EntityChangedEvent
{
    /// <summary>
    /// "promotion", "work-item", "deployment", "request", "approval", "rollback",
    /// "release-note" or "webhook-delivery".
    /// </summary>
    public required string Entity { get; init; }

    /// <summary>"created", "updated", "approved", … — informational; most clients just refetch.</summary>
    public required string Action { get; init; }

    public string? Id { get; init; }

    /// <summary>Work-item key for work-item events (work items have no GUID of their own).</summary>
    public string? Key { get; init; }

    public string? Product { get; init; }

    public string? Environment { get; init; }

    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
}
