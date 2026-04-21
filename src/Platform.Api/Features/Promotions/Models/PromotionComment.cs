namespace Platform.Api.Features.Promotions.Models;

/// <summary>
/// Free-text notes attached to a promotion candidate by any authenticated user. Distinct from
/// <see cref="PromotionApproval"/>: approvals carry a decision (Approved/Rejected) and are
/// append-only per approver; comments are plain discussion and editable/deletable by their author.
/// </summary>
public class PromotionComment
{
    public Guid Id { get; set; }
    public Guid CandidateId { get; set; }
    public string AuthorEmail { get; set; } = "";
    public string AuthorName { get; set; } = "";
    public string Body { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? UpdatedAt { get; set; }
}
