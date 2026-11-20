using System.IO.Compression;
using Platform.Api.Features.ServiceInventory.Documents;
using Platform.Api.Features.ServiceInventory.Models;

namespace Platform.Api.Features.ServiceInventory;

/// <summary>The query rows of one snapshot.</summary>
public sealed record ProjectedRows(
    IReadOnlyList<InventoryService> Services,
    IReadOnlyList<InventoryCostMonth> CostMonths,
    IReadOnlyList<InventoryCostService> CostServices,
    IReadOnlyList<InventoryCostUnallocated> CostUnallocated)
{
    public static readonly ProjectedRows Empty = new([], [], [], []);
}

/// <summary>
/// Turns one stored file into its query rows. Pure: the same snapshot always gives the same rows,
/// so the rows can be dropped and rebuilt from the raw copy at any time. Amounts are copied as
/// published, one row per currency; nothing here adds currencies together.
/// </summary>
public static class InventoryProjector
{
    /// <summary>Raise when the rows change shape: current snapshots are re-projected on startup.</summary>
    public const int Version = 1;

    public static ProjectedRows Project(Guid snapshotId, string feedType, object document) =>
        (feedType.ToLowerInvariant(), document) switch
        {
            (InventoryFeedTypes.Catalog, CatalogDocument catalog) => ProjectCatalog(snapshotId, catalog),
            (InventoryFeedTypes.Status, StatusDocument) => ProjectedRows.Empty,
            (InventoryFeedTypes.Cost, CostDocument cost) => ProjectCost(snapshotId, cost),
            _ => throw new ArgumentException($"A {feedType} feed cannot project {document.GetType().Name}."),
        };

    private static ProjectedRows ProjectCatalog(Guid snapshotId, CatalogDocument catalog)
    {
        var services = catalog.Services
            .GroupBy(s => s.Id.Trim().ToLowerInvariant())
            .Select(g => new InventoryService
            {
                SnapshotId = snapshotId,
                ServiceId = g.Key,
                Product = g.First().Product ?? "",
                Team = g.First().Team ?? "",
                Lifecycle = g.First().Lifecycle ?? "",
            })
            .ToList();
        return new ProjectedRows(services, [], [], []);
    }

    private static ProjectedRows ProjectCost(Guid snapshotId, CostDocument cost)
    {
        var period = cost.Period;
        var months = new List<InventoryCostMonth>();

        static IEnumerable<string> CurrenciesOf(CostTotals t) =>
            t.Total.Keys.Concat(t.Direct.Keys).Concat(t.Shared.Keys).Concat(t.SharedUnsplit.Keys).Concat(t.Unallocated.Keys);

        var monthCurrencies = CurrenciesOf(cost.Totals)
            .Concat(cost.Unallocated.Untagged.Keys).Concat(cost.Unallocated.UnknownTag.Keys)
            .Distinct().Order(StringComparer.Ordinal).ToList();

        foreach (var currency in monthCurrencies)
        {
            months.Add(new InventoryCostMonth
            {
                SnapshotId = snapshotId, Source = "", Currency = currency, Period = period, Complete = cost.Complete,
                Total = Amount(cost.Totals.Total, currency),
                Direct = Amount(cost.Totals.Direct, currency),
                Shared = Amount(cost.Totals.Shared, currency),
                SharedUnsplit = Amount(cost.Totals.SharedUnsplit, currency),
                Unallocated = Amount(cost.Totals.Unallocated, currency),
                Untagged = Amount(cost.Unallocated.Untagged, currency),
                UnknownTag = Amount(cost.Unallocated.UnknownTag, currency),
            });
        }

        foreach (var source in cost.Sources.Where(s => !string.IsNullOrEmpty(s.Id)).GroupBy(s => s.Id).Select(g => g.First()))
        {
            foreach (var currency in CurrenciesOf(source.Totals).Distinct().Order(StringComparer.Ordinal))
            {
                months.Add(new InventoryCostMonth
                {
                    SnapshotId = snapshotId, Source = source.Id, Currency = currency, Period = period, Complete = cost.Complete,
                    Total = Amount(source.Totals.Total, currency),
                    Direct = Amount(source.Totals.Direct, currency),
                    Shared = Amount(source.Totals.Shared, currency),
                    SharedUnsplit = Amount(source.Totals.SharedUnsplit, currency),
                    Unallocated = Amount(source.Totals.Unallocated, currency),
                    // The file splits unallocated only for all sources together.
                    Untagged = null,
                    UnknownTag = null,
                });
            }
        }

        // Every service of the month, in every currency of the month — zeros included — so
        // "services by cost" lists them all. A currency only the service has is kept too.
        var services = new List<InventoryCostService>();
        foreach (var service in cost.Services.GroupBy(s => s.Id.Trim().ToLowerInvariant()).Select(g => (Id: g.Key, Row: g.First())))
        {
            var currencies = monthCurrencies
                .Concat(service.Row.Direct.Keys).Concat(service.Row.Shared.Keys).Concat(service.Row.Total.Keys)
                .Distinct().Order(StringComparer.Ordinal);
            foreach (var currency in currencies)
            {
                services.Add(new InventoryCostService
                {
                    SnapshotId = snapshotId, ServiceId = service.Id, Currency = currency, Period = period,
                    Product = service.Row.Product ?? "", Team = service.Row.Team ?? "",
                    Direct = Amount(service.Row.Direct, currency),
                    Shared = Amount(service.Row.Shared, currency),
                    Total = Amount(service.Row.Total, currency),
                });
            }
        }

        var unallocated = new List<InventoryCostUnallocated>();
        var ordinal = 0;
        foreach (var scope in cost.Unallocated.ByScope)
        {
            foreach (var (currency, amount) in scope.Cost.OrderBy(c => c.Key, StringComparer.Ordinal))
            {
                unallocated.Add(new InventoryCostUnallocated
                {
                    SnapshotId = snapshotId, Ordinal = ordinal++, Period = period,
                    Source = scope.Source, Scope = scope.Scope, ResourceGroup = scope.Group ?? "",
                    Currency = currency, Amount = amount,
                });
            }
        }

        return new ProjectedRows([], months, services, unallocated);
    }

    private static decimal Amount(Money money, string currency) => money.TryGetValue(currency, out var v) ? v : 0m;
}

public static class Gzip
{
    public static byte[] Compress(byte[] content)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
            gzip.Write(content);
        return output.ToArray();
    }

    public static byte[] Decompress(byte[] compressed)
    {
        using var input = new MemoryStream(compressed);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        gzip.CopyTo(output);
        return output.ToArray();
    }
}
