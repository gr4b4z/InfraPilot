using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.Api.Features.Guides;

namespace Platform.Api.Tests.Features.Guides;

/// <summary>
/// Guards the authored guide corpus against the two ways it rots: a guide referring to a control
/// the UI no longer has, and a guide referring to another guide that no longer exists.
///
/// The anchor check is the important one. A walkthrough whose anchor has been renamed away fails
/// silently at runtime — the spotlight just cannot find the element — so nothing short of a build
/// failure catches it before a user does.
/// </summary>
public class GuideCorpusTests
{
    private static readonly Lazy<GuideRegistry> Registry = new(() =>
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Guides:Path"] = GuidesDir })
            .Build();
        return new GuideRegistry(new GuideYamlLoader(config, NullLogger<GuideYamlLoader>.Instance));
    });

    private static string RepoRoot
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "InfraPilot.slnx")))
                dir = dir.Parent;
            return dir?.FullName ?? throw new InvalidOperationException("Could not locate the repository root");
        }
    }

    private static string GuidesDir => Path.Combine(RepoRoot, "guides");
    private static string WebSrcDir => Path.Combine(RepoRoot, "src", "Platform.Web", "src");

    [Fact]
    public void Corpus_IsNotEmpty()
        => Assert.NotEmpty(Registry.Value.All);

    [Fact]
    public void Every_Guide_Has_Id_Title_Summary_Route_And_Steps()
    {
        foreach (var guide in Registry.Value.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(guide.Id), $"{guide.SourcePath}: missing id");
            Assert.False(string.IsNullOrWhiteSpace(guide.Title), $"{guide.Id}: missing title");
            Assert.False(string.IsNullOrWhiteSpace(guide.Summary), $"{guide.Id}: missing summary");
            Assert.False(string.IsNullOrWhiteSpace(guide.Route), $"{guide.Id}: missing route");
            Assert.NotEmpty(guide.Steps);
            Assert.All(guide.Steps, s =>
                Assert.False(string.IsNullOrWhiteSpace(s.Text), $"{guide.Id}: a step has no text"));
        }
    }

    [Fact]
    public void Guide_Ids_Are_Unique()
    {
        var duplicates = Registry.Value.All
            .GroupBy(g => g.Id, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        Assert.True(duplicates.Count == 0, $"Duplicate guide ids: {string.Join(", ", duplicates)}");
    }

    [Fact]
    public void Related_Guides_All_Exist()
    {
        var missing = new List<string>();

        foreach (var guide in Registry.Value.All)
            foreach (var related in guide.Related)
                if (Registry.Value.GetById(related) is null)
                    missing.Add($"{guide.Id} → {related}");

        Assert.True(missing.Count == 0, $"Guides reference ids that do not exist: {string.Join(", ", missing)}");
    }

    /// <summary>
    /// Every anchor a guide names must be present in the web source as a `data-guide-anchor`. This
    /// is a text scan rather than a render: it cannot prove the element is reachable, only that the
    /// name still exists — which is exactly the failure mode a rename introduces.
    /// </summary>
    [Fact]
    public void Every_Referenced_Anchor_Exists_In_The_Web_Source()
    {
        var declared = DeclaredAnchors();

        var missing = Registry.Value.All
            .SelectMany(g => g.Steps
                .Where(s => !string.IsNullOrWhiteSpace(s.Anchor))
                .Select(s => (Guide: g.Id, Anchor: s.Anchor!)))
            .Where(x => !declared.Contains(x.Anchor))
            .Select(x => $"{x.Guide} → '{x.Anchor}'")
            .Distinct()
            .ToList();

        Assert.True(
            missing.Count == 0,
            "Guide steps point at data-guide-anchor values that are not in the web source. "
            + "Either the control was renamed or the guide was written ahead of the markup: "
            + string.Join(", ", missing));
    }

    /// <summary>
    /// Every route a guide navigates to must be a real route. Catches a guide that outlives the
    /// page it describes, which would otherwise strand the user on a blank screen.
    /// </summary>
    [Fact]
    public void Every_Referenced_Route_Is_Declared_In_The_Router()
    {
        var appTsx = Path.Combine(WebSrcDir, "App.tsx");
        Assert.True(File.Exists(appTsx), $"Could not find {appTsx}");

        var source = File.ReadAllText(appTsx);
        var declared = Regex.Matches(source, @"path=""([^""]+)""")
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var routes = Registry.Value.All
            .SelectMany(g => new[] { g.Route }.Concat(g.Steps.Select(s => s.Route)))
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Select(r => r!)
            .Distinct();

        var missing = routes.Where(route => !RouteIsDeclared(route, declared)).ToList();

        Assert.True(missing.Count == 0, $"Guides navigate to routes the router does not declare: {string.Join(", ", missing)}");
    }

    /// <summary>
    /// Matches a concrete path against the router's declarations. Nested routes are declared as
    /// relative segments (the settings tabs are "rollbacks" under "/settings"), so a path also
    /// counts as declared when each of its segments appears.
    /// </summary>
    private static bool RouteIsDeclared(string route, HashSet<string> declared)
    {
        if (declared.Contains(route))
            return true;

        var segments = route.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
            return declared.Contains("/");

        return declared.Contains("/" + segments[0])
            && segments.Skip(1).All(segment => declared.Contains(segment));
    }

    /// <summary>All `data-guide-anchor` values present in the web source.</summary>
    private static HashSet<string> DeclaredAnchors()
    {
        var anchors = new HashSet<string>(StringComparer.Ordinal);

        foreach (var file in Directory.EnumerateFiles(WebSrcDir, "*.tsx", SearchOption.AllDirectories))
        {
            var source = File.ReadAllText(file);

            // Literal form: data-guide-anchor="rollbacks-new-button"
            foreach (Match m in Regex.Matches(source, @"data-guide-anchor=""([^""]+)"""))
                anchors.Add(m.Groups[1].Value);

            // Computed form: data-guide-anchor={navAnchor(item.to)} — the values come from routes,
            // so derive them the same way the component does rather than leaving them unverifiable.
            if (source.Contains("data-guide-anchor={navAnchor("))
                foreach (Match m in Regex.Matches(source, @"to:\s*'([^']+)'"))
                    anchors.Add("nav-" + (m.Groups[1].Value.TrimStart('/').Replace('/', '-') is { Length: > 0 } s ? s : "home"));
        }

        return anchors;
    }
}
