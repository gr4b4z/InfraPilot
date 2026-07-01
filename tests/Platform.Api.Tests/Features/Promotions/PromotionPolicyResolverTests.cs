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
        var result = await _sut.ResolveAsync("acme", "api", "staging", "prod");
        Assert.Null(result);
    }

    // Helper: a single-step / single-requirement policy using the given group + minApprovers.
    private static List<ApprovalStep> Steps(string group, int minApprovers = 1) => new()
    {
        new("Release Approval", new()
        {
            new ApproverRequirement("Approvers", new() { new GroupRef(group, group) }, new(), minApprovers),
        }),
    };

    [Fact]
    public async Task ResolveAsync_ProductDefaultOnly_ReturnsIt()
    {
        var policy = new PromotionPolicy
        {
            Id = Guid.NewGuid(),
            Product = "acme",
            Service = null,
            SourceEnv = "staging",
            TargetEnv = "prod",
            ApprovalSteps = Steps("ops"),
        };
        _db.PromotionPolicies.Add(policy);
        await _db.SaveChangesAsync();

        var result = await _sut.ResolveAsync("acme", "api", "staging", "prod");
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
            SourceEnv = "staging",
            TargetEnv = "prod",
            ApprovalSteps = Steps("ops"),
        };
        var specific = new PromotionPolicy
        {
            Id = Guid.NewGuid(),
            Product = "acme",
            Service = "api",
            SourceEnv = "staging",
            TargetEnv = "prod",
            ApprovalSteps = Steps("api-leads", minApprovers: 2),
        };
        _db.PromotionPolicies.AddRange(productDefault, specific);
        await _db.SaveChangesAsync();

        var result = await _sut.ResolveAsync("acme", "api", "staging", "prod");
        Assert.NotNull(result);
        Assert.Equal(specific.Id, result!.Id);
        Assert.Equal("api-leads", result.ApprovalSteps.Single().Requirements.Single().Groups.Single().Id);
    }

    [Fact]
    public async Task ResolveAsync_DifferentSourceEnv_DoesNotResolve()
    {
        // Policy is configured for the dev→prod edge; a request for staging→prod must NOT match it.
        var policy = new PromotionPolicy
        {
            Id = Guid.NewGuid(),
            Product = "acme",
            Service = "api",
            SourceEnv = "dev",
            TargetEnv = "prod",
            ApprovalSteps = Steps("ops"),
        };
        _db.PromotionPolicies.Add(policy);
        await _db.SaveChangesAsync();

        Assert.Null(await _sut.ResolveAsync("acme", "api", "staging", "prod"));
        // Same policy resolves for its own edge.
        var onEdge = await _sut.ResolveAsync("acme", "api", "dev", "prod");
        Assert.NotNull(onEdge);
        Assert.Equal(policy.Id, onEdge!.Id);
    }

    [Fact]
    public async Task ResolveForTargetAsync_MatchesRegardlessOfSource()
    {
        // Target-only resolution ignores the source edge: a dev→prod policy resolves for target "prod".
        var policy = new PromotionPolicy
        {
            Id = Guid.NewGuid(),
            Product = "acme",
            Service = "api",
            SourceEnv = "dev",
            TargetEnv = "prod",
            ApprovalSteps = Steps("ops"),
        };
        _db.PromotionPolicies.Add(policy);
        await _db.SaveChangesAsync();

        var result = await _sut.ResolveForTargetAsync("acme", "api", "prod");
        Assert.NotNull(result);
        Assert.Equal(policy.Id, result!.Id);
    }

    [Fact]
    public async Task SnapshotAsync_NoMatch_ReturnsAutoApproveSnapshot()
    {
        var snap = await _sut.SnapshotAsync("acme", "api", "staging", "prod");
        Assert.Null(snap.PolicyId);
        Assert.Empty(snap.ApprovalSteps);
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
            SourceEnv = "staging",
            TargetEnv = "prod",
            ApprovalSteps = Steps("ops", minApprovers: 2),
            TimeoutHours = 48,
            EscalationGroup = "leads",
        };
        _db.PromotionPolicies.Add(policy);
        await _db.SaveChangesAsync();

        var snap = await _sut.SnapshotAsync("acme", "api", "staging", "prod");
        Assert.Equal(policy.Id, snap.PolicyId);
        var req = snap.AllRequirements.Single();
        Assert.Equal("ops", req.Groups.Single().Id);
        Assert.Equal(2, req.MinApprovers);
        Assert.Equal(48, snap.TimeoutHours);
        Assert.Equal("leads", snap.EscalationGroup);
        Assert.False(snap.IsAutoApprove);
    }
}
