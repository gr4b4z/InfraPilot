using Microsoft.EntityFrameworkCore;
using Platform.Api.Features.Promotions;
using Platform.Api.Features.Promotions.Models;
using Platform.Api.Infrastructure.Persistence;

namespace Platform.Api.Tests.Features.Promotions;

public class PromotionPolicyResolverTests : IDisposable
{
    private readonly PlatformDbContext _db;
    private readonly PromotionPolicyResolver _sut;

    public PromotionPolicyResolverTests()
    {
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _db = new PlatformDbContext(options);
        _sut = new PromotionPolicyResolver(_db);
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task ResolveAsync_NoRows_ReturnsNull()
    {
        var result = await _sut.ResolveAsync("acme", "api", "prod");
        Assert.Null(result);
    }

    [Fact]
    public async Task ResolveAsync_ProductDefaultOnly_ReturnsIt()
    {
        var policy = new PromotionPolicy
        {
            Id = Guid.NewGuid(),
            Product = "acme",
            Service = null,
            TargetEnv = "prod",
            ApproverGroup = "ops",
            Strategy = PromotionStrategy.Any,
        };
        _db.PromotionPolicies.Add(policy);
        await _db.SaveChangesAsync();

        var result = await _sut.ResolveAsync("acme", "api", "prod");
        Assert.NotNull(result);
        Assert.Equal(policy.Id, result!.Id);
    }

    [Fact]
    public async Task ResolveAsync_ServiceSpecificWinsOverProductDefault()
    {
        var productDefault = new PromotionPolicy
        {
            Id = Guid.NewGuid(),
            Product = "acme",
            Service = null,
            TargetEnv = "prod",
            ApproverGroup = "ops",
            Strategy = PromotionStrategy.Any,
        };
        var specific = new PromotionPolicy
        {
            Id = Guid.NewGuid(),
            Product = "acme",
            Service = "api",
            TargetEnv = "prod",
            ApproverGroup = "api-leads",
            Strategy = PromotionStrategy.NOfM,
            MinApprovers = 2,
        };
        _db.PromotionPolicies.AddRange(productDefault, specific);
        await _db.SaveChangesAsync();

        var result = await _sut.ResolveAsync("acme", "api", "prod");
        Assert.NotNull(result);
        Assert.Equal(specific.Id, result!.Id);
        Assert.Equal("api-leads", result.ApproverGroup);
    }

    [Fact]
    public async Task SnapshotAsync_NoMatch_ReturnsAutoApproveSnapshot()
    {
        var snap = await _sut.SnapshotAsync("acme", "api", "prod");
        Assert.Null(snap.PolicyId);
        Assert.Null(snap.ApproverGroup);
        Assert.True(snap.IsAutoApprove);
    }

    [Fact]
    public async Task SnapshotAsync_Match_PopulatesFieldsFromPolicy()
    {
        var policy = new PromotionPolicy
        {
            Id = Guid.NewGuid(),
            Product = "acme",
            Service = null,
            TargetEnv = "prod",
            ApproverGroup = "ops",
            Strategy = PromotionStrategy.NOfM,
            MinApprovers = 2,
            ExcludeRole = "triggered-by",
            TimeoutHours = 48,
            EscalationGroup = "leads",
        };
        _db.PromotionPolicies.Add(policy);
        await _db.SaveChangesAsync();

        var snap = await _sut.SnapshotAsync("acme", "api", "prod");
        Assert.Equal(policy.Id, snap.PolicyId);
        Assert.Equal("ops", snap.ApproverGroup);
        Assert.Equal(PromotionStrategy.NOfM, snap.Strategy);
        Assert.Equal(2, snap.MinApprovers);
        Assert.Equal("triggered-by", snap.ExcludeRole);
        Assert.Equal(48, snap.TimeoutHours);
        Assert.Equal("leads", snap.EscalationGroup);
        Assert.False(snap.IsAutoApprove);
    }
}
