namespace Platform.Api.Features.Promotions.Models;

/// <summary>
/// Free-text notes attached to a promotion candidate by any authenticated user. Distinct from
/// <see cref="PromotionApproval"/>: approvals carry a decision (Approved/Rejected) and are
/// append-only per approver; comments are plain discussion and editable/deletable by their author.
/// </summary>
public class PromotionComment
{
    /// <summary>
    /// <see cref="AuthorEmail"/> for the entries the platform writes itself — one per action taken
    /// on the promotion, so the thread doubles as its history. Used even when a person triggered the
    /// action (they are named in the body): system authorship is what makes the entry immutable, and
    /// a record of what happened is not something anyone should be able to rewrite.
    /// Mirrors <see cref="WorkItemComment.SystemAuthor"/>.
    /// </summary>
    public const string SystemAuthor = "system";

    public Guid Id { get; set; }
    public Guid CandidateId { get; set; }
    public string AuthorEmail { get; set; } = "";
    public string AuthorName { get; set; } = "";
    public string Body { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? UpdatedAt { get; set; }
}
