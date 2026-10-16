using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Platform.Api.Features.Webhooks;
using Platform.Api.Features.Webhooks.Models;
using Platform.Api.Infrastructure.Persistence;

namespace Platform.Api.Tests.Features.Webhooks;

/// <summary>
/// Subscription filters as sets: a receiver names the handful of products, services or environments
/// it cares about instead of standing up one subscription per value. The rules that matter are which
/// events a set lets through, and — since most events are product-wide — which dimensions an event
/// can be filtered on at all.
/// </summary>
public class WebhookFilterMatchingTests : IDisposable
{
    private readonly PlatformDbContext _db;
    private readonly WebhookDispatcher _sut;

    public WebhookFilterMatchingTests()
    {
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _db = new PlatformDbContext(options);
        _sut = new WebhookDispatcher(_db, Substitute.For<ILogger<WebhookDispatcher>>());
    }

    public void Dispose() => _db.Dispose();

    private void SeedSubscription(
        string name = "hook",
        string[]? events = null,
        string[]? products = null,
        string[]? services = null,
        string[]? environments = null)
    {
        _db.WebhookSubscriptions.Add(new WebhookSubscription
        {
            Id = Guid.NewGuid(),
            Name = name,
            Url = "https://example.com/hook",
            EncryptedSecret = "x",
            EventsJson = System.Text.Json.JsonSerializer.Serialize(events ?? ["deployment.created"]),
            FilterProductsJson = WebhookSubscriptionFilters.Serialize(products),
            FilterServicesJson = WebhookSubscriptionFilters.Serialize(services),
            FilterEnvironmentsJson = WebhookSubscriptionFilters.Serialize(environments),
            Active = true,
        });
        _db.SaveChanges();
    }

    // ── Multi-value dimensions ──────────────────────────────────────────────

    [Fact]
    public async Task AProductSetMatchesEveryProductInIt()
    {
        SeedSubscription(products: ["billing", "catalog"]);

        Assert.Equal(1, await Dispatch(product: "billing"));
        Assert.Equal(1, await Dispatch(product: "catalog"));
    }

    [Fact]
    public async Task AProductSetBlocksAProductOutsideIt()
    {
        SeedSubscription(products: ["billing", "catalog"]);

        Assert.Equal(0, await Dispatch(product: "identity"));
    }

    [Fact]
    public async Task AnEmptySetMatchesEveryValue()
    {
        SeedSubscription();

        Assert.Equal(1, await Dispatch(product: "billing", environment: "prod"));
        Assert.Equal(1, await Dispatch(product: "identity", environment: "test"));
    }

    [Fact]
    public async Task DimensionsAreAndedTogether()
    {
        SeedSubscription(products: ["billing"], environments: ["prod", "preprod"]);

        Assert.Equal(1, await Dispatch(product: "billing", environment: "preprod"));
        // Right product, wrong environment — the receiver asked for both to hold.
        Assert.Equal(0, await Dispatch(product: "billing", environment: "dev"));
        Assert.Equal(0, await Dispatch(product: "catalog", environment: "prod"));
    }

    [Fact]
    public async Task TheServiceDimensionFiltersPerServiceEvents()
    {
        SeedSubscription(services: ["api", "worker"]);

        Assert.Equal(1, await Dispatch(product: "billing", service: "worker"));
        Assert.Equal(0, await Dispatch(product: "billing", service: "web"));
    }

    // ── Dimensions the event does not carry ─────────────────────────────────

    [Fact]
    public async Task ADimensionTheEventDoesNotStateDoesNotBlockDelivery()
    {
        // A release note or a rollback is product-wide: it names no single service, so a
        // service-filtered subscription still hears about its own product.
        SeedSubscription(events: ["release_note.generated"], products: ["billing"], services: ["api"]);

        Assert.Equal(1, await Dispatch("release_note.generated", product: "billing", environment: "prod"));
    }

    [Fact]
    public async Task AnEventWithNoFiltersAtAllReachesEveryFilteredSubscription()
    {
        // `ping` and the maintenance re-sends carry no dimensions to match on.
        SeedSubscription(events: ["ping"], products: ["billing"], environments: ["prod"]);

        Assert.Equal(1, await _sut.DispatchAsync("ping", new { }));
    }

    // ── Storage normalisation ───────────────────────────────────────────────

    [Fact]
    public void StoredSetsAreTrimmedDeduplicatedAndStrippedOfBlanks()
    {
        var stored = WebhookSubscriptionFilters.Serialize([" billing ", "billing", "BILLING", "", null, "catalog"]);

        Assert.Equal(["billing", "catalog"], WebhookSubscriptionFilters.Parse(stored));
    }

    [Fact]
    public void AnUnreadableSetReadsAsNoFilterRatherThanThrowing()
    {
        Assert.Empty(WebhookSubscriptionFilters.Parse("not json"));
        Assert.Empty(WebhookSubscriptionFilters.Parse(""));
        Assert.Empty(WebhookSubscriptionFilters.Parse(null));
    }

    [Fact]
    public void ADimensionIsRejectedWhenItIsTooWideOrHoldsAnOverlongValue()
    {
        var tooMany = Enumerable.Range(0, WebhookSubscriptionFilters.MaxValuesPerDimension + 1)
            .Select(i => $"product-{i}").ToArray();
        Assert.NotNull(WebhookSubscriptionFilters.Validate("products", tooMany));

        var tooLong = new[] { new string('x', WebhookSubscriptionFilters.MaxValueLength + 1) };
        Assert.NotNull(WebhookSubscriptionFilters.Validate("services", tooLong));

        Assert.Null(WebhookSubscriptionFilters.Validate("environments", ["prod", "preprod"]));
    }

    private Task<int> Dispatch(
        string eventType = "deployment.created",
        string? product = null,
        string? environment = null,
        string? service = null)
        => _sut.DispatchAsync(
            eventType, new { }, new WebhookEventFilters(product, environment, service));
}
