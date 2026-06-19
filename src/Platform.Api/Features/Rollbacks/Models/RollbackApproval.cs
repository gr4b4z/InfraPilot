using Platform.Api.Features.Promotions.Models;

namespace Platform.Api.Features.Rollbacks.Models;

/// <summary>
/// One approver's decision on a <see cref="RollbackRequest"/>. Mirrors
/// <see cref="PromotionApproval"/> (and reuses <see cref="PromotionDecision"/>) — rollbacks
/// deliberately follow the same approval rules as promotions. The DB-level UNIQUE on
/// (RequestId, ApproverEmail) guards against double-approval races.
/// </summary>
public class RollbackApproval
{
    public Guid Id { get; set; }
    public Guid RequestId { get; set; }
    public string ApproverEmail { get; set; } = "";
    public string ApproverName { get; set; } = "";
    public string? Comment { get; set; }
    public PromotionDecision Decision { get; set; } = PromotionDecision.Approved;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
