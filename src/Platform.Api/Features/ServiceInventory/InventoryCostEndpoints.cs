using Microsoft.EntityFrameworkCore;
using Platform.Api.Features.ServiceInventory.Documents;
using Platform.Api.Features.ServiceInventory.Models;
using Platform.Api.Infrastructure.Auth;
using Platform.Api.Infrastructure.Persistence;

namespace Platform.Api.Features.ServiceInventory;

/// <summary>
/// What the cost and coverage pages read: cost per product or team, and how much of a month's cost
/// is attributed to services. Everything is read from the stored projections of the current cost
/// snapshots; amounts are an object per currency, as published, and are never added across
/// currencies. Direct and shared cost stay separate.
/// <para>
/// A request takes each month's snapshot id once and reads everything about that month through it,
/// so a refresh during the request never mixes two copies. Projected rows of a copy that stopped
/// being current are pruned; when they are gone, the same rows are built from the stored file.
/// </para>
/// </summary>
public static class InventoryCostEndpoints
{
    public static RouteGroupBuilder MapInventoryCostEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/cost", GetCostAsync);
        group.MapGet("/coverage", GetCoverageAsync);
        return group;
    }

    /// <summary>The current snapshot of every cost month, oldest first.</summary>
    private sealed record Month(string Period, Guid SnapshotId, bool Complete);

    private static async Task<List<Month>> MonthsAsync(InventoryReadService read, PlatformDbContext db, CancellationToken ct)
    {
        var feed = await read.FeedAsync(InventoryFeedTypes.Cost, ct: ct);
        if (feed is null) return [];
        var rows = await db.InventoryFeeds.AsNoTracking()
            .Where(f => f.FeedName == feed.FeedName && f.Period != "" && f.CurrentSnapshotId != null)
            .Select(f => new { f.Period, f.CurrentSnapshotId, f.Complete })
            .ToListAsync(ct);
        return rows.OrderBy(r => r.Period, StringComparer.Ordinal)
            .Select(r => new Month(r.Period, r.CurrentSnapshotId!.Value, r.Complete)).ToList();
    }

    private static SortedDictionary<string, decimal> Sum<T>(IEnumerable<T> rows, Func<T, string> currency, Func<T, decimal> amount) =>
        new(rows.GroupBy(currency).ToDictionary(g => g.Key, g => g.Sum(amount)), StringComparer.Ordinal);

    /// <summary>The currency a month's lists are sorted by: the one with the largest total.</summary>
    private static string? LargestCurrency(IEnumerable<InventoryCostMonth> allSources) =>
        allSources.OrderByDescending(m => m.Total).ThenBy(m => m.Currency, StringComparer.Ordinal).FirstOrDefault()?.Currency;

    private static async Task<(Month? Month, List<Month> Months, IResult? Problem)> ResolveAsync(
        string? period, InventoryReadService read, PlatformDbContext db, CancellationToken ct)
    {
        if (period is not null && !ServiceInventoryOptionsValidator.PeriodPattern().IsMatch(period))
            return (null, [], Results.BadRequest(new { error = "period must be YYYY-MM" }));
        var months = await MonthsAsync(read, db, ct);
        // Without a period: the month of the latest cost file, the same default as a service's cost.
        var latest = (await read.FeedAsync(InventoryFeedTypes.Cost, ct: ct))?.CurrentSnapshotId;
        var month = period is null
            ? months.FirstOrDefault(m => m.SnapshotId == latest) ?? months.LastOrDefault()
            : months.FirstOrDefault(m => m.Period == period);
        return (month, months, null);
    }

    /// <summary>One month's copy, read through the snapshot id the request took at its start.</summary>
    private sealed class Pinned(Month month, InventoryReadService read)
    {
        private (CostDocument Document, InventorySnapshot Snapshot)? _stored;
        private ProjectedRows? _projected;
        private bool _loaded;

        public Month Month => month;

        /// <summary>The stored file, or null when the copy itself has been removed meanwhile.</summary>
        public async Task<(CostDocument Document, InventorySnapshot Snapshot)?> StoredAsync(CancellationToken ct)
        {
            if (_loaded) return _stored;
            _stored = await read.SnapshotDocumentAsync<CostDocument>(month.SnapshotId, ct);
            _loaded = true;
            return _stored;
        }

        /// <summary>
        /// The stored rows; when there are none (pruned after a newer copy replaced this one, or the
        /// file simply has none), the rows the projector builds from the file. Null: the copy is gone.
        /// </summary>
        public async Task<List<T>?> RowsAsync<T>(IQueryable<T> stored, Func<ProjectedRows, IReadOnlyList<T>> pick, CancellationToken ct)
        {
            var rows = await stored.ToListAsync(ct);
            if (rows.Count > 0) return rows;
            if (await StoredAsync(ct) is not { } file) return null;
            _projected ??= InventoryProjector.Project(month.SnapshotId, InventoryFeedTypes.Cost, file.Document);
            return pick(_projected).ToList();
        }
    }

    private static async Task<FeedFreshness> FreshnessAsync(
        string? requestedPeriod, Month? month, InventorySnapshot? snapshot,
        InventoryReadService read, bool detailed, CancellationToken ct)
    {
        var feedRow = await read.FeedAsync(InventoryFeedTypes.Cost, ct: ct);
        var period = month?.Period ?? requestedPeriod;
        var monthRow = period is null ? null : await read.FeedAsync(InventoryFeedTypes.Cost, period, ct);
        var stateRow = InventoryReadService.CostStateRow(feedRow, monthRow, requestedPeriod is not null);
        return InventoryReadService.Freshness(stateRow, snapshot, InventoryFeedTypes.Cost, detailed);
    }

    /// <summary>What a pinned copy was removed under; the request starts over once, then gives up.</summary>
    private static IResult Changed() =>
        Results.Json(new { error = "The cost data changed while it was being read. Try again." }, statusCode: StatusCodes.Status503ServiceUnavailable);

    private static string Norm(string? id) => (id ?? "").Trim();
    private static bool Same(string? a, string? b) => string.Equals(Norm(a), Norm(b), StringComparison.OrdinalIgnoreCase);

    private static async Task<IResult> GetCostAsync(
        string? period, string? product, string? team,
        InventoryReadService read, PlatformDbContext db, ICurrentUser user, CancellationToken ct) =>
        await CostAsync(period, product, team, read, db, user, ct)
        ?? await CostAsync(period, product, team, read, db, user, ct)
        ?? Changed();

    /// <summary>Null when the month's copy was removed during the request.</summary>
    private static async Task<IResult?> CostAsync(
        string? period, string? product, string? team,
        InventoryReadService read, PlatformDbContext db, ICurrentUser user, CancellationToken ct)
    {
        var (month, months, problem) = await ResolveAsync(period, read, db, ct);
        if (problem is not null) return problem;

        // Ids are compared trimmed and without regard to case, everywhere: the catalog and the cost
        // file are published separately and need not agree on spelling. The catalog's spelling is shown.
        var catalog = await read.DocumentAsync<CatalogDocument>(await read.FeedAsync(InventoryFeedTypes.Catalog, ct: ct), ct);
        var catalogProducts = (catalog?.Document.Products ?? [])
            .Where(p => Norm(p.Id).Length > 0)
            .GroupBy(p => Norm(p.Id), StringComparer.OrdinalIgnoreCase)
            .Select(g => (Id: g.Key, Name: g.First().Name ?? g.Key))
            .ToList();
        var catalogTeams = (catalog?.Document.Teams ?? [])
            .Select(t => Norm(t.Id)).Where(t => t.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var productNames = catalogProducts.ToDictionary(p => p.Id, p => p.Name, StringComparer.OrdinalIgnoreCase);
        var monthList = months.Select(m => new { m.Period, m.Complete });
        // What the page's product-or-team picker offers: everything the catalog names.
        var options = new
        {
            products = catalogProducts.Select(p => new { id = p.Id, name = p.Name }).OrderBy(p => p.name, StringComparer.OrdinalIgnoreCase).ToList(),
            teams = catalogTeams.Order(StringComparer.Ordinal).ToList(),
        };

        var scopeKind = !string.IsNullOrWhiteSpace(product) ? "product" : !string.IsNullOrWhiteSpace(team) ? "team" : null;
        var asked = Norm(scopeKind == "product" ? product : team);
        var inCatalog = scopeKind == "product"
            ? catalogProducts.Where(p => Same(p.Id, asked)).Select(p => p.Id).FirstOrDefault()
            : catalogTeams.FirstOrDefault(t => Same(t, asked));
        var scopeId = inCatalog ?? asked;
        var scope = scopeKind is null ? null : new
        {
            kind = scopeKind,
            id = scopeId,
            name = scopeKind == "product" ? productNames.GetValueOrDefault(scopeId, scopeId) : scopeId,
            // Whether the catalog knows it at all — a page for a typo says so rather than showing zeros.
            known = inCatalog is not null,
        };

        if (month is null)
        {
            var none = await FreshnessAsync(period, null, null, read, user.IsAdmin, ct);
            return Results.Ok(new { period = (string?)null, months = monthList, options, scope, freshness = none });
        }

        var pinned = new Pinned(month, read);
        var all = await pinned.RowsAsync(
            db.InventoryCostServices.AsNoTracking().Where(r => r.SnapshotId == month.SnapshotId), p => p.CostServices, ct);
        var monthRows = await pinned.RowsAsync(
            db.InventoryCostMonths.AsNoTracking().Where(m => m.SnapshotId == month.SnapshotId), p => p.CostMonths, ct);
        var snapshot = await db.InventorySnapshots.AsNoTracking().Where(s => s.Id == month.SnapshotId)
            .Select(s => new InventorySnapshot
            {
                Id = s.Id, FeedType = s.FeedType, Period = s.Period, Complete = s.Complete,
                GeneratedAt = s.GeneratedAt, DocumentVersion = s.DocumentVersion, FetchedAt = s.FetchedAt,
            })
            .FirstOrDefaultAsync(ct);
        if (all is null || monthRows is null || snapshot is null) return null;

        var monthTotals = monthRows.Where(m => m.Source == "").ToList();
        var sortBy = LargestCurrency(monthTotals);
        var freshness = await FreshnessAsync(period, month, snapshot, read, user.IsAdmin, ct);

        var header = new
        {
            period = month.Period,
            complete = month.Complete,
            generatedAt = snapshot.GeneratedAt,
            catalogVersion = snapshot.DocumentVersion,
            // The whole month, for saying "nothing is attributed yet" before showing zeros.
            month = new
            {
                total = Sum(monthTotals, m => m.Currency, m => m.Total),
                direct = Sum(monthTotals, m => m.Currency, m => m.Direct),
                shared = Sum(monthTotals, m => m.Currency, m => m.Shared),
                unallocated = Sum(monthTotals, m => m.Currency, m => m.Unallocated),
            },
        };

        if (scope is null)
        {
            object Group(Func<InventoryCostService, string> key, IEnumerable<string> known, Func<string, string> name) =>
                // The catalog's ids first, so its spelling is the one a group is listed under.
                known.Concat(all.Select(r => Norm(key(r)))).Where(k => k.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)
                    .Select(id =>
                    {
                        var rows = all.Where(r => Same(key(r), id)).ToList();
                        return new
                        {
                            id,
                            name = name(id),
                            services = rows.Select(r => r.ServiceId).Distinct().Count(),
                            direct = Sum(rows, r => r.Currency, r => r.Direct),
                            shared = Sum(rows, r => r.Currency, r => r.Shared),
                            total = Sum(rows, r => r.Currency, r => r.Total),
                            sort = rows.Where(r => r.Currency == sortBy).Sum(r => r.Total),
                        };
                    })
                    .OrderByDescending(g => g.sort).ThenByDescending(g => g.services).ThenBy(g => g.id, StringComparer.Ordinal)
                    .Select(g => new { g.id, g.name, g.services, g.direct, g.shared, g.total })
                    .ToList();

            return Results.Ok(new
            {
                header.period, header.complete, header.generatedAt, header.catalogVersion, header.month,
                months = monthList,
                options,
                scope,
                products = Group(r => r.Product, catalogProducts.Select(p => p.Id), id => productNames.GetValueOrDefault(id, id)),
                teams = Group(r => r.Team, catalogTeams, id => id),
                freshness,
            });
        }

        bool InScope(string rowProduct, string rowTeam) => Same(scopeKind == "product" ? rowProduct : rowTeam, scopeId);
        var scoped = all.Where(r => InScope(r.Product, r.Team)).ToList();

        // Which shared resources a service's shared cost comes from: only in the file itself. An id
        // the file repeats counts once, its first entry, as in the projected rows.
        if (await pinned.StoredAsync(ct) is not { } file) return null;
        var sharedFrom = file.Document.Services
            .GroupBy(s => s.Id.Trim().ToLowerInvariant())
            .ToDictionary(g => g.Key, g => g.First().SharedFrom.Select(f => f.Shared).ToList());

        // Where each service's own page is: the product it is deployed under in the portal, which
        // need not be the product the cost file names. A service never deployed has no page to go to.
        var serviceIds = scoped.Select(r => r.ServiceId).Distinct().ToList();
        var deployed = serviceIds.Count == 0 ? [] : await db.DeployEvents.AsNoTracking()
            .Where(e => serviceIds.Contains(e.Service.ToLower()))
            .Select(e => new { e.Product, e.Service }).Distinct().ToListAsync(ct);
        object? Deployments(string id, string costProduct) => deployed
            .Where(d => string.Equals(d.Service, id, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(d => Same(d.Product, costProduct)).ThenBy(d => d.Product, StringComparer.Ordinal)
            .Select(d => new { product = d.Product, service = d.Service })
            .FirstOrDefault();

        var services = scoped.GroupBy(r => r.ServiceId)
            .Select(g => new
            {
                id = g.Key,
                product = g.First().Product,
                productName = productNames.GetValueOrDefault(Norm(g.First().Product), g.First().Product),
                team = g.First().Team,
                direct = Sum(g, r => r.Currency, r => r.Direct),
                shared = Sum(g, r => r.Currency, r => r.Shared),
                total = Sum(g, r => r.Currency, r => r.Total),
                sharedFrom = sharedFrom.GetValueOrDefault(g.Key, []),
                deployments = Deployments(g.Key, g.First().Product),
                sort = g.Where(r => r.Currency == sortBy).Sum(r => r.Total),
            })
            .OrderByDescending(s => s.sort).ThenBy(s => s.id, StringComparer.Ordinal)
            .Select(s => new { s.id, s.product, s.productName, s.team, s.direct, s.shared, s.total, s.sharedFrom, s.deployments })
            .ToList();

        // The trend: this scope in every month, one row per month and currency. The database narrows
        // the rows; the same comparison as above decides.
        var snapshotIds = months.Select(m => m.SnapshotId).ToList();
        var lowered = scopeId.ToLowerInvariant();
        var trendRows = (await (scopeKind == "product"
                    ? db.InventoryCostServices.AsNoTracking().Where(r => snapshotIds.Contains(r.SnapshotId) && r.Product.Trim().ToLower() == lowered)
                    : db.InventoryCostServices.AsNoTracking().Where(r => snapshotIds.Contains(r.SnapshotId) && r.Team.Trim().ToLower() == lowered))
                .Select(r => new { r.SnapshotId, r.Product, r.Team, r.Currency, r.Direct, r.Shared })
                .ToListAsync(ct))
            .Where(r => InScope(r.Product, r.Team))
            .Select(r => (r.SnapshotId, r.Currency, r.Direct, r.Shared))
            .ToList();
        // Read after the rows: a month that still has its month rows now had its service rows then.
        var projected = (await db.InventoryCostMonths.AsNoTracking()
            .Where(m => snapshotIds.Contains(m.SnapshotId)).Select(m => m.SnapshotId).Distinct().ToListAsync(ct)).ToHashSet();
        foreach (var other in months.Where(m => !projected.Contains(m.SnapshotId)))
        {
            if (other.SnapshotId == month.SnapshotId)
            {
                trendRows.AddRange(scoped.Select(r => (r.SnapshotId, r.Currency, r.Direct, r.Shared)));
                continue;
            }
            // Its rows are gone (or it never had any): from the file. A copy removed meanwhile was
            // replaced by a newer one of the same month; the month is then drawn without this scope.
            var rows = await new Pinned(other, read).RowsAsync(
                db.InventoryCostServices.AsNoTracking().Where(r => r.SnapshotId == other.SnapshotId), p => p.CostServices, ct);
            if (rows is null) return null;
            trendRows.RemoveAll(r => r.SnapshotId == other.SnapshotId);
            trendRows.AddRange(rows.Where(r => InScope(r.Product, r.Team)).Select(r => (r.SnapshotId, r.Currency, r.Direct, r.Shared)));
        }
        var trend = months.Select(m =>
        {
            var rows = trendRows.Where(r => r.SnapshotId == m.SnapshotId).ToList();
            return new
            {
                m.Period,
                m.Complete,
                direct = Sum(rows, r => r.Currency, r => r.Direct),
                shared = Sum(rows, r => r.Currency, r => r.Shared),
            };
        });

        return Results.Ok(new
        {
            header.period, header.complete, header.generatedAt, header.catalogVersion, header.month,
            months = monthList,
            options,
            scope,
            totals = new
            {
                direct = Sum(scoped, r => r.Currency, r => r.Direct),
                shared = Sum(scoped, r => r.Currency, r => r.Shared),
                total = Sum(scoped, r => r.Currency, r => r.Total),
            },
            services,
            trend,
            freshness,
        });
    }

    private static async Task<IResult> GetCoverageAsync(
        string? period, InventoryReadService read, PlatformDbContext db, ICurrentUser user, CancellationToken ct) =>
        await CoverageAsync(period, read, db, user, ct)
        ?? await CoverageAsync(period, read, db, user, ct)
        ?? Changed();

    /// <summary>Null when the month's copy was removed during the request.</summary>
    private static async Task<IResult?> CoverageAsync(
        string? period, InventoryReadService read, PlatformDbContext db, ICurrentUser user, CancellationToken ct)
    {
        var (month, months, problem) = await ResolveAsync(period, read, db, ct);
        if (problem is not null) return problem;
        var monthList = months.Select(m => new { m.Period, m.Complete });
        if (month is null)
        {
            var none = await FreshnessAsync(period, null, null, read, user.IsAdmin, ct);
            return Results.Ok(new { period = (string?)null, months = monthList, freshness = none });
        }

        // Sources' row counts and the shared resources no service is bound to are only in the file
        // itself: the same copy the rows below belong to.
        var pinned = new Pinned(month, read);
        if (await pinned.StoredAsync(ct) is not { } file) return null;
        var document = file.Document;
        var freshness = await FreshnessAsync(period, month, file.Snapshot, read, user.IsAdmin, ct);

        var parts = await pinned.RowsAsync(
            db.InventoryCostMonths.AsNoTracking().Where(m => m.SnapshotId == month.SnapshotId), p => p.CostMonths, ct);
        var unallocated = await pinned.RowsAsync(
            db.InventoryCostUnallocated.AsNoTracking().Where(u => u.SnapshotId == month.SnapshotId), p => p.CostUnallocated, ct);
        if (parts is null || unallocated is null) return null;

        var snapshotIds = months.Select(m => m.SnapshotId).ToList();
        var trendRows = (await db.InventoryCostMonths.AsNoTracking()
                .Where(m => snapshotIds.Contains(m.SnapshotId) && m.Source == "")
                .Select(m => new { m.SnapshotId, m.Currency, m.Total, m.Direct, m.Shared })
                .ToListAsync(ct))
            .Select(m => (m.SnapshotId, m.Currency, m.Total, m.Direct, m.Shared))
            .ToList();
        var projected = trendRows.Select(r => r.SnapshotId).ToHashSet();
        foreach (var other in months.Where(m => !projected.Contains(m.SnapshotId)))
        {
            var rows = other.SnapshotId == month.SnapshotId
                ? parts
                : await new Pinned(other, read).RowsAsync(
                    db.InventoryCostMonths.AsNoTracking().Where(m => m.SnapshotId == other.SnapshotId), p => p.CostMonths, ct);
            if (rows is null) return null;
            trendRows.AddRange(rows.Where(m => m.Source == "").Select(m => (m.SnapshotId, m.Currency, m.Total, m.Direct, m.Shared)));
        }

        return Results.Ok(new
        {
            period = month.Period,
            complete = month.Complete,
            months = monthList,
            // One entry per currency: the parts `total` is made of, never added across currencies.
            currencies = parts.Where(p => p.Source == "").OrderByDescending(p => p.Total).ThenBy(p => p.Currency, StringComparer.Ordinal)
                .Select(p => new
                {
                    currency = p.Currency,
                    total = p.Total,
                    direct = p.Direct,
                    shared = p.Shared,
                    sharedUnsplit = p.SharedUnsplit,
                    untagged = p.Untagged ?? 0m,
                    unknownTag = p.UnknownTag ?? 0m,
                    // The largest unattributed amounts, by resource group and summed per scope.
                    largestGroups = unallocated.Where(u => u.Currency == p.Currency && u.Amount > 0)
                        .OrderByDescending(u => u.Amount).Take(10)
                        .Select(u => new { source = u.Source, scope = u.Scope, group = u.ResourceGroup, amount = u.Amount }),
                    largestScopes = unallocated.Where(u => u.Currency == p.Currency)
                        .GroupBy(u => new { u.Source, u.Scope })
                        .Select(g => new { source = g.Key.Source, scope = g.Key.Scope, amount = g.Sum(u => u.Amount) })
                        .Where(g => g.amount > 0).OrderByDescending(g => g.amount).Take(10),
                }),
            sources = parts.Where(p => p.Source != "").OrderBy(p => p.Source, StringComparer.Ordinal).ThenBy(p => p.Currency, StringComparer.Ordinal)
                .Select(p => new
                {
                    source = p.Source,
                    currency = p.Currency,
                    total = p.Total,
                    attributed = p.Direct + p.Shared,
                    rows = document.Sources.FirstOrDefault(s => s.Id == p.Source)?.Rows,
                    scopes = document.Sources.FirstOrDefault(s => s.Id == p.Source)?.Scopes,
                }),
            unsplitShared = document.Shared.Where(s => s.Unsplit is { Count: > 0 })
                .Select(s => new { id = s.Id, unsplit = s.Unsplit }),
            trend = months.SelectMany(m => trendRows.Where(r => r.SnapshotId == m.SnapshotId)
                .Select(r => new { m.Period, m.Complete, currency = r.Currency, total = r.Total, attributed = r.Direct + r.Shared })),
            freshness,
        });
    }
}
