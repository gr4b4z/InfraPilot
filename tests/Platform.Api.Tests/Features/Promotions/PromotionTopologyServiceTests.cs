using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Platform.Api.Features.Promotions;
using Platform.Api.Features.Promotions.Models;
using Platform.Api.Infrastructure.Audit;
using Platform.Api.Infrastructure.Features;
using Platform.Api.Infrastructure.Persistence;

namespace Platform.Api.Tests.Features.Promotions;

public class PromotionTopologyServiceTests : IDisposable
{
    private readonly PlatformDbContext _db;
    private readonly PromotionTopologyService _sut;

    public PromotionTopologyServiceTests()
    {
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _db = new PlatformDbContext(options);
        _sut = new PromotionTopologyService(
            _db, Substitute.For<IAuditLogger>(), Substitute.For<ILogger<PromotionTopologyService>>());
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task GetAsync_NoRow_ReturnsEmpty()
    {
        var topo = await _sut.GetAsync();
        Assert.Empty(topo.Environments);
        Assert.Empty(topo.Edges);
    }

    [Fact]
    public async Task SaveAsync_RoundTripsEnvironmentsAndEdges()
    {
        var topo = new PromotionTopology(
            new[] { "dev", "staging", "prod" },
            new[] { new PromotionEdge("dev", "staging"), new PromotionEdge("staging", "prod") });

        await _sut.SaveAsync(topo, "admin@example.com");

        var reloaded = await _sut.GetAsync();
        Assert.Equal(new[] { "dev", "staging", "prod" }, reloaded.Environments);
        Assert.Equal(2, reloaded.Edges.Count);
        Assert.Contains(reloaded.Edges, e => e.From == "dev" && e.To == "staging");
        Assert.Contains(reloaded.Edges, e => e.From == "staging" && e.To == "prod");
    }

    [Fact]
    public async Task GetNextEnvironmentsAsync_ReturnsDownstreamEnvs()
    {
        await _sut.SaveAsync(
            new PromotionTopology(
                new[] { "dev", "staging", "prod", "canary" },
                new[]
                {
                    new PromotionEdge("dev", "staging"),
                    new PromotionEdge("staging", "prod"),
                    new PromotionEdge("staging", "canary"),
                }),
            "admin");

        var nexts = await _sut.GetNextEnvironmentsAsync("staging");
        Assert.Equal(new[] { "prod", "canary" }, nexts);
    }

    [Fact]
    public async Task SaveAsync_EdgeToUnknownEnv_Throws()
    {
        var bad = new PromotionTopology(
            new[] { "dev" },
            new[] { new PromotionEdge("dev", "staging") }); // staging not in envs list

        await Assert.ThrowsAsync<ArgumentException>(() => _sut.SaveAsync(bad, "admin"));
    }

    [Fact]
    public async Task SaveAsync_SelfLoop_Throws()
    {
        var bad = new PromotionTopology(
            new[] { "dev" },
            new[] { new PromotionEdge("dev", "dev") });

        await Assert.ThrowsAsync<ArgumentException>(() => _sut.SaveAsync(bad, "admin"));
    }

    [Fact]
    public async Task SaveAsync_DuplicateEdges_Throws()
    {
        var bad = new PromotionTopology(
            new[] { "dev", "staging" },
            new[]
            {
                new PromotionEdge("dev", "staging"),
                new PromotionEdge("dev", "staging"),
            });

        await Assert.ThrowsAsync<ArgumentException>(() => _sut.SaveAsync(bad, "admin"));
    }

    [Fact]
    public async Task GetAsync_MalformedJson_ReturnsEmpty()
    {
        _db.PlatformSettings.Add(new PlatformSetting
        {
            Key = PromotionTopologyService.SettingKey,
            Value = "not-json",
            UpdatedBy = "test",
        });
        await _db.SaveChangesAsync();

        var topo = await _sut.GetAsync();
        Assert.Empty(topo.Environments);
        Assert.Empty(topo.Edges);
    }
}
