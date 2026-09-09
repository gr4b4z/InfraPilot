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
/// in <c>WorkItemApproval</c> keyed on <c>(WorkItemKey, Product, Service, TargetEnv)</c> so they
/// survive a supersede.</para>
/// </summary>
public class PromotionWorkItem
{
    public Guid Id { get; set; }
    public Guid CandidateId { get; set; }

    // The ticket key, e.g. "FOO-123". Required.
    public string WorkItemKey { get; set; } = "";

    // Product / service / target env carried over from the parent candidate so approval queries can
    // scope by (key, product, service, env) without joining back. Denormalised on purpose.
    //
    // Service is part of the work item's identity: the same Jira ticket carried by two services of
    // one product is two work items — mpt-helpdesk/MPT-1 and mpt-platform/MPT-1 — each with its own
    // sign-off, thread and assignments. Two promotions of the SAME service (a new version, a second
    // edge into the same env) still share one work item.
    public string Product { get; set; } = "";
    public string Service { get; set; } = "";
    public string TargetEnv { get; set; } = "";

    public string? Provider { get; set; }
    public string? Url { get; set; }
    public string? Title { get; set; }

    // Secondary display line: the messages of the commits this ticket rode in on, joined — one
    // ticket routinely carries several. Title is the tracker's own name for the item, this is what
    // actually changed. Resolved at sync time by WorkItemDisplay; null when the payload linked no
    // commits, or when the one commit is named the same thing as the ticket.
    public string? SubTitle { get; set; }

    // The ticket body as the producer sent it — Jira description, PR description, commit message
    // body. Unbounded: a description is prose, not a label.
    public string? Content { get; set; }

    // The tracker's priority label ("High") and issue type ("Bug", "Story") as the producer read
    // them. Labels, not enums: trackers name their levels differently and the UI only needs to
    // recognise the common ones to colour them. Null when the producer didn't send them.
    public string? Priority { get; set; }
    public string? WorkItemType { get; set; }

    public string? Revision { get; set; }

    // When the change carrying this ticket entered trunk — same resolution as
    // <see cref="Deployments.Models.DeployEventWorkItem.CommittedAt"/>, computed from the
    // candidate's references. Null when the producer sent no timestamps.
    public DateTimeOffset? CommittedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
