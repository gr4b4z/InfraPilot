namespace Platform.Api.Features.Builds.Models;

/// <summary>
/// One published build, registered by the publishing pipeline via <c>POST /api/builds</c>.
/// Every published build lands here — main, release and feature branches alike — which is what
/// makes "what builds exist, from which branch?" queryable at all. A row is provenance plus the
/// build manifest; it says nothing about deployment (that remains the deploy-event ledger's job).
/// </summary>
public class Build
{
    public Guid Id { get; set; }
    public string Product { get; set; } = "";
    public string Service { get; set; } = "";
    /// <summary>Build number as the pipeline stamped it, e.g. <c>5.0.347-g495d92f0</c>.</summary>
    public string Version { get; set; } = "";
    /// <summary>Full git ref that produced the build, e.g. <c>refs/heads/feature/MPT-1234-x</c>.</summary>
    public string Branch { get; set; } = "";
    public string? CommitSha { get; set; }
    /// <summary>The CI system's run identifier (ADO Build.BuildId). A string: not every CI numbers its runs.</summary>
    public string? BuildId { get; set; }
    public string? BuildUrl { get; set; }
    /// <summary>
    /// The full BuildMetadata document, inline. The registry holds its own copy so it never needs
    /// ACR or storage credentials — the producer pushes, the registry receives.
    /// </summary>
    public string? ManifestJson { get; set; }
    /// <summary>OCI reference of the manifest artifact in ACR, e.g. <c>acr.io/repo/build-metadata:5.0.347</c>.</summary>
    public string? ArtifactRef { get; set; }
    /// <summary>Digest of the manifest artifact — the immutable pointer deploy workflows pull by.</summary>
    public string? ArtifactDigest { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    /// <summary>Set when a re-POST of the same (Product, Service, Version) updated the row in place.</summary>
    public DateTimeOffset? UpdatedAt { get; set; }
}
