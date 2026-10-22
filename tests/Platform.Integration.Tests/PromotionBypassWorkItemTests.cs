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
/// work items: it marks the undecided ones <see cref="WorkItemDecision.Bypassed"/> exactly when the
/// policy would have held the promotion for them
/// (<see cref="ResolvedPolicySnapshot.RequireAllWorkItemsApproved"/>), and nothing else. The mark is
/// not a sign-off — it clears no gate, a person's decision replaces it, and cancelling the approval
/// takes it back.
/// </summary>
public class PromotionBypassWorkItemTests
{
    private const string Admin = "admin@example.com";
    private const string Other = "other@example.com";

    [Fact]
    public async Task Bypass_UnderRequiredWorkItemGate_MarksTheUndecidedItemsBypassed()
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
            // is a person's hold — neither gets a mark on top.
            var rows = await db.WorkItemApprovals.AsNoTracking()
                .OrderBy(a => a.WorkItemKey).ThenBy(a => a.ApproverEmail).ToListAsync();
            Assert.Equal(new[] { "K-1 admin Bypassed", "K-2 other Issue", "K-3 other Blocked" },
                rows.Select(r => $"{r.WorkItemKey} {r.ApproverEmail.Split('@')[0]} {r.Decision}"));
            var marked = rows[0];
            Assert.Equal("Admin", marked.ApproverName);
            Assert.Equal("api", marked.Service);
            Assert.Contains("admin bypass", marked.Comment);
            Assert.Contains("INC-7 hotfix", marked.Comment);

            // The thread records it the way it records a decision.
            var thread = await db.WorkItemComments.AsNoTracking().ToListAsync();
            var entry = Assert.Single(thread);
            Assert.Equal("K-1", entry.WorkItemKey);
            Assert.Equal(WorkItemDecision.Bypassed, entry.Decision);
            Assert.Equal(Admin, entry.AuthorEmail);

            // The promotion's own history names what the bypass marked.
            var system = await db.PromotionComments.AsNoTracking()
                .Where(c => c.CandidateId == id).Select(c => c.Body).ToListAsync();
            Assert.Contains(system, b => b.Contains("1 pending work item (K-1) marked Bypassed"));

            var audit = await db.AuditLog.AsNoTracking().ToListAsync();
            var bypassed = Assert.Single(audit, a => a.Action == "promotion.bypassed");
            using (var state = JsonDocument.Parse(bypassed.AfterState!))
            {
                Assert.Equal(new[] { "K-1" }, state.RootElement.GetProperty("bypassedWorkItems")
                    .EnumerateArray().Select(e => e.GetString()));
            }
            // Announced as a bypass, never as an approval.
            Assert.DoesNotContain(audit, a => a.Action == "promotion.ticket.approved");
            var ticket = Assert.Single(audit, a => a.Action == "promotion.ticket.bypassed");
            Assert.Equal(id, ticket.EntityId);
            using (var state = JsonDocument.Parse(ticket.AfterState!))
            {
                Assert.Equal("K-1", state.RootElement.GetProperty("workItemKey").GetString());
                Assert.Equal("administrator-bypass", state.RootElement.GetProperty("trigger").GetString());
            }

            // The instance reads Bypassed — not Pending, not Approved.
            var overall = await WorkItemOverallStatus.LoadOneAsync(db, "K-1", "acme", "prod", default);
            Assert.Equal(WorkItemInstanceState.Bypassed, overall!.State);
            Assert.Equal(1, overall.Bypassed);
            Assert.False(overall.ClearsGateEverywhere);
        }
    }

    /// <summary>
    /// A policy that does not hold the promotion for its work items was not waiting for them, so
    /// overriding it says nothing about them — with no work-item gate at all, and with only the
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
                a => a.StartsWith("promotion.ticket."));
        }
    }

    /// <summary>
    /// Only this promotion's instance is marked. The same ticket on another service is a different
    /// work item with its own sign-off — even when this gate waited for every instance.
    /// </summary>
    [Fact]
    public async Task Bypass_MarksOnlyThisServicesInstance()
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
            Assert.Equal(WorkItemDecision.Bypassed, row.Decision);
            var web = await db.PromotionCandidates.AsNoTracking().SingleAsync(c => c.Id == webId);
            Assert.Equal(PromotionStatus.Pending, web.Status);
        }
    }

    /// <summary>
    /// The mark is not a sign-off. A second edge into the same target carrying the item, whose policy
    /// would go through the moment every work item cleared, still waits for a real decision.
    /// </summary>
    [Fact]
    public async Task Bypass_MarkDoesNotClearAnotherPromotionsGate()
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
        {
            var promotions = scope.ServiceProvider.GetRequiredService<PromotionService>();
            await promotions.BypassAsync(bypassedId, "INC-7 hotfix");
            // Even when something asks the gate again.
            await promotions.ReevaluateAsync(otherEdgeId);
        }

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            var other = await db.PromotionCandidates.AsNoTracking().SingleAsync(c => c.Id == otherEdgeId);
            Assert.Equal(PromotionStatus.Pending, other.Status);
        }
    }

    /// <summary>
    /// Cancelling the bypassed promotion's approval takes the marks back: the promotion is waiting
    /// again, and so are the work items it took through. A person's decision on another item stays.
    /// </summary>
    [Fact]
    public async Task CancelApproval_TakesTheBypassMarksBack()
    {
        await using var factory = NewFactory();
        Guid id;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            id = AddCandidate(db, Snapshot(requireAll: true), new[] { "K-1", "K-2" }).Id;
            db.WorkItemApprovals.Add(Decision("K-2", Other, WorkItemDecision.Issue));
            await db.SaveChangesAsync();
        }

        using (var scope = factory.Services.CreateScope())
        {
            var promotions = scope.ServiceProvider.GetRequiredService<PromotionService>();
            await promotions.BypassAsync(id, "INC-7 hotfix");
            var result = await promotions.CancelApprovalAsync(id, "wrong promotion");
            Assert.Equal(PromotionStatus.Pending, result.Candidate.Status);
        }

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            var row = Assert.Single(await db.WorkItemApprovals.AsNoTracking().ToListAsync());
            Assert.Equal("K-2 Issue", $"{row.WorkItemKey} {row.Decision}");

            var overall = await WorkItemOverallStatus.LoadOneAsync(db, "K-1", "acme", "prod", default);
            Assert.Equal(WorkItemInstanceState.Pending, overall!.State);

            // K-1's thread says the bypass was taken back, after the entry that recorded it.
            var thread = await db.WorkItemComments.AsNoTracking()
                .Where(c => c.WorkItemKey == "K-1").OrderBy(c => c.CreatedAt).ToListAsync();
            Assert.Equal(2, thread.Count);
            Assert.Equal(WorkItemDecision.Bypassed, thread[0].Decision);
            Assert.Equal(WorkItemComment.SystemAuthor, thread[1].AuthorEmail);
            Assert.Contains("Bypass taken back", thread[1].Body);

            var system = await db.PromotionComments.AsNoTracking()
                .Where(c => c.CandidateId == id).Select(c => c.Body).ToListAsync();
            Assert.Contains(system, b => b.Contains("1 bypassed work item (K-1) went back to pending"));

            var cancelled = Assert.Single(await db.AuditLog.AsNoTracking()
                .Where(a => a.Action == "promotion.approval.cancelled").ToListAsync());
            using var state = JsonDocument.Parse(cancelled.AfterState!);
            Assert.Equal(new[] { "K-1" }, state.RootElement.GetProperty("unbypassedWorkItems")
                .EnumerateArray().Select(e => e.GetString()));
        }
    }

    /// <summary>
    /// Once a person decides on a bypassed item, their decision is the item's state: the mark goes,
    /// so the item never reads two ways.
    /// </summary>
    [Fact]
    public async Task PersonsDecision_ReplacesTheBypassMark()
    {
        await using var factory = NewFactory();
        Guid id;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            id = AddCandidate(db, Snapshot(requireAll: true), new[] { "K-1" }).Id;
            await db.SaveChangesAsync();
        }

        using (var scope = factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<PromotionService>().BypassAsync(id, "INC-7 hotfix");

        factory.Current.Email = "qa@example.com";
        factory.Current.Name = "QA";
        factory.Current.RolesList = new() { "InfraPortal.QA" };
        using (var scope = factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<WorkItemApprovalService>()
                .ApproveAsync("K-1", "acme", "api", "prod", "checked after the hotfix");

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            var row = Assert.Single(await db.WorkItemApprovals.AsNoTracking().ToListAsync());
            Assert.Equal("qa@example.com Approved", $"{row.ApproverEmail} {row.Decision}");
        }
    }

    [Theory]
    [InlineData(new[] { WorkItemDecision.Bypassed }, WorkItemInstanceState.Bypassed)]
    [InlineData(new[] { WorkItemDecision.Bypassed, WorkItemDecision.Approved }, WorkItemInstanceState.Approved)]
    [InlineData(new[] { WorkItemDecision.Approved, WorkItemDecision.Bypassed }, WorkItemInstanceState.Approved)]
    [InlineData(new[] { WorkItemDecision.Bypassed, WorkItemDecision.Issue }, WorkItemInstanceState.Issue)]
    [InlineData(new[] { WorkItemDecision.Bypassed, WorkItemDecision.Blocked }, WorkItemInstanceState.Blocked)]
    public void StateOf_AnyDecisionOutranksABypass(WorkItemDecision[] decisions, WorkItemInstanceState expected)
    {
        var state = WorkItemOverallStatus.StateOf(decisions);
        Assert.Equal(expected, state);
        Assert.Equal(expected is WorkItemInstanceState.Approved or WorkItemInstanceState.Issue,
            WorkItemOverallStatus.ClearsGate(state));
    }

    [Fact]
    public void Aggregate_ReadsBypassedOnlyWhenNothingIsLeftUndecided()
    {
        WorkItemInstanceStatusView I(string service, WorkItemInstanceState state) => new(service, null, state);

        var shipped = WorkItemOverallStatus.Aggregate(new[]
        {
            I("api", WorkItemInstanceState.Bypassed), I("web", WorkItemInstanceState.Approved),
        });
        Assert.Equal(WorkItemInstanceState.Bypassed, shipped.State);
        Assert.Equal((1, 1, 0), (shipped.Approved, shipped.Bypassed, shipped.Pending));
        Assert.False(shipped.ClearsGateEverywhere);

        var waiting = WorkItemOverallStatus.Aggregate(new[]
        {
            I("api", WorkItemInstanceState.Bypassed), I("web", WorkItemInstanceState.Pending),
        });
        Assert.Equal(WorkItemInstanceState.Pending, waiting.State);
    }

    // ── Data ──────────────────────────────────────────────────────────────────

    internal static WorkItemTestFactory NewFactory()
    {
        var factory = new WorkItemTestFactory();
        factory.Current.Email = Admin;
        factory.Current.Name = "Admin";
        factory.Current.RolesList = new() { "InfraPortal.Admin" };
        return factory;
    }

    private static readonly JsonSerializerOptions CamelCase = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>A human-gated policy snapshot ("any one ReleaseApprovers member") with the given work-item flags.</summary>
    internal static string Snapshot(bool requireAll, bool autoApproveOnAll = false, bool allInstances = false)
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
    internal static PromotionCandidate AddCandidate(
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
                Title = $"Title of {key}",
                CreatedAt = DateTimeOffset.UtcNow,
            });
        }
        return candidate;
    }

    internal static WorkItemApproval Decision(string key, string email, WorkItemDecision decision)
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
