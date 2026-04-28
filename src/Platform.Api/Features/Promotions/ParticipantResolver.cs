using System.Text.Json;
using Platform.Api.Features.Deployments.Models;
using Platform.Api.Infrastructure;

namespace Platform.Api.Features.Promotions;

/// <summary>
/// Centralised reader for the two-level participant model on a deploy event:
/// <list type="bullet">
///   <item>Event-level — <c>DeployEvent.ParticipantsJson</c>, the legacy top-level list.</item>
///   <item>Reference-level — nested under each entry of <c>DeployEvent.ReferencesJson</c>.</item>
/// </list>
/// <para>The role-search helper here favours reference-level matches over event-level; the
/// read assumption is that a participant attached directly to a PR/ticket/commit is a more
/// specific signal than the event-level fallback.</para>
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
    /// Looks up a participant by canonicalised role across reference-level entries first
    /// (optionally filtered by <paramref name="referenceKey"/>), then falls back to event-level.
    /// Returns <c>null</c> when nothing matches.
    /// <para>Match semantics mirror <see cref="PromotionApprovalAuthorizer.EmailMatchesExcludedRole"/>:
    /// <see cref="RoleNormalizer.Normalize"/> on both sides so casing/punctuation differences
    /// don't matter.</para>
    /// </summary>
    public static ParticipantDto? FindByRole(
        string? participantsJson,
        string? referencesJson,
        string role,
        string? referenceKey = null)
    {
        var canonical = RoleNormalizer.Normalize(role);
        if (canonical.Length == 0) return null;

        // Reference-level first — more specific signal than the event-level fallback.
        foreach (var (r, p) in GetReferenceParticipants(referencesJson))
        {
            if (referenceKey is not null
                && !string.Equals(r.Key, referenceKey, StringComparison.OrdinalIgnoreCase))
                continue;
            if (RoleNormalizer.Normalize(p.Role) == canonical) return p;
        }

        foreach (var p in GetEventParticipants(participantsJson))
        {
            if (RoleNormalizer.Normalize(p.Role) == canonical) return p;
        }

        return null;
    }
}
