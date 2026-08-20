namespace Platform.Api.Features.Deployments.Models;

/// <summary>
/// Relational projection of <see cref="DeployEvent.References"/> entries with
/// <c>Type == "work-item"</c>. Populated at ingest time so we can answer
/// "which builds carry ticket FOO-123?" without scanning every event's JSON.
///
/// Approvals on tickets live in a separate <c>WorkItemApproval</c> table (PR2)
/// and key on <c>(WorkItemKey, Product, TargetEnv)</c>. This table is the
/// build→ticket index that connects the two.
/// </summary>
public class DeployEventWorkItem
{
    public Guid Id { get; set; }
    public Guid DeployEventId { get; set; }

    // The ticket key, e.g. "FOO-123". Required.
    public string WorkItemKey { get; set; } = "";

    // Product carried over from the parent event so approval queries can scope
    // by (key, product, env) without joining back. Denormalised on purpose.
    public string Product { get; set; } = "";

    public string? Provider { get; set; }
    public string? Url { get; set; }
    public string? Title { get; set; }

    // Secondary display line: when the producer names the change by its commit subject (Title),
    // this carries the tracker's own summary — e.g. the Jira ticket title. Null when the producer
    // sent a single name.
    public string? SubTitle { get; set; }

    // The ticket body as the producer sent it — Jira description, PR description, commit message
    // body. Unbounded: a description is prose, not a label.
    public string? Content { get; set; }

    public string? Revision { get; set; }

    // When the change carrying this ticket entered trunk — min OccurredAt over the ticket's
    // linked pull-request references (fallback: commit references). Resolved at sync time by
    // WorkItemCommitTime so lead-time analytics can subtract it from DeployedAt without
    // scanning ReferencesJson. Null when the producer sent no timestamps (older events).
    public DateTimeOffset? CommittedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
