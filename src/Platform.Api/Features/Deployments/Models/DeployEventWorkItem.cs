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
    public string? Revision { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
