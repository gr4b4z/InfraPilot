using Platform.Api.Features.Promotions.Models;
using Platform.Api.Features.Settings.Models;
using Platform.Api.Infrastructure;

namespace Platform.Api.Features.Settings;

/// <summary>
/// Cleans and validates the participant-role list on a settings save — the role-vocabulary twin of
/// <see cref="EnvironmentAliasValidator"/>.
///
/// <para>An alias only means something if exactly one role answers to it, so the two states this
/// rejects are both ambiguity: the same alias on two roles, and an alias that is also a role of its
/// own. The second is what an admin reaches for first — list <c>qa</c> under <c>qa-owner</c> while
/// the built-in <c>qa</c> role is still in the list — and it can't be honoured: <c>qa</c> would be
/// a role someone can be assigned to and, at the same time, another name for <c>qa-owner</c>. The
/// fix is to delete the <c>qa</c> row, which costs nothing here: roles are resolved on read (see
/// <see cref="RoleAliasMap"/>), so there is no history to move.</para>
///
/// <para>Redundancy is not an error: blank aliases, an alias equal to its own role, repeats, and
/// blank or repeated groups are dropped quietly.</para>
/// </summary>
public static class RoleConfigValidator
{
    /// <summary>
    /// One role row as it should be stored: trimmed key and label, aliases canonicalised to
    /// lower-kebab (the form every comparison uses), groups trimmed with a name defaulting to the id.
    /// </summary>
    public static RoleConfigDto Clean(RoleConfigDto role)
    {
        var key = (role.Key ?? "").Trim();
        return new RoleConfigDto(
            key,
            (role.DisplayName ?? "").Trim(),
            CleanAliases(key, role.Aliases),
            CleanGroups(role.AssigneeGroups));
    }

    /// <summary>
    /// Canonicalised, deduped aliases with anything resolving to the role's own key removed. Order is
    /// the admin's.
    /// </summary>
    public static List<string> CleanAliases(string? roleKey, IEnumerable<string>? aliases)
    {
        var key = RoleNormalizer.Normalize(roleKey);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var cleaned = new List<string>();
        foreach (var raw in aliases ?? [])
        {
            var alias = RoleNormalizer.Normalize(raw);
            if (alias.Length == 0 || alias == key || !seen.Add(alias)) continue;
            cleaned.Add(alias);
        }
        return cleaned;
    }

    /// <summary>Trimmed groups with a non-blank id, deduped on id (case-insensitive).</summary>
    public static List<GroupRef> CleanGroups(IEnumerable<GroupRef>? groups)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var cleaned = new List<GroupRef>();
        foreach (var g in groups ?? [])
        {
            var id = (g?.Id ?? "").Trim();
            if (id.Length == 0 || !seen.Add(id)) continue;
            var name = (g!.Name ?? "").Trim();
            cleaned.Add(new GroupRef(id, name.Length > 0 ? name : id));
        }
        return cleaned;
    }

    /// <summary>
    /// The reasons this role list cannot be saved, or empty when it can. Each message names both
    /// sides of the collision and the way out. Expects <see cref="Clean"/> to have run.
    /// </summary>
    public static List<string> Validate(IEnumerable<RoleConfigDto>? roles)
    {
        var list = (roles ?? []).ToList();
        var errors = new List<string>();

        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var role in list)
        {
            var key = RoleNormalizer.Normalize(role.Key);
            if (key.Length > 0) keys.Add(key);
        }

        var aliasOwners = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var role in list)
        {
            var key = RoleNormalizer.Normalize(role.Key);
            if (key.Length == 0) continue;
            foreach (var alias in role.Aliases ?? [])
            {
                var name = RoleNormalizer.Normalize(alias);
                if (name.Length == 0 || name == key) continue;

                if (keys.Contains(name))
                {
                    errors.Add($"'{name}' is listed as an alias of '{key}' but is also a role of its own. "
                             + $"Remove the '{name}' role to make it another name for '{key}'.");
                    continue;
                }

                if (aliasOwners.TryGetValue(name, out var owner) && owner != key)
                {
                    errors.Add($"'{name}' is listed as an alias of both '{owner}' and '{key}'. "
                             + "An alias can only belong to one role.");
                    continue;
                }

                aliasOwners[name] = key;
            }
        }

        return errors;
    }
}
