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
