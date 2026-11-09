using Microsoft.EntityFrameworkCore;
using Platform.Api.Features.Builds.Models;
using Platform.Api.Features.Deployments;
using Platform.Api.Features.Deployments.Models;
using Platform.Api.Infrastructure.Persistence;

namespace Platform.Api.Features.Builds;

/// <summary>
/// Versions whose artifacts were removed from the container registry (see <see cref="RetiredVersion"/>).
/// The registry cleanup retires them right after it untags their images, and restores them if it
/// puts a tag back. Rollbacks read the set and never offer or target a retired version.
/// </summary>
public class RetiredVersionService
{
    private readonly PlatformDbContext _db;
    private readonly ServiceProductOverrideService _productOverrides;
    private readonly ILogger<RetiredVersionService> _logger;

    public RetiredVersionService(
        PlatformDbContext db,
        ServiceProductOverrideService productOverrides,
        ILogger<RetiredVersionService> logger)
    {
        _db = db;
        _productOverrides = productOverrides;
        _logger = logger;
    }

    /// <summary>Thrown when an item names a product the calling key may not act for.</summary>
    public class ProductNotAllowedException(string product)
        : Exception($"This key may not act for product '{product}'.")
    {
        public string Product { get; } = product;
    }

    public static List<string> Validate(RetireVersionsDto? dto)
    {
        var errors = new List<string>();
        var items = dto?.Items;
        if (items is null || items.Count == 0)
        {
            errors.Add("'items' must hold at least one version");
            return errors;
        }
        if (items.Count > RetireVersionsDto.MaxItems)
            errors.Add($"'items' holds {items.Count} entries; the limit is {RetireVersionsDto.MaxItems} per call");
        for (var i = 0; i < items.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(items[i].Service)) errors.Add($"items[{i}]: 'service' is required");
            if (string.IsNullOrWhiteSpace(items[i].Version)) errors.Add($"items[{i}]: 'version' is required");
            if ((items[i].Version?.Length ?? 0) > 200) errors.Add($"items[{i}]: 'version' is longer than 200 characters");
        }
        if ((dto!.Reason?.Length ?? 0) > 500) errors.Add("'reason' is longer than 500 characters");
        return errors;
    }

    /// <summary>
    /// Retires every item, idempotently. An item without a product is retired under every product
    /// its service is known under (deploy events or builds). An item whose service is not known at
    /// all is counted as <see cref="RetireVersionsResultDto.Unknown"/> and not stored.
    /// </summary>
    public Task<RetireVersionsResultDto> RetireAsync(
        RetireVersionsDto dto, string retiredBy, IReadOnlyCollection<string> allowedProducts, CancellationToken ct = default) =>
        RetireCoreAsync(dto, retiredBy, allowedProducts, retryOnConflict: true, ct);

    private async Task<RetireVersionsResultDto> RetireCoreAsync(
        RetireVersionsDto dto, string retiredBy, IReadOnlyCollection<string> allowedProducts, bool retryOnConflict, CancellationToken ct)
    {
        var keys = await ResolveKeysAsync(dto.Items!, allowedProducts, ct);
        var unknown = keys.Count(k => k.Product is null);
        var wanted = keys.Where(k => k.Product is not null)
            .Select(k => (Product: k.Product!, k.Service, k.Version))
            .Distinct()
            .ToList();

        var existing = await ExistingAsync(wanted, ct);
        var now = DateTimeOffset.UtcNow;
        var reason = string.IsNullOrWhiteSpace(dto.Reason) ? null : dto.Reason.Trim();
        var added = 0;
        foreach (var key in wanted)
        {
            if (existing.Contains(key)) continue;
            _db.RetiredVersions.Add(new RetiredVersion
            {
                Id = Guid.NewGuid(),
                Product = key.Product,
                Service = key.Service,
                Version = key.Version,
                Reason = reason,
                RetiredBy = retiredBy,
                RetiredAt = now,
            });
            added++;
        }

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException) when (added > 0 && retryOnConflict)
        {
            // A concurrent call retired some of the same versions (unique index). Start over once from
            // what is stored now: the outcome is the same set, retired once.
            foreach (var entry in _db.ChangeTracker.Entries<RetiredVersion>().Where(e => e.State == EntityState.Added).ToList())
                entry.State = EntityState.Detached;
            return await RetireCoreAsync(dto, retiredBy, allowedProducts, retryOnConflict: false, ct);
        }

        _logger.LogInformation(
            "Retired {Added} version(s) ({Already} already retired, {Unknown} unknown) by {By}: {Reason}",
            added, wanted.Count - added, unknown, retiredBy, reason);
        return new RetireVersionsResultDto(added, wanted.Count - added, unknown);
    }

    /// <summary>Un-retires the items (product optional, as for retiring). Idempotent.</summary>
    public async Task<RestoreVersionsResultDto> RestoreAsync(
        RetireVersionsDto dto, IReadOnlyCollection<string> allowedProducts, CancellationToken ct = default)
    {
        var restored = 0;
        var notRetired = 0;
        foreach (var item in dto.Items!)
        {
            var service = item.Service!.Trim();
            var version = item.Version!.Trim();
            var product = string.IsNullOrWhiteSpace(item.Product)
                ? null
                : await _productOverrides.ResolveProductAsync(item.Product.Trim(), service, ct);
            if (product is not null) EnsureAllowed(product, allowedProducts);

            var rows = await _db.RetiredVersions
                .Where(r => r.Service.ToLower() == service.ToLower() && r.Version == version
                         && (product == null || r.Product == product))
                .ToListAsync(ct);
            // Without a product, a scoped key only restores what it may act for.
            if (product is null && allowedProducts.Count > 0)
                rows = rows.Where(r => allowedProducts.Contains(r.Product, StringComparer.OrdinalIgnoreCase)).ToList();

            if (rows.Count == 0) { notRetired++; continue; }
            _db.RetiredVersions.RemoveRange(rows);
            restored += rows.Count;
        }
        await _db.SaveChangesAsync(ct);
        return new RestoreVersionsResultDto(restored, notRetired);
    }

    public async Task<List<RetiredVersionDto>> ListAsync(string? product, string? service, int limit, CancellationToken ct = default)
    {
        var query = _db.RetiredVersions.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(product)) query = query.Where(r => r.Product.ToLower() == product.ToLower());
        if (!string.IsNullOrWhiteSpace(service)) query = query.Where(r => r.Service.ToLower() == service.ToLower());
        return await query
            .OrderByDescending(r => r.RetiredAt)
            .Take(limit)
            .Select(r => new RetiredVersionDto(r.Id, r.Product, r.Service, r.Version, r.Reason, r.RetiredBy, r.RetiredAt))
            .ToListAsync(ct);
    }

    public Task<bool> IsRetiredAsync(string product, string service, string version, CancellationToken ct = default) =>
        _db.RetiredVersions.AsNoTracking()
            .AnyAsync(r => r.Product == product && r.Service == service && r.Version == version, ct);

    // ---------------------------------------------------------------------

    private record ResolvedKey(string? Product, string Service, string Version);

    private async Task<List<ResolvedKey>> ResolveKeysAsync(
        List<RetireVersionItemDto> items, IReadOnlyCollection<string> allowedProducts, CancellationToken ct)
    {
        // Products per service, from deploy history and the build registry, for items without one.
        // Service names are matched case-insensitively; the stored spelling is kept.
        var needLookup = items.Where(i => string.IsNullOrWhiteSpace(i.Product))
            .Select(i => i.Service!.Trim().ToLowerInvariant())
            .Distinct()
            .ToList();
        var known = new Dictionary<string, List<(string Product, string Service)>>(StringComparer.OrdinalIgnoreCase);
        if (needLookup.Count > 0)
        {
            var fromEvents = await _db.DeployEvents.AsNoTracking()
                .Where(e => needLookup.Contains(e.Service.ToLower()))
                .Select(e => new { e.Product, e.Service })
                .Distinct()
                .ToListAsync(ct);
            var fromBuilds = await _db.Builds.AsNoTracking()
                .Where(b => needLookup.Contains(b.Service.ToLower()))
                .Select(b => new { b.Product, b.Service })
                .Distinct()
                .ToListAsync(ct);
            foreach (var row in fromEvents.Concat(fromBuilds))
            {
                if (!known.TryGetValue(row.Service, out var list)) known[row.Service] = list = new();
                if (!list.Contains((row.Product, row.Service))) list.Add((row.Product, row.Service));
            }
        }

        var keys = new List<ResolvedKey>();
        foreach (var item in items)
        {
            var service = item.Service!.Trim();
            var version = item.Version!.Trim();
            if (!string.IsNullOrWhiteSpace(item.Product))
            {
                var product = await _productOverrides.ResolveProductAsync(item.Product.Trim(), service, ct);
                EnsureAllowed(item.Product.Trim(), allowedProducts);
                keys.Add(new ResolvedKey(product, service, version));
                continue;
            }

            if (!known.TryGetValue(service, out var matches) || matches.Count == 0)
            {
                keys.Add(new ResolvedKey(null, service, version));
                continue;
            }
            foreach (var (product, storedService) in matches)
            {
                // A product-scoped key only retires under the products it may act for.
                if (allowedProducts.Count > 0 && !allowedProducts.Contains(product, StringComparer.OrdinalIgnoreCase)) continue;
                keys.Add(new ResolvedKey(product, storedService, version));
            }
        }
        return keys;
    }

    private async Task<HashSet<(string Product, string Service, string Version)>> ExistingAsync(
        List<(string Product, string Service, string Version)> wanted, CancellationToken ct)
    {
        var result = new HashSet<(string, string, string)>();
        foreach (var group in wanted.GroupBy(w => (w.Product, w.Service)))
        {
            var versions = group.Select(g => g.Version).ToList();
            var stored = await _db.RetiredVersions.AsNoTracking()
                .Where(r => r.Product == group.Key.Product && r.Service == group.Key.Service && versions.Contains(r.Version))
                .Select(r => r.Version)
                .ToListAsync(ct);
            foreach (var v in stored) result.Add((group.Key.Product, group.Key.Service, v));
        }
        return result;
    }

    private static void EnsureAllowed(string product, IReadOnlyCollection<string> allowedProducts)
    {
        if (allowedProducts.Count > 0 && !allowedProducts.Contains(product, StringComparer.OrdinalIgnoreCase))
            throw new ProductNotAllowedException(product);
    }
}

/// <summary>Keeps retired versions out of rollback candidate lists.</summary>
public static class RetiredVersionQueryExtensions
{
    public static IQueryable<DeployEvent> ExcludingRetiredVersions(this IQueryable<DeployEvent> query, PlatformDbContext db) =>
        query.Where(e => !db.RetiredVersions.Any(r => r.Product == e.Product && r.Service == e.Service && r.Version == e.Version));
}
