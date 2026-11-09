namespace Platform.Api.Features.Analytics;

/// <summary>The slice of a deploy event the counting endpoints read — one light row per event.</summary>
public sealed record EventRow(
    Guid Id, string Product, string Service, string Environment,
    string Version, string? PreviousVersion, bool IsRollback, string Status, DateTimeOffset DeployedAt);

public enum EventKind { Counted, Failed, Rollback, RollbackCounted, Skipped }

/// <summary>
/// How a deploy event is counted, shared by the frequency endpoint and the delivery report so the two
/// can never disagree on what a "deploy" is: succeeded and not a rollback counts; a failed attempt is
/// reported apart; a rollback is reported apart (and counted only when asked); a redeploy of the
/// version already running is skipped unless asked for.
/// </summary>
public static class DeployEventRows
{
    public static bool IsSucceeded(string status)
        => string.Equals(status, "succeeded", StringComparison.OrdinalIgnoreCase);

    public static EventKind Classify(EventRow e, bool includeRollbacks, bool includeRedeploys)
    {
        if (e.IsRollback)
            return includeRollbacks && IsSucceeded(e.Status) ? EventKind.RollbackCounted : EventKind.Rollback;
        if (!IsSucceeded(e.Status)) return EventKind.Failed;
        var isRedeploy = e.PreviousVersion is not null && e.Version == e.PreviousVersion;
        if (isRedeploy && !includeRedeploys) return EventKind.Skipped;
        return EventKind.Counted;
    }
}
