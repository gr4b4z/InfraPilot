using System.Text.Json;

namespace Platform.Api.Features.Builds.Models;

// --- Input DTOs ---

/// <summary>
/// Registration payload posted by the publishing pipeline. The manifest rides along inline —
/// the registry is a pure recipient and never fetches from ACR or storage. Idempotent on
/// (Product, Service, Version): a re-POST updates the existing row in place, so pipeline
/// retries are safe.
/// </summary>
public record RegisterBuildDto(
    string Product,
    string Service,
    string Version,
    string Branch,
    string? CommitSha = null,
    string? BuildId = null,
    string? BuildUrl = null,
    // The full BuildMetadata document as JSON. Stored verbatim.
    JsonElement? Manifest = null,
    // OCI reference + digest of the manifest artifact in ACR.
    string? ArtifactRef = null,
    string? ArtifactDigest = null);

// --- Output DTOs ---

/// <summary>List-row projection — everything but the manifest, which can be large.</summary>
public record BuildSummaryDto(
    Guid Id,
    string Product,
    string Service,
    string Version,
    string Branch,
    string? CommitSha,
    string? BuildId,
    string? BuildUrl,
    string? ArtifactRef,
    string? ArtifactDigest,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt);

/// <summary>Detail projection — the summary plus the inline manifest.</summary>
public record BuildDetailDto(
    Guid Id,
    string Product,
    string Service,
    string Version,
    string Branch,
    string? CommitSha,
    string? BuildId,
    string? BuildUrl,
    string? ArtifactRef,
    string? ArtifactDigest,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt,
    JsonElement? Manifest);

// --- Query + facets ---

/// <summary>
/// Every narrowing the registry read surface understands, in one value so the list query and the
/// facet counts cannot drift apart.
///
/// <para><paramref name="Query"/> is the free-text box: a case-insensitive substring matched across
/// product, service, version, branch, commit and CI build id at once, so "aws" finds
/// <c>swo-extension-aws</c> without the reader knowing which column the word lives in. The named
/// fields are the opposite — they exist to *identify*, and each is filled from a known value
/// (a facet pick or a link), so product and service match exactly (case-insensitively) and
/// <paramref name="Version"/> stays exact so a promotion's "built from …" link points at one build.
/// <paramref name="Branch"/> keeps its substring match: "MPT-1234" is how people name a branch.</para>
/// </summary>
public record BuildQuery(
    string? Product = null,
    string? Service = null,
    string? Branch = null,
    string? Version = null,
    string? Query = null,
    // Registration-time window, inclusive of Since and exclusive of Until — "builds cut on the 14th".
    DateTimeOffset? Since = null,
    DateTimeOffset? Until = null);

/// <summary>One value a facet field can take, with how many builds the current view holds for it.</summary>
public record BuildFacetValueDto(string Value, int Count);

/// <summary>
/// The pick lists behind the registry's combo boxes. Each list is counted with every *other*
/// filter applied but not its own — so selecting a product narrows the service and branch lists
/// (which is the useful part) while still showing the other products you could switch to.
/// </summary>
public record BuildFacetsDto(
    List<BuildFacetValueDto> Products,
    List<BuildFacetValueDto> Services,
    List<BuildFacetValueDto> Branches);
