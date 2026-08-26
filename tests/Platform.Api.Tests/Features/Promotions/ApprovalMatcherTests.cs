using Platform.Api.Features.Promotions;
using Platform.Api.Features.Promotions.Models;

namespace Platform.Api.Tests.Features.Promotions;

/// <summary>
/// Unit tests for <see cref="ApprovalMatcher"/> — the pure distinct-person assignment that decides
/// whether a set of approvers satisfies a flattened requirement set (plan §8.4, decision D9).
/// </summary>
public class ApprovalMatcherTests
{
    // Eligibility = the approver is listed in the requirement's user list (case-insensitive).
    private static bool ByUser(string email, ApproverRequirement req) =>
        req.Users.Any(u => string.Equals(u, email, StringComparison.OrdinalIgnoreCase));

    private static ApproverRequirement Req(string name, int min, params string[] users) =>
        new(name, new(), users.ToList(), min);

    [Fact]
    public void Empty_Requirements_AllSatisfied()
    {
        var result = ApprovalMatcher.Match(new List<ApproverRequirement>(), new[] { "a@x" }, ByUser);
        Assert.True(result.AllSatisfied);
    }

    [Fact]
    public void Single_Requirement_Met_By_One_Approver()
    {
        var reqs = new[] { Req("r1", 1, "alice@x") };
        var result = ApprovalMatcher.Match(reqs, new[] { "alice@x" }, ByUser);
        Assert.True(result.AllSatisfied);
    }

    [Fact]
    public void Single_Requirement_Unmet_When_No_Eligible_Approver()
    {
        var reqs = new[] { Req("r1", 1, "alice@x") };
        var result = ApprovalMatcher.Match(reqs, new[] { "bob@x" }, ByUser);
        Assert.False(result.AllSatisfied);
        Assert.Equal(0, result.Requirements.Single().Matched);
    }

    [Fact]
    public void NOfM_Requires_Distinct_People()
    {
        var reqs = new[] { Req("r1", 2, "alice@x", "bob@x") };

        // Only one distinct approver → not satisfied (no double-counting a single person).
        Assert.False(ApprovalMatcher.Match(reqs, new[] { "alice@x" }, ByUser).AllSatisfied);

        // Two distinct approvers → satisfied.
        Assert.True(ApprovalMatcher.Match(reqs, new[] { "alice@x", "bob@x" }, ByUser).AllSatisfied);
    }

    /// <summary>
    /// The §8.4 worked example. R1 = {Alice} (1), R2 = {Alice, Bob} (1). Alice and Bob have both
    /// approved. A naive pass that gives Alice to R2 starves R1; matching the most-constrained
    /// requirement (R1, fewest eligible) first hands Alice to R1 and Bob to R2 → both satisfied.
    /// </summary>
    [Fact]
    public void AliceBob_MostConstrainedFirst_BothSatisfied()
    {
        var reqs = new[]
        {
            Req("R1", 1, "alice@x"),
            Req("R2", 1, "alice@x", "bob@x"),
        };

        var result = ApprovalMatcher.Match(reqs, new[] { "alice@x", "bob@x" }, ByUser);

        Assert.True(result.AllSatisfied);
        Assert.All(result.Requirements, o => Assert.True(o.Satisfied));
    }

    [Fact]
    public void AliceBob_OnlyAlice_R1WinsButR2Starves()
    {
        var reqs = new[]
        {
            Req("R1", 1, "alice@x"),
            Req("R2", 1, "alice@x", "bob@x"),
        };

        // Only Alice approved. She can only satisfy one requirement; the other is unmet.
        var result = ApprovalMatcher.Match(reqs, new[] { "alice@x" }, ByUser);

        Assert.False(result.AllSatisfied);
        Assert.Equal(1, result.Requirements.Count(o => o.Satisfied));
    }

    [Fact]
    public void Person_Counts_Toward_At_Most_One_Requirement()
    {
        // Two requirements both satisfiable only by Alice → one person can't cover both.
        var reqs = new[]
        {
            Req("R1", 1, "alice@x"),
            Req("R2", 1, "alice@x"),
        };

        var result = ApprovalMatcher.Match(reqs, new[] { "alice@x" }, ByUser);
        Assert.False(result.AllSatisfied);
    }

    [Fact]
    public void Determinism_SameInputSameOutcome()
    {
        var reqs = new[]
        {
            Req("R1", 1, "alice@x", "bob@x"),
            Req("R2", 1, "alice@x", "bob@x"),
        };
        var approvers = new[] { "bob@x", "alice@x" };

        var a = ApprovalMatcher.Match(reqs, approvers, ByUser);
        var b = ApprovalMatcher.Match(reqs, approvers, ByUser);
        Assert.Equal(a.AllSatisfied, b.AllSatisfied);
        Assert.True(a.AllSatisfied);
    }

    // ── Pinned attribution ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Alice is eligible for both R1 (index 0) and R2 (index 1) but pins herself to R1. She must
    /// count for R1 even though the greedy matcher would otherwise have been free to place her at R2.
    /// With Bob unpinned filling R2, both are satisfied — and specifically R1's slot is Alice's.
    /// </summary>
    [Fact]
    public void Pinned_To_R1_Counts_For_R1_Even_When_Eligible_For_R2()
    {
        var reqs = new[]
        {
            Req("R1", 1, "alice@x", "bob@x"),
            Req("R2", 1, "alice@x", "bob@x"),
        };

        var decisions = new[]
        {
            new ApproverDecision("alice@x", 0), // pinned to R1
            new ApproverDecision("bob@x", null), // unpinned
        };

        var result = ApprovalMatcher.Match(reqs, decisions, ByUser);

        Assert.True(result.AllSatisfied);
        Assert.Equal(1, result.Requirements[0].Matched); // R1 satisfied by Alice's pin
        Assert.Equal(1, result.Requirements[1].Matched); // R2 satisfied by Bob (greedy)
    }

    /// <summary>
    /// A pin to an already-satisfied requirement is surplus: the pinned approver is consumed (not
    /// reassigned) and does NOT rescue a different, starving requirement. Here Bob pins to R1 (which
    /// Alice's pin already satisfies); R2 — which only Bob could have satisfied — stays unmet.
    /// </summary>
    [Fact]
    public void Pinned_To_Satisfied_Requirement_Is_Surplus_Does_Not_Rescue_Other()
    {
        var reqs = new[]
        {
            Req("R1", 1, "alice@x", "bob@x"),
            Req("R2", 1, "bob@x"),
        };

        var decisions = new[]
        {
            new ApproverDecision("alice@x", 0), // pins R1
            new ApproverDecision("bob@x", 0),   // also pins R1 (surplus) — NOT reassigned to R2
        };

        var result = ApprovalMatcher.Match(reqs, decisions, ByUser);

        Assert.False(result.AllSatisfied);
        Assert.Equal(1, result.Requirements[0].Matched); // R1 capped at need=1 (Alice); Bob surplus
        Assert.Equal(0, result.Requirements[1].Matched); // R2 starves — Bob honoured his R1 choice
    }

    /// <summary>Unpinned approvers still run the original greedy fill (the Alice/Bob case is unchanged).</summary>
    [Fact]
    public void Unpinned_Approvers_Still_Greedy_Fill()
    {
        var reqs = new[]
        {
            Req("R1", 1, "alice@x"),
            Req("R2", 1, "alice@x", "bob@x"),
        };

        var decisions = new[]
        {
            new ApproverDecision("alice@x", null),
            new ApproverDecision("bob@x", null),
        };

        var result = ApprovalMatcher.Match(reqs, decisions, ByUser);

        Assert.True(result.AllSatisfied);
        Assert.All(result.Requirements, o => Assert.True(o.Satisfied));
    }
}
