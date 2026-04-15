using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Platform.Api.Infrastructure.Features;
using Platform.Api.Infrastructure.Persistence;

namespace Platform.Api.Tests.Infrastructure.Features;

public class FeatureFlagsTests : IDisposable
{
    private readonly PlatformDbContext _db;
    private readonly FeatureFlags _flags;

    public FeatureFlagsTests()
    {
        // Shared in-process cache — reset between tests so they don't pollute each other.
        FeatureFlags.ClearCacheForTesting();

        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _db = new PlatformDbContext(options);
        _flags = new FeatureFlags(_db, Substitute.For<ILogger<FeatureFlags>>());
    }

    public void Dispose()
    {
        _db.Dispose();
        FeatureFlags.ClearCacheForTesting();
    }

    [Fact]
    public async Task IsEnabled_MissingKey_ReturnsFalse()
    {
        Assert.False(await _flags.IsEnabled("features.unknown"));
    }

    [Fact]
    public async Task IsEnabled_RowWithTrue_ReturnsTrue()
    {
        _db.PlatformSettings.Add(new PlatformSetting
        {
            Key = FeatureFlagKeys.Promotions,
            Value = "true",
            UpdatedBy = "test",
        });
        await _db.SaveChangesAsync();

        Assert.True(await _flags.IsEnabled(FeatureFlagKeys.Promotions));
    }

    [Fact]
    public async Task IsEnabled_RowWithFalse_ReturnsFalse()
    {
        _db.PlatformSettings.Add(new PlatformSetting
        {
            Key = FeatureFlagKeys.Promotions,
            Value = "false",
            UpdatedBy = "test",
        });
        await _db.SaveChangesAsync();

        Assert.False(await _flags.IsEnabled(FeatureFlagKeys.Promotions));
    }

    [Fact]
    public async Task SetEnabled_InsertsWhenMissing()
    {
        await _flags.SetEnabled(FeatureFlagKeys.Promotions, true, "admin@example.com");

        var row = await _db.PlatformSettings.SingleAsync(s => s.Key == FeatureFlagKeys.Promotions);
        Assert.Equal("true", row.Value);
        Assert.Equal("admin@example.com", row.UpdatedBy);
    }

    [Fact]
    public async Task SetEnabled_UpdatesExisting()
    {
        _db.PlatformSettings.Add(new PlatformSetting
        {
            Key = FeatureFlagKeys.Promotions,
            Value = "true",
            UpdatedBy = "initial",
        });
        await _db.SaveChangesAsync();

        await _flags.SetEnabled(FeatureFlagKeys.Promotions, false, "admin@example.com");

        var row = await _db.PlatformSettings.SingleAsync(s => s.Key == FeatureFlagKeys.Promotions);
        Assert.Equal("false", row.Value);
        Assert.Equal("admin@example.com", row.UpdatedBy);
    }

    [Fact]
    public async Task SetEnabled_InvalidatesCache()
    {
        // Prime the cache with the false (unset) value.
        Assert.False(await _flags.IsEnabled(FeatureFlagKeys.Promotions));

        // Flip it — the cache should now report the new value immediately on this node.
        await _flags.SetEnabled(FeatureFlagKeys.Promotions, true, "admin@example.com");

        Assert.True(await _flags.IsEnabled(FeatureFlagKeys.Promotions));
    }

    [Fact]
    public async Task Seeder_SeedDefaults_InsertsPromotionsWhenConfigDefaultFalse()
    {
        var config = new ConfigurationBuilder().Build();
        await FeatureFlagSeeder.SeedDefaults(_db, config);

        var row = await _db.PlatformSettings.SingleAsync(s => s.Key == FeatureFlagKeys.Promotions);
        Assert.Equal("false", row.Value);
    }

    [Fact]
    public async Task Seeder_SeedDefaults_HonoursConfigOverride()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Features:Promotions:DefaultEnabled"] = "true",
            })
            .Build();

        await FeatureFlagSeeder.SeedDefaults(_db, config);

        var row = await _db.PlatformSettings.SingleAsync(s => s.Key == FeatureFlagKeys.Promotions);
        Assert.Equal("true", row.Value);
    }

    [Fact]
    public async Task Seeder_SeedDefaults_DoesNotOverwriteExisting()
    {
        // Operator has explicitly set it to true; a subsequent startup must not revert it.
        _db.PlatformSettings.Add(new PlatformSetting
        {
            Key = FeatureFlagKeys.Promotions,
            Value = "true",
            UpdatedBy = "operator",
        });
        await _db.SaveChangesAsync();

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Features:Promotions:DefaultEnabled"] = "false",
            })
            .Build();

        await FeatureFlagSeeder.SeedDefaults(_db, config);

        var row = await _db.PlatformSettings.SingleAsync(s => s.Key == FeatureFlagKeys.Promotions);
        Assert.Equal("true", row.Value);
        Assert.Equal("operator", row.UpdatedBy);
    }

    [Fact]
    public async Task Seeder_SeedDefaults_IsIdempotent()
    {
        var config = new ConfigurationBuilder().Build();
        await FeatureFlagSeeder.SeedDefaults(_db, config);
        var first = await _db.PlatformSettings.CountAsync();

        await FeatureFlagSeeder.SeedDefaults(_db, config);
        var second = await _db.PlatformSettings.CountAsync();

        Assert.Equal(first, second);
    }
}
