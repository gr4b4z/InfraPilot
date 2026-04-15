namespace Platform.Api.Infrastructure.Features;

/// <summary>
/// Server-authoritative feature flag store backed by the <c>platform_settings</c> table.
/// Keys are namespaced, e.g. <c>features.promotions</c>. All reads are cached in-process
/// for a short TTL so hot paths don't hit the DB on every request.
/// </summary>
public interface IFeatureFlags
{
    /// <summary>
    /// Returns the current value of the named feature flag. Unknown keys default to <c>false</c>.
    /// </summary>
    Task<bool> IsEnabled(string feature, CancellationToken ct = default);

    /// <summary>
    /// Sets the named feature flag and invalidates the in-process cache so the change takes
    /// effect immediately on this node. (Other API nodes will pick it up on next cache expiry.)
    /// </summary>
    Task SetEnabled(string feature, bool enabled, string updatedBy, CancellationToken ct = default);
}

/// <summary>
/// Centralised feature-flag key constants so typos don't silently mask a flag lookup.
/// </summary>
public static class FeatureFlagKeys
{
    public const string Promotions = "features.promotions";
}
