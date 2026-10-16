using Platform.Api.Features.Promotions.Models;
using Platform.Api.Features.Settings.Models;
using Platform.Api.Infrastructure;

namespace Platform.Api.Features.Settings;

/// <summary>
/// The configured participant-role vocabulary as a lookup: which canonical role a role string
/// means, once the admin's aliases (<see cref="RoleConfigDto.Aliases"/>) are applied, and who may be
/// picked for it (<see cref="RoleConfigDto.AssigneeGroups"/>).
///
/// <para>Producers don't agree on what a role is called. Jira's "QA" field reaches a ticket as
/// <c>qa</c> while the promotion policy asks every work item for a <c>qa-owner</c>; left alone, the
/// ticket has a QA owner in all but name and still reads "Needs QA owner". An admin lists
/// <c>qa</c> as an alias of <c>qa-owner</c>, and every comparison that used to canonicalise with
/// <see cref="RoleNormalizer"/> alone goes through <see cref="Resolve"/> instead — completeness,
/// the queue's person and role filters, assignment and its dedupe.</para>
///
/// <para><b>Resolved on read, not rewritten.</b> Unlike an environment alias, nothing is migrated:
/// ingest keeps recording a role as the producer sent it, and the alias makes the stored
/// <c>qa</c> count as <c>qa-owner</c> wherever it is read. That way adding an alias fixes the
/// tickets already on screen rather than only the next ones, and removing it puts them back.
/// Manual assignment is the one write that converges: it stores the canonical key and replaces any
/// participant whose role resolves to it, so a ticket never ends up with one person under each name.</para>
///
/// <para>Pure and immutable — <see cref="ParticipantRoleCatalog"/> is the scoped service that loads
/// the settings row and memoises one of these per request. A role nobody configured (neither a key
/// nor an alias) resolves to its own lower-kebab form, which is exactly what the comparisons did
/// before aliases existed.</para>
/// </summary>
public sealed class RoleAliasMap
{
    /// <summary>Map with nothing configured — every role resolves to its own canonical form.</summary>
    public static readonly RoleAliasMap Empty = Build(null);

    private readonly Dictionary<string, string> _resolve;
    private readonly Dictionary<string, IReadOnlyList<GroupRef>> _assigneeGroups;

    /// <summary>The configured canonical role keys, deduped, in settings order.</summary>
    public IReadOnlyList<string> Keys { get; }

    private RoleAliasMap(
        Dictionary<string, string> resolve,
        Dictionary<string, IReadOnlyList<GroupRef>> assigneeGroups,
        List<string> keys)
    {
        _resolve = resolve;
        _assigneeGroups = assigneeGroups;
        Keys = keys;
    }

    /// <summary>
    /// Builds the lookup from the configured roles. Every key is registered before any alias, so a
    /// configured role always resolves to itself — even in a settings row that slipped past
    /// <see cref="RoleConfigValidator"/> and lists one role's key as another's alias. Among aliases,
    /// earlier roles win a collision, so an ambiguous row still resolves deterministically.
    /// </summary>
    public static RoleAliasMap Build(IEnumerable<RoleConfigDto>? roles)
    {
        var list = (roles ?? []).ToList();
        var resolve = new Dictionary<string, string>(StringComparer.Ordinal);
        var assigneeGroups = new Dictionary<string, IReadOnlyList<GroupRef>>(StringComparer.Ordinal);
        var keys = new List<string>();

        foreach (var role in list)
        {
            var key = RoleNormalizer.Normalize(role.Key);
            if (key.Length == 0 || !resolve.TryAdd(key, key)) continue;
            keys.Add(key);
            var groups = (role.AssigneeGroups ?? [])
                .Where(g => !string.IsNullOrWhiteSpace(g.Id))
                .ToList();
            if (groups.Count > 0) assigneeGroups[key] = groups;
        }

        foreach (var role in list)
        {
            var key = RoleNormalizer.Normalize(role.Key);
            if (key.Length == 0) continue;
            foreach (var alias in role.Aliases ?? [])
            {
                var name = RoleNormalizer.Normalize(alias);
                if (name.Length > 0) resolve.TryAdd(name, key);
            }
        }

        return new RoleAliasMap(resolve, assigneeGroups, keys);
    }

    /// <summary>
    /// The canonical role <paramref name="role"/> means: the configured role it is a key or an alias
    /// of, otherwise its own lower-kebab form. Blank in, blank out. This is the drop-in replacement
    /// for <see cref="RoleNormalizer.Normalize"/> anywhere two roles are compared.
    /// </summary>
    public string Resolve(string? role)
    {
        var canonical = RoleNormalizer.Normalize(role);
        if (canonical.Length == 0) return canonical;
        return _resolve.TryGetValue(canonical, out var key) ? key : canonical;
    }

    /// <summary>Whether <paramref name="role"/> resolves onto a configured role, directly or by alias.</summary>
    public bool IsConfigured(string? role)
    {
        var canonical = RoleNormalizer.Normalize(role);
        return canonical.Length > 0 && _resolve.ContainsKey(canonical);
    }

    /// <summary>
    /// The directory groups a person picked for <paramref name="role"/> must belong to (any of
    /// them), after alias resolution. Empty when the role has no restriction or isn't configured.
    /// </summary>
    public IReadOnlyList<GroupRef> AssigneeGroups(string? role)
        => _assigneeGroups.TryGetValue(Resolve(role), out var groups) ? groups : Array.Empty<GroupRef>();
}
