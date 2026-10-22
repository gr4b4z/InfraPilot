using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Platform.Api.Features.Promotions;
using Platform.Api.Features.Promotions.Models;
using Platform.Api.Infrastructure.Persistence;
using static Platform.Integration.Tests.WorkItemApprovalTests;

namespace Platform.Integration.Tests;

/// <summary>
/// What an administrator bypass (<see cref="PromotionService.BypassAsync"/>) does to the promotion's
/// work items: it signs off the undecided ones exactly when the policy would have held the promotion
/// for them (<see cref="ResolvedPolicySnapshot.RequireAllWorkItemsApproved"/>), and nothing else.
/// </summary>
public class PromotionBypassWorkItemTests
{
    private const string Admin = "admin@example.com";
    private const string Other = "other@example.com";

    [Fact]
    public async Task Bypass_UnderRequiredWorkItemGate_SignsOffTheUndecidedItems()
    {
        await using var factory = NewFactory();
        Guid id;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            var candidate = AddCandidate(db, Snapshot(requireAll: true), new[] { "K-1", "K-2", "K-3" });
            id = candidate.Id;
            db.WorkItemApprovals.AddRange(
                Decision("K-2", Other, WorkItemDecision.Issue),
                Decision("K-3", Other, WorkItemDecision.Blocked));
            await db.SaveChangesAsync();
        }

        using (var scope = factory.Services.CreateScope())
        {
            var result = await scope.ServiceProvider.GetRequiredService<PromotionService>()
                .BypassAsync(id, "INC-7 hotfix");
            Assert.Equal(PromotionStatus.Approved, result.Status);
        }

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();

            // K-1 was the only one nobody had ruled on. The Issue already cleared K-2, and K-3's Block
            // is a person's hold — neither gets an admin row on top.
            var rows = await db.WorkItemApprovals.AsNoTracking()
                .OrderBy(a => a.WorkItemKey).ThenBy(a => a.ApproverEmail).ToListAsync();
            Assert.Equal(new[] { "K-1 admin Approved", "K-2 other Issue", "K-3 other Blocked" },
                rows.Select(r => $"{r.WorkItemKey} {r.ApproverEmail.Split('@')[0]} {r.Decision}"));
            var signed = rows[0];
            Assert.Equal("Admin", signed.ApproverName);
            Assert.Equal("api", signed.Service);
            Assert.Contains("admin bypass", signed.Comment);
            Assert.Contains("INC-7 hotfix", signed.Comment);

            // The thread records the sign-off the way a click does.
            var thread = await db.WorkItemComments.AsNoTracking().ToListAsync();
            var entry = Assert.Single(thread);
            Assert.Equal("K-1", entry.WorkItemKey);
            Assert.Equal(WorkItemDecision.Approved, entry.Decision);
            Assert.Equal(Admin, entry.AuthorEmail);

            // The promotion's own history names what the bypass signed.
            var system = await db.PromotionComments.AsNoTracking()
                .Where(c => c.CandidateId == id).Select(c => c.Body).ToListAsync();
            Assert.Contains(system, b => b.Contains("signed off 1 pending work item (K-1)"));

            var audit = await db.AuditLog.AsNoTracking().ToListAsync();
            var bypassed = Assert.Single(audit, a => a.Action == "promotion.bypassed");
            using (var state = JsonDocument.Parse(bypassed.AfterState!))
            {
                Assert.Equal(new[] { "K-1" }, state.RootElement.GetProperty("approvedWorkItems")
                    .EnumerateArray().Select(e => e.GetString()));
            }
            var ticket = Assert.Single(audit, a => a.Action == "promotion.ticket.approved");
            Assert.Equal(id, ticket.EntityId);
            using (var state = JsonDocument.Parse(ticket.AfterState!))
            {
                Assert.Equal("K-1", state.RootElement.GetProperty("workItemKey").GetString());
                Assert.Equal("administrator-bypass", state.RootElement.GetProperty("trigger").GetString());
            }
        }
    }

    /// <summary>
    /// A policy that does not hold the promotion for its work items was not waiting for them, so
    /// overriding it decides nothing about them — with no work-item gate at all, and with only the
    /// auto-approve accelerator, which never blocks.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Bypass_WithoutRequiredWorkItemGate_LeavesItemsPending(bool autoApproveOnAll)
    {
        await using var factory = NewFactory();
        Guid id;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            id = AddCandidate(db, Snapshot(requireAll: false, autoApproveOnAll: autoApproveOnAll), new[] { "K-1" }).Id;
            await db.SaveChangesAsync();
        }

        using (var scope = factory.Services.CreateScope())
        {
            var result = await scope.ServiceProvider.GetRequiredService<PromotionService>()
                .BypassAsync(id, "INC-7 hotfix");
            Assert.Equal(PromotionStatus.Approved, result.Status);
        }

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            Assert.Empty(await db.WorkItemApprovals.AsNoTracking().ToListAsync());
            Assert.Empty(await db.WorkItemComments.AsNoTracking().ToListAsync());
            Assert.DoesNotContain(await db.AuditLog.AsNoTracking().Select(a => a.Action).ToListAsync(),
                a => a == "promotion.ticket.approved");
        }
    }

    /// <summary>
    /// Only this promotion's instance is signed. The same ticket on another service is a different
    /// work item with its own sign-off — even when this gate waited for every instance.
    /// </summary>
    [Fact]
    public async Task Bypass_SignsOnlyThisServicesInstance()
    {
        await using var factory = NewFactory();
        Guid apiId, webId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            apiId = AddCandidate(db, Snapshot(requireAll: true, allInstances: true), new[] { "K-1" }).Id;
            webId = AddCandidate(db, Snapshot(requireAll: true), new[] { "K-1" }, service: "web").Id;
            await db.SaveChangesAsync();
        }

        using (var scope = factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<PromotionService>().BypassAsync(apiId, "INC-7 hotfix");

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            var row = Assert.Single(await db.WorkItemApprovals.AsNoTracking().ToListAsync());
            Assert.Equal("api", row.Service);
            var web = await db.PromotionCandidates.AsNoTracking().SingleAsync(c => c.Id == webId);
            Assert.Equal(PromotionStatus.Pending, web.Status);
        }
    }

    /// <summary>
    /// A sign-off is shared by every promotion of the service carrying the item, so a second edge
    /// into the same target that was waiting only for it goes through — as after a clicked sign-off.
    /// </summary>
    [Fact]
    public async Task Bypass_ReevaluatesOtherPromotionsCarryingTheSignedItems()
    {
        await using var factory = NewFactory();
        Guid bypassedId, otherEdgeId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            bypassedId = AddCandidate(db, Snapshot(requireAll: true), new[] { "K-1" }).Id;
            otherEdgeId = AddCandidate(db, Snapshot(requireAll: true, autoApproveOnAll: true), new[] { "K-1" },
                sourceEnv: "uat").Id;
            await db.SaveChangesAsync();
        }

        using (var scope = factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<PromotionService>().BypassAsync(bypassedId, "INC-7 hotfix");

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            var other = await db.PromotionCandidates.AsNoTracking().SingleAsync(c => c.Id == otherEdgeId);
            Assert.Equal(PromotionStatus.Approved, other.Status);
        }
    }

    // ── Data ──────────────────────────────────────────────────────────────────

    private static WorkItemTestFactory NewFactory()
    {
        var factory = new WorkItemTestFactory();
        factory.Current.Email = Admin;
        factory.Current.Name = "Admin";
        factory.Current.RolesList = new() { "InfraPortal.Admin" };
        return factory;
    }

    private static readonly JsonSerializerOptions CamelCase = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>A human-gated policy snapshot ("any one ReleaseApprovers member") with the given work-item flags.</summary>
    private static string Snapshot(bool requireAll, bool autoApproveOnAll = false, bool allInstances = false)
        => JsonSerializer.Serialize(new ResolvedPolicySnapshot(Guid.NewGuid(), null)
        {
            ApprovalSteps = new()
            {
                new ApprovalStep("Approval", new()
                {
                    new ApproverRequirement("Approvers", new() { new GroupRef("ReleaseApprovers", "ReleaseApprovers") }, new(), 1),
                }),
            },
            RequireAllWorkItemsApproved = requireAll,
            AutoApproveOnAllWorkItemsApproved = autoApproveOnAll,
            RequireAllWorkItemInstancesApproved = allInstances,
        }, CamelCase);

    /// <summary>A Pending acme/{service} v1 promotion into prod carrying <paramref name="keys"/>. Staged, not saved.</summary>
    private static PromotionCandidate AddCandidate(
        PlatformDbContext db, string snapshot, string[] keys, string service = "api", string sourceEnv = "staging")
    {
        var candidate = new PromotionCandidate
        {
            Id = Guid.NewGuid(),
            Product = "acme",
            Service = service,
            SourceEnv = sourceEnv,
            TargetEnv = "prod",
            Version = "v1.0.0",
            Status = PromotionStatus.Pending,
            ResolvedPolicyJson = snapshot,
            CreatedAt = DateTimeOffset.UtcNow,
            ParticipantsJson = "[]",
        };
        db.PromotionCandidates.Add(candidate);
        foreach (var key in keys)
        {
            db.PromotionWorkItems.Add(new PromotionWorkItem
            {
                Id = Guid.NewGuid(),
                CandidateId = candidate.Id,
                WorkItemKey = key,
                Product = candidate.Product,
                Service = service,
                TargetEnv = candidate.TargetEnv,
                CreatedAt = DateTimeOffset.UtcNow,
            });
        }
        return candidate;
    }

    private static WorkItemApproval Decision(string key, string email, WorkItemDecision decision)
        => new()
        {
            Id = Guid.NewGuid(),
            WorkItemKey = key,
            Product = "acme",
            Service = "api",
            TargetEnv = "prod",
            ApproverEmail = email,
            ApproverName = "Other",
            Decision = decision,
            CreatedAt = DateTimeOffset.UtcNow,
        };
}
