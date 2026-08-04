using System.Text.Json;
using Platform.Api.Features.Deployments.Models;
using Platform.Api.Features.Promotions.Models;
using Platform.Api.Infrastructure;

namespace Platform.Api.Features.Promotions;

/// <summary>
/// The "who is answerable for this work item?" rule: a promotion policy may declare participant roles
/// (<see cref="PromotionPolicy.RequiredWorkItemRoles"/>, e.g. <c>qa-owner</c>) that every work item on
/// a candidate gated by it must have somebody in. A work item with an unfilled required role is
/// <b>incomplete</b> — surfaced on the promotions list, the promotion view, the work-items queue and
/// the work-item page, each of which asks for someone to be put on the role.
///
/// <para>Always <b>derived</b>, never stored: the answer is a function of the candidate's current
/// policy snapshot and its current participants, so it is automatically right when a work item is
/// attached to a promotion later, when somebody is assigned or removed, and when the policy is edited
/// (pending candidates get a fresh snapshot — see
/// <see cref="PromotionService.RefreshPolicySnapshotsAsync"/>). A persisted "incomplete" flag would
/// have needed a backfill for each of those three paths.</para>
///
/// <para>This is deliberately not part of the approval gate. It records data completeness, not
/// authority; the blocking work-item condition remains
/// <see cref="ResolvedPolicySnapshot.RequireAllWorkItemsApproved"/>.</para>
/// </summary>
public static class WorkItemRoleRequirements
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// The canonical roles a candidate's work items must each have somebody in, read off the
    /// candidate's own policy snapshot. Best-effort: a missing or unparseable snapshot yields an empty
    /// list, so a data problem shows up as "no requirement" rather than flagging every work item.
    /// </summary>
    public static IReadOnlyList<string> RequiredRoles(PromotionCandidate candidate)
    {
        var snapshot = TryReadSnapshot(candidate);
        return snapshot is null ? Array.Empty<string>() : RequiredRoles(snapshot);
    }

    /// <summary>
    /// Whether this candidate's edge creates work items at all
    /// (<see cref="ResolvedPolicySnapshot.TracksWorkItems"/>). Defaults to <c>true</c> for a candidate
    /// with no readable snapshot, matching the pre-flag behaviour.
    /// </summary>
    public static bool TracksWorkItems(PromotionCandidate candidate)
        => TryReadSnapshot(candidate)?.TracksWorkItems ?? true;

    /// <summary>
    /// Best-effort snapshot read. Returns <c>null</c> when the candidate has no snapshot or its JSON
    /// won't parse — this type only ever answers presentational questions, so a data problem should
    /// degrade to "no requirement" rather than fail a list request.
    /// </summary>
    private static ResolvedPolicySnapshot? TryReadSnapshot(PromotionCandidate candidate)
    {
        if (string.IsNullOrEmpty(candidate.ResolvedPolicyJson)) return null;
        try
        {
            return JsonSerializer.Deserialize<ResolvedPolicySnapshot>(
                candidate.ResolvedPolicyJson, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Canonicalises and dedupes a snapshot's required roles. The admin endpoint already stores them
    /// canonical; re-normalising here covers snapshots written by hand, by a seed, or by an older
    /// build.
    /// </summary>
    public static IReadOnlyList<string> RequiredRoles(ResolvedPolicySnapshot snapshot)
    {
        // An edge that creates no work items has nothing to require people on. Short-circuiting here
        // rather than at each caller is what keeps every surface consistent: the promotions list, the
        // promotion page, the queue rows and the work-item page all read their roles through this.
        if (!snapshot.TracksWorkItems) return Array.Empty<string>();
        if (snapshot.RequiredWorkItemRoles is not { Count: > 0 } roles) return Array.Empty<string>();

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>(roles.Count);
        foreach (var role in roles)
        {
            var canonical = RoleNormalizer.Normalize(role);
            if (canonical.Length == 0 || !seen.Add(canonical)) continue;
            result.Add(canonical);
        }
        return result;
    }

    /// <summary>
    /// The effective participants of one work item on a candidate. The candidate is self-contained, so
    /// people come from its own data (no deploy-event join, no operator overrides):
    /// <list type="bullet">
    ///   <item>Reference-level participants nested in the matching work-item entry of
    ///         <see cref="PromotionCandidate.References"/>.</item>
    ///   <item>Promotion-level participants (<see cref="PromotionCandidate.Participants"/>) — for
    ///         any role not already resolved by the reference-level layer.</item>
    /// </list>
    /// Each canonical role appears at most once.
    /// </summary>
    public static IReadOnlyList<ParticipantDto> ResolveParticipants(
        PromotionCandidate candidate, string workItemKey)
        => ResolveParticipants(candidate.References, candidate.Participants, workItemKey);

    /// <summary>
    /// <see cref="ResolveParticipants(PromotionCandidate, string)"/> over already-deserialised lists.
    /// <see cref="PromotionCandidate.References"/> and <see cref="PromotionCandidate.Participants"/> parse
    /// their JSON column on every read, so callers walking several work items of one candidate (the
    /// promotions list, <see cref="Evaluate"/>) hoist the two reads out of the loop.
    /// </summary>
    public static IReadOnlyList<ParticipantDto> ResolveParticipants(
        IReadOnlyList<ReferenceDto> references,
        IReadOnlyList<PromotionParticipant> promotionParticipants,
        string workItemKey)
    {
        var merged = new List<ParticipantDto>();
        var seenCanonical = new HashSet<string>(StringComparer.Ordinal);

        // ── Layer 1: reference-level participants on the matching work-item reference ──
        var matchedRef = references.FirstOrDefault(r =>
            string.Equals(r.Key, workItemKey, StringComparison.OrdinalIgnoreCase)
            && string.Equals(r.Type, "work-item", StringComparison.OrdinalIgnoreCase));
        if (matchedRef?.Participants is { Count: > 0 } refParticipants)
        {
            foreach (var p in refParticipants)
            {
                var canonical = RoleNormalizer.Normalize(p.Role);
                if (canonical.Length == 0 || !seenCanonical.Add(canonical)) continue;
                merged.Add(p);
            }
        }

        // ── Layer 2: promotion-level participants for any role not yet covered ──
        foreach (var p in promotionParticipants)
        {
            var canonical = RoleNormalizer.Normalize(p.Role);
            if (canonical.Length == 0 || !seenCanonical.Add(canonical)) continue;
            merged.Add(new ParticipantDto(p.Role, p.DisplayName, p.Email));
        }

        return merged;
    }

    /// <summary>
    /// Which of <paramref name="requiredRoles"/> nobody holds on this work item, in the order the
    /// policy lists them. A role counts as filled only when a participant in it carries a non-empty
    /// email — a name with nobody reachable behind it isn't an owner, and it's the same bar the
    /// assignee filter applies.
    /// </summary>
    public static List<string> MissingRoles(
        IReadOnlyList<ParticipantDto> participants, IReadOnlyList<string> requiredRoles)
    {
        if (requiredRoles.Count == 0) return new();

        var filled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in participants)
        {
            if (string.IsNullOrWhiteSpace(p.Email)) continue;
            var canonical = RoleNormalizer.Normalize(p.Role);
            if (canonical.Length > 0) filled.Add(canonical);
        }

        return requiredRoles.Where(r => !filled.Contains(r)).ToList();
    }

    /// <summary>Convenience overload for one work item of a candidate.</summary>
    public static List<string> MissingRoles(
        PromotionCandidate candidate, string workItemKey, IReadOnlyList<string> requiredRoles)
        => MissingRoles(ResolveParticipants(candidate, workItemKey), requiredRoles);

    /// <summary>
    /// Every work item on the candidate that is missing at least one required role, deduped on key in
    /// reference order. Empty when the policy declares no required roles — which is the common case, so
    /// callers can render the whole "needs attention" affordance off <c>Count == 0</c>.
    /// </summary>
    public static List<WorkItemRoleGap> Evaluate(PromotionCandidate candidate)
    {
        var required = RequiredRoles(candidate);
        if (required.Count == 0) return new();

        // Both JSON columns are read once here rather than per work item — this runs for every row of
        // the promotions list.
        var references = candidate.References;
        var promotionParticipants = candidate.Participants;

        var gaps = new List<WorkItemRoleGap>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var reference in references)
        {
            if (!string.Equals(reference.Type, "work-item", StringComparison.OrdinalIgnoreCase)) continue;
            var key = (reference.Key ?? "").Trim();
            if (key.Length == 0 || !seen.Add(key)) continue;

            var participants = ResolveParticipants(references, promotionParticipants, key);
            var missing = MissingRoles(participants, required);
            if (missing.Count == 0) continue;
            gaps.Add(new WorkItemRoleGap(key, reference.Title, missing));
        }
        return gaps;
    }
}

/// <summary>
/// One work item on a candidate that is missing people: <see cref="MissingRoles"/> are the canonical
/// policy-required roles nobody holds on it.
/// </summary>
public record WorkItemRoleGap(string WorkItemKey, string? Title, IReadOnlyList<string> MissingRoles);
