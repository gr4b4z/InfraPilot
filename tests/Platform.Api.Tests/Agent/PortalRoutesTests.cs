using Platform.Api.Agent;

namespace Platform.Api.Tests.Agent;

/// <summary>
/// Pins the shape of every link the assistant can emit.
///
/// These exist because the model was previously handed URL templates in its prompt and asked to
/// fill them in, and produced <c>/deployments/mkp-extension-adobe</c> — a service name in the
/// product slot. A template cannot express "this one needs both"; a function can, and this is what
/// keeps it that way.
/// </summary>
public class PortalRoutesTests
{
    /// <summary>The exact case that was reported wrong.</summary>
    [Fact]
    public void Service_link_includes_the_product()
    {
        var route = PortalRoutes.Service("mkp-extensions", "mkp-extension-adobe");

        Assert.Equal("/deployments/mkp-extensions/mkp-extension-adobe", route);
        // The shape that was produced before: service name where the product belongs.
        Assert.NotEqual("/deployments/mkp-extension-adobe", route);
    }

    [Fact]
    public void Service_history_hangs_off_the_service()
        => Assert.Equal(
            "/deployments/mkp-extensions/mkp-extension-adobe/history",
            PortalRoutes.ServiceHistory("mkp-extensions", "mkp-extension-adobe"));

    [Fact]
    public void Product_link_is_the_state_matrix()
        => Assert.Equal("/deployments/mkp", PortalRoutes.Product("mkp"));

    [Fact]
    public void Product_activity_carries_the_tab()
        => Assert.Equal("/deployments/mkp?tab=activity", PortalRoutes.ProductActivity("mkp"));

    [Fact]
    public void Product_activity_appends_only_the_filters_given()
    {
        Assert.Equal("/deployments/mkp?tab=activity&env=production",
            PortalRoutes.ProductActivity("mkp", "production"));

        Assert.Equal("/deployments/mkp?tab=activity&env=staging&atime=24h",
            PortalRoutes.ProductActivity("mkp", "staging", "24h"));

        Assert.Equal("/deployments/mkp?tab=activity&atime=today",
            PortalRoutes.ProductActivity("mkp", null, "today"));
    }

    [Fact]
    public void Blank_filters_are_treated_as_absent()
        => Assert.Equal("/deployments/mkp?tab=activity", PortalRoutes.ProductActivity("mkp", "  ", ""));

    [Fact]
    public void Promotion_and_deploy_event_links_use_their_ids()
    {
        var id = Guid.Parse("11111111-2222-3333-4444-555555555555");

        Assert.Equal($"/promotions/{id}", PortalRoutes.Promotion(id));
        Assert.Equal($"/deployments/events/{id}", PortalRoutes.DeployEvent(id));
    }

    [Fact]
    public void Work_item_link_is_scoped_to_its_service()
        => Assert.Equal("/work-items/auth-api/MKP-1234", PortalRoutes.WorkItem("auth-api", "MKP-1234"));

    [Fact]
    public void Release_notes_link_narrows_to_a_product_when_given_one()
    {
        Assert.Equal("/release-notes", PortalRoutes.ReleaseNotes());
        Assert.Equal("/release-notes/mkp", PortalRoutes.ReleaseNotesForProduct("mkp"));
    }

    [Fact]
    public void Settings_link_targets_a_tab_when_given_one()
    {
        Assert.Equal("/settings", PortalRoutes.Settings());
        Assert.Equal("/settings/rollbacks", PortalRoutes.Settings("rollbacks"));
    }

    /// <summary>
    /// Names reach this code from deploy ingest and from the model, so a stray slash must not be
    /// able to bend a link onto a different route.
    /// </summary>
    [Theory]
    [InlineData("/mkp-extensions/", "mkp-extension-adobe", "/deployments/mkp-extensions/mkp-extension-adobe")]
    [InlineData("  mkp  ", "auth-api", "/deployments/mkp/auth-api")]
    [InlineData("mkp", "a/b", "/deployments/mkp/a%2Fb")]
    public void Names_are_trimmed_and_escaped(string product, string service, string expected)
        => Assert.Equal(expected, PortalRoutes.Service(product, service));

    /// <summary>
    /// Every route must be relative. An absolute one would pin the answer to whichever host the
    /// server thinks it is on, which is wrong the moment dev and prod share a corpus of answers.
    /// </summary>
    [Fact]
    public void Every_route_is_relative_to_the_portal_origin()
    {
        var id = Guid.NewGuid();
        string[] routes =
        [
            PortalRoutes.Deployments(),
            PortalRoutes.Product("mkp"),
            PortalRoutes.ProductActivity("mkp", "prod", "today"),
            PortalRoutes.Service("mkp", "auth-api"),
            PortalRoutes.ServiceHistory("mkp", "auth-api"),
            PortalRoutes.DeployEvent(id),
            PortalRoutes.Promotions(),
            PortalRoutes.Promotion(id),
            PortalRoutes.Rollbacks(),
            PortalRoutes.ReleaseNotes(),
            PortalRoutes.ReleaseNotesForProduct("mkp"),
            PortalRoutes.Requests(),
            PortalRoutes.Request(id),
            PortalRoutes.Approvals(),
            PortalRoutes.Catalog(),
            PortalRoutes.CatalogItem("create-repo"),
            PortalRoutes.WorkItem("auth-api", "MKP-1"),
            PortalRoutes.Analytics(),
            PortalRoutes.Artifacts(),
            PortalRoutes.Webhooks(),
            PortalRoutes.Settings("rollbacks"),
        ];

        Assert.All(routes, r =>
        {
            Assert.StartsWith("/", r);
            Assert.DoesNotContain("://", r);
        });
    }
}
