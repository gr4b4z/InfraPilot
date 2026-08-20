namespace Platform.Api.Features.Promotions.Models;

/// <summary>
/// Candidate-scoped relational projection of <see cref="PromotionCandidate.References"/> entries
/// with <c>Type == "work-item"</c>. Populated at create time from the external payload so the gate
/// evaluator and the approval queue/assignment/lookup surfaces can query tickets directly by
/// candidate — and across candidates by ticket key, product, and env — without scanning the
/// candidate's JSON.
///
/// <para>This is the candidate analogue of <see cref="Deployments.Models.DeployEventWorkItem"/>:
/// that table stays keyed on the deploy event for deploy-history ("which builds carry ticket X").
/// This one is keyed on the candidate and feeds the promotion gate. Approvals on tickets still live
/// in <c>WorkItemApproval</c> keyed on <c>(WorkItemKey, Product, TargetEnv)</c> so they survive a
/// supersede.</para>
/// </summary>
public class PromotionWorkItem
{
    public Guid Id { get; set; }
    public Guid CandidateId { get; set; }

    // The ticket key, e.g. "FOO-123". Required.
    public string WorkItemKey { get; set; } = "";

    // Product / target env carried over from the parent candidate so approval queries can scope
    // by (key, product, env) without joining back. Denormalised on purpose.
    public string Product { get; set; } = "";
    public string TargetEnv { get; set; } = "";

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

    // When the change carrying this ticket entered trunk — same resolution as
    // <see cref="Deployments.Models.DeployEventWorkItem.CommittedAt"/>, computed from the
    // candidate's references. Null when the producer sent no timestamps.
    public DateTimeOffset? CommittedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
