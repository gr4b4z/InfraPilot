using Platform.Api.Features.Analytics;

namespace Platform.Api.Tests.Features.Analytics;

/// <summary>
/// The server-side production resolution the delivery report uses — the same three rules as the web's
/// <c>resolveProductionEnvs</c>: marked in settings wins; else name-based default mapping for keys the
/// settings don't know; else the last environment in order.
/// </summary>
public class EnvironmentOrderingTests
{
    [Fact]
    public void Order_SettingsFirst_ThenUnknownByStage()
    {
        var ordered = EnvironmentOrdering.Order(
            ["prod", "zeta", "test", "dev", "staging"],
            ["dev", "staging"]);

        // Configured keys in settings order, then unknown keys dev < test < staging < unrecognised < prod.
        Assert.Equal(["dev", "staging", "test", "zeta", "prod"], ordered);
    }

    [Fact]
    public void Order_IsCaseInsensitive_AndKeepsObservedSpelling()
    {
        var ordered = EnvironmentOrdering.Order(["PROD", "Dev"], ["dev", "prod"]);
        Assert.Equal(["Dev", "PROD"], ordered);
    }

    [Fact]
    public void ResolveProduction_MarkedWins_AndKeepsEveryMarkedRegion()
    {
        var result = EnvironmentOrdering.ResolveProduction(
            ["dev", "test", "prod-eu", "prod-us"],
            [("dev", false), ("test", false), ("prod-eu", true), ("prod-us", true)]);

        Assert.Equal("marked", result.Source);
        Assert.Equal(["prod-eu", "prod-us"], result.Environments);
    }

    [Fact]
    public void ResolveProduction_UnconfiguredProdLikeName_IsDefaultMapping()
    {
        var result = EnvironmentOrdering.ResolveProduction(
            ["dev", "preprod", "live"],
            [("dev", false)]);

        Assert.Equal("default-name", result.Source);
        Assert.Equal(["live"], result.Environments);
    }

    [Fact]
    public void ResolveProduction_ConfiguredButUnmarkedProdName_DoesNotMatchByName()
    {
        // An admin who added "prod" to settings without ticking Prod has made a choice; the name rule
        // only covers keys settings don't know. Falls through to the last-in-order convention.
        var result = EnvironmentOrdering.ResolveProduction(
            ["dev", "prod", "test"],
            [("dev", false), ("prod", false), ("test", false)]);

        Assert.Equal("convention", result.Source);
        Assert.Equal(["test"], result.Environments);
    }

    [Fact]
    public void ResolveProduction_EmptyUniverse_IsEmpty()
    {
        var result = EnvironmentOrdering.ResolveProduction([], []);
        Assert.Equal("convention", result.Source);
        Assert.Empty(result.Environments);
    }
}
