using System.Text.Json;
using Platform.Api.Features.Builds.Models;
using Platform.Api.Features.Deployments.Models;

namespace Platform.Api.Features.Builds;

/// <summary>
/// The pieces shared by everything that turns a registered <see cref="Build"/> into a promotion
/// candidate — the auto-create hook and the user-facing deploy-a-build endpoint. One home, so the
/// two paths can never drift on what "the candidate for this build" contains.
/// </summary>
public static class BuildPromotions
{
    /// <summary>
    /// The synthetic source environment for candidates created from the build registry. Policies on
    /// <c>build → *</c> edges are expected to set <c>SourceRequiresDeploy = false</c> — nothing is
    /// ever deployed to "build"; it is where builds come from, not somewhere they run.
    /// </summary>
    public const string SourceEnv = "build";

    /// <summary>The reference type carrying the manifest's OCI ref + digest to deploy workflows.</summary>
    public const string ManifestReferenceType = "build-manifest";

    /// <summary>
    /// True when <paramref name="branch"/> matches any pattern. Patterns are full refs compared
    /// ordinally (git refs are case-sensitive), with <c>*</c> matching any run of characters —
    /// enough for <c>refs/heads/main</c> and <c>refs/heads/release/*</c> without inviting regex
    /// into policy configuration.
    /// </summary>
    public static bool BranchMatches(string branch, IReadOnlyList<string> patterns)
        => patterns.Any(p => GlobMatches(branch, p));

    private static bool GlobMatches(string value, string pattern)
    {
        if (!pattern.Contains('*')) return string.Equals(value, pattern, StringComparison.Ordinal);
        var regex = "^" + string.Join(".*", pattern.Split('*').Select(System.Text.RegularExpressions.Regex.Escape)) + "$";
        return System.Text.RegularExpressions.Regex.IsMatch(value, regex);
    }

    /// <summary>
    /// The candidate's change-set references, derived from the build: the manifest's own
    /// <c>references</c> section copied through (D13 — this is what hands the <c>build → staging</c>
    /// edge its work-item gating with zero extra plumbing), plus a <see cref="ManifestReferenceType"/>
    /// reference carrying the OCI ref/digest so the deploy workflow can <c>oras pull</c> the exact
    /// manifest this candidate was created from.
    ///
    /// <para>The manifest parser is deliberately tolerant: each property under <c>references</c> may
    /// be a single object or an array of them, the property name becomes the reference type, and
    /// only recognisable fields are lifted. BuildMetadata is another team's schema — a field this
    /// misses costs a link in the UI, not a failed registration.</para>
    /// </summary>
    public static List<ReferenceDto> BuildReferences(Build build)
    {
        var references = new List<ReferenceDto>();

        if (!string.IsNullOrEmpty(build.ManifestJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(build.ManifestJson);
                if (doc.RootElement.ValueKind == JsonValueKind.Object
                    && doc.RootElement.TryGetProperty("references", out var refs)
                    && refs.ValueKind == JsonValueKind.Object)
                {
                    foreach (var property in refs.EnumerateObject())
                    {
                        var items = property.Value.ValueKind == JsonValueKind.Array
                            ? property.Value.EnumerateArray().ToList()
                            : [property.Value];
                        foreach (var item in items)
                        {
                            if (item.ValueKind != JsonValueKind.Object) continue;
                            references.Add(MapManifestReference(property.Name, item));
                        }
                    }
                }
            }
            catch (JsonException)
            {
                // Unparseable manifest — the registration already stored it verbatim; the candidate
                // just carries no copied references.
            }
        }

        if (!string.IsNullOrEmpty(build.ArtifactRef) || !string.IsNullOrEmpty(build.ArtifactDigest))
        {
            references.Add(new ReferenceDto(
                Type: ManifestReferenceType,
                Key: build.ArtifactRef,
                Revision: build.ArtifactDigest,
                Title: "Build manifest (OCI artifact)"));
        }

        return references;
    }

    private static ReferenceDto MapManifestReference(string type, JsonElement obj) => new(
        Type: type,
        Url: GetString(obj, "url", "href"),
        Provider: GetString(obj, "provider"),
        Key: GetString(obj, "key", "id", "name"),
        Revision: GetString(obj, "revision", "commit", "sha"),
        Title: GetString(obj, "title", "summary") ?? GetString(obj, "branch"));

    private static string? GetString(JsonElement obj, params string[] names)
    {
        foreach (var name in names)
        {
            if (obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
            {
                var s = value.GetString();
                if (!string.IsNullOrWhiteSpace(s)) return s;
            }
        }
        return null;
    }
}
