using Platform.Api.Features.Settings.Models;
using Platform.Api.Infrastructure;

namespace Platform.Api.Features.Settings;

/// <summary>
/// Cleans and validates the alias lists on a settings save.
///
/// <para>An alias only means anything if exactly one environment answers to it, so the two states
/// this rejects are both forms of ambiguity: the same alias on two environments, and an alias that
/// is also some <i>other</i> environment's key. The second is the one an admin reaches for by
/// instinct when consolidating — list "production" as an alias of "prod" while "production" is
/// still its own row — and it cannot be honoured: the resolver would have to send new deploys to
/// "prod" while the deployment matrix still shows "production" as a live environment holding
/// history. <see cref="EnvironmentMergeService"/> is the operation that actually does this — it
/// moves the rows, records the alias, and drops the source row in one step — so the error points
/// there rather than inventing a half-migrated state.</para>
///
/// <para>Redundancy, by contrast, is not an error: an alias equal to its own environment's key, a
/// blank entry, or the same alias twice on one environment are all silently dropped. The editor
/// lets an admin type freely and there is nothing ambiguous to resolve.</para>
/// </summary>
public static class EnvironmentAliasValidator
{
    /// <summary>
    /// Drops blank, self-referential and duplicate aliases, trimming what survives. Order is the
    /// admin's; the first spelling of a repeated alias is the one kept.
    /// </summary>
    public static List<string> CleanAliases(string? environmentKey, IEnumerable<string>? aliases)
    {
        var key = (environmentKey ?? "").Trim();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var cleaned = new List<string>();

        foreach (var raw in aliases ?? [])
        {
            var alias = (raw ?? "").Trim();
            if (alias.Length == 0) continue;
            // Case-insensitive against the key: "Production" as an alias of "production" adds
            // nothing the resolver would not already match.
            if (string.Equals(alias, key, StringComparison.OrdinalIgnoreCase)) continue;
            if (!seen.Add(alias)) continue;
            cleaned.Add(alias);
        }

        return cleaned;
    }

    /// <summary>
    /// The reasons this set of environments cannot be saved, or an empty list when it can. Every
    /// message names both sides of the collision and what to do about it — an admin reading it in a
    /// toast has no other way to tell which of two rows is the problem.
    /// <para>Expects <see cref="CleanAliases"/> to have run already; it does not re-clean.</para>
    /// </summary>
    public static List<string> Validate(IEnumerable<EnvironmentConfigDto>? environments)
    {
        var envs = (environments ?? []).Where(e => !string.IsNullOrWhiteSpace(e.Key)).ToList();
        var errors = new List<string>();

        // Keys are matched on the same lower-kebab form the resolver falls back to, so "pre-prod"
        // and "pre_prod" count as one environment here too.
        var keyOwners = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var env in envs)
        {
            var key = env.Key.Trim();
            var normalized = Normalize(key);
            if (keyOwners.TryGetValue(normalized, out var first))
            {
                errors.Add($"'{key}' and '{first}' are the same environment key. Remove one, "
                         + "or merge them so the history follows.");
                continue;
            }
            keyOwners[normalized] = key;
        }

        var aliasOwners = new Dictionary<string, (string Env, string Alias)>(StringComparer.Ordinal);
        foreach (var env in envs)
        {
            var key = env.Key.Trim();
            foreach (var alias in env.Aliases ?? [])
            {
                var name = (alias ?? "").Trim();
                if (name.Length == 0) continue;
                var normalized = Normalize(name);

                if (keyOwners.TryGetValue(normalized, out var owner)
                    && !string.Equals(owner, key, StringComparison.Ordinal))
                {
                    errors.Add($"'{name}' is an alias of '{key}' but also an environment of its own. "
                             + $"Merge '{owner}' into '{key}' instead — that moves the history, records "
                             + "the alias, and removes the duplicate environment in one step.");
                    continue;
                }

                if (aliasOwners.TryGetValue(normalized, out var claimed)
                    && !string.Equals(claimed.Env, key, StringComparison.Ordinal))
                {
                    errors.Add($"'{name}' is listed as an alias of both '{claimed.Env}' and '{key}'. "
                             + "An alias can only belong to one environment.");
                    continue;
                }

                aliasOwners[normalized] = (key, name);
            }
        }

        return errors;
    }

    private static string Normalize(string value)
    {
        var normalized = RoleNormalizer.Normalize(value);
        // A name made entirely of characters the kebab canonicaliser strips would collapse to "",
        // which would collide with every other such name. Fall back to the lowered original.
        return normalized.Length > 0 ? normalized : value.Trim().ToLowerInvariant();
    }
}
