using Platform.Api.Features.Settings.Models;
using Platform.Api.Infrastructure;

namespace Platform.Api.Features.Settings;

/// <summary>
/// Resolves an environment name a caller sent to the canonical key an admin configured for it.
///
/// <para>Producers name the same physical environment whatever their pipeline calls it — "dev",
/// "develop", "development"; "prod", "production", "prd". Left alone that is three environments on
/// the deployment matrix, three columns in analytics, and three separate promotion edges to
/// configure. An admin lists the variants as <see cref="EnvironmentConfigDto.Aliases"/> on the one
/// real environment, and every write path resolves through this map before storing, so new traffic
/// converges on the canonical key. <see cref="EnvironmentMergeService"/> is how the rows that
/// arrived before that follow.</para>
///
/// <para>Pure and immutable — <see cref="EnvironmentAliasResolver"/> is the scoped service that
/// loads the settings row and memoises one of these. Anything not configured (neither a key nor an
/// alias) passes through unchanged: an environment nobody has curated yet must keep working.</para>
/// </summary>
public sealed class EnvironmentAliasMap
{
    /// <summary>Map with nothing configured — every lookup passes the input straight through.</summary>
    public static readonly EnvironmentAliasMap Empty = Build(null);

    private readonly Dictionary<string, string> _byLower;
    private readonly Dictionary<string, string> _byNormalized;

    /// <summary>The configured canonical keys, in settings order.</summary>
    public IReadOnlyList<string> Keys { get; }

    private EnvironmentAliasMap(
        Dictionary<string, string> byLower,
        Dictionary<string, string> byNormalized,
        List<string> keys)
    {
        _byLower = byLower;
        _byNormalized = byNormalized;
        Keys = keys;
    }

    /// <summary>
    /// Builds the lookup from the configured environments. Earlier entries win a collision, so the
    /// map is deterministic even for a settings row that slipped past
    /// <see cref="EnvironmentAliasValidator"/> (a hand-edited JSON row, or one written before the
    /// validation existed) — an ambiguous alias resolves to the environment listed first rather
    /// than to whichever the dictionary happened to see last.
    /// </summary>
    public static EnvironmentAliasMap Build(IEnumerable<EnvironmentConfigDto>? environments)
    {
        var byLower = new Dictionary<string, string>(StringComparer.Ordinal);
        var byNormalized = new Dictionary<string, string>(StringComparer.Ordinal);
        var keys = new List<string>();

        foreach (var env in environments ?? [])
        {
            var key = (env.Key ?? "").Trim();
            if (key.Length == 0) continue;
            keys.Add(key);

            // The key itself is registered first: an environment always resolves to its own
            // spelling, whatever casing the sender used.
            Add(byLower, byNormalized, key, key);
            foreach (var alias in env.Aliases ?? [])
            {
                var name = (alias ?? "").Trim();
                if (name.Length > 0) Add(byLower, byNormalized, name, key);
            }
        }

        return new EnvironmentAliasMap(byLower, byNormalized, keys);
    }

    private static void Add(
        Dictionary<string, string> byLower, Dictionary<string, string> byNormalized,
        string name, string canonical)
    {
        byLower.TryAdd(name.ToLowerInvariant(), canonical);
        var normalized = RoleNormalizer.Normalize(name);
        if (normalized.Length > 0) byNormalized.TryAdd(normalized, canonical);
    }

    /// <summary>
    /// The canonical key for <paramref name="sent"/>, or the trimmed input when nothing is
    /// configured for it. Blank in, blank out.
    ///
    /// <para>Matching is three-tier, narrowest first: the exact string, then case-insensitively,
    /// then on the lower-kebab form (<see cref="RoleNormalizer"/>) so "Pre Prod", "pre_prod" and
    /// "preProd" all reach an alias written "pre-prod". The kebab tier is the same canonicaliser
    /// deploy ingest already applies to environment strings by default, so it recognises nothing
    /// the stored value would not already have collapsed to.</para>
    /// </summary>
    public string Resolve(string? sent) => Match(sent).Key;

    /// <summary>
    /// Null for a blank input, otherwise <see cref="Resolve"/>. For query filters, where "no
    /// environment given" and "an environment given" are different questions.
    /// </summary>
    public string? ResolveFilter(string? sent)
        => string.IsNullOrWhiteSpace(sent) ? null : Resolve(sent);

    /// <summary>
    /// The resolution and how it was reached. <see cref="Resolution.Aliased"/> is what callers log or
    /// report off — a rewrite worth mentioning, as opposed to a sender that was already right.
    /// </summary>
    public Resolution Match(string? sent)
    {
        var name = (sent ?? "").Trim();
        if (name.Length == 0) return new Resolution("", "", false);

        if (_byLower.TryGetValue(name.ToLowerInvariant(), out var canonical)
            || _byNormalized.TryGetValue(RoleNormalizer.Normalize(name), out canonical))
        {
            // Ordinal: a match that differs only in casing IS a rewrite (it converges the stored
            // spelling on the admin's), and only an exact hit means the sender needed nothing.
            return new Resolution(canonical, name, !string.Equals(canonical, name, StringComparison.Ordinal));
        }

        return new Resolution(name, name, false);
    }

    /// <param name="Key">The canonical key to store.</param>
    /// <param name="Sent">The trimmed name the caller supplied.</param>
    /// <param name="Aliased">Whether the map changed the answer.</param>
    public record Resolution(string Key, string Sent, bool Aliased);
}
