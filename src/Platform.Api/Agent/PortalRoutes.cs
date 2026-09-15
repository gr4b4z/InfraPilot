namespace Platform.Api.Agent;

/// <summary>
/// Builds links into the portal. The single source of truth for what a URL to a thing looks like.
/// </summary>
/// <remarks>
/// This exists because the model used to be handed URL *templates* in its prompt and asked to fill
/// them in. It produced <c>/deployments/mpt-extension-adobe</c> — the service name in the product
/// slot — because a service link needs both and the template only showed one. Templates cannot
/// express that; code can. Every link the assistant emits now comes from here, and tool results
/// carry their own <c>url</c> so there is nothing left to construct.
///
/// Paths are relative, matching the routes in <c>App.tsx</c>. The client resolves them against its
/// own origin, so the same answer is correct on any installation.
/// </remarks>
public static class PortalRoutes
{
    public static string Deployments() => "/deployments";

    /// <summary>A product's deployment state matrix — every service against every environment.</summary>
    public static string Product(string product) => $"/deployments/{Slug(product)}";

    /// <summary>A product's deployment activity, optionally filtered.</summary>
    public static string ProductActivity(string product, string? environment = null, string? time = null)
    {
        var url = $"{Product(product)}?tab=activity";
        if (!string.IsNullOrWhiteSpace(environment)) url += $"&env={Uri.EscapeDataString(environment.Trim())}";
        if (!string.IsNullOrWhiteSpace(time)) url += $"&atime={Uri.EscapeDataString(time.Trim())}";
        return url;
    }

    /// <summary>
    /// One service. Needs the product as well — services live under their product, and this is the
    /// link that was being built wrong.
    /// </summary>
    public static string Service(string product, string service) =>
        $"/deployments/{Slug(product)}/{Slug(service)}";

    public static string ServiceHistory(string product, string service) =>
        $"{Service(product, service)}/history";

    public static string DeployEvent(Guid id) => $"/deployments/events/{id}";

    public static string Promotions() => "/promotions";

    public static string Promotion(Guid id) => $"/promotions/{id}";

    public static string Rollbacks() => "/rollbacks";

    public static string ReleaseNotes() => "/release-notes";

    public static string ReleaseNotesForProduct(string product) => $"/release-notes/{Slug(product)}";

    public static string Requests() => "/requests";

    public static string Request(Guid id) => $"/requests/{id}";

    public static string Approvals() => "/approvals";

    public static string Catalog() => "/catalog";

    public static string CatalogItem(string slug) => $"/catalog/{Slug(slug)}";

    public static string WorkItem(string service, string key) =>
        $"/work-items/{Slug(service)}/{Uri.EscapeDataString(key.Trim())}";

    public static string Analytics() => "/analytics";

    public static string Artifacts() => "/artifacts";

    public static string Webhooks() => "/webhooks";

    public static string Settings(string? tab = null) =>
        string.IsNullOrWhiteSpace(tab) ? "/settings" : $"/settings/{Slug(tab)}";

    /// <summary>
    /// Product and service names are already lowercase hyphenated slugs by convention, but they
    /// arrive from ingest and from the model, so they are normalised and escaped rather than
    /// trusted — a stray space or slash would otherwise build a link to the wrong route entirely.
    /// </summary>
    private static string Slug(string value) =>
        Uri.EscapeDataString(value.Trim().Trim('/'));
}
