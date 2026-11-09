namespace Platform.Api.Features.Analytics;

/// <summary>
/// Default pipeline-stage mapping for environment keys the app settings don't know. Producers
/// send whatever their pipelines call the environment ("dev", "prod", "cloudiq_test"); until an
/// admin adds the key to Settings → Environments, analytics still needs to order it sensibly
/// and to guess whether it is a production stage. Name-based, deliberately conservative, and
/// ALWAYS overridden by explicit settings — this is the default, not the truth.
///
/// <para>The web mirrors this ranking (settingsStore.getOrderedEnvironments /
/// lib/envStage.ts) — change them together.</para>
/// </summary>
public static class EnvironmentStage
{
    /// <summary>
    /// Rank for ordering unknown keys: dev-like &lt; test-like &lt; staging-like &lt; unrecognised
    /// &lt; prod-like. Unrecognised keys sit just before production — most bespoke names in the
    /// wild are pre-production environments.
    /// </summary>
    public static int DefaultRank(string key)
    {
        var k = key.Trim().ToLowerInvariant();
        if (k.StartsWith("dev")) return 0;
        if (k.StartsWith("test") || k.StartsWith("qa") || k.StartsWith("int")) return 1;
        if (k.StartsWith("stag") || k.StartsWith("uat") || k.StartsWith("preprod") || k.StartsWith("pre-prod")) return 2;
        if (IsProductionByName(k)) return 4;
        return 3;
    }

    /// <summary>
    /// Whether an unconfigured key reads as a production stage: <c>prod</c>, <c>production</c>,
    /// <c>prd</c>, <c>live</c> — alone or with a suffix (<c>prod-eu</c>, <c>production_us</c>).
    /// Prefix-guarded so <c>preprod</c> and friends never match.
    /// </summary>
    public static bool IsProductionByName(string key)
    {
        var k = key.Trim().ToLowerInvariant();
        foreach (var name in (string[])["production", "prod", "prd", "live"])
        {
            if (k == name) return true;
            if (k.StartsWith(name) && k.Length > name.Length && (k[name.Length] is '-' or '_' or '.')) return true;
        }
        return false;
    }
}

/// <summary>Which environments a report treats as production, and which rule picked them.</summary>
/// <param name="Source"><c>marked</c> (settings), <c>default-name</c> (name-based default mapping) or
/// <c>convention</c> (last environment in order).</param>
public sealed record ProductionEnvironments(List<string> Environments, string Source);

/// <summary>
/// Server-side twin of the web's <c>lib/envStage.ts</c> ordering and production resolution, so a
/// report computed by the API and a tile computed by the browser name the same environments. Change
/// them together.
/// </summary>
public static class EnvironmentOrdering
{
    /// <summary>
    /// Settings order first (only the keys present in <paramref name="universe"/>), then the keys
    /// settings don't know in default stage order (dev &lt; test &lt; staging &lt; prod), ties
    /// alphabetical. Case-insensitive throughout, like every other environment lookup.
    /// </summary>
    public static List<string> Order(IEnumerable<string> universe, IEnumerable<string> configuredKeys)
    {
        var all = universe.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var configured = configuredKeys.ToList();
        var known = configured
            .Where(k => all.Contains(k, StringComparer.OrdinalIgnoreCase))
            .Select(k => all.First(u => string.Equals(u, k, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        var unknown = all
            .Where(k => !configured.Contains(k, StringComparer.OrdinalIgnoreCase))
            .OrderBy(EnvironmentStage.DefaultRank)
            .ThenBy(k => k, StringComparer.OrdinalIgnoreCase);
        known.AddRange(unknown);
        return known;
    }

    /// <summary>
    /// Resolves the production set out of <paramref name="ordered"/> (already ordered by
    /// <see cref="Order"/>): environments marked <c>isProduction</c> in settings win; else
    /// unconfigured keys whose name reads as production (<see cref="EnvironmentStage.IsProductionByName"/>);
    /// else the last environment in order. Several may be marked (multi-region production).
    /// </summary>
    public static ProductionEnvironments ResolveProduction(
        IReadOnlyList<string> ordered, IEnumerable<(string Key, bool IsProduction)> configured)
    {
        var configuredList = configured.ToList();
        bool IsConfigured(string k) => configuredList.Any(c => string.Equals(c.Key, k, StringComparison.OrdinalIgnoreCase));
        bool IsMarked(string k) => configuredList.Any(c => c.IsProduction && string.Equals(c.Key, k, StringComparison.OrdinalIgnoreCase));

        var marked = ordered.Where(IsMarked).ToList();
        if (marked.Count > 0) return new ProductionEnvironments(marked, "marked");

        var byName = ordered.Where(k => !IsConfigured(k) && EnvironmentStage.IsProductionByName(k)).ToList();
        if (byName.Count > 0) return new ProductionEnvironments(byName, "default-name");

        return new ProductionEnvironments(ordered.Count > 0 ? [ordered[^1]] : [], "convention");
    }
}
