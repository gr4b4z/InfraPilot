using Platform.Api.Agent;

namespace Platform.Api.Tests.Agent;

/// <summary>
/// The pieces that decide what the assistant rings on the user's screen and where it sends them.
/// They are pure so they can be pinned here without a model in the loop.
/// </summary>
public class ScreenContextTests
{
    // ── Anchor names ─────────────────────────────────────────────────────────────────────────────
    // These must match what the web app renders (ProductDeploymentsPage, ServiceDetailPage,
    // PromotionsPage). A rename on either side without the other silently rings nothing.

    [Fact]
    public void Anchor_names_follow_the_client_patterns()
    {
        Assert.Equal("service-row:auth-api", PortalAnchors.ServiceRow("auth-api"));
        Assert.Equal("env-cell:auth-api:staging", PortalAnchors.EnvCell("auth-api", "staging"));
        Assert.Equal("env-column:production", PortalAnchors.EnvColumn("production"));
        Assert.Equal("service-environments", PortalAnchors.ServiceEnvironments);

        var id = Guid.Parse("11111111-2222-3333-4444-555555555555");
        Assert.Equal($"promotion-row:{id}", PortalAnchors.PromotionRow(id));
    }

    [Fact]
    public void Anchor_names_are_trimmed()
        => Assert.Equal("env-cell:auth-api:staging", PortalAnchors.EnvCell(" auth-api ", " staging"));

    [Theory]
    [InlineData("service-row:x", true)]
    [InlineData("env-cell:x:y", true)]
    [InlineData("promotion-approve-button", true)]
    [InlineData("work-item-signoff", true)]
    [InlineData("nav-promotions", true)]
    [InlineData("service-environments", true)]
    [InlineData("the-big-red-button", false)]
    [InlineData("", false)]
    public void Convention_check_recognises_the_families_the_app_uses(string anchor, bool expected)
        => Assert.Equal(expected, PortalAnchors.FollowsConvention(anchor));

    // ── Filtering what the model asked to ring ───────────────────────────────────────────────────

    private static HighlightTarget T(string anchor, string? label = null) => new() { Anchor = anchor, Label = label };

    [Fact]
    public void Only_anchors_on_screen_are_accepted_when_the_screen_is_known()
    {
        var onScreen = new[] { "promotion-status", "promotion-approve-button" };

        var (accepted, dropped) = HighlightResolver.Filter(
            [T("promotion-status", "Approved"), T("promotion-magic-button"), T("env-cell:a:b")],
            onScreen,
            destinationUnseen: false);

        Assert.Equal(["promotion-status"], accepted.Select(a => a.Anchor));
        Assert.Equal(["promotion-magic-button", "env-cell:a:b"], dropped);
    }

    /// <summary>
    /// The model rings things on the page it is sending the user to, which no inventory describes
    /// yet. Conventional names pass; inventions still do not.
    /// </summary>
    [Fact]
    public void Conventional_anchors_pass_unverified_when_the_destination_is_not_on_screen_yet()
    {
        var onScreen = new[] { "deployments-product-list" };

        var (accepted, dropped) = HighlightResolver.Filter(
            [T("env-cell:auth-api:staging"), T("something-made-up")],
            onScreen,
            destinationUnseen: true);

        Assert.Equal(["env-cell:auth-api:staging"], accepted.Select(a => a.Anchor));
        Assert.Equal(["something-made-up"], dropped);
    }

    [Fact]
    public void Without_an_inventory_conventional_anchors_are_trusted()
    {
        var (accepted, _) = HighlightResolver.Filter([T("service-row:x")], onScreen: null, destinationUnseen: false);
        Assert.Single(accepted);

        (accepted, _) = HighlightResolver.Filter([T("service-row:x")], onScreen: [], destinationUnseen: false);
        Assert.Single(accepted);
    }

    [Fact]
    public void Duplicates_and_blanks_are_removed_and_the_count_is_capped()
    {
        var many = Enumerable.Range(0, 20).Select(i => T($"service-row:s{i}")).ToList();
        many.Insert(0, T("service-row:s0"));
        many.Insert(0, T("  "));

        var (accepted, dropped) = HighlightResolver.Filter(many, onScreen: null, destinationUnseen: false);

        Assert.Equal(HighlightResolver.MaxTargets, accepted.Count);
        Assert.Equal(accepted.Count, accepted.Select(a => a.Anchor).Distinct().Count());
        Assert.Equal(20 - HighlightResolver.MaxTargets, dropped.Count);
    }

    [Fact]
    public void Labels_are_trimmed_and_kept_short()
    {
        var (accepted, _) = HighlightResolver.Filter(
            [T("service-row:x", "  Here  "), T("service-row:y", new string('a', 100)), T("service-row:z", "   ")],
            onScreen: null, destinationUnseen: false);

        Assert.Equal("Here", accepted[0].Label);
        Assert.Equal(61, accepted[1].Label!.Length);
        Assert.Null(accepted[2].Label);
    }

    // ── Which environments a question names ──────────────────────────────────────────────────────

    [Theory]
    [InlineData("what version is on staging?", "staging")]
    [InlineData("What's in Production right now", "production")]
    [InlineData("compare dev and prod", "dev,prod")]
    [InlineData("what version is auth-api", "")]
    public void Finds_the_environments_a_message_names(string message, string expected)
    {
        var envs = new[] { "dev", "staging", "prod", "production" };
        var found = EnvironmentMentions.Find(message, envs);
        Assert.Equal(expected, string.Join(",", found));
    }

    /// <summary>"prod" must not fire on "product", nor "dev" on "developer".</summary>
    [Fact]
    public void Environment_matches_are_whole_words()
    {
        var found = EnvironmentMentions.Find("which product does the developer own", ["prod", "dev"]);
        Assert.Empty(found);
    }

    [Fact]
    public void No_message_means_no_environments()
    {
        Assert.Empty(EnvironmentMentions.Find(null, ["prod"]));
        Assert.Empty(EnvironmentMentions.Find("  ", ["prod"]));
    }

    // ── Am I already there? ──────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("/deployments/mpt", "/deployments/mpt", true)]
    [InlineData("/deployments/mpt/", "/deployments/mpt", true)]
    [InlineData("/deployments/mpt?tab=activity", "/deployments/mpt", true)]
    [InlineData("/deployments/mpt", "/deployments/mpt?tab=activity&env=prod", true)]
    [InlineData("/Deployments/MPT", "/deployments/mpt", true)]
    [InlineData("/deployments/mpt", "/deployments/mpt/auth-api", false)]
    [InlineData("/deployments", "/deployments/mpt", false)]
    [InlineData(null, "/deployments", false)]
    [InlineData("/deployments", null, false)]
    public void Route_match_ignores_query_and_trailing_slash_but_not_segments(string? current, string? route, bool expected)
        => Assert.Equal(expected, RouteMatch.IsOn(current, route));
}
