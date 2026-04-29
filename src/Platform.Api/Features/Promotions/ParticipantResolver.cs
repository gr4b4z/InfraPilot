using System.Text.Json;
using Platform.Api.Features.Deployments.Models;
using Platform.Api.Infrastructure;

namespace Platform.Api.Features.Promotions;

/// <summary>
/// Centralised reader for the participant model on a deploy event. Three layers, in
/// descending precedence:
/// <list type="bullet">
///   <item>Override — operator-supplied <see cref="ReferenceParticipantOverride"/> rows
///         (incl. tombstones, which suppress lower layers).</item>
///   <item>Reference-level — nested under each entry of <c>DeployEvent.ReferencesJson</c>.</item>
///   <item>Event-level — <c>DeployEvent.ParticipantsJson</c>, the legacy top-level list.</item>
/// </list>
/// <para>The role-search helper here favours the most specific signal: an operator
/// override beats a Jira-supplied reference participant beats an event-level participant.
/// Tombstones (override rows with <c>AssigneeEmail == null</c>) explicitly suppress
/// fall-through to lower layers — that's how operators express "remove this Jira person."</para>
/// </summary>
public static class ParticipantResolver
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// Returns the event-level participants — what was historically in <c>ParticipantsJson</c>.
    /// Empty list when the JSON is null/blank or fails to parse (best-effort, never throws).
    /// </summary>
    public static IReadOnlyList<ParticipantDto> GetEventParticipants(string? participantsJson)
    {
        if (string.IsNullOrWhiteSpace(participantsJson)) return Array.Empty<ParticipantDto>();
        try
        {
            return JsonSerializer.Deserialize<List<ParticipantDto>>(participantsJson, JsonOptions)
                ?? (IReadOnlyList<ParticipantDto>)Array.Empty<ParticipantDto>();
        }
        catch
        {
            return Array.Empty<ParticipantDto>();
        }
    }

    /// <summary>
    /// Flattens reference-level participants into <c>(Ref, Participant)</c> tuples across all
    /// references on the event. Empty when no references carry nested participants — backwards
    /// compatible with payloads predating the two-level model.
    /// </summary>
    public static IReadOnlyList<(ReferenceDto Ref, ParticipantDto Participant)> GetReferenceParticipants(
        string? referencesJson)
    {
        if (string.IsNullOrWhiteSpace(referencesJson))
            return Array.Empty<(ReferenceDto, ParticipantDto)>();

        List<ReferenceDto>? refs;
        try
        {
            refs = JsonSerializer.Deserialize<List<ReferenceDto>>(referencesJson, JsonOptions);
        }
        catch
        {
            return Array.Empty<(ReferenceDto, ParticipantDto)>();
        }
        if (refs is null) return Array.Empty<(ReferenceDto, ParticipantDto)>();

        var output = new List<(ReferenceDto, ParticipantDto)>();
        foreach (var r in refs)
        {
            if (r.Participants is null) continue;
            foreach (var p in r.Participants) output.Add((r, p));
        }
        return output;
    }

    /// <summary>
    /// Result of a role-scoped participant lookup. Distinguishes three states:
    /// <list type="bullet">
    ///   <item><see cref="Found"/>: a participant with a non-null email is the effective value.</item>
    ///   <item><see cref="Suppressed"/>: a tombstone override exists for this (referenceKey, role)
    ///         and explicitly clears the slot — callers must NOT fall back to lower layers.</item>
    ///   <item>Both null/false: no match at any layer; callers may treat that as "no participant."</item>
    /// </list>
    /// </summary>
    public readonly record struct ParticipantLookup(ParticipantDto? Found, bool Suppressed)
    {
        public static readonly ParticipantLookup None = new(null, Suppressed: false);
        public static ParticipantLookup Hit(ParticipantDto p) => new(p, Suppressed: false);
        public static readonly ParticipantLookup Tombstone = new(null, Suppressed: true);
    }

    /// <summary>
    /// Override-aware lookup: precedence is override → reference-level → event-level. Returns
    /// a <see cref="ParticipantLookup"/> so callers can distinguish "tombstoned, do not fall
    /// through" from "no match at any layer."
    /// <para>The override match canonicalises both sides (<see cref="RoleNormalizer.Normalize"/>)
    /// and matches <paramref name="referenceKey"/> exactly. When <paramref name="referenceKey"/>
    /// is null no override is consulted (overrides are always reference-scoped).</para>
    /// </summary>
    public static ParticipantLookup FindByRoleWithOverrides(
        string? participantsJson,
        string? referencesJson,
        IReadOnlyList<ReferenceParticipantOverride>? overrides,
        string role,
        string? referenceKey = null)
    {
        var canonical = RoleNormalizer.Normalize(role);
        if (canonical.Length == 0) return ParticipantLookup.None;

        // Override layer — only checked when referenceKey is supplied (overrides are
        // reference-scoped). Tombstones short-circuit and explicitly suppress fallthrough.
        if (referenceKey is not null && overrides is { Count: > 0 })
        {
            foreach (var o in overrides)
            {
                if (!string.Equals(o.ReferenceKey, referenceKey, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (RoleNormalizer.Normalize(o.Role) != canonical) continue;

                if (o.AssigneeEmail is null) return ParticipantLookup.Tombstone;

                return ParticipantLookup.Hit(new ParticipantDto(
                    Role: o.Role,
                    DisplayName: o.AssigneeDisplayName,
                    Email: o.AssigneeEmail,
                    IsOverride: true,
                    AssignedBy: o.AssignedByName));
            }
        }

        // Reference-level — more specific signal than event-level fallback.
        foreach (var (r, p) in GetReferenceParticipants(referencesJson))
        {
            if (referenceKey is not null
                && !string.Equals(r.Key, referenceKey, StringComparison.OrdinalIgnoreCase))
                continue;
            if (RoleNormalizer.Normalize(p.Role) == canonical) return ParticipantLookup.Hit(p);
        }

        foreach (var p in GetEventParticipants(participantsJson))
        {
            if (RoleNormalizer.Normalize(p.Role) == canonical) return ParticipantLookup.Hit(p);
        }

        return ParticipantLookup.None;
    }

    /// <summary>
    /// Backwards-compatible 4-arg overload — no overrides loaded. Equivalent to
    /// <see cref="FindByRoleWithOverrides"/> with a null overrides list. Returns the participant
    /// directly (or null). Kept so call sites that haven't been migrated to the override-aware
    /// path keep compiling.
    /// </summary>
    public static ParticipantDto? FindByRole(
        string? participantsJson,
        string? referencesJson,
        string role,
        string? referenceKey = null)
    {
        var lookup = FindByRoleWithOverrides(participantsJson, referencesJson, overrides: null, role, referenceKey);
        return lookup.Found;
    }
}
