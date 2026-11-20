namespace Platform.Api.Features.Incidents.Models;

/// <summary>
/// A production incident, as far as delivery metrics need it: which service in which environment,
/// when it started, when service was restored, and — when known — which deployment caused it. A
/// pointer to the real incident (<see cref="Source"/> + <see cref="ExternalId"/>, <see cref="Url"/>),
/// not a copy of it: the incident system owns the title, timeline, severity narrative and postmortem.
/// A field goes in here only if a calculation or the correlation with deploys reads it.
///
/// <para>Feeds DORA's change failure rate (deploys with an attributed incident) and time to restore
/// (<see cref="ResolvedAt"/> − <see cref="StartedAt"/>). Attribution to a deploy is resolved at query
/// time from <see cref="CausedByDeployEventId"/>, else <see cref="Version"/>, else the version live
/// at <see cref="StartedAt"/> — see the delivery metrics notes.</para>
/// </summary>
public class Incident
{
    public Guid Id { get; set; }

    public string Product { get; set; } = "";
    public string Service { get; set; } = "";
    public string Environment { get; set; } = "";

    /// <summary>When the incident started (detection or alert time).</summary>
    public DateTimeOffset StartedAt { get; set; }

    /// <summary>When service was restored. Null while the incident is open.</summary>
    public DateTimeOffset? ResolvedAt { get; set; }

    /// <summary>The service version that was running, when the reporter knows it — explicit attribution.</summary>
    public string? Version { get; set; }

    /// <summary>The deploy event that caused it, when the reporter picked one — the strongest attribution.</summary>
    public Guid? CausedByDeployEventId { get; set; }

    /// <summary><c>manual</c> for incidents recorded in the portal; otherwise the system that sent it.</summary>
    public string Source { get; set; } = "manual";

    /// <summary>The incident's id in its source system. With <see cref="Source"/>, the upsert key.</summary>
    public string? ExternalId { get; set; }

    /// <summary>Link to the real incident.</summary>
    public string? Url { get; set; }

    public string CreatedBy { get; set; } = "";
    public string CreatedByName { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public string? UpdatedBy { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public static class IncidentSources
{
    public const string Manual = "manual";
}
