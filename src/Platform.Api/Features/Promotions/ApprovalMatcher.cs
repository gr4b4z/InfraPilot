using Platform.Api.Features.Promotions.Models;

namespace Platform.Api.Features.Promotions;

/// <summary>
/// Pure (no I/O) matcher that decides whether a set of approvals satisfies a flattened set of
/// <see cref="ApproverRequirement"/>s under the <b>per-requirement</b> distinct-person rule:
/// within one requirement each person counts at most once (an N-of-M gate needs N different
/// people), but the same person may satisfy several requirements — one recorded approval per
/// requirement. A QA manager who is also the release manager approves the "QA Review" gate and the
/// "Release Approval" gate separately, and both count.
///
/// <para>This replaces the original global distinct-person rule (plan §8, decision D9), under
/// which a person eligible for two gates could clear only one of them and the promotion stalled
/// whenever nobody else was eligible for the other.</para>
///
/// <para>Approvals arrive as <see cref="ApproverDecision"/>s. A <b>pinned</b> decision carries the
/// requirement the approver explicitly approved as, and counts toward exactly that requirement.
/// An <b>unpinned</b> decision (legacy rows recorded before attribution existed, or a caller that
/// doesn't carry attribution) is ambiguous — we don't know which gate the person meant — so it is
/// still placed conservatively: at most one requirement per unpinned approver, most-constrained
/// requirement first. Consider (plan §8.4): R1 needs 1 of {Alice}, R2 needs 1 of {Alice, Bob}.
/// If Alice and Bob both approved unpinned and we greedily hand Alice to R2, R1 starves. Matching
/// the requirement with the fewest eligible approvers first hands Alice to R1 and Bob to R2.</para>
///
/// <para>The greedy fill is a bounded fewest-options-first assignment, sufficient for the small
/// requirement trees this policy model allows; it is deterministic and not a general bipartite
/// max-matching.</para>
/// </summary>
public static class ApprovalMatcher
{
    /// <summary>
    /// Backward-compatible overload: all approvers are treated as UNPINNED (no explicit attribution),
    /// so the matcher runs the pure greedy fewest-options-first assignment where each person fills at
    /// most one requirement. Kept for callers (rollbacks) and tests that don't carry attribution.
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
    /// Decides which approval counts toward which requirement. The caller supplies a predicate
    /// <paramref name="isEligible"/> (typically wrapping group/user membership) so the matcher stays
    /// free of any identity/Graph dependency. Returns the per-requirement outcome plus an overall
    /// <see cref="MatchResult.AllSatisfied"/> flag.
    ///
    /// <para>Assignment runs in two passes:</para>
    /// <list type="number">
    ///   <item><b>Pinned pass:</b> each pinned decision is counted toward exactly the requirement it
    ///         names, up to that requirement's need. The same person is counted at most once per
    ///         requirement (duplicate rows collapse), but may appear pinned to several requirements
    ///         and counts toward each. Surplus pins beyond <c>need</c> are not counted and are
    ///         <b>not</b> reassigned elsewhere — the approver's stated intent is honoured.</item>
    ///   <item><b>Greedy fill pass:</b> the unpinned decisions fill the still-unsatisfied
    ///         requirements via the most-constrained-first greedy. Each unpinned approver fills at
    ///         most one requirement, and never one they are already counted on through a pin.</item>
    /// </list>
    /// </summary>
    /// <param name="requirements">Flattened requirement set (all requirements across all steps).</param>
    /// <param name="approvers">
    /// Approval decisions (email + optional pinned requirement index). The same email may appear
    /// more than once with different pins.
    /// </param>
    /// <param name="isEligible">True when the given approver can count toward the given requirement.</param>
    public static MatchResult Match(
        IReadOnlyList<ApproverRequirement> requirements,
        IReadOnlyCollection<ApproverDecision> approvers,
        Func<string, ApproverRequirement, bool> isEligible)
    {
        // Build eligible-approver lists per requirement (unpinned approvers only — pinned ones are
        // attributed explicitly in the pinned pass below).
        var unpinned = approvers
            .Where(a => a.PinnedRequirementIndex is null && !string.IsNullOrEmpty(a.Approver))
            .Select(a => a.Approver)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var slots = requirements
            .Select(req => new RequirementSlot(
                req,
                unpinned.Where(a => isEligible(a, req)).ToList()))
            .ToList();

        // (approver, requirement index) pairs that have been counted — the per-requirement
        // distinct-person rule: one person is worth at most one slot on any given requirement.
        var counted = new HashSet<(string Approver, int Index)>(PairComparer.Instance);
        // Unpinned approvers consumed by the greedy pass — each fills at most one requirement.
        var assigned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var matchedCounts = new int[slots.Count];

        // ── Pinned pass ──────────────────────────────────────────────────────────────────────
        // Honour each approver's explicit choice: count them on that requirement, once, up to the
        // requirement's need. A pinned approver is never reassigned — surplus beyond need is just
        // not counted (Matched stays capped).
        foreach (var decision in approvers)
        {
            if (decision.PinnedRequirementIndex is not { } idx) continue;
            if (idx < 0 || idx >= slots.Count) continue; // stale/unknown pin — ignore
            if (string.IsNullOrEmpty(decision.Approver)) continue;
            if (!counted.Add((decision.Approver, idx))) continue; // same person, same gate — dup row

            var need = Math.Max(1, slots[idx].Requirement.MinApprovers);
            if (matchedCounts[idx] < need) matchedCounts[idx]++;
            // else: surplus pinned approver — consumed (not reassigned), but not counted.
        }

        // ── Greedy fill pass ─────────────────────────────────────────────────────────────────
        // Fewest-options-first: repeatedly take the unsatisfied requirement with the fewest
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
                    .Where(a => !assigned.Contains(a) && !counted.Contains((a, i)))
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
            counted.Add((bestPick, best));
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

    /// <summary>Case-insensitive on the approver email, exact on the requirement index.</summary>
    private sealed class PairComparer : IEqualityComparer<(string Approver, int Index)>
    {
        public static readonly PairComparer Instance = new();

        public bool Equals((string Approver, int Index) x, (string Approver, int Index) y)
            => x.Index == y.Index && string.Equals(x.Approver, y.Approver, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string Approver, int Index) obj)
            => HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Approver), obj.Index);
    }
}

/// <summary>
/// One approval fed to <see cref="ApprovalMatcher.Match"/>. <see cref="PinnedRequirementIndex"/>
/// is the approver's explicit choice of which requirement they approved as — an index into the
/// flattened requirement list passed to the matcher. Null means unpinned (legacy / auto-attributed):
/// the greedy fill pass attributes it. The same approver may be passed once per requirement they
/// approved.
/// </summary>
public readonly record struct ApproverDecision(string Approver, int? PinnedRequirementIndex);

/// <summary>Outcome of <see cref="ApprovalMatcher.Match"/> for a single requirement.</summary>
public record RequirementOutcome(ApproverRequirement Requirement, int Matched, int Required, bool Satisfied);

/// <summary>Overall outcome of <see cref="ApprovalMatcher.Match"/>.</summary>
public record MatchResult(bool AllSatisfied, IReadOnlyList<RequirementOutcome> Requirements);
