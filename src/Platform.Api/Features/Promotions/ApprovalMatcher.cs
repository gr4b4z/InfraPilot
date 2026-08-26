using Platform.Api.Features.Promotions.Models;

namespace Platform.Api.Features.Promotions;

/// <summary>
/// Pure (no I/O) matcher that decides whether a set of approvers satisfies a flattened set of
/// <see cref="ApproverRequirement"/>s under the global distinct-person rule (plan §8, decision D9):
/// each approver counts toward <b>at most one</b> requirement.
///
/// <para>Because a single person can be eligible for several requirements, a naive count-per-
/// requirement can wrongly report "not satisfied". Consider (plan §8.4): requirement R1 needs 1 of
/// {Alice}, requirement R2 needs 1 of {Alice, Bob}. If only Alice and Bob have approved and we greedily
/// hand Alice to R2, R1 starves. Matching the <b>most-constrained</b> requirement first (fewest
/// eligible approvers) hands Alice to R1 and Bob to R2 — both satisfied.</para>
///
/// <para>The matcher is a bounded greedy assignment (fewest-options-first) which is sufficient for
/// the small, bounded requirement trees this policy model allows; it is not a general bipartite
/// max-matching but produces the correct result for these shapes and is deterministic.</para>
/// </summary>
public static class ApprovalMatcher
{
    /// <summary>
    /// Backward-compatible overload: all approvers are treated as UNPINNED (no explicit attribution),
    /// so the matcher runs the pure greedy fewest-options-first assignment. Kept for callers/tests
    /// that don't carry pinned-requirement attribution.
    /// </summary>
    public static MatchResult Match(
        IReadOnlyList<ApproverRequirement> requirements,
        IReadOnlyCollection<string> approvers,
        Func<string, ApproverRequirement, bool> isEligible)
        => Match(
            requirements,
            approvers.Select(a => new ApproverDecision(a, null)).ToList(),
            isEligible);

    /// <summary>
    /// Decides which approver is eligible for which requirement. The caller supplies a predicate
    /// <paramref name="isEligible"/> (typically wrapping group/user membership) so the matcher stays
    /// free of any identity/Graph dependency. Returns the per-requirement outcome plus an overall
    /// <see cref="MatchResult.AllSatisfied"/> flag.
    ///
    /// <para>Each approver is supplied as an <see cref="ApproverDecision"/> that may carry a
    /// <see cref="ApproverDecision.PinnedRequirementIndex"/> (an index into <paramref name="requirements"/>).
    /// Assignment runs in two passes:</para>
    /// <list type="number">
    ///   <item><b>Pinned pass:</b> each pinned approver is assigned to exactly the requirement they
    ///         chose (honouring their intent), consuming their single distinct-person slot, counted
    ///         up to the requirement's need. Surplus pinned approvers beyond <c>need</c> are simply
    ///         not counted and are <b>not</b> reassigned elsewhere.</item>
    ///   <item><b>Greedy fill pass:</b> the remaining unpinned approvers fill the still-unsatisfied
    ///         requirements via the most-constrained-first greedy (the Alice/Bob case).</item>
    /// </list>
    /// </summary>
    /// <param name="requirements">Flattened requirement set (all requirements across all steps).</param>
    /// <param name="approvers">Distinct approver decisions (email + optional pinned requirement index).</param>
    /// <param name="isEligible">True when the given approver can count toward the given requirement.</param>
    public static MatchResult Match(
        IReadOnlyList<ApproverRequirement> requirements,
        IReadOnlyCollection<ApproverDecision> approvers,
        Func<string, ApproverRequirement, bool> isEligible)
    {
        // Build eligible-approver lists per requirement (unpinned approvers only — pinned ones are
        // attributed explicitly in the pinned pass below).
        var unpinned = approvers.Where(a => a.PinnedRequirementIndex is null).Select(a => a.Approver).ToList();
        var slots = requirements
            .Select(req => new RequirementSlot(
                req,
                unpinned.Where(a => isEligible(a, req)).ToList()))
            .ToList();

        var assigned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var matchedCounts = new int[slots.Count];

        // ── Pinned pass ──────────────────────────────────────────────────────────────────────
        // Honour each approver's explicit choice: assign them to that requirement, consuming their
        // distinct-person slot, counted only up to the requirement's need. A pinned approver is
        // never reassigned — surplus beyond need is just not counted (Matched stays capped).
        foreach (var decision in approvers)
        {
            if (decision.PinnedRequirementIndex is not { } idx) continue;
            if (idx < 0 || idx >= slots.Count) continue; // stale/unknown pin — ignore
            if (string.IsNullOrEmpty(decision.Approver)) continue;
            if (!assigned.Add(decision.Approver)) continue; // already consumed (dup row)

            var need = Math.Max(1, slots[idx].Requirement.MinApprovers);
            if (matchedCounts[idx] < need) matchedCounts[idx]++;
            // else: surplus pinned approver — consumed (not reassigned), but not counted.
        }

        // Greedy fewest-options-first: repeatedly take the unsatisfied requirement with the fewest
        // *still-available* eligible approvers and give it one. This avoids starving a constrained
        // requirement by spending a shared approver on a looser one (the Alice/Bob case).
        while (true)
        {
            int best = -1;
            int bestAvail = int.MaxValue;
            string? bestPick = null;

            for (var i = 0; i < slots.Count; i++)
            {
                var need = Math.Max(1, slots[i].Requirement.MinApprovers);
                if (matchedCounts[i] >= need) continue; // already satisfied

                var available = slots[i].Eligible
                    .Where(a => !assigned.Contains(a))
                    .ToList();
                if (available.Count == 0) continue; // can't progress this one right now

                if (available.Count < bestAvail)
                {
                    bestAvail = available.Count;
                    best = i;
                    // Deterministic pick: lexicographically smallest available approver.
                    bestPick = available.OrderBy(a => a, StringComparer.OrdinalIgnoreCase).First();
                }
            }

            if (best < 0 || bestPick is null) break; // no further progress possible
            assigned.Add(bestPick);
            matchedCounts[best]++;
        }

        var outcomes = new List<RequirementOutcome>(slots.Count);
        var all = true;
        for (var i = 0; i < slots.Count; i++)
        {
            var need = Math.Max(1, slots[i].Requirement.MinApprovers);
            var satisfied = matchedCounts[i] >= need;
            if (!satisfied) all = false;
            outcomes.Add(new RequirementOutcome(slots[i].Requirement, matchedCounts[i], need, satisfied));
        }

        return new MatchResult(all, outcomes);
    }

    private readonly record struct RequirementSlot(ApproverRequirement Requirement, List<string> Eligible);
}

/// <summary>
/// One approver's decision fed to <see cref="ApprovalMatcher.Match"/>. <see cref="PinnedRequirementIndex"/>
/// is the approver's explicit choice of which requirement they approve as — an index into the
/// flattened requirement list passed to the matcher. Null means unpinned (legacy / auto-attributed):
/// the greedy fill pass attributes it.
/// </summary>
public readonly record struct ApproverDecision(string Approver, int? PinnedRequirementIndex);

/// <summary>Outcome of <see cref="ApprovalMatcher.Match"/> for a single requirement.</summary>
public record RequirementOutcome(ApproverRequirement Requirement, int Matched, int Required, bool Satisfied);

/// <summary>Overall outcome of <see cref="ApprovalMatcher.Match"/>.</summary>
public record MatchResult(bool AllSatisfied, IReadOnlyList<RequirementOutcome> Requirements);
