using Platform.Api.Features.Settings;
using Platform.Api.Features.Settings.Models;

namespace Platform.Api.Tests.Features.Settings;

/// <summary>
/// Tests for <see cref="EnvironmentAliasMap"/> — the lookup that turns whatever a producer calls an
/// environment into the key an admin configured for it.
/// </summary>
public class EnvironmentAliasMapTests
{
    private static EnvironmentAliasMap Build(params EnvironmentConfigDto[] envs)
        => EnvironmentAliasMap.Build(envs);

    private static EnvironmentConfigDto Env(string key, params string[] aliases)
        => new(key, key, null, false, [.. aliases]);

    [Fact]
    public void Resolve_AliasReachesCanonicalKey()
    {
        var map = Build(Env("prod", "production", "productions", "prd"));

        Assert.Equal("prod", map.Resolve("production"));
        Assert.Equal("prod", map.Resolve("productions"));
        Assert.Equal("prod", map.Resolve("prd"));
        Assert.Equal("prod", map.Resolve("prod"));
    }

    [Theory]
    // Case-insensitive on both the key and the alias.
    [InlineData("PRODUCTION", "prod")]
    [InlineData("Prod", "prod")]
    // Kebab tier: separators and camelCase boundaries collapse to the alias's own spelling.
    [InlineData("Pre Prod", "staging")]
    [InlineData("pre_prod", "staging")]
    [InlineData("preProd", "staging")]
    public void Resolve_MatchesAcrossCasingAndSeparators(string sent, string expected)
    {
        var map = Build(Env("prod", "production"), Env("staging", "pre-prod"));
        Assert.Equal(expected, map.Resolve(sent));
    }

    [Fact]
    public void Resolve_UnconfiguredNamePassesThroughTrimmed()
    {
        var map = Build(Env("prod", "production"));

        // An environment nobody has curated has to keep working — the whole point of the settings
        // list being optional.
        Assert.Equal("cloudiq-test", map.Resolve("  cloudiq-test  "));
        Assert.Equal("", map.Resolve(null));
        Assert.Equal("", map.Resolve("   "));
    }

    [Fact]
    public void Resolve_ConvergesCasingOnTheAdminsSpelling()
    {
        var map = Build(Env("Production"));

        // The stored value follows the configured key, not the sender, so two pipelines shouting the
        // environment in different cases still land in one column.
        Assert.Equal("Production", map.Resolve("production"));
        Assert.Equal("Production", map.Resolve("PRODUCTION"));
    }

    [Fact]
    public void Match_ReportsWhetherTheMapChangedTheAnswer()
    {
        var map = Build(Env("prod", "production"));

        Assert.False(map.Match("prod").Aliased);
        Assert.True(map.Match("production").Aliased);
        // Casing-only differences count as a rewrite: the stored spelling changes.
        Assert.True(map.Match("PROD").Aliased);
        Assert.False(map.Match("unknown").Aliased);
    }

    [Fact]
    public void Build_EarlierEntryWinsAnAmbiguousAlias()
    {
        // A hand-edited settings row can hold what the validator rejects. Resolution still has to be
        // deterministic rather than dictionary-insertion-order dependent.
        var map = Build(Env("prod", "live"), Env("staging", "live"));
        Assert.Equal("prod", map.Resolve("live"));
    }

    [Fact]
    public void Build_IgnoresBlankKeysAndAliases()
    {
        var map = EnvironmentAliasMap.Build([
            new("   ", "Blank", null, false, ["ghost"]),
            new("prod", "Production", null, false, ["", "  ", "production"]),
        ]);

        Assert.Equal(["prod"], map.Keys);
        Assert.Equal("ghost", map.Resolve("ghost"));
        Assert.Equal("prod", map.Resolve("production"));
    }

    [Fact]
    public void ResolveFilter_KeepsAllEnvironmentsMeaningBlank()
    {
        var map = Build(Env("prod", "production"));

        // Null in, null out: "no environment filter" and "an environment filter" are different
        // questions, and collapsing them would silently scope an unfiltered query.
        Assert.Null(map.ResolveFilter(null));
        Assert.Null(map.ResolveFilter("  "));
        Assert.Equal("prod", map.ResolveFilter("production"));
    }

    [Fact]
    public void Empty_PassesEverythingThrough()
    {
        Assert.Equal("anything", EnvironmentAliasMap.Empty.Resolve("anything"));
        Assert.Empty(EnvironmentAliasMap.Empty.Keys);
    }
}
