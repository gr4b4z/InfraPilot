using System.Security.Cryptography;

namespace Platform.Api.Features.ServiceInventory.Sources;

/// <summary>A downloaded artifact file.</summary>
/// <param name="Content">The file's bytes, as published.</param>
/// <param name="Version">What change detection compares next time: a manifest digest, an ETag, a
/// Last-Modified date or the body's hash, prefixed with its kind.</param>
/// <param name="Location">Where it came from, for display. Never carries a credential.</param>
public sealed record ArtifactFile(byte[] Content, string Version, string Location, string SourceKind);

/// <summary>The outcome of asking a source for its file.</summary>
public sealed record FetchResult(bool Changed, string? Version, ArtifactFile? File)
{
    public static FetchResult Unchanged(string? version) => new(false, version, null);
    public static FetchResult Downloaded(ArtifactFile file) => new(true, file.Version, file);
}

/// <summary>
/// Where one artifact file comes from (ADR-0004). The rest of the inventory asks for "the current
/// catalog" and never knows which kind of place answered.
/// </summary>
public interface IArtifactSource
{
    string Kind { get; }

    /// <summary>For display. Never carries a credential.</summary>
    string Location { get; }

    /// <summary>
    /// What this source reads (URL, or registry/repository:tag plus file). A stored version is only
    /// sent back to the source with the same identity, so moving a feed always fetches afresh.
    /// </summary>
    string Identity { get; }

    /// <summary>
    /// Downloads the file only when it changed since <paramref name="lastVersion"/> (null: always).
    /// Throws <see cref="Http.InventoryFetchException"/> on failure.
    /// </summary>
    Task<FetchResult> FetchIfChangedAsync(string? lastVersion, CancellationToken ct);

    /// <summary>
    /// Closed months (YYYY-MM) from <paramref name="from"/> on that may be published and are not in
    /// <paramref name="stored"/>, oldest first.
    /// </summary>
    Task<IReadOnlyList<string>> ListClosedMonthsAsync(string from, ISet<string> stored, DateTimeOffset now, CancellationToken ct);

    /// <summary>The artifact of one closed month.</summary>
    IArtifactSource ForMonth(string period);
}

public static class ContentHash
{
    public static string Sha256(byte[] content) => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(content));
}
