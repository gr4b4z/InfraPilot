using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Platform.Api.Features.Promotions;
using Platform.Api.Features.Promotions.Models;
using Platform.Api.Infrastructure.Persistence;

namespace Platform.Integration.Tests;

/// <summary>
/// Covers the promotions list's approval-gate filter: "which promotions are waiting for Release
/// Approval", and the narrower question an approver of that gate actually has — "which ones am I the
/// last signature on".
///
/// <para>The filter itself is a one-line <c>Where</c> in <c>PromotionEndpoints</c> over
/// <see cref="PromotionService.GetGateStatusesAsync"/>, so everything worth pinning lives here: which
/// steps a candidate is reported as waiting on, and the two predicates
/// (<see cref="PromotionGateStatus.IsWaitingFor"/> / <see cref="PromotionGateStatus.IsWaitingOnlyFor"/>)
/// the two filter modes are.</para>
/// </summary>
public class PromotionGateFilterTests
{
    private const string ReleaseGate = "Release Approval";
    private const string SecurityGate = "Security Sign-off";
    private const string ApproverGroup = "ReleaseApprovers";

    [Fact]
    public async Task EveryStepIsOutstanding_WhenNobodyHasApproved()
    {
        await using var factory = new GateFixture();

        Guid id;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            id = (await SeedAsync(db, ReleaseGate, SecurityGate)).Id;
        }

        var status = await GateStatusAsync(factory, id);

        Assert.Equal(new[] { ReleaseGate, SecurityGate }, status.OutstandingSteps);
        Assert.True(status.IsWaitingFor(ReleaseGate));
        // Waiting for two things is not waiting only for one of them — this is the whole distinction
        // the "only gate left" narrowing draws.
        Assert.False(status.IsWaitingOnlyFor(ReleaseGate));
    }

    [Fact]
    public async Task OnlyTheUnsignedStepIsOutstanding_OnceTheOtherIsSatisfied()
    {
        await using var factory = new GateFixture();

        Guid id;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            var candidate = await SeedAsync(db, ReleaseGate, SecurityGate);
            id = candidate.Id;
            // Attributed to the security step, so the matcher cannot count it towards the other one.
            db.PromotionApprovals.Add(NewApproval(id, "sec@example.com", SecurityGate));
            await db.SaveChangesAsync();
        }

        var status = await GateStatusAsync(factory, id);

        Assert.Equal(new[] { ReleaseGate }, status.OutstandingSteps);
        Assert.True(status.IsWaitingOnlyFor(ReleaseGate));
        Assert.False(status.IsWaitingFor(SecurityGate));
    }

    [Fact]
    public async Task GateNameMatchingIgnoresCase()
    {
        // The filter value travels in a URL people edit and paste — matching has to survive the casing
        // they type, not just the casing the policy was written in.
        await using var factory = new GateFixture();

        Guid id;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            id = (await SeedAsync(db, ReleaseGate)).Id;
        }

        var status = await GateStatusAsync(factory, id);

        Assert.True(status.IsWaitingFor("release approval"));
        Assert.True(status.IsWaitingOnlyFor("RELEASE APPROVAL"));
    }

    [Fact]
    public async Task AnOutstandingWorkItemGateRulesOutWaitingOnlyForAStep()
    {
        // The step is the only human sign-off left, but the policy also holds the promotion until every
        // work item is signed off — so it is not one signature away from going out, and must not show
        // up under "only gate left".
        await using var factory = new GateFixture();

        Guid id;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            id = (await SeedAsync(
                db,
                new[] { ReleaseGate },
                requireAllWorkItemsApproved: true,
                workItemKeys: new[] { "FOO-1" })).Id;
        }

        var status = await GateStatusAsync(factory, id);

        Assert.True(status.WorkItemsOutstanding);
        Assert.True(status.IsWaitingFor(ReleaseGate));
        Assert.False(status.IsWaitingOnlyFor(ReleaseGate));
    }

    [Fact]
    public async Task TheStepIsTheLastThingLeft_OnceTheWorkItemsAreSignedOff()
    {
        await using var factory = new GateFixture();

        Guid id;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            var candidate = await SeedAsync(
                db,
                new[] { ReleaseGate },
                requireAllWorkItemsApproved: true,
                workItemKeys: new[] { "FOO-1" });
            id = candidate.Id;
            db.WorkItemApprovals.Add(NewWorkItemDecision(candidate, "FOO-1"));
            await db.SaveChangesAsync();
        }

        var status = await GateStatusAsync(factory, id);

        Assert.False(status.WorkItemsOutstanding);
        Assert.True(status.IsWaitingOnlyFor(ReleaseGate));
    }

    [Fact]
    public async Task AResolvedPromotionWaitsOnNothing()
    {
        // Only a Pending promotion is waiting on anything — which is also why a gate filter narrows the
        // list to Pending without the endpoint having to say so.
        await using var factory = new GateFixture();

        Guid id;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            var candidate = await SeedAsync(db, ReleaseGate);
            candidate.Status = PromotionStatus.Approved;
            await db.SaveChangesAsync();
            id = candidate.Id;
        }

        var status = await GateStatusAsync(factory, id);

        Assert.Empty(status.OutstandingSteps);
        Assert.False(status.IsWaitingFor(ReleaseGate));
    }

    [Fact]
    public async Task AnAutoApproveEdgeWaitsOnNoGate()
    {
        await using var factory = new GateFixture();

        Guid id;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            id = (await SeedAsync(db, Array.Empty<string>())).Id;
        }

        Assert.Empty((await GateStatusAsync(factory, id)).OutstandingSteps);
    }

    [Fact]
    public async Task AnUnnamedStepIsReportedAsApproval()
    {
        // Same label the detail page's progress panel gives it, so the filter value always matches
        // what the reader saw.
        await using var factory = new GateFixture();

        Guid id;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            id = (await SeedAsync(db, "")).Id;
        }

        Assert.Equal(new[] { "Approval" }, (await GateStatusAsync(factory, id)).OutstandingSteps);
    }

    [Fact]
    public async Task FilterOptionsOfferTheGatesOnPendingPromotions()
    {
        await using var factory = new GateFixture();

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            await SeedAsync(db, new[] { ReleaseGate, SecurityGate }, service: "api");

            // A gate only a resolved promotion ever carried is not an option: filtering by it could
            // only ever produce an empty list.
            var done = await SeedAsync(db, new[] { "Legacy Gate" }, service: "old-api");
            done.Status = PromotionStatus.Deployed;
            await db.SaveChangesAsync();
        }

        using (var scope = factory.Services.CreateScope())
        {
            var svc = scope.ServiceProvider.GetRequiredService<PromotionService>();
            var options = await svc.GetFilterOptionsAsync();

            Assert.Equal(new[] { ReleaseGate, SecurityGate }, options.Gates);
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static async Task<PromotionGateStatus> GateStatusAsync(GateFixture factory, Guid id)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var svc = scope.ServiceProvider.GetRequiredService<PromotionService>();
        var candidate = await db.PromotionCandidates.AsNoTracking().FirstAsync(c => c.Id == id);
        var statuses = await svc.GetGateStatusesAsync(new[] { candidate });
        return statuses[id];
    }

    private static Task<PromotionCandidate> SeedAsync(PlatformDbContext db, params string[] stepNames)
        => SeedAsync(db, stepNames, requireAllWorkItemsApproved: false);

    /// <summary>
    /// Seeds a Pending candidate whose snapshot carries one single-approver requirement per named
    /// step — the shape a "Release Approval plus Security Sign-off" policy resolves to.
    /// </summary>
    private static async Task<PromotionCandidate> SeedAsync(
        PlatformDbContext db,
        IReadOnlyList<string> stepNames,
        bool requireAllWorkItemsApproved = false,
        IEnumerable<string>? workItemKeys = null,
        string product = "acme",
        string service = "api",
        string targetEnv = "prod")
    {
        var snapshot = new ResolvedPolicySnapshot(PolicyId: Guid.NewGuid(), EscalationGroup: null)
        {
            ApprovalSteps = stepNames
                .Select(name => new ApprovalStep(name, new()
                {
                    new ApproverRequirement(
                        $"{name} approvers",
                        new() { new GroupRef(ApproverGroup, ApproverGroup) },
                        new(),
                        1),
                }))
                .ToList(),
            RequireAllWorkItemsApproved = requireAllWorkItemsApproved,
        };

        var candidate = new PromotionCandidate
        {
            Id = Guid.NewGuid(),
            Product = product,
            Service = service,
            SourceEnv = "staging",
            TargetEnv = targetEnv,
            Version = "v1.0.0",
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
                Service = service,
                TargetEnv = targetEnv,
                CreatedAt = DateTimeOffset.UtcNow,
            });
        }

        await db.SaveChangesAsync();
        return candidate;
    }

    private static PromotionApproval NewApproval(Guid candidateId, string email, string stepName) => new()
    {
        Id = Guid.NewGuid(),
        CandidateId = candidateId,
        ApproverEmail = email,
        ApproverName = email,
        Decision = PromotionDecision.Approved,
        StepName = stepName,
        RequirementName = $"{stepName} approvers",
        CreatedAt = DateTimeOffset.UtcNow,
    };

    private static WorkItemApproval NewWorkItemDecision(PromotionCandidate candidate, string key) => new()
    {
        Id = Guid.NewGuid(),
        WorkItemKey = key,
        Product = candidate.Product,
        Service = candidate.Service,
        TargetEnv = candidate.TargetEnv,
        ApproverEmail = "qa@example.com",
        ApproverName = "qa@example.com",
        Decision = WorkItemDecision.Approved,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    private sealed class GateFixture : PromotionGateTests.GateTestFactory
    {
    }
}
