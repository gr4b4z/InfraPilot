namespace Platform.Api.Features.Promotions.Models;

/// <summary>
/// A sign-off decision on one work item, for one <c>(product, service, targetEnv)</c>.
///
/// <para><see cref="Blocked"/> is the only decision that holds the promotion gate. An issue is
/// "something's wrong here" about a change that is still going out, so <see cref="Approved"/> and
/// <see cref="Issue"/> both clear the item; a block is "this is not going out", and it stalls the
/// gate without cancelling the promotion. Every decision is reversible — the same person can switch
/// to another later, and a new version of the promotion clears the held ones and asks again.</para>
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
    /// Something is wrong with the item, flagged without declaring it undeliverable — so it does not
    /// hold the promotion. Counted separately by the gate so a cleared bundle reads as "4 of 5
    /// approved, 1 with an issue" rather than hiding the flag behind a green tick.
    /// </summary>
    Issue,

    /// <summary>
    /// The item is held back — the one verdict that stops the promotion. It takes precedence over a
    /// sibling approval or issue on the same item, and outranks them on the ticket's roll-up across
    /// services. Nothing cascades: the promotion stays Pending rather than being rejected, and the
    /// decision can be changed.
    /// </summary>
    Blocked,
}
