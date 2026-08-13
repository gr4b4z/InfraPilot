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
