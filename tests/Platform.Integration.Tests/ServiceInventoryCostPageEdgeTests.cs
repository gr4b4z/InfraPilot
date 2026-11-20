using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Platform.Api.Features.Deployments.Models;
using Platform.Api.Features.ServiceInventory;
using Platform.Api.Features.ServiceInventory.Models;
using Platform.Api.Infrastructure.Persistence;

namespace Platform.Integration.Tests;

/// <summary>
/// The cost and coverage pages on data that is not tidy: files that disagree on spelling or repeat
/// an id, projections pruned from under a month, a month whose own refresh failed. Each test has its
/// own host, because it changes what is stored.
/// </summary>
public class ServiceInventoryCostPageEdgeTests
{
    private static async Task<JsonElement> Get(ServiceInventoryEndpointTests.InventoryFactory factory, string path, bool admin = false)
    {
        var client = admin ? factory.CreateAdminClient() : factory.CreateAuthenticatedClient("user@localhost", "user123");
        var response = await client.GetAsync("/api/service-inventory" + path);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
    }

    /// <summary>The response with every number in one notation: 0.0 from a file and 0 from a row are the same amount.</summary>
    private static async Task<string> Canonical(ServiceInventoryEndpointTests.InventoryFactory factory, string path)
    {
        static JsonNode? Walk(JsonNode? node) => node switch
        {
            JsonObject o => new JsonObject(o.Select(p => KeyValuePair.Create(p.Key, Walk(p.Value)))),
            JsonArray a => new JsonArray(a.Select(Walk).ToArray()),
            JsonValue v when v.GetValueKind() == JsonValueKind.Number => JsonValue.Create(v.GetValue<decimal>().ToString("G29", System.Globalization.CultureInfo.InvariantCulture)),
            _ => node?.DeepClone(),
        };
        return Walk(JsonNode.Parse((await Get(factory, path)).GetRawText()))!.ToJsonString();
    }

    private static JsonNode Fixture(string name) => JsonNode.Parse(InventoryFiles.Bytes(name))!;
    private static byte[] Bytes(JsonNode node) => Encoding.UTF8.GetBytes(node.ToJsonString());

    [Fact]
    public async Task Files_that_disagree_on_spelling_or_repeat_an_id_are_still_read_as_one_product()
    {
        using var factory = new ServiceInventoryEndpointTests.InventoryFactory();
        // The catalog names the product twice, in two spellings; the cost file spells the product
        // and the team its own way and lists the service twice.
        var catalog = Fixture("catalog.json");
        catalog["products"]!.AsArray().Add(new JsonObject { ["id"] = "Payments", ["name"] = "Payments again" });
        var cost = Fixture("cost-2026-10.json");
        var ledger = cost["services"]!.AsArray().Single(s => s!["id"]!.GetValue<string>() == "ledger-api")!;
        ledger["product"] = " Payments";
        ledger["team"] = "Platform-Team";
        cost["services"]!.AsArray().Add(ledger.DeepClone());
        factory.Sources.Overrides["catalog.json"] = Bytes(catalog);
        factory.Sources.Overrides["cost-2026-10.json"] = Bytes(cost);
        factory.Seed();

        var index = await Get(factory, "/cost?period=2026-10");
        var payments = Assert.Single(index.GetProperty("products").EnumerateArray(),
            p => string.Equals(p.GetProperty("id").GetString(), "payments", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("payments", payments.GetProperty("id").GetString());
        Assert.Equal(1, payments.GetProperty("services").GetInt32());
        Assert.Equal(1514.80m, payments.GetProperty("total").GetProperty("USD").GetDecimal());
        Assert.Single(index.GetProperty("options").GetProperty("products").EnumerateArray(),
            p => string.Equals(p.GetProperty("id").GetString(), "payments", StringComparison.OrdinalIgnoreCase));
        Assert.Single(index.GetProperty("teams").EnumerateArray(),
            t => string.Equals(t.GetProperty("id").GetString(), "platform-team", StringComparison.OrdinalIgnoreCase));

        var product = await Get(factory, "/cost?product=PAYMENTS&period=2026-10");
        Assert.Equal("payments", product.GetProperty("scope").GetProperty("id").GetString());
        Assert.True(product.GetProperty("scope").GetProperty("known").GetBoolean());
        Assert.Equal("ledger-api", Assert.Single(product.GetProperty("services").EnumerateArray()).GetProperty("id").GetString());
        Assert.Equal(1514.80m, product.GetProperty("totals").GetProperty("total").GetProperty("USD").GetDecimal());
        // The trend agrees with the month's own totals.
        var october = product.GetProperty("trend").EnumerateArray().Single(t => t.GetProperty("period").GetString() == "2026-10");
        Assert.Equal(
            product.GetProperty("totals").GetProperty("direct").GetProperty("USD").GetDecimal(),
            october.GetProperty("direct").GetProperty("USD").GetDecimal());

        var team = await Get(factory, "/cost?team=platform-team&period=2026-10");
        Assert.True(team.GetProperty("scope").GetProperty("known").GetBoolean());
        Assert.Contains("ledger-api", team.GetProperty("services").EnumerateArray().Select(s => s.GetProperty("id").GetString()));
    }

    [Fact]
    public async Task A_month_whose_projected_rows_were_pruned_reads_the_same_from_its_stored_file()
    {
        using var factory = new ServiceInventoryEndpointTests.InventoryFactory();
        factory.Seed();
        var costBefore = await Canonical(factory, "/cost?product=storefront&period=2026-10");
        var indexBefore = await Canonical(factory, "/cost?period=2026-10");
        var coverageBefore = await Canonical(factory, "/coverage?period=2026-10");
        var novemberBefore = await Canonical(factory, "/coverage");

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            db.RemoveRange(await db.InventoryCostServices.ToListAsync());
            db.RemoveRange(await db.InventoryCostMonths.ToListAsync());
            db.RemoveRange(await db.InventoryCostUnallocated.ToListAsync());
            await db.SaveChangesAsync();
        }

        Assert.Equal(costBefore, await Canonical(factory, "/cost?product=storefront&period=2026-10"));
        Assert.Equal(indexBefore, await Canonical(factory, "/cost?period=2026-10"));
        Assert.Equal(coverageBefore, await Canonical(factory, "/coverage?period=2026-10"));
        Assert.Equal(novemberBefore, await Canonical(factory, "/coverage"));
    }

    [Fact]
    public async Task A_month_asked_for_by_name_reports_its_own_refresh_state()
    {
        using var factory = new ServiceInventoryEndpointTests.InventoryFactory();
        factory.Seed();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            var october = await db.InventoryFeeds.SingleAsync(f => f.FeedType == InventoryFeedTypes.Cost && f.Period == "2026-10");
            october.LastError = "files.example.com answered 503";
            october.LastErrorAt = DateTimeOffset.UtcNow;
            october.ConsecutiveFailures = 3;
            await db.SaveChangesAsync();
        }

        foreach (var page in new[] { "/cost", "/coverage" })
        {
            // October's own failure, in general words for a user and in full for an admin.
            var october = (await Get(factory, page + "?period=2026-10")).GetProperty("freshness");
            Assert.Equal("2026-10", october.GetProperty("period").GetString());
            Assert.Equal(InventoryReadService.RefreshFailed, october.GetProperty("lastError").GetString());
            Assert.Equal(3, october.GetProperty("consecutiveFailures").GetInt32());
            var forAdmin = (await Get(factory, page + "?period=2026-10", admin: true)).GetProperty("freshness");
            Assert.Equal("files.example.com answered 503", forAdmin.GetProperty("lastError").GetString());

            // The latest month is unaffected by another month's failure.
            var latest = (await Get(factory, page)).GetProperty("freshness");
            Assert.Equal("2026-11", latest.GetProperty("period").GetString());
            Assert.Equal(JsonValueKind.Null, latest.GetProperty("lastError").ValueKind);
        }
    }

    [Fact]
    public async Task A_service_links_to_the_product_it_is_deployed_under_or_nowhere()
    {
        using var factory = new ServiceInventoryEndpointTests.InventoryFactory();
        factory.Seed();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            // Deployed under another product than the cost file names, and spelled its own way.
            db.DeployEvents.Add(new DeployEvent
            {
                Id = Guid.NewGuid(), Product = "shop", Service = "Order-Router", Environment = "dev", Version = "1.0.0",
                Source = "ci", Status = "succeeded", DeployedAt = DateTimeOffset.UtcNow, CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var storefront = await Get(factory, "/cost?product=storefront&period=2026-10");
        var deployed = Assert.Single(storefront.GetProperty("services").EnumerateArray()).GetProperty("deployments");
        Assert.Equal("shop", deployed.GetProperty("product").GetString());
        Assert.Equal("Order-Router", deployed.GetProperty("service").GetString());

        var payments = await Get(factory, "/cost?product=payments&period=2026-10");
        Assert.Equal(JsonValueKind.Null, Assert.Single(payments.GetProperty("services").EnumerateArray()).GetProperty("deployments").ValueKind);
    }

    [Fact]
    public async Task A_service_with_nothing_the_deployments_pages_show_has_no_link()
    {
        using var factory = new ServiceInventoryEndpointTests.InventoryFactory();
        factory.Seed();
        static DeployEvent Event(string product, string service, string status, int minutesAgo) => new()
        {
            Id = Guid.NewGuid(), Product = product, Service = service, Environment = "dev", Version = "1.0.0",
            Source = "ci", Status = status, DeployedAt = DateTimeOffset.UtcNow.AddMinutes(-minutesAgo), CreatedAt = DateTimeOffset.UtcNow,
        };
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            // order-router: deployed, then taken out of its only environment.
            db.DeployEvents.Add(Event("storefront", "order-router", "succeeded", 60));
            db.DeployEvents.Add(Event("storefront", "order-router", DeployEventStatuses.Removed, 5));
            // ledger-api: under a product an admin deleted it from, and still current under another.
            db.DeployEvents.Add(Event("payments", "ledger-api", "succeeded", 60));
            db.DeletedServices.Add(new DeletedService { Id = Guid.NewGuid(), Product = "payments", Service = "ledger-api" });
            db.DeployEvents.Add(Event("billing", "ledger-api", "succeeded", 30));
            await db.SaveChangesAsync();
        }

        var storefront = await Get(factory, "/cost?product=storefront&period=2026-10");
        Assert.Equal(JsonValueKind.Null, Assert.Single(storefront.GetProperty("services").EnumerateArray()).GetProperty("deployments").ValueKind);

        // The deleted product matches the cost file's, but only the visible one is a destination.
        var payments = await Get(factory, "/cost?product=payments&period=2026-10");
        Assert.Equal("billing", Assert.Single(payments.GetProperty("services").EnumerateArray()).GetProperty("deployments").GetProperty("product").GetString());
    }
}
