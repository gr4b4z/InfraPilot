namespace Platform.Api.Features.Promotions.Models;

/// <summary>
/// Free-text discussion attached to a work item for a specific (product, target environment).
/// Keyed the same way as <see cref="WorkItemApproval"/> — <c>(WorkItemKey, Product, TargetEnv)</c> —
/// so a thread carries across superseded candidates: the conversation belongs to the ticket's
/// sign-off, not to whichever build happened to be live when it was written.
///
/// <para>Distinct from <see cref="PromotionComment"/> (scoped to one candidate) and from
/// <see cref="WorkItemApproval.Comment"/> (the note attached to a single decision). Comments are
/// editable/deletable by their author (or an admin); decisions are not.</para>
/// </summary>
public class WorkItemComment
{
    public Guid Id { get; set; }
    public string WorkItemKey { get; set; } = "";
    public string Product { get; set; } = "";
    public string TargetEnv { get; set; } = "";
    public string AuthorEmail { get; set; } = "";
    public string AuthorName { get; set; } = "";
    public string Body { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? UpdatedAt { get; set; }
}
