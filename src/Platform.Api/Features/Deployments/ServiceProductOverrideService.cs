using Microsoft.EntityFrameworkCore;
using Platform.Api.Features.Deployments.Models;
using Platform.Api.Infrastructure;
using Platform.Api.Infrastructure.Auth;
using Platform.Api.Infrastructure.Persistence;

namespace Platform.Api.Features.Deployments;

/// <summary>
/// Decides which product a service's entities belong to, and moves history when the answer changes.
///
/// <para>Every create path that takes product from the caller — deploy ingest, manual deploy, build
/// registration, external promotion create — asks <see cref="ResolveAsync"/> first. When an admin has
/// configured a <see cref="ServiceProductOverride"/> for the service, the stored product is the
/// admin's, not the sender's. Without a matching row the sender's value passes through untouched, so
/// the overwhelming majority of traffic is unaffected.</para>
///
/// <para><b>One resolution point, not four.</b> The alternative — each feature reading the table for
/// itself — is how a build ends up filed under a different product than the deploy event for the same
/// version, which is the exact failure this table exists to prevent. Promotion candidates created by
/// the ingest hooks inherit product from the build/deploy row they are derived from, so they need no
/// resolution of their own.</para>
/// </summary>
public class ServiceProductOverrideService
{
    private readonly PlatformDbContext _db;
    private readonly ICurrentUser _user;
    private readonly ILogger<ServiceProductOverrideService> _logger;

    /// <summary>
    /// Request-lifetime memo of the whole table, mirroring how <c>UserPreferencesService</c> memoises
    /// hidden products. The table holds one row per service being migrated — tens, not thousands — and
    /// an ingest resolves once, so loading it whole is cheaper than a query per lookup and lets the
    /// most-specific-match rule be plain C# rather than SQL that has to mean the same thing on two
    /// providers. Scoped lifetime means a settings change is picked up by the next request.
    /// </summary>
    private List<ServiceProductOverride>? _memo;

    public ServiceProductOverrideService(
        PlatformDbContext db, ICurrentUser user, ILogger<ServiceProductOverrideService> logger)
    {
        _db = db;
        _user = user;
        _logger = logger;
    }

    /// <summary>
    /// Outcome of a lookup. <see cref="Product"/> is always the product to store — the sender's value
    /// when nothing matched. <see cref="Applied"/> is the row that changed the answer, or null when the
    /// sender was already right (including when a row matched but agreed with the payload), which is
    /// what callers key their "redirected" reporting off.
    /// </summary>
    public record Resolution(string Product, string SentProduct, ServiceProductOverride? Applied)
    {
        public bool Overridden => Applied is not null;
    }

    /// <summary>
    /// Resolves the product for <paramref name="service"/> given the <paramref name="sentProduct"/> the
    /// caller supplied. Both are trimmed; matching is case-insensitive, so a sender that switches to
    /// "SWO-Extension-MSCSP" keeps hitting the row an admin wrote as "swo-extension-mscsp".
    /// <para>A blank service can't be mapped — there is nothing to key on — so it passes through; the
    /// endpoint validators reject it moments later anyway.</para>
    /// </summary>
    public async Task<Resolution> ResolveAsync(
        string? sentProduct, string? service, CancellationToken ct = default)
    {
        var sent = (sentProduct ?? "").Trim();
        var svc = (service ?? "").Trim();
        if (svc.Length == 0) return new Resolution(sent, sent, null);

        var rows = await LoadAsync(ct);
        if (rows.Count == 0) return new Resolution(sent, sent, null);

        var forService = rows.Where(r => Eq(r.Service, svc)).ToList();
        if (forService.Count == 0) return new Resolution(sent, sent, null);

        // Most specific wins: a row naming the sending product beats the catch-all, so one service can
        // be redirected for a single misconfigured pipeline without touching the others.
        var match =
            forService.FirstOrDefault(r => r.FromProduct is not null && Eq(r.FromProduct, sent))
            ?? forService.FirstOrDefault(r => r.FromProduct is null);
        if (match is null) return new Resolution(sent, sent, null);

        var resolved = match.Product.Trim();

        // Ordinal, not case-insensitive: a row whose target differs from the payload only by casing IS
        // a rewrite worth performing (it converges the stored spelling on the admin's) and worth
        // reporting. Only an exact match counts as "the sender was already right".
        if (string.Equals(resolved, sent, StringComparison.Ordinal))
            return new Resolution(resolved, sent, null);

        _logger.LogInformation(
            "Service product override: {Service} sent as {SentProduct}, stored under {Product} (override {OverrideId})",
            LogSanitizer.Clean(svc), LogSanitizer.Clean(sent), LogSanitizer.Clean(resolved), match.Id);

        return new Resolution(resolved, sent, match);
    }

    /// <summary>Convenience wrapper for callers that only need the product to store.</summary>
    public async Task<string> ResolveProductAsync(
        string? sentProduct, string? service, CancellationToken ct = default)
        => (await ResolveAsync(sentProduct, service, ct)).Product;

    private async Task<List<ServiceProductOverride>> LoadAsync(CancellationToken ct)
        => _memo ??= await _db.ServiceProductOverrides.AsNoTracking().ToListAsync(ct);

    private static bool Eq(string? a, string? b)
        => string.Equals((a ?? "").Trim(), (b ?? "").Trim(), StringComparison.OrdinalIgnoreCase);

    // ── Configuration (admin) ────────────────────────────────────────────────

    /// <summary>All configured overrides, ordered by service then sending product, for the settings list.</summary>
    public async Task<List<ServiceProductOverride>> ListAsync(CancellationToken ct = default)
        => await _db.ServiceProductOverrides.AsNoTracking()
            .OrderBy(o => o.Service).ThenBy(o => o.FromProduct)
            .ToListAsync(ct);

    public async Task<ServiceProductOverride?> GetAsync(Guid id, CancellationToken ct = default)
        => await _db.ServiceProductOverrides.AsNoTracking().FirstOrDefaultAsync(o => o.Id == id, ct);

    /// <summary>
    /// An override plus how many entities sit on each side of it. <paramref name="Stored"/> counts
    /// deploy events and builds already filed under the target product for this service;
    /// <paramref name="Stranded"/> counts those still filed elsewhere.
    /// </summary>
    public record OverrideWithCounts(ServiceProductOverride Override, int Stored, int Stranded);

    /// <summary>
    /// The settings list with its two indicator counts. <c>Stored</c> answers "is this row doing
    /// anything" — a mapping that stays at zero is almost always spelling the service differently than
    /// the sender does, which is otherwise invisible until somebody notices the deploys never moved.
    /// <c>Stranded</c> answers "is history still split", i.e. whether a remap has anything to do.
    /// <para>Two grouped counts per row, on a page an admin opens deliberately. The scoping is the
    /// simple reading — everything under <c>FromProduct</c>, or everything not already on target for a
    /// catch-all — so for a service that also has a more specific row the number can exceed what a
    /// remap would actually move. <c>ServiceProductRemapService.PreviewAsync</c> is the authority on
    /// that; this is the at-a-glance signal.</para>
    /// </summary>
    public async Task<List<OverrideWithCounts>> ListWithCountsAsync(CancellationToken ct = default)
    {
        var rows = await ListAsync(ct);
        var result = new List<OverrideWithCounts>(rows.Count);

        foreach (var row in rows)
        {
            var lowered = row.Service.Trim().ToLowerInvariant();

            var byProduct = await _db.DeployEvents.AsNoTracking()
                .Where(e => e.Service.ToLower() == lowered)
                .GroupBy(e => e.Product)
                .Select(g => new { Product = g.Key, Count = g.Count() })
                .ToListAsync(ct);

            var buildsByProduct = await _db.Builds.AsNoTracking()
                .Where(b => b.Service.ToLower() == lowered)
                .GroupBy(b => b.Product)
                .Select(g => new { Product = g.Key, Count = g.Count() })
                .ToListAsync(ct);

            var all = byProduct.Concat(buildsByProduct);
            var stored = 0;
            var stranded = 0;
            foreach (var group in all)
            {
                if (Eq(group.Product, row.Product)) stored += group.Count;
                else if (row.FromProduct is null || Eq(group.Product, row.FromProduct)) stranded += group.Count;
            }

            result.Add(new OverrideWithCounts(row, stored, stranded));
        }

        return result;
    }

    /// <summary>
    /// Creates or updates the override for <c>(service, fromProduct)</c>. Upsert rather than
    /// insert-or-fail because correcting a mapping mid-migration is the normal case, not an error — the
    /// admin who typed the wrong target product should be able to fix it in place.
    /// <para>The natural key is enforced here rather than by a unique index: see the note in
    /// <c>PlatformDbContext</c> on why NULL and collation semantics make the index the wrong home for
    /// it. The lookup is case-insensitive on both parts, matching <see cref="ResolveAsync"/>, so
    /// "Api"/"api" cannot become two rows that shadow each other.</para>
    /// <para>Throws <see cref="ArgumentException"/> when service or product is blank, or when the row
    /// would redirect a product onto itself.</para>
    /// </summary>
    public async Task<ServiceProductOverride> UpsertAsync(
        string? service, string? fromProduct, string? product, string? reason, CancellationToken ct = default)
    {
        var svc = (service ?? "").Trim();
        var target = (product ?? "").Trim();
        var from = string.IsNullOrWhiteSpace(fromProduct) ? null : fromProduct.Trim();

        if (svc.Length == 0) throw new ArgumentException("service is required.", nameof(service));
        if (target.Length == 0) throw new ArgumentException("product is required.", nameof(product));
        if (from is not null && Eq(from, target))
            throw new ArgumentException(
                $"fromProduct and product are both '{target}' — the row would redirect the product onto itself.",
                nameof(fromProduct));

        var rows = await _db.ServiceProductOverrides.ToListAsync(ct);
        var existing = rows.FirstOrDefault(o =>
            Eq(o.Service, svc)
            && (o.FromProduct is null ? from is null : from is not null && Eq(o.FromProduct, from)));

        if (existing is null)
        {
            existing = new ServiceProductOverride
            {
                Id = Guid.NewGuid(),
                Service = svc,
                FromProduct = from,
                CreatedAt = DateTimeOffset.UtcNow,
            };
            _db.ServiceProductOverrides.Add(existing);
        }
        else
        {
            // Re-stamp the spellings from this write so the list shows the admin's latest casing.
            existing.Service = svc;
            existing.FromProduct = from;
            existing.UpdatedAt = DateTimeOffset.UtcNow;
        }

        existing.Product = target;
        existing.Reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        existing.UpdatedById = _user.Id;
        existing.UpdatedByName = _user.Name;

        await _db.SaveChangesAsync(ct);
        _memo = null;

        _logger.LogInformation(
            "Service product override saved: {Service} (from {FromProduct}) → {Product} by {Actor}",
            LogSanitizer.Clean(svc), LogSanitizer.Clean(from ?? "*"),
            LogSanitizer.Clean(target), LogSanitizer.Clean(_user.Name));

        return existing;
    }

    /// <summary>
    /// Deletes one override. Entities already stored under the target product stay there — removing the
    /// mapping stops future redirection, it does not undo past ones. Returns null when the row is gone.
    /// </summary>
    public async Task<ServiceProductOverride?> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var row = await _db.ServiceProductOverrides.FirstOrDefaultAsync(o => o.Id == id, ct);
        if (row is null) return null;

        _db.ServiceProductOverrides.Remove(row);
        await _db.SaveChangesAsync(ct);
        _memo = null;

        _logger.LogInformation(
            "Service product override removed: {Service} (from {FromProduct}) → {Product} by {Actor}",
            LogSanitizer.Clean(row.Service), LogSanitizer.Clean(row.FromProduct ?? "*"),
            LogSanitizer.Clean(row.Product), LogSanitizer.Clean(_user.Name));

        return row;
    }
}
