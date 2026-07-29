using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Platform.Api.Features.Deployments.Models;
using Platform.Api.Features.Promotions;
using Platform.Api.Features.Promotions.Models;
using Platform.Api.Infrastructure.Persistence;

namespace Platform.Integration.Tests;

/// <summary>
/// Covers <see cref="PromotionService.RefreshPolicySnapshotsAsync"/> — the retroactive half of the
/// policy contract. A candidate is gated on its own snapshot rather than a live join, so editing a
/// policy has to actively re-stamp the promotions still waiting on it. These tests pin the four
/// behaviours that make that safe:
/// <list type="bullet">
///   <item>Relaxing a gate re-gates pending candidates and promotes the ones that now pass.</item>
///   <item>Tightening a gate re-stamps them too, without un-approving anything.</item>
///   <item>Candidates past Pending are frozen — a policy edit cannot rewrite a fired decision.</item>
///   <item>Deleting the last policy on an edge leaves snapshots alone rather than auto-approving.</item>
/// </list>
/// Reuses <see cref="PromotionGateTests.GateTestFactory"/> (in-memory SQLite, fake current user,
/// captured webhook dispatcher).
/// </summary>
public class PromotionPolicyReapplyTests
{
    private const string Product = "reapply-acme";
    private const string Service = "api";
    private const string SourceEnv = "staging";
    private const string TargetEnv = "prod";
    private const string Version = "v1.0.0";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    // ── 1. Relaxing the gate promotes what was waiting on it ──────────────────

    [Fact]
    public async Task RelaxingGateToAutoApprove_PromotesPendingCandidate()
    {
        await using var factory = new PromotionGateTests.GateTestFactory();
        factory.Current.Email = "admin@example.com";
        factory.Current.Name = "Admin";

        Guid candidateId, policyId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            var policy = await SeedPolicyAsync(db, RequirementTree("ReleaseApprovers"));
            var candidate = await SeedPendingCandidateAsync(db, policy);
            candidateId = candidate.Id;
            policyId = policy.Id;

            // Sanity: it really is blocked on the human requirement.
            Assert.Equal(PromotionStatus.Pending, candidate.Status);
        }

        // The operator drops the approval requirement — the edge becomes auto-approve.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            var policy = await db.PromotionPolicies.FirstAsync(p => p.Id == policyId);
            policy.ApprovalSteps = new();
            await db.SaveChangesAsync();

            var svc = scope.ServiceProvider.GetRequiredService<PromotionService>();
            var count = await svc.RefreshPolicySnapshotsAsync(Product, Service, SourceEnv, TargetEnv);
            Assert.Equal(1, count);
        }

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            var candidate = await db.PromotionCandidates.AsNoTracking().FirstAsync(c => c.Id == candidateId);

            // Re-gated under the new rules and promoted on the spot.
            Assert.Equal(PromotionStatus.Approved, candidate.Status);
            Assert.NotNull(candidate.ApprovedAt);
            Assert.True(ReadSnapshot(candidate).IsAutoApprove);

            // Both the re-stamp and the resulting transition are on the record.
            Assert.Single(await db.AuditLog.AsNoTracking()
                .Where(a => a.Action == "promotion.policy.reapplied" && a.EntityId == candidateId)
                .ToListAsync());
            Assert.Single(await db.AuditLog.AsNoTracking()
                .Where(a => a.Action == "promotion.approved" && a.EntityId == candidateId)
                .ToListAsync());
        }
    }

    // ── 2. Tightening the gate re-stamps without approving ───────────────────

    [Fact]
    public async Task TighteningGate_RestampsPendingCandidateAndKeepsItPending()
    {
        await using var factory = new PromotionGateTests.GateTestFactory();
        factory.Current.Email = "admin@example.com";

        Guid candidateId, policyId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            var policy = await SeedPolicyAsync(db, RequirementTree("ReleaseApprovers"));
            candidateId = (await SeedPendingCandidateAsync(db, policy)).Id;
            policyId = policy.Id;
        }

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            var policy = await db.PromotionPolicies.FirstAsync(p => p.Id == policyId);
            policy.RequireAllWorkItemsApproved = true;
            policy.ApprovalSteps = RequirementTree("SecurityApprovers", minApprovers: 2);
            await db.SaveChangesAsync();

            var svc = scope.ServiceProvider.GetRequiredService<PromotionService>();
            Assert.Equal(1, await svc.RefreshPolicySnapshotsAsync(Product, Service, SourceEnv, TargetEnv));
        }

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            var candidate = await db.PromotionCandidates.AsNoTracking().FirstAsync(c => c.Id == candidateId);
            Assert.Equal(PromotionStatus.Pending, candidate.Status);

            var snapshot = ReadSnapshot(candidate);
            Assert.True(snapshot.RequireAllWorkItemsApproved);
            var requirement = Assert.Single(snapshot.AllRequirements);
            Assert.Equal(2, requirement.MinApprovers);
            Assert.Equal("SecurityApprovers", Assert.Single(requirement.Groups).Id);
        }
    }

    // ── 3. Anything past Pending is frozen ───────────────────────────────────

    [Fact]
    public async Task ApprovedCandidate_IsNotRestamped()
    {
        await using var factory = new PromotionGateTests.GateTestFactory();
        factory.Current.Email = "admin@example.com";

        Guid candidateId, policyId;
        string originalJson;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            var policy = await SeedPolicyAsync(db, RequirementTree("ReleaseApprovers"));
            var candidate = await SeedPendingCandidateAsync(db, policy);

            // It already cleared its gate and fired its webhook — the rules it was judged under stand.
            candidate.Status = PromotionStatus.Approved;
            candidate.ApprovedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();

            candidateId = candidate.Id;
            policyId = policy.Id;
            originalJson = candidate.ResolvedPolicyJson!;
        }

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            var policy = await db.PromotionPolicies.FirstAsync(p => p.Id == policyId);
            policy.ApprovalSteps = RequirementTree("SomeoneElse", minApprovers: 3);
            await db.SaveChangesAsync();

            var svc = scope.ServiceProvider.GetRequiredService<PromotionService>();
            Assert.Equal(0, await svc.RefreshPolicySnapshotsAsync(Product, Service, SourceEnv, TargetEnv));
        }

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            var candidate = await db.PromotionCandidates.AsNoTracking().FirstAsync(c => c.Id == candidateId);
            Assert.Equal(PromotionStatus.Approved, candidate.Status);
            Assert.Equal(originalJson, candidate.ResolvedPolicyJson);
        }
    }

    // ── 4. Deleting the gate must not promote what it was holding ────────────

    [Fact]
    public async Task PolicyDeletedWithNoFallback_KeepsSnapshotAndStaysPending()
    {
        await using var factory = new PromotionGateTests.GateTestFactory();
        factory.Current.Email = "admin@example.com";

        Guid candidateId;
        string originalJson;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            var policy = await SeedPolicyAsync(db, RequirementTree("ReleaseApprovers"));
            var candidate = await SeedPendingCandidateAsync(db, policy);
            candidateId = candidate.Id;
            originalJson = candidate.ResolvedPolicyJson!;

            db.PromotionPolicies.Remove(policy);
            await db.SaveChangesAsync();
        }

        using (var scope = factory.Services.CreateScope())
        {
            var svc = scope.ServiceProvider.GetRequiredService<PromotionService>();
            Assert.Equal(0, await svc.RefreshPolicySnapshotsAsync(Product, Service, SourceEnv, TargetEnv));
        }

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            var candidate = await db.PromotionCandidates.AsNoTracking().FirstAsync(c => c.Id == candidateId);

            // Un-enrolling an edge is not an approval decision.
            Assert.Equal(PromotionStatus.Pending, candidate.Status);
            Assert.Equal(originalJson, candidate.ResolvedPolicyJson);
        }
    }

    // ── 5. A product-default edit reaches every service on the edge ──────────

    [Fact]
    public async Task ProductDefaultPolicyEdit_ReachesAllServicesOnTheEdge()
    {
        await using var factory = new PromotionGateTests.GateTestFactory();
        factory.Current.Email = "admin@example.com";

        Guid apiCandidateId, webCandidateId, policyId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            // service: null ⇒ product-default, applies to every service under the product.
            var policy = await SeedPolicyAsync(db, RequirementTree("ReleaseApprovers"), service: null);
            apiCandidateId = (await SeedPendingCandidateAsync(db, policy, service: "api")).Id;
            webCandidateId = (await SeedPendingCandidateAsync(db, policy, service: "web")).Id;
            policyId = policy.Id;
        }

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            var policy = await db.PromotionPolicies.FirstAsync(p => p.Id == policyId);
            policy.ApprovalSteps = new();
            await db.SaveChangesAsync();

            var svc = scope.ServiceProvider.GetRequiredService<PromotionService>();
            // service: null mirrors the policy's own scope — refresh every service on the edge.
            Assert.Equal(2, await svc.RefreshPolicySnapshotsAsync(Product, null, SourceEnv, TargetEnv));
        }

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            foreach (var id in new[] { apiCandidateId, webCandidateId })
            {
                var candidate = await db.PromotionCandidates.AsNoTracking().FirstAsync(c => c.Id == id);
                Assert.Equal(PromotionStatus.Approved, candidate.Status);
            }
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static List<ApprovalStep> RequirementTree(string group, int minApprovers = 1) => new()
    {
        new ApprovalStep("Approval", new()
        {
            new ApproverRequirement("Approvers", new() { new GroupRef(group, group) }, new(), minApprovers),
        }),
    };

    private static async Task<PromotionPolicy> SeedPolicyAsync(
        PlatformDbContext db, List<ApprovalStep> steps, string? service = Service)
    {
        var now = DateTimeOffset.UtcNow;
        var policy = new PromotionPolicy
        {
            Id = Guid.NewGuid(),
            Product = Product,
            Service = service,
            SourceEnv = SourceEnv,
            TargetEnv = TargetEnv,
            ApprovalSteps = steps,
            TimeoutHours = 24,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.PromotionPolicies.Add(policy);
        await db.SaveChangesAsync();
        return policy;
    }

    /// <summary>
    /// Seeds a Pending candidate stamped with the policy as it stands now, plus the source deploy
    /// event that keeps the gate's source-drift check quiet (source runs the candidate's version).
    /// </summary>
    private static async Task<PromotionCandidate> SeedPendingCandidateAsync(
        PlatformDbContext db, PromotionPolicy policy, string service = Service)
    {
        db.DeployEvents.Add(new DeployEvent
        {
            Id = Guid.NewGuid(),
            Product = Product,
            Service = service,
            Environment = SourceEnv,
            Version = Version,
            Source = "ci",
            Status = "succeeded",
            DeployedAt = DateTimeOffset.UtcNow,
            ReferencesJson = "[]",
            ParticipantsJson = "[]",
            MetadataJson = "{}",
            CreatedAt = DateTimeOffset.UtcNow,
        });

        var snapshot = new ResolvedPolicySnapshot(policy.Id, policy.TimeoutHours, policy.EscalationGroup)
        {
            ApprovalSteps = policy.ApprovalSteps,
            RequireAllWorkItemsApproved = policy.RequireAllWorkItemsApproved,
            AutoApproveOnAllWorkItemsApproved = policy.AutoApproveOnAllWorkItemsApproved,
            AutoApproveWhenNoWorkItems = policy.AutoApproveWhenNoWorkItems,
            SourceRequiresDeploy = policy.SourceRequiresDeploy,
        };

        var candidate = new PromotionCandidate
        {
            Id = Guid.NewGuid(),
            Product = Product,
            Service = service,
            SourceEnv = SourceEnv,
            TargetEnv = TargetEnv,
            Version = Version,
            Status = PromotionStatus.Pending,
            PolicyId = policy.Id,
            ResolvedPolicyJson = JsonSerializer.Serialize(snapshot, JsonOptions),
            CreatedAt = DateTimeOffset.UtcNow,
            ParticipantsJson = "[]",
        };
        db.PromotionCandidates.Add(candidate);
        await db.SaveChangesAsync();
        return candidate;
    }

    private static ResolvedPolicySnapshot ReadSnapshot(PromotionCandidate candidate) =>
        JsonSerializer.Deserialize<ResolvedPolicySnapshot>(candidate.ResolvedPolicyJson!, JsonOptions)!;
}
