using System.Text.RegularExpressions;

namespace Platform.Api.Agent;

/// <summary>
/// One element on the user's screen the assistant wants ringed, by its <c>data-guide-anchor</c>.
/// </summary>
public class HighlightTarget
{
    [System.Text.Json.Serialization.JsonPropertyName("anchor")]
    public string Anchor { get; set; } = "";

    /// <summary>Short caption shown beside the ring — what this element is, in the user's terms.</summary>
    [System.Text.Json.Serialization.JsonPropertyName("label")]
    public string? Label { get; set; }
}

/// <summary>
/// The <c>data-guide-anchor</c> names the web app gives to elements whose identity is data rather
/// than a fixed control — a row for one service, the cell for one environment. The client renders
/// these from the same pattern, so a change on either side has to be made on both; this is the
/// only place the server spells them.
/// </summary>
/// <remarks>
/// Fixed controls (<c>promotion-approve-button</c>, <c>nav-promotions</c>) are not built here: the
/// client tells the server which of those are on screen on every turn, and the model picks from
/// that list. These builders exist for the case the list cannot cover — the page the user is
/// about to be sent to, whose anchors are not on any screen yet.
/// </remarks>
public static class PortalAnchors
{
    public static string ServiceRow(string service) => $"service-row:{service.Trim()}";

    /// <summary>The version cell of one service in one environment, on the product matrix and the service page alike.</summary>
    public static string EnvCell(string service, string environment) => $"env-cell:{service.Trim()}:{environment.Trim()}";

    /// <summary>A column header on the product matrix.</summary>
    public static string EnvColumn(string environment) => $"env-column:{environment.Trim()}";

    /// <summary>The block of environment cards on a service page.</summary>
    public const string ServiceEnvironments = "service-environments";

    public static string PromotionRow(Guid candidateId) => $"promotion-row:{candidateId}";

    /// <summary>
    /// Prefixes an anchor may carry when it cannot be checked against the live screen. Anything
    /// else the model invents for a page it cannot see is dropped rather than sent to ring nothing.
    /// </summary>
    private static readonly string[] KnownPrefixes =
    [
        "service-row:", "env-cell:", "env-column:", "promotion-row:", "promotion-", "work-item-",
        "nav-", "deployments-", "promotions-", "rollback", "release-notes-", "catalog-", "requests-",
        "settings-", ServiceEnvironments,
    ];

    public static bool FollowsConvention(string anchor) =>
        KnownPrefixes.Any(p => anchor.StartsWith(p, StringComparison.Ordinal));
}

/// <summary>
/// Decides which of the anchors the model asked to ring can actually be rung.
/// </summary>
public static class HighlightResolver
{
    public const int MaxTargets = 12;

    /// <summary>
    /// Keeps the targets that are on screen. When the client sent no inventory, or the user is being
    /// moved to a page whose inventory does not exist yet, targets that follow a known naming
    /// convention are allowed through unverified — the client simply finds nothing if they are wrong.
    /// </summary>
    /// <returns>Accepted targets in the order given, without duplicates, and the anchors dropped.</returns>
    public static (List<HighlightTarget> Accepted, List<string> Dropped) Filter(
        IEnumerable<HighlightTarget> requested,
        IReadOnlyCollection<string>? onScreen,
        bool destinationUnseen)
    {
        var accepted = new List<HighlightTarget>();
        var dropped = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var target in requested)
        {
            var anchor = target.Anchor?.Trim() ?? "";
            if (anchor.Length == 0 || !seen.Add(anchor)) continue;
            if (accepted.Count >= MaxTargets) { dropped.Add(anchor); continue; }

            var present = onScreen is { Count: > 0 } && onScreen.Contains(anchor);
            var unverifiable = onScreen is null or { Count: 0 } || destinationUnseen;

            if (present || (unverifiable && PortalAnchors.FollowsConvention(anchor)))
                accepted.Add(new HighlightTarget { Anchor = anchor, Label = Trim(target.Label) });
            else
                dropped.Add(anchor);
        }

        return (accepted, dropped);
    }

    private static string? Trim(string? label)
    {
        if (string.IsNullOrWhiteSpace(label)) return null;
        var s = label.Trim();
        return s.Length <= 60 ? s : s[..60] + "…";
    }
}

/// <summary>
/// Which environments a message talks about, so "what's on staging" can ring the staging cell and
/// only that one. Matches on the stored environment keys as whole words; a message that names no
/// environment yields none, and the caller decides what "all of them" looks like.
/// </summary>
public static class EnvironmentMentions
{
    public static List<string> Find(string? message, IEnumerable<string> environments)
    {
        if (string.IsNullOrWhiteSpace(message)) return [];
        var lower = message.ToLowerInvariant();

        return environments
            .Where(env => !string.IsNullOrWhiteSpace(env))
            .Where(env => Regex.IsMatch(lower, $@"(?<![a-z0-9]){Regex.Escape(env.ToLowerInvariant())}(?![a-z0-9])"))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}

/// <summary>Whether the user is already on the page a route points at, ignoring its query string.</summary>
public static class RouteMatch
{
    public static bool IsOn(string? currentPath, string? route)
    {
        if (string.IsNullOrWhiteSpace(currentPath) || string.IsNullOrWhiteSpace(route)) return false;
        return Normalize(currentPath).Equals(Normalize(route), StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string path)
    {
        var q = path.IndexOf('?');
        if (q >= 0) path = path[..q];
        return '/' + path.Trim().Trim('/');
    }
}
