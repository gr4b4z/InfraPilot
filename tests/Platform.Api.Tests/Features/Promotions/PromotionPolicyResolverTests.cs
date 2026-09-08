using System.Text.Json;
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
            EscalationGroup = "leads",
        };
        _db.PromotionPolicies.Add(policy);
        await _db.SaveChangesAsync();

        var snap = await _sut.SnapshotAsync("acme", "api", "staging", "prod");
        Assert.Equal(policy.Id, snap.PolicyId);
        var req = snap.AllRequirements.Single();
        Assert.Equal("ops", req.Groups.Single().Id);
        Assert.Equal(2, req.MinApprovers);
        Assert.Equal("leads", snap.EscalationGroup);
        Assert.False(snap.IsAutoApprove);
        // Defaults, not "absent": every edge deploys off its approval unless a policy says otherwise.
        Assert.True(snap.DeploysOnApproval);
    }

    // ── DeploysOnApproval ─────────────────────────────────────────────────────
    // Display-only, but it decides which of two opposite sentences the approve card shows, so a
    // value that silently defaults the wrong way tells an approver their sign-off does not deploy
    // when it does. Pinned at each of the three points it passes through: policy → snapshot,
    // snapshot → JSON → snapshot, and JSON that predates the field.

    [Fact]
    public void Project_CarriesDeploysOnApprovalFalse()
    {
        var policy = new PromotionPolicy
        {
            Id = Guid.NewGuid(),
            Product = "mpt",
            SourceEnv = "staging",
            TargetEnv = "prod",
            ApprovalSteps = Steps("qa"),
            DeploysOnApproval = false,
        };

        var snap = PromotionPolicyResolver.Project(policy);

        Assert.False(snap.DeploysOnApproval);
    }

    [Fact]
    public void Snapshot_SurvivesARoundTripThroughJson()
    {
        var policy = new PromotionPolicy
        {
            Id = Guid.NewGuid(),
            Product = "mpt",
            SourceEnv = "staging",
            TargetEnv = "prod",
            DeploysOnApproval = false,
        };
        var json = JsonSerializer.Serialize(PromotionPolicyResolver.Project(policy), SnapshotJson);

        var read = ResolvedPolicySnapshot.TryRead(json);

        Assert.NotNull(read);
        Assert.False(read!.DeploysOnApproval);
    }

    [Fact]
    public void TryRead_SnapshotJsonWithoutTheField_ReadsAsDeploysOnApproval()
    {
        // A candidate stamped before the flag existed. Its edge deployed off the approval then, so
        // that is what it has to keep saying — the opposite default would have the page telling
        // every historical approver to go and release it somewhere else.
        var read = ResolvedPolicySnapshot.TryRead("""{"policyId":null,"approvalSteps":[]}""");

        Assert.NotNull(read);
        Assert.True(read!.DeploysOnApproval);
    }

    [Fact]
    public void TryRead_NoSnapshotOrUnparseable_ReturnsNull()
    {
        Assert.Null(ResolvedPolicySnapshot.TryRead(null));
        Assert.Null(ResolvedPolicySnapshot.TryRead(""));
        Assert.Null(ResolvedPolicySnapshot.TryRead("{not json"));
    }

    private static readonly JsonSerializerOptions SnapshotJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };
}
