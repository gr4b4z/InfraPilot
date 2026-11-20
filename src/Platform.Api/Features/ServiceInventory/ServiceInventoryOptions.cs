using System.Text.RegularExpressions;

namespace Platform.Api.Features.ServiceInventory;

/// <summary>
/// Where the service inventory reads its artifacts from (ADR-0004). Bound from the
/// <c>ServiceInventory</c> section. Credential values are never written here: they come from the
/// secret configuration (environment variables, a key vault provider) under the same keys, e.g.
/// <c>ServiceInventory__Credentials__registry__Password</c>.
/// </summary>
public class ServiceInventoryOptions
{
    public const string SectionName = "ServiceInventory";

    /// <summary>Every host the inventory may call. HTTPS only; matched case-insensitively.</summary>
    public List<string> AllowedHosts { get; set; } = [];

    /// <summary>Largest response body read, per request.</summary>
    public long MaxResponseBytes { get; set; } = 20 * 1024 * 1024;

    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>Named credentials, referenced by <see cref="InventoryFeedOptions.Credential"/>.</summary>
    public Dictionary<string, InventoryCredentialOptions> Credentials { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    public List<InventoryFeedOptions> Feeds { get; set; } = [];

    public bool IsHostAllowed(string host) =>
        AllowedHosts.Any(h => string.Equals(h.Trim(), host, StringComparison.OrdinalIgnoreCase));
}

public static class InventoryFeedTypes
{
    public const string Catalog = "catalog";
    public const string Status = "status";
    public const string Cost = "cost";

    public static readonly string[] All = [Catalog, Status, Cost];
}

public static class InventorySourceKinds
{
    public const string Oci = "oci";
    public const string Https = "https";
}

public static class InventoryCredentialKinds
{
    public const string None = "none";
    public const string Bearer = "bearer";
    public const string Basic = "basic";
    public const string Header = "header";
    public const string Query = "query";
}

public class InventoryCredentialOptions
{
    /// <summary><c>none</c>, <c>bearer</c>, <c>basic</c>, <c>header</c> or <c>query</c>.</summary>
    public string Kind { get; set; } = InventoryCredentialKinds.None;

    /// <summary>Bearer token.</summary>
    public string? Token { get; set; }

    /// <summary>Basic user name (for a registry: the token's name).</summary>
    public string? Username { get; set; }

    /// <summary>Basic password.</summary>
    public string? Password { get; set; }

    /// <summary>Header name for <c>header</c>.</summary>
    public string? HeaderName { get; set; }

    /// <summary>Header value for <c>header</c>; the query string (e.g. a SAS token) for <c>query</c>.</summary>
    public string? Value { get; set; }
}

public class InventoryFeedOptions
{
    public string Name { get; set; } = "";

    /// <summary><c>catalog</c>, <c>status</c> or <c>cost</c>.</summary>
    public string Type { get; set; } = "";

    /// <summary>Name of an entry in <see cref="ServiceInventoryOptions.Credentials"/>, or null.</summary>
    public string? Credential { get; set; }

    public int PollMinutes { get; set; } = 15;

    public InventorySourceOptions Source { get; set; } = new();
}

public class InventorySourceOptions
{
    /// <summary><c>oci</c> or <c>https</c>.</summary>
    public string Kind { get; set; } = "";

    // ── oci ──
    public string? Registry { get; set; }
    public string? Repository { get; set; }
    public string Tag { get; set; } = "latest";
    /// <summary>The layer's <c>org.opencontainers.image.title</c>, e.g. <c>catalog.json</c>.</summary>
    public string? File { get; set; }
    /// <summary>
    /// The registry's token service when it is on another host (its challenge's <c>realm</c>). The
    /// feed's credential is sent to the registry host and to this host only; a challenge naming any
    /// other host is refused, even if that host is on the allowlist for another feed.
    /// </summary>
    public string? AuthHost { get; set; }

    // ── https ──
    public string? Url { get; set; }

    /// <summary>Cost only: how closed months are found and fetched once.</summary>
    public ClosedMonthsOptions? ClosedMonths { get; set; }
}

public class ClosedMonthsOptions
{
    /// <summary>First month (YYYY-MM) to look for.</summary>
    public string From { get; set; } = "";

    /// <summary>https only: the URL of one month, with <c>{month}</c> for YYYY-MM.</summary>
    public string? UrlTemplate { get; set; }
}

/// <summary>
/// Checks the configuration once. An invalid configuration disables the inventory (the sync
/// service does nothing and logs why); it never stops the portal from starting.
/// </summary>
public static partial class ServiceInventoryOptionsValidator
{
    [GeneratedRegex(@"^\d{4}-(0[1-9]|1[0-2])$")]
    public static partial Regex PeriodPattern();

    public static IReadOnlyList<string> Validate(ServiceInventoryOptions options)
    {
        var errors = new List<string>();

        foreach (var (name, credential) in options.Credentials)
        {
            var kind = credential.Kind?.Trim().ToLowerInvariant();
            switch (kind)
            {
                case InventoryCredentialKinds.None:
                    break;
                case InventoryCredentialKinds.Bearer:
                    if (string.IsNullOrEmpty(credential.Token)) errors.Add($"credential '{name}': a bearer credential needs Token");
                    break;
                case InventoryCredentialKinds.Basic:
                    if (string.IsNullOrEmpty(credential.Username)) errors.Add($"credential '{name}': a basic credential needs Username");
                    break;
                case InventoryCredentialKinds.Header:
                    if (string.IsNullOrWhiteSpace(credential.HeaderName)) errors.Add($"credential '{name}': a header credential needs HeaderName");
                    break;
                case InventoryCredentialKinds.Query:
                    if (string.IsNullOrEmpty(credential.Value)) errors.Add($"credential '{name}': a query credential needs Value");
                    break;
                default:
                    errors.Add($"credential '{name}': unknown kind '{credential.Kind}'");
                    break;
            }
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var types = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var feed in options.Feeds)
        {
            var label = string.IsNullOrWhiteSpace(feed.Name) ? "(unnamed)" : feed.Name;
            if (string.IsNullOrWhiteSpace(feed.Name)) errors.Add("a feed has no Name");
            else if (!names.Add(feed.Name)) errors.Add($"feed '{label}': the name is used twice");

            if (!InventoryFeedTypes.All.Contains(feed.Type, StringComparer.OrdinalIgnoreCase))
                errors.Add($"feed '{label}': unknown type '{feed.Type}'");
            else if (!types.Add(feed.Type))
                errors.Add($"feed '{label}': only one {feed.Type.ToLowerInvariant()} feed is allowed");

            if (feed.PollMinutes < 1) errors.Add($"feed '{label}': PollMinutes must be at least 1");

            InventoryCredentialOptions? credential = null;
            if (!string.IsNullOrWhiteSpace(feed.Credential)
                && !options.Credentials.TryGetValue(feed.Credential, out credential))
                errors.Add($"feed '{label}': credential '{feed.Credential}' is not configured");

            var source = feed.Source;
            switch (source.Kind?.Trim().ToLowerInvariant())
            {
                case InventorySourceKinds.Oci:
                    if (string.IsNullOrWhiteSpace(source.Registry)) errors.Add($"feed '{label}': an oci source needs Registry");
                    else CheckHost(options, label, source.Registry, errors);
                    if (string.IsNullOrWhiteSpace(source.Repository)) errors.Add($"feed '{label}': an oci source needs Repository");
                    if (string.IsNullOrWhiteSpace(source.Tag)) errors.Add($"feed '{label}': an oci source needs Tag");
                    if (string.IsNullOrWhiteSpace(source.File)) errors.Add($"feed '{label}': an oci source needs File");
                    if (credential is not null && IsKind(credential, InventoryCredentialKinds.Query))
                        errors.Add($"feed '{label}': a query credential cannot be used with an oci source");
                    if (!string.IsNullOrWhiteSpace(source.AuthHost)) CheckHost(options, label, source.AuthHost.Trim(), errors);
                    break;
                case InventorySourceKinds.Https:
                    CheckUrl(options, label, "Url", source.Url, errors);
                    if (source.ClosedMonths is not null)
                    {
                        if (string.IsNullOrWhiteSpace(source.ClosedMonths.UrlTemplate)
                            || !source.ClosedMonths.UrlTemplate.Contains("{month}", StringComparison.Ordinal))
                            errors.Add($"feed '{label}': an https source finds closed months through a UrlTemplate with {{month}}");
                        else
                            CheckUrl(options, label, "UrlTemplate",
                                source.ClosedMonths.UrlTemplate.Replace("{month}", "2000-01", StringComparison.Ordinal), errors);
                    }
                    break;
                default:
                    errors.Add($"feed '{label}': unknown source kind '{source.Kind}'");
                    break;
            }

            if (source.ClosedMonths is not null)
            {
                if (!string.Equals(feed.Type, InventoryFeedTypes.Cost, StringComparison.OrdinalIgnoreCase))
                    errors.Add($"feed '{label}': only a cost feed has closed months");
                if (!PeriodPattern().IsMatch(source.ClosedMonths.From ?? ""))
                    errors.Add($"feed '{label}': ClosedMonths.From must be YYYY-MM");
            }
        }

        if (options.MaxResponseBytes < 1024) errors.Add("MaxResponseBytes must be at least 1024");
        if (options.TimeoutSeconds < 1) errors.Add("TimeoutSeconds must be at least 1");
        return errors;
    }

    private static bool IsKind(InventoryCredentialOptions c, string kind) =>
        string.Equals(c.Kind?.Trim(), kind, StringComparison.OrdinalIgnoreCase);

    private static void CheckUrl(ServiceInventoryOptions options, string feed, string field, string? url, List<string> errors)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            errors.Add($"feed '{feed}': {field} is not an absolute URL");
            return;
        }
        if (uri.Scheme != Uri.UriSchemeHttps) errors.Add($"feed '{feed}': {field} must use https");
        if (!string.IsNullOrEmpty(uri.UserInfo))
            errors.Add($"feed '{feed}': {field} must not carry a user name or password; use a named credential");
        if (!string.IsNullOrEmpty(uri.Query)) errors.Add($"feed '{feed}': {field} must not carry a query string; put a SAS token in a query credential");
        CheckHost(options, feed, uri.Host, errors);
    }

    private static void CheckHost(ServiceInventoryOptions options, string feed, string host, List<string> errors)
    {
        if (!options.IsHostAllowed(host)) errors.Add($"feed '{feed}': host '{host}' is not in AllowedHosts");
    }
}
