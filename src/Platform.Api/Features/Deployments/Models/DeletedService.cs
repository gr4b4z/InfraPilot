namespace Platform.Api.Features.Deployments.Models;

/// <summary>
/// A tombstone marking one <c>(Product, Service)</c> pair as retired: the service still has all of
/// its deploy history, but it is dropped from every deployment and promotion list so an obsolete
/// component stops occupying a row in the matrix people read every day.
///
/// <para><b>Why a tombstone and not a flag on the service.</b> There is no service table — a service
/// exists because deploy events mention it. Recording the retirement as its own row is what lets an
/// admin retire something without touching a single historical event, and what makes the decision
/// reversible: delete the row and the service is back, exactly as it was.</para>
///
/// <para><b>Resurrection is automatic.</b> A deploy event whose <c>DeployedAt</c> is later than
/// <see cref="DeletedAt"/> removes this row during ingest — if the pipeline is still deploying the
/// thing, it is not obsolete, whatever an admin concluded earlier. Comparing timestamps rather than
/// clearing on any ingest means backfilling old history does not undo a retirement.</para>
/// </summary>
public class DeletedService
{
    public Guid Id { get; set; }
    public string Product { get; set; } = "";
    public string Service { get; set; } = "";

    /// <summary>When the admin retired it. Also the cutoff a reviving deploy event must beat.</summary>
    public DateTimeOffset DeletedAt { get; set; } = DateTimeOffset.UtcNow;

    public string DeletedById { get; set; } = "";
    public string DeletedByName { get; set; } = "";

    /// <summary>Optional note from the admin ("migrated to billing-api"), shown on the restore list.</summary>
    public string? Reason { get; set; }
}
