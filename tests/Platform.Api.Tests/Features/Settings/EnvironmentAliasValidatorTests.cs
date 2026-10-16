using Platform.Api.Features.Settings;
using Platform.Api.Features.Settings.Models;

namespace Platform.Api.Tests.Features.Settings;

/// <summary>
/// Tests for <see cref="EnvironmentAliasValidator"/> — what a settings save quietly tidies up versus
/// what it refuses outright. The line is ambiguity: redundancy is cleaned, two possible answers for
/// one name is rejected.
/// </summary>
public class EnvironmentAliasValidatorTests
{
    private static EnvironmentConfigDto Env(string key, params string[] aliases)
        => new(key, key, null, false, [.. aliases]);

    [Fact]
    public void CleanAliases_DropsBlanksDuplicatesAndSelfReferences()
    {
        var cleaned = EnvironmentAliasValidator.CleanAliases(
            "production", ["  prod  ", "", "   ", "prod", "PROD", "Production", "prd"]);

        // First spelling of a repeated alias wins; "Production" is its own key so it adds nothing.
        Assert.Equal(["prod", "prd"], cleaned);
    }

    [Fact]
    public void CleanAliases_HandlesNull()
        => Assert.Empty(EnvironmentAliasValidator.CleanAliases("prod", null));

    [Fact]
    public void Validate_AcceptsDistinctAliases()
    {
        var errors = EnvironmentAliasValidator.Validate([
            Env("dev", "develop", "development"),
            Env("prod", "production", "prd"),
        ]);

        Assert.Empty(errors);
    }

    [Fact]
    public void Validate_RejectsAnAliasThatIsAlsoAnotherEnvironmentsKey()
    {
        // The state an admin reaches for by instinct when consolidating. It cannot be honoured: new
        // deploys would go to "prod" while the matrix still shows "production" holding history.
        var errors = EnvironmentAliasValidator.Validate([
            Env("prod", "production"),
            Env("production"),
        ]);

        var error = Assert.Single(errors);
        Assert.Contains("'production'", error);
        Assert.Contains("'prod'", error);
        // The message has to point at the operation that actually does this, or the admin is stuck.
        Assert.Contains("Merge", error);
    }

    [Fact]
    public void Validate_RejectsAnAliasClaimedByTwoEnvironments()
    {
        var errors = EnvironmentAliasValidator.Validate([
            Env("prod", "live"),
            Env("staging", "live"),
        ]);

        var error = Assert.Single(errors);
        Assert.Contains("'live'", error);
        Assert.Contains("only belong to one environment", error);
    }

    [Fact]
    public void Validate_RejectsDuplicateKeys()
    {
        // Matched on the same kebab form the resolver falls back to, so these are one environment
        // spelled two ways rather than two environments.
        var errors = EnvironmentAliasValidator.Validate([Env("pre-prod"), Env("pre_prod")]);

        var error = Assert.Single(errors);
        Assert.Contains("same environment key", error);
    }

    [Fact]
    public void Validate_AliasOnItsOwnEnvironmentIsNotAnError()
    {
        // CleanAliases removes these; Validate must not also fail on one that slipped past it.
        Assert.Empty(EnvironmentAliasValidator.Validate([Env("prod", "PROD")]));
    }

    [Fact]
    public void Validate_IgnoresBlankKeyRows()
    {
        // The endpoint drops these before saving; validation must agree so a half-typed row in the
        // editor doesn't produce a confusing error about an environment with no name.
        Assert.Empty(EnvironmentAliasValidator.Validate([Env("prod", "production"), Env("  ")]));
    }
}
