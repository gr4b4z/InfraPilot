namespace Platform.Api.Features.Promotions.Models;

/// <summary>
/// A sign-off decision on one work item, for one <c>(product, targetEnv)</c>.
///
/// <para>Only <see cref="Approved"/> releases the promotion gate. <see cref="Issue"/> and
/// <see cref="Blocked"/> are both "not approved": they leave the item unresolved, which stalls the
/// gate without cancelling the promotion, and both are reversible — the same person can switch to
/// Approved later, and a new version of the promotion clears them and asks again. They are
/// mechanically identical; the difference is what the reviewer is saying. An issue is "something's
/// wrong here"; a block is "this is not going out".</para>
///
/// <para>Deliberately separate from <see cref="PromotionDecision"/>, which governs the promotion
/// itself and whose <c>Rejected</c> is a genuine veto that terminates the candidate. Sharing one
/// enum put a work-item verb and a promotion verb behind the same name.</para>
///
/// <para><b>Reading historical data:</b> these decisions were once named on a shift of one — what is
/// now <see cref="Issue"/> was stored as <c>Blocked</c>, and what is now <see cref="Blocked"/> was
/// stored as <c>Rejected</c>. The <c>RenameWorkItemDecisions</c> migration rewrote the stored values,
/// so the database is consistent; audit rows and webhook deliveries emitted before it are not, and
/// carry the old event names (<c>work-item.blocked</c> for today's issue,
/// <c>work-item.rejected</c> for today's block).</para>
/// </summary>
public enum WorkItemDecision
{
    Approved,

    /// <summary>
    /// Something is wrong with the item, flagged without declaring it undeliverable. Counted
    /// separately by the gate so a shortfall reads as "3 of 5 approved, 1 issue" rather than an
    /// unexplained gap.
    /// </summary>
    Issue,

    /// <summary>
    /// The item is held back. Stronger than <see cref="Issue"/> in what it says, identical in what
    /// it does: the promotion stays Pending, nothing cascades, and the decision can be changed.
    /// </summary>
    Blocked,
}
