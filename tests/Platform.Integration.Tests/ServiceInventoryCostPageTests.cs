using System.Net;
using System.Text.Json;

namespace Platform.Integration.Tests;

/// <summary>
/// What the cost and coverage pages read. Seeded with a closed October (two currencies, some cost
/// attributed) and a November to date in which nothing is attributed yet.
/// </summary>
public class ServiceInventoryCostPageTests : IClassFixture<ServiceInventoryEndpointTests.InventoryFactory>
{
    private readonly ServiceInventoryEndpointTests.InventoryFactory _factory;

    public ServiceInventoryCostPageTests(ServiceInventoryEndpointTests.InventoryFactory factory)
    {
        _factory = factory;
        _factory.Seed();
    }

    private async Task<JsonElement> Get(string path)
    {
        var response = await _factory.CreateAuthenticatedClient("user@localhost", "user123").GetAsync("/api/service-inventory" + path);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
    }

    [Fact]
    public async Task Without_a_period_the_month_of_the_latest_file_is_shown_and_all_months_are_listed()
    {
        var body = await Get("/cost");

        Assert.Equal("2026-11", body.GetProperty("period").GetString());
        Assert.False(body.GetProperty("complete").GetBoolean());
        Assert.Equal(["2026-10", "2026-11"], body.GetProperty("months").EnumerateArray().Select(m => m.GetProperty("period").GetString()));
        // Nothing attributed in November: the page can say so from the month's own totals.
        Assert.Equal(9876.40m, body.GetProperty("month").GetProperty("unallocated").GetProperty("USD").GetDecimal());
        Assert.Equal(0m, body.GetProperty("month").GetProperty("direct").GetProperty("USD").GetDecimal());
    }

    [Fact]
    public async Task The_index_lists_every_catalog_product_and_team_also_those_without_services()
    {
        var body = await Get("/cost?period=2026-10");

        var products = body.GetProperty("products").EnumerateArray().ToList();
        Assert.Equal(["payments", "storefront", "identity", "platform"], products.Select(p => p.GetProperty("id").GetString()));
        Assert.Equal("Payments", products[0].GetProperty("name").GetString());
        Assert.Equal(1514.80m, products[0].GetProperty("total").GetProperty("USD").GetDecimal());
        Assert.Equal(0, products[2].GetProperty("services").GetInt32());
        Assert.Empty(products[2].GetProperty("total").EnumerateObject());
        Assert.Contains("search-team", body.GetProperty("teams").EnumerateArray().Select(t => t.GetProperty("id").GetString()));
    }

    [Fact]
    public async Task A_product_gets_direct_and_shared_apart_per_currency_its_services_and_its_trend()
    {
        var body = await Get("/cost?product=Storefront&period=2026-10");

        Assert.Equal("Storefront", body.GetProperty("scope").GetProperty("name").GetString());
        Assert.True(body.GetProperty("scope").GetProperty("known").GetBoolean());
        var totals = body.GetProperty("totals");
        Assert.Equal(113.40m, totals.GetProperty("direct").GetProperty("USD").GetDecimal());
        Assert.Equal(82.50m, totals.GetProperty("shared").GetProperty("USD").GetDecimal());
        Assert.Equal(205.00m, totals.GetProperty("total").GetProperty("EUR").GetDecimal());

        var service = Assert.Single(body.GetProperty("services").EnumerateArray());
        Assert.Equal("order-router", service.GetProperty("id").GetString());
        Assert.Equal("shared-postgres", service.GetProperty("sharedFrom")[0].GetString());

        var trend = body.GetProperty("trend").EnumerateArray().ToList();
        Assert.Equal(["2026-10", "2026-11"], trend.Select(t => t.GetProperty("period").GetString()));
        Assert.True(trend[0].GetProperty("complete").GetBoolean());
        Assert.False(trend[1].GetProperty("complete").GetBoolean());
        Assert.Equal(113.40m, trend[0].GetProperty("direct").GetProperty("USD").GetDecimal());
        Assert.Equal(0m, trend[1].GetProperty("direct").GetProperty("USD").GetDecimal());
    }

    [Fact]
    public async Task A_team_without_services_and_a_name_the_catalog_does_not_know_are_told_apart()
    {
        var empty = await Get("/cost?team=search-team&period=2026-10");
        var unknown = await Get("/cost?team=no-such-team&period=2026-10");

        Assert.True(empty.GetProperty("scope").GetProperty("known").GetBoolean());
        Assert.Empty(empty.GetProperty("services").EnumerateArray());
        Assert.False(unknown.GetProperty("scope").GetProperty("known").GetBoolean());
    }

    [Fact]
    public async Task Coverage_gives_the_parts_of_the_total_per_currency_the_largest_unattributed_and_the_trend()
    {
        var body = await Get("/coverage?period=2026-10");

        var usd = body.GetProperty("currencies").EnumerateArray().Single(c => c.GetProperty("currency").GetString() == "USD");
        var total = usd.GetProperty("total").GetDecimal();
        var parts = new[] { "direct", "shared", "sharedUnsplit", "untagged", "unknownTag" }.Sum(p => usd.GetProperty(p).GetDecimal());
        Assert.Equal(10215.70m, total);
        Assert.Equal(total, parts);
        Assert.Equal("aks-prod-nodes", usd.GetProperty("largestGroups")[0].GetProperty("group").GetString());
        Assert.Equal(4448.60m, usd.GetProperty("largestScopes")[0].GetProperty("amount").GetDecimal());

        Assert.Equal(["azure", "mongodb-atlas"], body.GetProperty("sources").EnumerateArray().Select(s => s.GetProperty("source").GetString()));

        var trend = body.GetProperty("trend").EnumerateArray().Where(t => t.GetProperty("currency").GetString() == "USD").ToList();
        Assert.Equal(1710.70m, trend[0].GetProperty("attributed").GetDecimal());
        Assert.Equal(0m, trend[1].GetProperty("attributed").GetDecimal());
    }

    [Fact]
    public async Task A_malformed_period_is_refused()
    {
        var response = await _factory.CreateAdminClient().GetAsync("/api/service-inventory/coverage?period=2026-13");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
