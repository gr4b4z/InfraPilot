using Microsoft.EntityFrameworkCore;
using Platform.Api.Features.Deployments.Models;
using Platform.Api.Features.Promotions.Models;
using Platform.Api.Infrastructure.Auth;
using Platform.Api.Infrastructure.Persistence;

namespace Platform.Api.Features.Deployments;

/// <summary>
/// Retiring a service — the soft delete behind <see cref="DeletedService"/>.
///
/// <para>Nothing is erased. A retirement writes one tombstone row, and every list query in
/// Deployments and Promotions filters against it (see <see cref="DeletedServiceQueryExtensions"/>).
/// That is deliberate: services go obsolete during a migration, but their deploy history is still
/// the record of what shipped, and the audit trail behind a promotion that was approved last year
/// must not change because somebody tidied up the matrix today.</para>
///
/// <para>The filtering lives in the API rather than in each page for the same reason the
/// hidden-products filter does: a page that forgets to apply it would quietly show the service
/// again, and there is no way to notice that from the UI side.</para>
/// </summary>
public class ServiceDeletionService
{
    private readonly PlatformDbContext _db;
    private readonly ICurrentUser _user;
    private readonly ILogger<ServiceDeletionService> _logger;

    public ServiceDeletionService(
        PlatformDbContext db, ICurrentUser user, ILogger<ServiceDeletionService> logger)
    {
        _db = db;
        _user = user;
        _logger = logger;
    }

    /// <summary>What a retirement takes out of view, so the admin sees the consequence, not just the row.</summary>
    public record Impact(int Deployments, int OpenPromotions);

    /// <summary>Currently retired services, newest retirement first. Optionally narrowed to one product.</summary>
    public async Task<List<DeletedService>> ListAsync(string? product = null, CancellationToken ct = default)
    {
        var q = _db.DeletedServices.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(product)) q = q.Where(d => d.Product == product);
        return await q.OrderByDescending(d => d.DeletedAt).ToListAsync(ct);
    }

    /// <summary>
    /// Retires <c>(product, service)</c>, or refreshes the existing tombstone when it is already
    /// retired — re-retiring is how an admin re-hides a service that a stray deploy brought back, so
    /// it has to move <see cref="DeletedService.DeletedAt"/> forward rather than fail as a duplicate.
    /// <para>Throws <see cref="KeyNotFoundException"/> when the pair has no deploy history: a name
    /// that was never deployed is a typo, and silently storing it would hide nothing while looking
    /// like it worked.</para>
    /// </summary>
    public async Task<(DeletedService Row, Impact Impact)> DeleteAsync(
        string product, string service, string? reason, CancellationToken ct = default)
    {
        product = (product ?? "").Trim();
        service = (service ?? "").Trim();

        var deployments = await _db.DeployEvents
            .CountAsync(e => e.Product == product && e.Service == service, ct);
        if (deployments == 0)
            throw new KeyNotFoundException($"No deployments recorded for {product}/{service}.");

        var openPromotions = await _db.PromotionCandidates
            .CountAsync(c => c.Product == product && c.Service == service
                          && (c.Status == PromotionStatus.Pending || c.Status == PromotionStatus.Approved), ct);

        var row = await _db.DeletedServices
            .FirstOrDefaultAsync(d => d.Product == product && d.Service == service, ct);

        if (row is null)
        {
            row = new DeletedService { Id = Guid.NewGuid(), Product = product, Service = service };
            _db.DeletedServices.Add(row);
        }

        row.DeletedAt = DateTimeOffset.UtcNow;
        row.DeletedById = _user.Id;
        row.DeletedByName = _user.Name;
        row.Reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();

        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Service {Product}/{Service} retired by {Actor}; {Deployments} deployment(s) and {Promotions} open promotion(s) leave the lists",
            product, service, _user.Name, deployments, openPromotions);

        return (row, new Impact(deployments, openPromotions));
    }

    /// <summary>
    /// Brings a retired service back. Returns false when it was not retired in the first place, so
    /// the caller can answer 404 rather than pretend it undid something.
    /// </summary>
    public async Task<bool> RestoreAsync(string product, string service, CancellationToken ct = default)
    {
        product = (product ?? "").Trim();
        service = (service ?? "").Trim();

        var row = await _db.DeletedServices
            .FirstOrDefaultAsync(d => d.Product == product && d.Service == service, ct);
        if (row is null) return false;

        _db.DeletedServices.Remove(row);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Service {Product}/{Service} restored by {Actor}", product, service, _user.Name);
        return true;
    }

    /// <summary>
    /// Clears the tombstone for a service that just deployed again — the "…until a new deployment is
    /// sent with it" half of the feature. Staged into the caller's unit of work (no save), because
    /// ingest saves the event and this in one transaction; a revival that committed while the event
    /// behind it rolled back would un-hide a service for no reason.
    ///
    /// <para>A deploy dated at or before the retirement leaves the tombstone alone: backfilling old
    /// history is not evidence the service is alive.</para>
    /// </summary>
    /// <returns>True when a tombstone was staged for removal.</returns>
    public async Task<bool> ReviveOnDeployAsync(
        string product, string service, DateTimeOffset deployedAt, CancellationToken ct = default)
    {
        var row = await _db.DeletedServices
            .FirstOrDefaultAsync(d => d.Product == product && d.Service == service, ct);
        if (row is null || deployedAt <= row.DeletedAt) return false;

        _db.DeletedServices.Remove(row);

        _logger.LogInformation(
            "Service {Product}/{Service} was retired on {DeletedAt} but deployed again at {DeployedAt}; restoring it",
            product, service, row.DeletedAt, deployedAt);

        return true;
    }
}

/// <summary>
/// The one filter that keeps retired services out of the lists. Written as a correlated
/// <c>NOT EXISTS</c> rather than a materialised name set because the identity is the
/// <c>(product, service)</c> pair — a list of service names alone would hide a same-named service
/// belonging to another product.
/// </summary>
public static class DeletedServiceQueryExtensions
{
    public static IQueryable<DeployEvent> ExcludingDeletedServices(
        this IQueryable<DeployEvent> query, PlatformDbContext db) =>
        query.Where(e => !db.DeletedServices.Any(d => d.Product == e.Product && d.Service == e.Service));

    public static IQueryable<PromotionCandidate> ExcludingDeletedServices(
        this IQueryable<PromotionCandidate> query, PlatformDbContext db) =>
        query.Where(c => !db.DeletedServices.Any(d => d.Product == c.Product && d.Service == c.Service));
}
