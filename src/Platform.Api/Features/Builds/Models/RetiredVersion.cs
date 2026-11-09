namespace Platform.Api.Features.Builds.Models;

/// <summary>
/// A version of a service whose artifacts are gone from the container registry, typically removed
/// by the registry cleanup (ops-tools <c>deployment/acr</c>). Rolling back to it would deploy an
/// image that can no longer be pulled. The rollback picker leaves it out, and manual, align and
/// last-known-good rollbacks refuse it.
///
/// <para>Keyed by (Product, Service, Version) like a <see cref="Build"/>, but independent of the
/// build registry: most retired versions predate it. Retiring is reversible. When the cleanup
/// restores a tag it un-retires the version (<c>POST /api/builds/retired/restore</c>).</para>
/// </summary>
public class RetiredVersion
{
    public Guid Id { get; set; }
    public string Product { get; set; } = "";
    public string Service { get; set; } = "";
    public string Version { get; set; } = "";
    /// <summary>Free text from the caller, e.g. "acr cleanup MPT-20257 plan 3c9cff94".</summary>
    public string? Reason { get; set; }
    /// <summary>Who retired it: the API key name or the user's email.</summary>
    public string RetiredBy { get; set; } = "";
    public DateTimeOffset RetiredAt { get; set; } = DateTimeOffset.UtcNow;
}
