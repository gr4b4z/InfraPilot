using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Platform.Api.Features.Deployments.Models;
using Platform.Api.Features.Promotions;
using Platform.Api.Features.Promotions.Models;
using Platform.Api.Infrastructure.Persistence;

namespace Platform.Integration.Tests;

/// <summary>
/// Covers what <c>canApprove</c> promises. The promotions list spends it on claims that the current
/// user can act — the "Awaiting your approval" badge, the my-approvals tab, the bulk selection, the
/// my-tasks badge — so it has to mean "<see cref="PromotionService.ApproveAsync"/> would accept this
/// right now", not merely "you are in one of the approver groups".
///
/// <para>Two ways it used to over-report, both regression-tested here: a requirement already satisfied
/// by somebody else, and a work-item gate still holding the promotion back. Reuses
/// <see cref="PromotionGateTests.GateTestFactory"/> (fake current user, so group membership is a role
/// claim) since these are service-level questions.</para>
/// </summary>
public class PromotionCanApproveTests
{
    private const string ApproverGroup = "ReleaseApprovers";

    // ── A requirement somebody else already satisfied ────────────────────────

    [Fact]
    public async Task CanApprove_False_WhenTheOnlyRequirementIsAlreadySatisfied()
    {
        await using var factory = new GateFixture();
        factory.AsUser("second@example.com");

        Guid candidateId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            var candidate = await SeedAsync(db, minApprovers: 1);
            candidateId = candidate.Id;

            // A colleague fills the requirement's only slot. Nothing is left for this user to approve —
            // ApproveAsync would answer with RequirementAlreadySatisfiedException.
            db.PromotionApprovals.Add(NewApproval(candidateId, "first@example.com"));
            await db.SaveChangesAsync();
        }

        Assert.False(await CanApproveAsync(factory, candidateId));
    }

    [Fact]
    public async Task CanApprove_True_WhenTheRequirementStillNeedsAnotherApprover()
    {
        // Same setup, but the requirement wants two distinct approvers — so one recorded approval
        // leaves it open and this user really is being waited on.
        await using var factory = new GateFixture();
        factory.AsUser("second@example.com");

        Guid candidateId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            var candidate = await SeedAsync(db, minApprovers: 2);
            candidateId = candidate.Id;
            db.PromotionApprovals.Add(NewApproval(candidateId, "first@example.com"));
            await db.SaveChangesAsync();
        }

        Assert.True(await CanApproveAsync(factory, candidateId));
    }

    [Fact]
    public async Task CanApprove_True_WhenNobodyHasApprovedYet()
    {
        await using var factory = new GateFixture();
        factory.AsUser("first@example.com");

        Guid candidateId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            candidateId = (await SeedAsync(db, minApprovers: 1)).Id;
        }

        Assert.True(await CanApproveAsync(factory, candidateId));
    }

    [Fact]
    public async Task CanApprove_False_ForAUserInNoApproverGroup()
    {
        await using var factory = new GateFixture();
        factory.AsUser("outsider@example.com", groups: Array.Empty<string>());

        Guid candidateId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            candidateId = (await SeedAsync(db, minApprovers: 1)).Id;
        }

        Assert.False(await CanApproveAsync(factory, candidateId));
    }

    // ── A work-item gate still holding the promotion ─────────────────────────

    [Fact]
    public async Task CanApprove_False_WhileTheWorkItemGateIsBlocking()
    {
        // The case that prompted this: an approver is authorised and the requirement is open, but the
        // policy holds approval until every work item is signed off. ApproveAsync refuses, so the list
        // must not claim the promotion is awaiting this user.
        await using var factory = new GateFixture();
        factory.AsUser("first@example.com");

        Guid candidateId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            candidateId = (await SeedAsync(
                db, minApprovers: 1,
                requireAllWorkItemsApproved: true,
                workItemKeys: new[] { "FOO-1", "FOO-2" })).Id;
        }

        Assert.False(await CanApproveAsync(factory, candidateId));
    }

    [Fact]
    public async Task CanApprove_False_WhenOnlySomeWorkItemsAreSignedOff()
    {
        await using var factory = new GateFixture();
        factory.AsUser("first@example.com");

        Guid candidateId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            var candidate = await SeedAsync(
                db, minApprovers: 1,
                requireAllWorkItemsApproved: true,
                workItemKeys: new[] { "FOO-1", "FOO-2" });
            candidateId = candidate.Id;
            db.WorkItemApprovals.Add(NewWorkItemDecision(candidate, "FOO-1", WorkItemDecision.Approved));
            await db.SaveChangesAsync();
        }

        Assert.False(await CanApproveAsync(factory, candidateId));
    }

    [Fact]
    public async Task CanApprove_True_OnceEveryWorkItemIsSignedOff()
    {
        await using var factory = new GateFixture();
        factory.AsUser("first@example.com");

        Guid candidateId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            var candidate = await SeedAsync(
                db, minApprovers: 1,
                requireAllWorkItemsApproved: true,
                workItemKeys: new[] { "FOO-1", "FOO-2" });
            candidateId = candidate.Id;
            db.WorkItemApprovals.Add(NewWorkItemDecision(candidate, "FOO-1", WorkItemDecision.Approved));
            db.WorkItemApprovals.Add(NewWorkItemDecision(candidate, "FOO-2", WorkItemDecision.Approved));
            await db.SaveChangesAsync();
        }

        Assert.True(await CanApproveAsync(factory, candidateId));
    }

    [Fact]
    public async Task CanApprove_False_WhenAWorkItemIsApprovedButAlsoBlocked()
    {
        // A block outranks a sibling approval — the same precedence the gate evaluator applies, so
        // canApprove must not read a blocked item as resolved.
        await using var factory = new GateFixture();
        factory.AsUser("first@example.com");

        Guid candidateId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            var candidate = await SeedAsync(
                db, minApprovers: 1,
                requireAllWorkItemsApproved: true,
                workItemKeys: new[] { "FOO-1" });
            candidateId = candidate.Id;
            db.WorkItemApprovals.Add(NewWorkItemDecision(candidate, "FOO-1", WorkItemDecision.Approved));
            db.WorkItemApprovals.Add(
                NewWorkItemDecision(candidate, "FOO-1", WorkItemDecision.Blocked, "blocker@example.com"));
            await db.SaveChangesAsync();
        }

        Assert.False(await CanApproveAsync(factory, candidateId));
    }

    [Fact]
    public async Task CanApprove_True_WhenTheGateIsSetButTheCandidateCarriesNoWorkItems()
    {
        // RequireAllWorkItemsApproved has nothing to wait for on a bundle with no work items — the
        // gate evaluator treats that as satisfied, and so must this.
        await using var factory = new GateFixture();
        factory.AsUser("first@example.com");

        Guid candidateId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            candidateId = (await SeedAsync(
                db, minApprovers: 1, requireAllWorkItemsApproved: true)).Id;
        }

        Assert.True(await CanApproveAsync(factory, candidateId));
    }

    [Fact]
    public async Task CanApprove_True_WhenWorkItemsAreOutstandingButThePolicyDoesNotGateOnThem()
    {
        // Outstanding work items only block when the policy says so. Without the flag the approver is
        // free to go ahead, which is the pre-existing behaviour this fix must not change.
        await using var factory = new GateFixture();
        factory.AsUser("first@example.com");

        Guid candidateId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            candidateId = (await SeedAsync(
                db, minApprovers: 1,
                requireAllWorkItemsApproved: false,
                workItemKeys: new[] { "FOO-1" })).Id;
        }

        Assert.True(await CanApproveAsync(factory, candidateId));
    }

    // ── Agreement with the detail view ───────────────────────────────────────

    [Fact]
    public async Task CanApprove_AgreesWithApproveAsync_AcrossTheBlockingCases()
    {
        // The point of the whole fix: wherever canApprove says false for a candidate the user is
        // authorised on, ApproveAsync must refuse — and where it says true, ApproveAsync must accept.
        await using var factory = new GateFixture();
        factory.AsUser("first@example.com");

        Guid blockedId;
        Guid approvableId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            blockedId = (await SeedAsync(
                db, minApprovers: 1,
                requireAllWorkItemsApproved: true,
                workItemKeys: new[] { "BLOCK-1" },
                service: "blocked-svc")).Id;
            approvableId = (await SeedAsync(
                db, minApprovers: 1, service: "open-svc")).Id;
        }

        Assert.False(await CanApproveAsync(factory, blockedId));
        Assert.True(await CanApproveAsync(factory, approvableId));

        using (var scope = factory.Services.CreateScope())
        {
            var svc = scope.ServiceProvider.GetRequiredService<PromotionService>();
            await Assert.ThrowsAsync<InvalidOperationException>(() => svc.ApproveAsync(blockedId, null));
        }

        using (var scope = factory.Services.CreateScope())
        {
            var svc = scope.ServiceProvider.GetRequiredService<PromotionService>();
            var approved = await svc.ApproveAsync(approvableId, null);
            Assert.Equal(PromotionStatus.Approved, approved.Status);
        }
    }

    [Fact]
    public async Task CanApprove_False_OnceThisUserHasDecided()
    {
        // Pre-existing behaviour, pinned so the rewrite can't drop it.
        await using var factory = new GateFixture();
        factory.AsUser("first@example.com");

        Guid candidateId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            candidateId = (await SeedAsync(db, minApprovers: 2)).Id;
            db.PromotionApprovals.Add(NewApproval(candidateId, "first@example.com"));
            await db.SaveChangesAsync();
        }

        Assert.False(await CanApproveAsync(factory, candidateId));
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static async Task<bool> CanApproveAsync(GateFixture factory, Guid candidateId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var svc = scope.ServiceProvider.GetRequiredService<PromotionService>();
        var candidate = await db.PromotionCandidates.AsNoTracking()
            .FirstAsync(c => c.Id == candidateId);
        var result = await svc.CanUserApproveManyAsync(new[] { candidate });
        return result[candidateId];
    }

    /// <summary>
    /// Seeds a Pending candidate on a single-requirement step tree, with a matching succeeded source
    /// deploy so the gate's source checks never interfere.
    /// </summary>
    private static async Task<PromotionCandidate> SeedAsync(
        PlatformDbContext db,
        int minApprovers,
        bool requireAllWorkItemsApproved = false,
        IEnumerable<string>? workItemKeys = null,
        string product = "acme",
        string service = "api",
        string targetEnv = "prod")
    {
        const string version = "v1.0.0";
        db.DeployEvents.Add(new DeployEvent
        {
            Id = Guid.NewGuid(),
            Product = product,
            Service = service,
            Environment = "staging",
            Version = version,
            Source = "ci",
            Status = "succeeded",
            DeployedAt = DateTimeOffset.UtcNow,
            ReferencesJson = "[]",
            ParticipantsJson = "[]",
            MetadataJson = "{}",
            CreatedAt = DateTimeOffset.UtcNow,
        });

        var snapshot = new ResolvedPolicySnapshot(PolicyId: Guid.NewGuid(), EscalationGroup: null)
        {
            ApprovalSteps = new()
            {
                new ApprovalStep("Approval", new()
                {
                    new ApproverRequirement(
                        "Approvers",
                        new() { new GroupRef(ApproverGroup, ApproverGroup) },
                        new(),
                        minApprovers),
                }),
            },
            RequireAllWorkItemsApproved = requireAllWorkItemsApproved,
        };

        var candidate = new PromotionCandidate
        {
            Id = Guid.NewGuid(),
            Product = product,
            Service = service,
            SourceEnv = "staging",
            TargetEnv = targetEnv,
            Version = version,
            Status = PromotionStatus.Pending,
            PolicyId = snapshot.PolicyId,
            ResolvedPolicyJson = JsonSerializer.Serialize(
                snapshot, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }),
            ReferencesJson = "[]",
            ParticipantsJson = "[]",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.PromotionCandidates.Add(candidate);

        foreach (var key in workItemKeys ?? Array.Empty<string>())
        {
            db.PromotionWorkItems.Add(new PromotionWorkItem
            {
                Id = Guid.NewGuid(),
                CandidateId = candidate.Id,
                WorkItemKey = key,
                Product = product,
                TargetEnv = targetEnv,
                CreatedAt = DateTimeOffset.UtcNow,
            });
        }

        await db.SaveChangesAsync();
        return candidate;
    }

    private static PromotionApproval NewApproval(Guid candidateId, string email) => new()
    {
        Id = Guid.NewGuid(),
        CandidateId = candidateId,
        ApproverEmail = email,
        ApproverName = email,
        Decision = PromotionDecision.Approved,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    private static WorkItemApproval NewWorkItemDecision(
        PromotionCandidate candidate,
        string key,
        WorkItemDecision decision,
        string email = "qa@example.com") => new()
    {
        Id = Guid.NewGuid(),
        WorkItemKey = key,
        Product = candidate.Product,
        TargetEnv = candidate.TargetEnv,
        ApproverEmail = email,
        ApproverName = email,
        Decision = decision,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    /// <summary>
    /// Thin wrapper over <see cref="PromotionGateTests.GateTestFactory"/> adding a one-liner for
    /// "run as this person, in these groups" — group membership resolves off the fake user's role
    /// claims in the Testing environment.
    /// </summary>
    private sealed class GateFixture : PromotionGateTests.GateTestFactory
    {
        public void AsUser(string email, string[]? groups = null)
        {
            Current.Email = email;
            Current.Name = email;
            Current.RolesList = (groups ?? new[] { ApproverGroup }).ToList();
        }
    }
}
