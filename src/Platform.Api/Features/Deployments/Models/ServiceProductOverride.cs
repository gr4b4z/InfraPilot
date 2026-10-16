namespace Platform.Api.Features.Deployments.Models;

/// <summary>
/// Admin-supplied mapping that decides which product a service's entities belong to, overruling the
/// product the sender put in the payload. One row says "anything for service <c>X</c> lands in
/// product <c>Y</c>" — applied at create time to deploy events, build registrations and externally
/// created promotion candidates.
///
/// <para><b>Why the sender is not trusted here.</b> Product and service arrive as free text on every
/// ingest, and a pipeline mid-migration keeps posting the product it was configured with years ago.
/// The result is one service split across two products: half its deploy history under the old name,
/// half under the new one, promotion policies matching neither. Correcting the pipelines is the real
/// fix, but they are owned by the teams being migrated and land one at a time — this table is how the
/// platform stays correct in the meantime, in one place an admin controls.</para>
///
/// <para><b>Scoping.</b> <see cref="FromProduct"/> is the sending product this row applies to;
/// <c>null</c> makes it a catch-all for the service, whatever was posted. The most specific match
/// wins: a row naming the sender's product beats the catch-all. That is what lets a service name
/// that is genuinely ambiguous across products ("api") be redirected for one sender only, while a
/// name that is unique platform-wide ("swo-extension-mscsp") needs a single catch-all row.</para>
///
/// <para><b>Forward-only.</b> Applying an override rewrites entities as they arrive; rows already
/// stored keep the product they were ingested with. History is moved separately and deliberately —
/// see <see cref="ServiceProductOverrideService.PreviewRemapAsync"/>.</para>
/// </summary>
public class ServiceProductOverride
{
    public Guid Id { get; set; }

    /// <summary>The service name as senders spell it. Matched case-insensitively.</summary>
    public string Service { get; set; } = "";

    /// <summary>
    /// Only rewrite when the sender posted this product. Null = catch-all: rewrite whatever product
    /// was sent. Matched case-insensitively.
    /// </summary>
    public string? FromProduct { get; set; }

    /// <summary>The product the service's entities are stored under.</summary>
    public string Product { get; set; } = "";

    /// <summary>Optional note from the admin ("MPT migration wave 3"), shown on the settings list.</summary>
    public string? Reason { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? UpdatedAt { get; set; }

    /// <summary>Stable id (oid/sub) of the admin who last wrote this row.</summary>
    public string UpdatedById { get; set; } = "";

    /// <summary>Display name of the admin who last wrote this row.</summary>
    public string UpdatedByName { get; set; } = "";
}
