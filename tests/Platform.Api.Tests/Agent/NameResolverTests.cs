using Platform.Api.Agent;

namespace Platform.Api.Tests.Agent;

/// <summary>
/// How the names people type reach the names the data uses. The cases here are the ones that
/// used to end in "no such service": a typo, a fragment, a name with spaces.
/// </summary>
public class NameResolverTests
{
    private static readonly string[] Services =
    [
        "mpt-extension-adobe", "mpt-extension-microsoft", "mpt-extension-aws", "adobe-sync-worker",
        "auth-api", "audit-log", "payments-worker", "identity-platform-ui",
    ];

    private static readonly string[] Products = ["mpt", "mpt-extensions", "identity-platform", "order-service"];

    /// <summary>The exact case that was reported.</summary>
    [Fact]
    public void Corrects_a_typo_in_a_service_name()
    {
        var r = NameResolver.Resolve("mpt-extentions-adobe", Services);

        Assert.Equal(NameMatchKind.Corrected, r.Kind);
        Assert.Equal("mpt-extension-adobe", r.Single);
        Assert.Contains("likely a typo", r.Note("service"));
        Assert.Contains("mpt-extension-adobe", r.Note("service"));
    }

    [Theory]
    [InlineData("auth-api")]
    [InlineData("Auth-API")]
    [InlineData("auth api")]
    [InlineData("auth_api")]
    [InlineData("  auth-api ")]
    public void Exact_matches_ignore_case_and_separators(string typed)
    {
        var r = NameResolver.Resolve(typed, Services);

        Assert.Equal(NameMatchKind.Exact, r.Kind);
        Assert.Equal("auth-api", r.Single);
        Assert.Null(r.Note("service"));
    }

    [Fact]
    public void Product_said_with_spaces_resolves_to_its_slug()
        => Assert.Equal("identity-platform", NameResolver.Resolve("identity platform", Products).Single);

    /// <summary>"adobe" means every adobe service — the answer is the list, not a guess at one.</summary>
    [Fact]
    public void A_fragment_matches_every_name_containing_it()
    {
        var r = NameResolver.Resolve("adobe", Services);

        Assert.Equal(NameMatchKind.Partial, r.Kind);
        Assert.Equal(["adobe-sync-worker", "mpt-extension-adobe"], r.Matches);
        Assert.Contains("2 services", r.Note("service"));
    }

    [Fact]
    public void Several_fragments_all_have_to_appear()
    {
        var r = NameResolver.Resolve("mpt adobe", Services);

        Assert.Equal(NameMatchKind.Partial, r.Kind);
        Assert.Equal(["mpt-extension-adobe"], r.Matches);
    }

    /// <summary>A misspelt fragment still finds its family: "extention" for the extensions.</summary>
    [Fact]
    public void A_fragment_with_a_typo_still_matches_by_token()
    {
        var r = NameResolver.Resolve("extention", Services);

        Assert.Equal(NameMatchKind.Partial, r.Kind);
        Assert.Equal(3, r.Matches.Count);
        Assert.All(r.Matches, m => Assert.StartsWith("mpt-extension-", m));
    }

    [Fact]
    public void Exact_beats_near_miss_and_fragment()
    {
        // "mpt" is a product exactly, and also a prefix of another.
        var r = NameResolver.Resolve("mpt", Products);

        Assert.Equal(NameMatchKind.Exact, r.Kind);
        Assert.Equal("mpt", r.Single);
    }

    [Fact]
    public void An_ambiguous_typo_returns_every_equally_close_name()
    {
        var r = NameResolver.Resolve("mpt-extension-adobs", ["mpt-extension-adobe", "mpt-extension-adobx"]);

        Assert.Equal(NameMatchKind.Corrected, r.Kind);
        Assert.Equal(2, r.Matches.Count);
        Assert.Null(r.Single);
        Assert.Contains("could be any of", r.Note("service"));
    }

    /// <summary>The allowance must not let unrelated short names turn into each other.</summary>
    [Theory]
    [InlineData("auth-ui")]
    [InlineData("xyz")]
    [InlineData("payments-api")]
    public void Unrelated_names_stay_unresolved(string typed)
    {
        var r = NameResolver.Resolve(typed, Services);

        Assert.Equal(NameMatchKind.None, r.Kind);
        Assert.Empty(r.Matches);
    }

    [Fact]
    public void Two_letter_fragments_are_too_short_to_mean_anything()
        => Assert.Equal(NameMatchKind.None, NameResolver.Resolve("ap", Services).Kind);

    [Fact]
    public void Blank_input_or_no_candidates_resolves_to_nothing()
    {
        Assert.Equal(NameMatchKind.None, NameResolver.Resolve("  ", Services).Kind);
        Assert.Equal(NameMatchKind.None, NameResolver.Resolve(null, Services).Kind);
        Assert.Equal(NameMatchKind.None, NameResolver.Resolve("auth-api", []).Kind);
    }

    [Fact]
    public void Partial_matches_are_capped_and_shortest_first()
    {
        var many = Enumerable.Range(0, 12).Select(i => $"svc-{new string('x', i)}-adobe").ToArray();
        var r = NameResolver.Resolve("adobe", many);

        Assert.Equal(NameResolver.MaxPartialMatches, r.Matches.Count);
        Assert.Equal("svc--adobe", r.Matches[0]);
    }

    [Theory]
    [InlineData("kitten", "sitting", 3)]
    [InlineData("abcd", "abdc", 1)]
    [InlineData("same", "same", 0)]
    [InlineData("", "abc", 3)]
    public void Edit_distance_counts_swaps_as_one(string a, string b, int expected)
        => Assert.Equal(expected, NameResolver.Distance(a, b));
}
