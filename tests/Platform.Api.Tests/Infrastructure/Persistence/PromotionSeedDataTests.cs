using Microsoft.EntityFrameworkCore;
using Platform.Api.Features.Promotions;
using Platform.Api.Features.Promotions.Models;
using Platform.Api.Infrastructure.Persistence;

namespace Platform.Api.Tests.Infrastructure.Persistence;

/// <summary>
/// Guards the demo data's promotion policies. The seed is the first thing anyone sees on a fresh
/// install, so a policy whose settings don't reach its candidates makes a working feature look broken —
/// which is exactly what happened before <c>MakeSnapshot</c> was folded into
/// <see cref="PromotionPolicyResolver.Project"/>: it had gone stale and silently dropped every policy
/// field added after it was written.
/// </summary>
public class PromotionSeedDataTests : IDisposable
{
    private readonly PlatformDbContext _db;

    public PromotionSeedDataTests()
    {
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _db = new PlatformDbContext(options);
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Seed_ProjectsEveryPolicySettingOntoCandidateSnapshots()
    {
        await SeedAsync();

        var policiesById = await _db.PromotionPolicies.ToDictionaryAsync(p => p.Id);
        var candidates = await _db.PromotionCandidates.Where(c => c.PolicyId != null).ToListAsync();
        Assert.NotEmpty(candidates);

        foreach (var candidate in candidates)
        {
            var policy = policiesById[candidate.PolicyId!.Value];
            var snapshot = ReadSnapshot(candidate);

            Assert.Equal(policy.TracksWorkItems, snapshot.TracksWorkItems);
            Assert.Equal(policy.RequiredWorkItemRoles, snapshot.RequiredWorkItemRoles);
            Assert.Equal(policy.RequireAllWorkItemsApproved, snapshot.RequireAllWorkItemsApproved);
            Assert.Equal(policy.SourceRequiresDeploy, snapshot.SourceRequiresDeploy);
            Assert.Equal(policy.EscalationGroup, snapshot.EscalationGroup);
        }
    }

    [Fact]
    public async Task Seed_ProductionEdge_RequiresAQaOwnerOnWorkItems()
    {
        await SeedAsync();

        var production = await _db.PromotionPolicies
            .Where(p => p.TargetEnv == "production")
            .ToListAsync();
        Assert.NotEmpty(production);
        Assert.All(production, p =>
        {
            Assert.True(p.TracksWorkItems);
            Assert.Contains("qa-owner", p.RequiredWorkItemRoles);
        });
    }

    [Fact]
    public async Task Seed_StagingEdge_CreatesNoWorkItems()
    {
        // The dev-facing edge: promotions land in staging to be integrated, not to be signed off, so its
        // tickets must stay out of the work-items queue entirely.
        await SeedAsync();

        var stagingPolicyIds = await _db.PromotionPolicies
            .Where(p => p.TargetEnv == "staging")
            .Select(p => p.Id)
            .ToListAsync();
        Assert.NotEmpty(stagingPolicyIds);
        Assert.All(
            await _db.PromotionPolicies.Where(p => p.TargetEnv == "staging").ToListAsync(),
            p => Assert.False(p.TracksWorkItems));

        var stagingCandidateIds = await _db.PromotionCandidates
            .Where(c => c.PolicyId != null && stagingPolicyIds.Contains(c.PolicyId.Value))
            .Select(c => c.Id)
            .ToListAsync();
        Assert.NotEmpty(stagingCandidateIds);

        Assert.False(
            await _db.PromotionWorkItems.AnyAsync(w => stagingCandidateIds.Contains(w.CandidateId)),
            "Candidates on an edge that doesn't track work items must have no work-item index rows.");
    }

    [Fact]
    public async Task Seed_ProductionEdge_DoesCreateWorkItems()
    {
        // The counterpart to the assertion above — otherwise "no rows anywhere" would pass it.
        await SeedAsync();

        var productionCandidateIds = await _db.PromotionCandidates
            .Where(c => c.TargetEnv == "production")
            .Select(c => c.Id)
            .ToListAsync();
        Assert.NotEmpty(productionCandidateIds);

        Assert.True(
            await _db.PromotionWorkItems.AnyAsync(w => productionCandidateIds.Contains(w.CandidateId)),
            "The production edge tracks work items, so its candidates should have index rows.");
    }

    [Fact]
    public async Task Seed_LeavesSomeWorkItemsWithoutTheRequiredRole()
    {
        // The demo data is meant to show both states: work items with a qa-owner, and work items where
        // somebody is named in another role but nobody is answerable. If every seeded item satisfied the
        // requirement, the "needs attention" surfaces would look unimplemented on a fresh install.
        await SeedAsync();

        var pending = await _db.PromotionCandidates
            .Where(c => c.Status == PromotionStatus.Pending && c.TargetEnv == "production")
            .ToListAsync();
        Assert.NotEmpty(pending);

        var keysByCandidate = (await _db.PromotionWorkItems.ToListAsync())
            .GroupBy(w => w.CandidateId)
            .ToDictionary(g => g.Key, g => g.Select(w => w.WorkItemKey).Distinct().ToList());

        var evaluated = 0;
        var withGap = 0;
        foreach (var candidate in pending)
        {
            foreach (var key in keysByCandidate.GetValueOrDefault(candidate.Id) ?? new())
            {
                evaluated++;
                var required = WorkItemRoleRequirements.RequiredRoles(candidate);
                if (WorkItemRoleRequirements.MissingRoles(candidate, key, required).Count > 0) withGap++;
            }
        }

        Assert.True(evaluated > 0, "Expected seeded pending production candidates to carry work items.");
        Assert.True(withGap > 0, "Expected some seeded work items to be missing their required role.");
        Assert.True(withGap < evaluated, "Expected some seeded work items to already have a qa-owner.");
    }

    private async Task SeedAsync()
    {
        // Promotion data is derived from deploy events, so the deployment seed has to run first. The
        // catalog / service-request demo seed is deliberately left out — it needs YAML-loaded catalog
        // items and nothing here reads it.
        await DeploymentSeedData.Seed(_db);
        await PromotionSeedData.Seed(_db);
    }

    private static ResolvedPolicySnapshot ReadSnapshot(PromotionCandidate candidate)
    {
        Assert.False(string.IsNullOrEmpty(candidate.ResolvedPolicyJson));
        var snapshot = System.Text.Json.JsonSerializer.Deserialize<ResolvedPolicySnapshot>(
            candidate.ResolvedPolicyJson!,
            new System.Text.Json.JsonSerializerOptions
            {
                PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
                PropertyNameCaseInsensitive = true,
            });
        Assert.NotNull(snapshot);
        return snapshot!;
    }
}
