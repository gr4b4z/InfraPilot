using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Platform.Api.BackgroundServices;
using Platform.Api.Features.ServiceInventory;
using Platform.Api.Features.ServiceInventory.Models;
using Platform.Api.Features.ServiceInventory.Sources;
using Platform.Api.Infrastructure.Features;
using Platform.Api.Infrastructure.Persistence;

namespace Platform.Api.Tests.Features.ServiceInventory;

public class ServiceInventorySyncServiceTests
{
    private readonly ManualClock _clock = new(new DateTimeOffset(2026, 11, 20, 16, 0, 0, TimeSpan.Zero));
    private readonly IFeatureFlags _flags = Substitute.For<IFeatureFlags>();
    private readonly Dictionary<string, Func<IArtifactSource>> _sources = new();
    private readonly string _database = Guid.NewGuid().ToString();
    private readonly List<string> _created = [];

    private static ServiceInventoryOptions Options(params string[] feeds) => new()
    {
        AllowedHosts = ["files.example.com"],
        Feeds = feeds.Select(f => new InventoryFeedOptions
        {
            Name = f, Type = f, PollMinutes = 15,
            Source = new() { Kind = "https", Url = $"https://files.example.com/{f}.json" },
        }).ToList(),
    };

    private sealed class Factory(ServiceInventorySyncServiceTests test) : IArtifactSourceFactory
    {
        public IArtifactSource Create(InventoryFeedOptions feed)
        {
            test._created.Add(feed.Name);
            return test._sources[feed.Name]();
        }
    }

    private (ServiceInventorySyncService Service, IServiceProvider Provider) Build(ServiceInventoryOptions options)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<PlatformDbContext>(o => o.UseInMemoryDatabase(_database));
        services.AddSingleton(_flags);
        services.AddSingleton<TimeProvider>(_clock);
        services.AddSingleton<IArtifactSourceFactory>(new Factory(this));
        services.AddScoped<InventoryRefresher>();
        services.AddScoped<InventoryRetention>();
        var provider = services.BuildServiceProvider();
        var service = new ServiceInventorySyncService(provider.GetRequiredService<IServiceScopeFactory>(),
            new StaticOptions<ServiceInventoryOptions>(options), _clock, NullLogger<ServiceInventorySyncService>.Instance);
        return (service, provider);
    }

    private ScriptedSource Catalog() => new() { Next = _ => ScriptedSource.File(Fixture.Bytes("catalog.json"), "v1") };

    [Fact]
    public async Task With_the_flag_off_nothing_is_called()
    {
        _flags.IsEnabled(FeatureFlagKeys.ServiceInventory, Arg.Any<CancellationToken>()).Returns(false);
        _sources["catalog"] = Catalog;
        var (service, _) = Build(Options("catalog"));

        await service.RunOnceAsync(default);

        Assert.Empty(_created);
    }

    [Fact]
    public async Task An_invalid_configuration_disables_the_inventory()
    {
        _flags.IsEnabled(FeatureFlagKeys.ServiceInventory, Arg.Any<CancellationToken>()).Returns(true);
        _sources["catalog"] = Catalog;
        var options = Options("catalog");
        options.AllowedHosts.Clear();
        var (service, _) = Build(options);

        await service.RunOnceAsync(default);

        Assert.Empty(_created);
    }

    [Fact]
    public async Task Only_feeds_whose_poll_interval_has_passed_are_refreshed()
    {
        _flags.IsEnabled(FeatureFlagKeys.ServiceInventory, Arg.Any<CancellationToken>()).Returns(true);
        _sources["catalog"] = Catalog;
        _sources["status"] = () => new ScriptedSource { Next = _ => ScriptedSource.File(Fixture.Bytes("status.json"), "s1") };
        var (service, provider) = Build(Options("catalog", "status"));
        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            db.InventoryFeeds.Add(new InventoryFeed { FeedKey = "status", FeedName = "status", FeedType = "status", LastAttemptAt = _clock.Now.AddMinutes(-5) });
            await db.SaveChangesAsync();
        }

        await service.RunOnceAsync(default);

        Assert.Equal(["catalog"], _created);
    }

    [Fact]
    public async Task A_failing_feed_does_not_stop_the_next_one()
    {
        _flags.IsEnabled(FeatureFlagKeys.ServiceInventory, Arg.Any<CancellationToken>()).Returns(true);
        _sources["catalog"] = () => throw new InvalidOperationException("boom");
        _sources["status"] = () => new ScriptedSource { Next = _ => ScriptedSource.File(Fixture.Bytes("status.json"), "s1") };
        var (service, provider) = Build(Options("catalog", "status"));

        await service.RunOnceAsync(default);

        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        Assert.NotNull(db.InventoryFeeds.Single(f => f.FeedKey == "status").CurrentSnapshotId);
    }
}
