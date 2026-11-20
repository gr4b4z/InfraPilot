using System.Text;
using System.Text.Json.Nodes;
using Platform.Api.Features.ServiceInventory;
using Platform.Api.Features.ServiceInventory.Documents;

namespace Platform.Api.Tests.Features.ServiceInventory;

public class DocumentReaderTests
{
    [Theory]
    [InlineData("catalog", "catalog.json")]
    [InlineData("status", "status.json")]
    [InlineData("cost", "cost-2026-11.json")]
    [InlineData("cost", "cost-2026-10.json")]
    public void The_fixtures_parse(string type, string file)
    {
        var result = DocumentReader.Read(type, Fixture.Bytes(file));
        Assert.True(result.Ok, result.Error);
    }

    [Fact]
    public void The_catalog_keeps_what_the_pages_need()
    {
        var catalog = (CatalogDocument)DocumentReader.Read("catalog", Fixture.Bytes("catalog.json")).Document!;

        Assert.Equal(5, catalog.CatalogVersion);
        var router = catalog.Services.Single(s => s.Id == "order-router");
        Assert.Equal("storefront", router.Product);
        Assert.Equal("aks-shared-01", router.Environments["prod"].Aks!.Cluster);
        var binding = Assert.Single(router.DatabaseBindings());
        Assert.Equal(("postgres", "order-router", "shared-postgres", true), (binding.Kind, binding.Database, binding.Server, binding.Owner));
        // No bindings while the service's own file is missing: an empty list, not a failure.
        Assert.Empty(catalog.Services.Single(s => s.Id == "ledger-api").DatabaseBindings());
    }

    [Fact]
    public void An_unknown_field_is_ignored()
    {
        var node = JsonNode.Parse(Fixture.Bytes("cost-2026-11.json"))!;
        node["a_field_added_later"] = new JsonObject { ["anything"] = 1 };
        node["services"]![0]!["new_detail"] = "x";

        var result = DocumentReader.Read("cost", Encoding.UTF8.GetBytes(node.ToJsonString()));

        Assert.True(result.Ok, result.Error);
    }

    [Fact]
    public void An_old_cost_format_is_rejected_whole()
    {
        var result = DocumentReader.Read("cost", Fixture.Bytes("cost-v1.json"));

        Assert.False(result.Ok);
        Assert.Null(result.Document);
        Assert.Equal(1, result.SchemaVersion);
        Assert.Contains("unsupported schemaVersion 1", result.Error);
    }

    [Fact]
    public void A_newer_format_is_rejected_whole()
    {
        var node = JsonNode.Parse(Fixture.Bytes("catalog.json"))!;
        node["schemaVersion"] = 2;
        var result = DocumentReader.Read("catalog", Encoding.UTF8.GetBytes(node.ToJsonString()));
        Assert.Contains("unsupported schemaVersion 2", result.Error);
    }

    [Fact]
    public void A_key_that_is_not_a_currency_code_is_rejected()
    {
        var node = JsonNode.Parse(Fixture.Bytes("cost-2026-11.json"))!;
        node["totals"]!["total"] = new JsonObject { ["usd"] = 1 };
        Assert.Contains("not a currency code", DocumentReader.Read("cost", Encoding.UTF8.GetBytes(node.ToJsonString())).Error);
    }

    [Fact]
    public void A_cost_period_must_be_a_month()
    {
        var node = JsonNode.Parse(Fixture.Bytes("cost-2026-11.json"))!;
        node["period"] = "November";
        Assert.Contains("period", DocumentReader.Read("cost", Encoding.UTF8.GetBytes(node.ToJsonString())).Error);
    }

    [Fact]
    public void Not_json_is_rejected()
    {
        Assert.Equal("the file is not valid JSON", DocumentReader.Read("status", "<html>"u8.ToArray()).Error);
    }
}

public class InventoryProjectorTests
{
    private static readonly Guid Snapshot = Guid.Parse("00000000-0000-0000-0000-0000000000aa");

    private static ProjectedRows Project(string type, string file) =>
        InventoryProjector.Project(Snapshot, type, DocumentReader.Read(type, Fixture.Bytes(file)).Document!);

    [Fact]
    public void The_catalog_gives_one_row_per_service()
    {
        var rows = Project("catalog", "catalog.json");
        Assert.Equal(["ledger-api", "order-router"], rows.Services.Select(s => s.ServiceId).Order());
        Assert.Equal("platform-team", rows.Services.Single(s => s.ServiceId == "ledger-api").Team);
    }

    [Fact]
    public void A_month_with_everything_unallocated_keeps_every_service_at_zero()
    {
        var rows = Project("cost", "cost-2026-11.json");

        Assert.Equal(2, rows.CostServices.Count);
        Assert.All(rows.CostServices, r => Assert.Equal(("USD", 0m, 0m, 0m), (r.Currency, r.Direct, r.Shared, r.Total)));

        var all = rows.CostMonths.Single(m => m.Source == "");
        Assert.Equal((9876.40m, 9876.40m, 9876.40m, 0m), (all.Total, all.Unallocated, all.Untagged!.Value, all.UnknownTag!.Value));
        var azure = rows.CostMonths.Single(m => m.Source == "azure");
        Assert.Null(azure.Untagged);
        Assert.Null(azure.UnknownTag);

        Assert.Equal([2310.25m, 1254.00m, 866.90m], rows.CostUnallocated.OrderBy(u => u.Ordinal).Select(u => u.Amount));
    }

    [Fact]
    public void Two_currencies_stay_two_rows_and_are_never_added()
    {
        var rows = Project("cost", "cost-2026-10.json");

        var router = rows.CostServices.Where(r => r.ServiceId == "order-router").OrderBy(r => r.Currency).ToList();
        Assert.Equal(["EUR", "USD"], router.Select(r => r.Currency));
        Assert.Equal((205.00m, 0m, 205.00m), (router[0].Direct, router[0].Shared, router[0].Total));
        Assert.Equal((113.40m, 82.50m, 195.90m), (router[1].Direct, router[1].Shared, router[1].Total));
        // A service with no EUR cost still gets a zero EUR row, so every list shows both currencies.
        Assert.Equal(0m, rows.CostServices.Single(r => r.ServiceId == "ledger-api" && r.Currency == "EUR").Total);
        Assert.Equal(["EUR", "USD"], rows.CostMonths.Where(m => m.Source == "").Select(m => m.Currency).Order());
    }

    [Fact]
    public void Projecting_the_same_snapshot_twice_gives_identical_rows()
    {
        var a = Project("cost", "cost-2026-10.json");
        var b = Project("cost", "cost-2026-10.json");

        static string Flatten(ProjectedRows r) => string.Join("|",
            r.CostServices.Select(x => $"{x.ServiceId},{x.Currency},{x.Direct},{x.Shared},{x.Total}")
                .Concat(r.CostMonths.Select(x => $"{x.Source},{x.Currency},{x.Total},{x.Untagged}"))
                .Concat(r.CostUnallocated.Select(x => $"{x.Ordinal},{x.Scope},{x.ResourceGroup},{x.Currency},{x.Amount}")));
        Assert.Equal(Flatten(a), Flatten(b));
    }

    [Fact]
    public void Status_has_no_rows_of_its_own()
    {
        Assert.Same(ProjectedRows.Empty, Project("status", "status.json"));
    }
}
