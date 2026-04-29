using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Platform.Api.Features.Deployments.Models;
using Platform.Api.Features.Promotions.Models;
using Platform.Api.Infrastructure;
using Platform.Api.Infrastructure.Auth;
using Platform.Api.Infrastructure.Identity;
using Platform.Api.Infrastructure.Persistence;

namespace Platform.Api.Features.Promotions;

/// <summary>
/// Capability checks shared between promotion-level and ticket-level approval flows.
/// Pulled out of <see cref="PromotionService"/> so <see cref="WorkItemApprovalService"/>
/// can reuse the same Graph-fallback logic without duplicating it. Stateless and scoped.
///
/// <para>Two responsibilities:</para>
/// <list type="bullet">
///   <item>Approver-group membership (role claim, group claim, live Graph) for the current user.</item>
///   <item>Excluded-role check against the source deploy event's participants for any email,
///         honouring operator-supplied <see cref="ReferenceParticipantOverride"/> rows
///         (override > reference-level > event-level; tombstones suppress fall-through).</item>
/// </list>
/// </summary>
public class PromotionApprovalAuthorizer
{
    private readonly PlatformDbContext _db;
    private readonly ICurrentUser _currentUser;
    private readonly IIdentityService _identity;
    private readonly ILogger<PromotionApprovalAuthorizer> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public PromotionApprovalAuthorizer(
        PlatformDbContext db,
        ICurrentUser currentUser,
        IIdentityService identity,
        ILogger<PromotionApprovalAuthorizer> logger)
    {
        _db = db;
        _currentUser = currentUser;
        _identity = identity;
        _logger = logger;
    }

    /// <summary>
    /// Checks whether the current user is in <paramref name="approverGroup"/>. Matches against
    /// (a) their role claims (for policies using a role string like "InfraPortal.Approver"),
    /// (b) their group claims (for policies using an Entra group object ID), and
    /// (c) live Graph membership (fallback, via <see cref="IIdentityService"/>).
    /// </summary>
    public async Task<bool> IsInApproverGroupAsync(string approverGroup, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(approverGroup)) return false;

        // Admin always qualifies — avoids bootstrapping hell when groups aren't wired up yet.
        if (_currentUser.IsAdmin) return true;

        // QA role qualifies for all promotions — lightweight alternative to AD groups for small teams.
        if (_currentUser.IsQA) return true;

        if (_currentUser.Roles.Contains(approverGroup, StringComparer.OrdinalIgnoreCase)) return true;
        if (_currentUser.IsInGroup(approverGroup)) return true;

        // Fall back to Graph. A stub/local identity service returns an empty list, which is fine.
        try
        {
            var members = await _identity.GetGroupMembers(approverGroup, ct);
            return members.Any(m =>
                string.Equals(m.Email, _currentUser.Email, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(m.Id, _currentUser.Id, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Group membership lookup failed for {Group}", approverGroup);
            return false;
        }
    }

    /// <summary>
    /// True when the policy specifies an excluded role and <paramref name="email"/> appears
    /// (after overrides are applied) on the candidate's source event(s) with that role.
    /// Returns false when no exclusion is configured, no source event id is supplied, the
    /// email is blank, or the email doesn't match the excluded role on any event in the
    /// supersede bundle.
    ///
    /// <para>The bundle of events scanned matches the existing pattern: the candidate's
    /// own <c>SourceDeployEventId</c> ∪ its <c>SupersededSourceEventIds</c>. Overrides for
    /// every event in the bundle are loaded in one query and passed to the resolver, so
    /// operator overrides on inherited (superseded) events are honoured too.</para>
    ///
    /// <para>Decoupled from <see cref="PromotionCandidate"/> so ticket-level flows that pick
    /// a candidate dynamically can pass <c>candidate.SourceDeployEventId</c> and the bundle
    /// directly.</para>
    /// </summary>
    public async Task<bool> IsEmailExcludedByRoleAsync(
        ResolvedPolicySnapshot snapshot,
        Guid? sourceDeployEventId,
        IReadOnlyCollection<Guid>? supersededEventIds,
        string email,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(snapshot.ExcludeRole)) return false;
        if (string.IsNullOrEmpty(email)) return false;
        if (sourceDeployEventId is null) return false;

        var bundle = (supersededEventIds ?? Array.Empty<Guid>())
            .Concat(new[] { sourceDeployEventId.Value })
            .Distinct()
            .ToList();

        var rows = await _db.DeployEvents.AsNoTracking()
            .Where(e => bundle.Contains(e.Id))
            .Select(e => new { e.Id, e.ParticipantsJson, e.ReferencesJson })
            .ToListAsync(ct);
        if (rows.Count == 0) return false;

        var overrides = await _db.ReferenceParticipantOverrides.AsNoTracking()
            .Where(o => bundle.Contains(o.DeployEventId))
            .ToListAsync(ct);
        var overridesByEvent = overrides
            .GroupBy(o => o.DeployEventId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<ReferenceParticipantOverride>)g.ToList());

        foreach (var row in rows)
        {
            var evOverrides = overridesByEvent.GetValueOrDefault(row.Id, Array.Empty<ReferenceParticipantOverride>());
            if (EmailMatchesExcludedRole(row.ParticipantsJson, row.ReferencesJson, evOverrides, snapshot.ExcludeRole, email))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Backwards-compatible 4-arg overload — used by tests that don't yet pass the bundle.
    /// Equivalent to passing an empty <c>supersededEventIds</c>: scans only the single event.
    /// </summary>
    public Task<bool> IsEmailExcludedByRoleAsync(
        ResolvedPolicySnapshot snapshot,
        Guid? sourceDeployEventId,
        string email,
        CancellationToken ct)
        => IsEmailExcludedByRoleAsync(snapshot, sourceDeployEventId, supersededEventIds: null, email, ct);

    /// <summary>
    /// Pure helper: checks a single event's JSON blobs (event-level + reference-level
    /// participants) plus its overrides for an entry matching the role and email. Walks each
    /// reference through <see cref="ParticipantResolver.FindByRoleWithOverrides"/> so tombstones
    /// suppress reference/event-level fallback per-reference. Then checks event-level on its own.
    ///
    /// <para>Used both here and from
    /// <see cref="PromotionService.CanUserApproveManyAsync"/> /
    /// <see cref="WorkItemApprovalService.GetPendingForCurrentUserAsync"/> where the JSONs +
    /// overrides have already been batch-loaded.</para>
    /// </summary>
    public static bool EmailMatchesExcludedRole(
        string? participantsJson,
        string? referencesJson,
        IReadOnlyList<ReferenceParticipantOverride>? overrides,
        string excludedRole,
        string email)
    {
        if (string.IsNullOrEmpty(email)) return false;
        var canonical = RoleNormalizer.Normalize(excludedRole);
        if (canonical.Length == 0) return false;

        // 1. Walk each reference: per-reference resolver call so a tombstone correctly
        //    suppresses the reference's own (and the event-level) match. The override layer
        //    can ALSO inject a new email under the excluded role — if that's the candidate's
        //    email, it must trip the exclusion.
        var refs = ParseReferences(referencesJson);
        var seenRefKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in refs)
        {
            if (string.IsNullOrEmpty(r.Key)) continue;
            seenRefKeys.Add(r.Key);
            var lookup = ParticipantResolver.FindByRoleWithOverrides(
                participantsJson, referencesJson, overrides, excludedRole, r.Key);
            // Tombstone on this (refKey, role) → no exclusion *via that reference*; keep going.
            if (lookup.Suppressed) continue;
            if (lookup.Found is { Email: var e } && !string.IsNullOrEmpty(e)
                && string.Equals(e, email, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        // 2. Override rows can introduce a reference key that didn't exist in ReferencesJson —
        //    walk them too so a freshly-added override can trip the exclusion.
        if (overrides is { Count: > 0 })
        {
            foreach (var o in overrides)
            {
                if (string.IsNullOrEmpty(o.ReferenceKey)) continue;
                if (seenRefKeys.Contains(o.ReferenceKey)) continue;
                var lookup = ParticipantResolver.FindByRoleWithOverrides(
                    participantsJson, referencesJson, overrides, excludedRole, o.ReferenceKey);
                if (lookup.Suppressed) continue;
                if (lookup.Found is { Email: var e } && !string.IsNullOrEmpty(e)
                    && string.Equals(e, email, StringComparison.OrdinalIgnoreCase))
                    return true;
                seenRefKeys.Add(o.ReferenceKey);
            }
        }

        // 3. Event-level fallback (unaffected by reference-scoped overrides — overrides are
        //    always tied to a referenceKey). Mirrors the legacy behaviour for triggered-by etc.
        foreach (var p in ParticipantResolver.GetEventParticipants(participantsJson))
        {
            if (RoleNormalizer.Normalize(p.Role) == canonical
                && !string.IsNullOrEmpty(p.Email)
                && string.Equals(p.Email, email, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Backwards-compatible overload — kept so callers that have only event-level / reference-level
    /// JSON to hand (legacy paths, tests pre-dating overrides) keep compiling. Equivalent to
    /// passing an empty overrides list.
    /// </summary>
    public static bool EmailMatchesExcludedRole(
        string? participantsJson,
        string? referencesJson,
        string excludedRole,
        string email)
        => EmailMatchesExcludedRole(participantsJson, referencesJson, overrides: null, excludedRole, email);

    /// <summary>
    /// Backwards-compatible 3-arg overload — only event-level JSON. Equivalent to passing
    /// <c>null</c> for both <c>referencesJson</c> and <c>overrides</c>. Used by older tests.
    /// </summary>
    public static bool EmailMatchesExcludedRole(string? participantsJson, string excludedRole, string email)
        => EmailMatchesExcludedRole(participantsJson, referencesJson: null, overrides: null, excludedRole, email);

    private static List<ReferenceDto> ParseReferences(string? referencesJson)
    {
        if (string.IsNullOrWhiteSpace(referencesJson)) return new();
        try
        {
            return JsonSerializer.Deserialize<List<ReferenceDto>>(referencesJson, JsonOptions) ?? new();
        }
        catch
        {
            return new();
        }
    }
}
