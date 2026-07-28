namespace Platform.Api.Features.Promotions.Models;

/// <summary>
/// Free-text discussion attached to a work item for a specific (product, target environment).
/// Keyed the same way as <see cref="WorkItemApproval"/> — <c>(WorkItemKey, Product, TargetEnv)</c> —
/// so a thread carries across superseded candidates: the conversation belongs to the ticket's
/// sign-off, not to whichever build happened to be live when it was written.
///
/// <para>Distinct from <see cref="PromotionComment"/> (scoped to one candidate) and from
/// <see cref="WorkItemApproval.Comment"/> (the note attached to a single decision). Human comments are
/// editable/deletable by their author (or an admin).</para>
///
/// <para>The thread also carries <i>decision entries</i> — rows with <see cref="Decision"/> set, written
/// automatically whenever someone approves / blocks / rejects the item, and by the system when a new
/// promotion version resets a decision. They read as part of the conversation but are immutable: the
/// edit and delete paths refuse them, because rewriting the record of a sign-off is not a comment edit.</para>
/// </summary>
public class WorkItemComment
{
    /// <summary>
    /// <see cref="AuthorEmail"/> for entries the platform writes itself — no human behind them, so
    /// they are immutable like decision entries.
    /// </summary>
    public const string SystemAuthor = "system";

    public Guid Id { get; set; }
    public string WorkItemKey { get; set; } = "";
    public string Product { get; set; } = "";
    public string TargetEnv { get; set; } = "";
    public string AuthorEmail { get; set; } = "";
    public string AuthorName { get; set; } = "";
    public string Body { get; set; } = "";

    /// <summary>
    /// Set when this entry records a sign-off rather than free-text discussion. Null for human comments.
    /// </summary>
    public PromotionDecision? Decision { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? UpdatedAt { get; set; }
}
