using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Platform.Api.Features.Deployments.Models;
using Platform.Api.Features.Promotions;
using Platform.Api.Features.Promotions.Models;
using Platform.Api.Infrastructure.Audit;
using Platform.Api.Infrastructure.Auth;
using Platform.Api.Infrastructure.Features;
using Platform.Api.Infrastructure.Identity;
using Platform.Api.Infrastructure.Persistence;
using Platform.Api.Features.Webhooks;

namespace Platform.Api.Tests.Features.Promotions;

public class PromotionIngestHookTests : IDisposable
{
    private readonly PlatformDbContext _db;
    private readonly IFeatureFlags _flags = Substitute.For<IFeatureFlags>();
    private readonly PromotionTopologyService _topology;
    private readonly PromotionService _promotions;
    private readonly PromotionIngestHook _sut;

    public PromotionIngestHookTests()
    {
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _db = new PlatformDbContext(options);

        // Default: feature is enabled.
        _flags.IsEnabled(FeatureFlagKeys.Promotions, Arg.Any<CancellationToken>()).Returns(true);

        var audit = Substitute.For<IAuditLogger>();
        _topology = new PromotionTopologyService(_db, audit, Substitute.For<ILogger<PromotionTopologyService>>());

        var currentUser = Substitute.For<ICurrentUser>();
        currentUser.Email.Returns("system@example.com");
        currentUser.Id.Returns("system");
        currentUser.Name.Returns("System");

        var identity = Substitute.For<IIdentityService>();
        identity.GetGroupMembers(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new List<UserInfo>());

        var resolver = new PromotionPolicyResolver(_db);
        var auth = new PromotionApprovalAuthorizer(
            _db, currentUser, identity,
            Substitute.For<ILogger<PromotionApprovalAuthorizer>>());
        _promotions = new PromotionService(
            _db, resolver, auth, currentUser, audit,
            Substitute.For<ILogger<PromotionService>>(),
            Substitute.For<IWebhookDispatcher>(),
            TestOptions.Normalization());

        _sut = new PromotionIngestHook(
            _flags, _topology, _promotions, _db,
            Substitute.For<ILogger<PromotionIngestHook>>());
    }

    public void Dispose() => _db.Dispose();

    private async Task SeedTopologyAsync()
    {
        await _topology.SaveAsync(
            new PromotionTopology(
                new[] { "dev", "staging", "prod" },
                new[]
                {
                    new PromotionEdge("dev", "staging"),
                    new PromotionEdge("staging", "prod"),
                }),
            "test");
    }

    /// <summary>
    /// Seeds auto-approve policies for the standard topology so candidates are created.
    /// Without a policy, candidate creation is skipped (product not enrolled).
    /// </summary>
    private async Task SeedAutoApprovePoliciesAsync()
    {
        _db.PromotionPolicies.AddRange(
            new PromotionPolicy
            {
                Id = Guid.NewGuid(),
                Product = "acme",
                Service = null,
                TargetEnv = "staging",
                ApproverGroup = null, // auto-approve
                Strategy = PromotionStrategy.Any,
                MinApprovers = 0,
            },
            new PromotionPolicy
            {
                Id = Guid.NewGuid(),
                Product = "acme",
                Service = null,
                TargetEnv = "prod",
                ApproverGroup = null, // auto-approve
                Strategy = PromotionStrategy.Any,
                MinApprovers = 0,
            });
        await _db.SaveChangesAsync();
    }

    private DeployEvent SeedDeploy(string env, string version = "v1")
    {
        var e = new DeployEvent
        {
            Id = Guid.NewGuid(),
            Product = "acme",
            Service = "api",
            Environment = env,
            Version = version,
            Status = "succeeded",
            Source = "ci",
            DeployedAt = DateTimeOffset.UtcNow,
            ParticipantsJson = "[]",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        _db.DeployEvents.Add(e);
        _db.SaveChanges();
        return e;
    }

    [Fact]
    public async Task FeatureDisabled_NoCandidatesCreated()
    {
        _flags.IsEnabled(FeatureFlagKeys.Promotions, Arg.Any<CancellationToken>()).Returns(false);
        await SeedTopologyAsync();
        var e = SeedDeploy("dev");

        await _sut.OnIngestedAsync(e);

        Assert.Empty(_db.PromotionCandidates);
    }

    [Fact]
    public async Task NoTopology_NoCandidatesCreated()
    {
        // Topology left empty.
        var e = SeedDeploy("dev");

        await _sut.OnIngestedAsync(e);

        Assert.Empty(_db.PromotionCandidates);
    }

    [Fact]
    public async Task NoPolicyForProduct_NoCandidatesCreated()
    {
        await SeedTopologyAsync();
        // No policies seeded — product is not enrolled in promotions.
        var e = SeedDeploy("dev");

        await _sut.OnIngestedAsync(e);

        Assert.Empty(_db.PromotionCandidates);
    }

    [Fact]
    public async Task SourceEnvHasDownstream_CandidateCreated()
    {
        await SeedTopologyAsync();
        await SeedAutoApprovePoliciesAsync();
        var e = SeedDeploy("dev");

        await _sut.OnIngestedAsync(e);

        var candidate = await _db.PromotionCandidates.SingleAsync();
        Assert.Equal("dev", candidate.SourceEnv);
        Assert.Equal("staging", candidate.TargetEnv);
    }

    [Fact]
    public async Task SourceEnvWithMultipleDownstream_MultipleCandidates()
    {
        await _topology.SaveAsync(
            new PromotionTopology(
                new[] { "staging", "prod", "canary" },
                new[]
                {
                    new PromotionEdge("staging", "prod"),
                    new PromotionEdge("staging", "canary"),
                }),
            "test");

        // Enroll product for both target envs.
        _db.PromotionPolicies.AddRange(
            new PromotionPolicy
            {
                Id = Guid.NewGuid(), Product = "acme", TargetEnv = "prod",
                Strategy = PromotionStrategy.Any,
            },
            new PromotionPolicy
            {
                Id = Guid.NewGuid(), Product = "acme", TargetEnv = "canary",
                Strategy = PromotionStrategy.Any,
            });
        await _db.SaveChangesAsync();

        var e = SeedDeploy("staging");
        await _sut.OnIngestedAsync(e);

        var targets = await _db.PromotionCandidates.Select(c => c.TargetEnv).ToListAsync();
        Assert.Equal(2, targets.Count);
        Assert.Contains("prod", targets);
        Assert.Contains("canary", targets);
    }

    [Fact]
    public async Task DeployInTargetEnv_MatchingCandidateMarkedDeployed()
    {
        await SeedTopologyAsync();
        await SeedAutoApprovePoliciesAsync();

        // Step 1: deploy to staging → creates candidate for prod (auto-approve policy)
        var stagingEvent = SeedDeploy("staging", version: "v1");
        await _sut.OnIngestedAsync(stagingEvent);

        var candidate = await _db.PromotionCandidates.SingleAsync(c => c.TargetEnv == "prod");
        Assert.Equal(PromotionStatus.Approved, candidate.Status); // auto-approve policy
        await _promotions.MarkDeployingAsync(candidate.Id, "https://ci/1");

        // Step 2: a deploy event lands on prod with matching version → close the candidate.
        var prodEvent = SeedDeploy("prod", version: "v1");
        await _sut.OnIngestedAsync(prodEvent);

        var reloaded = await _db.PromotionCandidates.FindAsync(candidate.Id);
        Assert.Equal(PromotionStatus.Deployed, reloaded!.Status);
        Assert.NotNull(reloaded.DeployedAt);
    }

    [Fact]
    public async Task DeployInTargetEnv_VersionMismatch_NoMatchDoesNothing()
    {
        await SeedTopologyAsync();
        await SeedAutoApprovePoliciesAsync();

        var stagingEvent = SeedDeploy("staging", version: "v1");
        await _sut.OnIngestedAsync(stagingEvent);

        var candidate = await _db.PromotionCandidates.SingleAsync(c => c.TargetEnv == "prod");
        await _promotions.MarkDeployingAsync(candidate.Id, null);

        // A different version landing on prod — should not close this candidate.
        var prodEvent = SeedDeploy("prod", version: "v2");
        await _sut.OnIngestedAsync(prodEvent);

        var reloaded = await _db.PromotionCandidates.FindAsync(candidate.Id);
        Assert.Equal(PromotionStatus.Deploying, reloaded!.Status);
    }

    [Fact]
    public async Task HookFailure_DoesNotThrow()
    {
        // Pass a broken topology that will throw on read by deleting the DB after init.
        // Simpler: disable the feature flag to avoid hook execution; this test just validates
        // that errors propagate safely when enabled but something inside throws.
        _flags.IsEnabled(FeatureFlagKeys.Promotions, Arg.Any<CancellationToken>())
            .Returns<bool>(_ => throw new InvalidOperationException("oops"));

        var e = SeedDeploy("dev");
        var ex = await Record.ExceptionAsync(() => _sut.OnIngestedAsync(e));

        // Hook swallows upstream exceptions so ingestion never fails.
        Assert.Null(ex);
    }
}
