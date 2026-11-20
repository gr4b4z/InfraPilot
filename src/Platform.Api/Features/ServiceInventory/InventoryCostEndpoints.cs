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

    private static async Task<FeedFreshness> FreshnessAsync(
        Month? month, InventoryReadService read, PlatformDbContext db, bool detailed, CancellationToken ct)
    {
        var feed = await read.FeedAsync(InventoryFeedTypes.Cost, ct: ct);
        InventorySnapshot? snapshot = null;
        if (month is not null)
        {
            snapshot = await db.InventorySnapshots.AsNoTracking().Where(s => s.Id == month.SnapshotId)
                .Select(s => new InventorySnapshot
                {
                    Id = s.Id, FeedType = s.FeedType, Period = s.Period, Complete = s.Complete,
                    GeneratedAt = s.GeneratedAt, DocumentVersion = s.DocumentVersion, FetchedAt = s.FetchedAt,
                })
                .FirstOrDefaultAsync(ct);
        }
        return InventoryReadService.Freshness(feed, snapshot, InventoryFeedTypes.Cost, detailed);
    }

    private static async Task<IResult> GetCostAsync(
        string? period, string? product, string? team,
        InventoryReadService read, PlatformDbContext db, ICurrentUser user, CancellationToken ct)
    {
        var (month, months, problem) = await ResolveAsync(period, read, db, ct);
        if (problem is not null) return problem;

        var catalog = await read.DocumentAsync<CatalogDocument>(await read.FeedAsync(InventoryFeedTypes.Catalog, ct: ct), ct);
        var productNames = catalog?.Document.Products.ToDictionary(p => p.Id, p => p.Name ?? p.Id, StringComparer.OrdinalIgnoreCase)
                           ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var freshness = await FreshnessAsync(month, read, db, user.IsAdmin, ct);
        var monthList = months.Select(m => new { m.Period, m.Complete });
        // What the page's product-or-team picker offers: everything the catalog names.
        var options = new
        {
            products = catalog?.Document.Products.Select(p => new { id = p.Id, name = p.Name ?? p.Id }).OrderBy(p => p.name, StringComparer.OrdinalIgnoreCase).ToList() ?? [],
            teams = catalog?.Document.Teams.Select(t => t.Id).Order(StringComparer.Ordinal).ToList() ?? [],
        };

        var scopeKind = !string.IsNullOrWhiteSpace(product) ? "product" : !string.IsNullOrWhiteSpace(team) ? "team" : null;
        var scopeId = (scopeKind == "product" ? product : team)?.Trim().ToLowerInvariant();
        var scope = scopeKind is null ? null : new
        {
            kind = scopeKind,
            id = scopeId,
            name = scopeKind == "product" ? productNames.GetValueOrDefault(scopeId!, scopeId!) : scopeId,
            // Whether the catalog knows it at all — a page for a typo says so rather than showing zeros.
            known = scopeKind == "product"
                ? catalog?.Document.Products.Any(p => p.Id == scopeId) ?? false
                : catalog?.Document.Teams.Any(t => t.Id == scopeId) ?? false,
        };

        if (month is null)
            return Results.Ok(new { period = (string?)null, months = monthList, options, scope, freshness });

        var all = await db.InventoryCostServices.AsNoTracking().Where(r => r.SnapshotId == month.SnapshotId).ToListAsync(ct);
        var monthTotals = await db.InventoryCostMonths.AsNoTracking()
            .Where(m => m.SnapshotId == month.SnapshotId && m.Source == "").ToListAsync(ct);
        var sortBy = LargestCurrency(monthTotals);
        var snapshot = await db.InventorySnapshots.AsNoTracking().Where(s => s.Id == month.SnapshotId)
            .Select(s => new { s.GeneratedAt, s.DocumentVersion }).FirstOrDefaultAsync(ct);

        var header = new
        {
            period = month.Period,
            complete = month.Complete,
            generatedAt = snapshot?.GeneratedAt,
            catalogVersion = snapshot?.DocumentVersion,
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
                known.Concat(all.Select(key)).Where(k => k.Length > 0).Distinct(StringComparer.Ordinal)
                    .Select(id =>
                    {
                        var rows = all.Where(r => key(r) == id).ToList();
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
                products = Group(r => r.Product, catalog?.Document.Products.Select(p => p.Id) ?? [], id => productNames.GetValueOrDefault(id, id)),
                teams = Group(r => r.Team, catalog?.Document.Teams.Select(t => t.Id) ?? [], id => id),
                freshness,
            });
        }

        bool InScope(InventoryCostService r) => scopeKind == "product" ? r.Product == scopeId : r.Team == scopeId;
        var scoped = all.Where(InScope).ToList();

        // Which shared resources a service's shared cost comes from: only in the file itself.
        var document = await read.DocumentAsync<CostDocument>(
            await read.FeedAsync(InventoryFeedTypes.Cost, month.Period, ct), ct);
        var sharedFrom = document?.Document.Services.ToDictionary(
                             s => s.Id.Trim().ToLowerInvariant(), s => s.SharedFrom.Select(f => f.Shared).ToList())
                         ?? [];

        var services = scoped.GroupBy(r => r.ServiceId)
            .Select(g => new
            {
                id = g.Key,
                product = g.First().Product,
                productName = productNames.GetValueOrDefault(g.First().Product, g.First().Product),
                team = g.First().Team,
                direct = Sum(g, r => r.Currency, r => r.Direct),
                shared = Sum(g, r => r.Currency, r => r.Shared),
                total = Sum(g, r => r.Currency, r => r.Total),
                sharedFrom = sharedFrom.GetValueOrDefault(g.Key, []),
                sort = g.Where(r => r.Currency == sortBy).Sum(r => r.Total),
            })
            .OrderByDescending(s => s.sort).ThenBy(s => s.id, StringComparer.Ordinal)
            .Select(s => new { s.id, s.product, s.productName, s.team, s.direct, s.shared, s.total, s.sharedFrom })
            .ToList();

        // The trend: this scope in every month, one row per month and currency.
        var snapshotIds = months.Select(m => m.SnapshotId).ToList();
        var trendRows = await (scopeKind == "product"
                ? db.InventoryCostServices.AsNoTracking().Where(r => snapshotIds.Contains(r.SnapshotId) && r.Product == scopeId)
                : db.InventoryCostServices.AsNoTracking().Where(r => snapshotIds.Contains(r.SnapshotId) && r.Team == scopeId))
            .Select(r => new { r.SnapshotId, r.Currency, r.Direct, r.Shared })
            .ToListAsync(ct);
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
        string? period, InventoryReadService read, PlatformDbContext db, ICurrentUser user, CancellationToken ct)
    {
        var (month, months, problem) = await ResolveAsync(period, read, db, ct);
        if (problem is not null) return problem;
        var freshness = await FreshnessAsync(month, read, db, user.IsAdmin, ct);
        var monthList = months.Select(m => new { m.Period, m.Complete });
        if (month is null)
            return Results.Ok(new { period = (string?)null, months = monthList, freshness });

        var parts = await db.InventoryCostMonths.AsNoTracking().Where(m => m.SnapshotId == month.SnapshotId).ToListAsync(ct);
        var unallocated = await db.InventoryCostUnallocated.AsNoTracking().Where(u => u.SnapshotId == month.SnapshotId).ToListAsync(ct);

        // Shared resources no service is bound to: only in the file itself.
        var document = await read.DocumentAsync<CostDocument>(
            await read.FeedAsync(InventoryFeedTypes.Cost, month.Period, ct), ct);

        var snapshotIds = months.Select(m => m.SnapshotId).ToList();
        var trendRows = await db.InventoryCostMonths.AsNoTracking()
            .Where(m => snapshotIds.Contains(m.SnapshotId) && m.Source == "")
            .Select(m => new { m.SnapshotId, m.Currency, m.Total, m.Direct, m.Shared })
            .ToListAsync(ct);

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
                    rows = document?.Document.Sources.FirstOrDefault(s => s.Id == p.Source)?.Rows,
                    scopes = document?.Document.Sources.FirstOrDefault(s => s.Id == p.Source)?.Scopes,
                }),
            unsplitShared = document?.Document.Shared.Where(s => s.Unsplit is { Count: > 0 })
                .Select(s => new { id = s.Id, unsplit = s.Unsplit }) ?? [],
            trend = months.SelectMany(m => trendRows.Where(r => r.SnapshotId == m.SnapshotId)
                .Select(r => new { m.Period, m.Complete, currency = r.Currency, total = r.Total, attributed = r.Direct + r.Shared })),
            freshness,
        });
    }
}
