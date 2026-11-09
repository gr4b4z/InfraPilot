using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Platform.Api.Features.Promotions;
using Platform.Api.Features.Promotions.Models;
using Platform.Api.Infrastructure.Persistence;
using static Platform.Integration.Tests.PromotionBypassWorkItemTests;

namespace Platform.Integration.Tests;

/// <summary>
/// The promotion page's "Approve all"
/// (<see cref="WorkItemApprovalService.ApproveAllOnCandidateAsync"/>): every work item still waiting
/// for a decision is signed off at once, through the ordinary per-item path, and only the ones the
/// confirmation showed.
/// </summary>
public class PromotionApproveAllWorkItemsTests
{
    private const string Qa = "qa@example.com";
    private const string Other = "other@example.com";

    /// <summary>
    /// The preview is what the confirmation lists: the waiting items with their titles — a bypass mark
    /// still waits — and, apart, the ones already decided. Nothing is written.
    /// </summary>
    [Fact]
    public async Task DryRun_ListsTheWaitingItems_AndTheDecidedOnesApart()
    {
        await using var factory = QaFactory();
        Guid id;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            id = AddCandidate(db, Snapshot(requireAll: true), new[] { "K-1", "K-2", "K-3", "K-4", "K-5" }).Id;
            db.WorkItemApprovals.AddRange(
                Decision("K-2", "admin@example.com", WorkItemDecision.Bypassed),
                Decision("K-3", Other, WorkItemDecision.Issue),
                Decision("K-4", Other, WorkItemDecision.Blocked),
                Decision("K-5", Other, WorkItemDecision.Approved));
            await db.SaveChangesAsync();
        }

        using (var scope = factory.Services.CreateScope())
        {
            var result = await scope.ServiceProvider.GetRequiredService<WorkItemApprovalService>()
                .ApproveAllOnCandidateAsync(id, dryRun: true, keys: null, comment: null);

            Assert.True(result.DryRun);
            Assert.Equal(new[] { "K-1 Title of K-1 Pending", "K-2 Title of K-2 Bypassed" },
                result.Approved.Select(r => $"{r.WorkItemKey} {r.Title} {r.State}"));
            Assert.Equal(new[] { "K-3 Issue", "K-4 Blocked", "K-5 Approved" },
                result.Unchanged.Select(r => $"{r.WorkItemKey} {r.State}"));
            Assert.Empty(result.Failed);
        }

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            Assert.Equal(4, await db.WorkItemApprovals.CountAsync());
            Assert.DoesNotContain(await db.WorkItemApprovals.Select(a => a.ApproverEmail).ToListAsync(), e => e == Qa);
        }
    }

    /// <summary>
    /// Only the confirmed keys are signed — in the caller's name, with the comment, the bypass mark
    /// replaced — and the last sign-off goes through the gate like any other, so it can approve the
    /// promotion. A key the promotion does not carry is refused on its row.
    /// </summary>
    [Fact]
    public async Task Apply_SignsTheConfirmedKeys_AndTheLastOneCanApproveThePromotion()
    {
        await using var factory = QaFactory();
        Guid id;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            id = AddCandidate(db, Snapshot(requireAll: true, autoApproveOnAll: true), new[] { "K-1", "K-2", "K-3" }).Id;
            db.WorkItemApprovals.Add(Decision("K-2", "admin@example.com", WorkItemDecision.Bypassed));
            await db.SaveChangesAsync();
        }

        using (var scope = factory.Services.CreateScope())
        {
            var svc = scope.ServiceProvider.GetRequiredService<WorkItemApprovalService>();
            var first = await svc.ApproveAllOnCandidateAsync(
                id, dryRun: false, keys: new[] { "K-1", "K-2", "NOPE-1" }, comment: "  Regression suite green  ");

            Assert.False(first.DryRun);
            Assert.Equal(new[] { "K-1 Approved", "K-2 Approved" }, first.Approved.Select(r => $"{r.WorkItemKey} {r.State}"));
            // K-3 was waiting but never confirmed, so it is not the caller's to approve.
            Assert.Equal(new[] { "K-3 Pending" }, first.Unchanged.Select(r => $"{r.WorkItemKey} {r.State}"));
            var refused = Assert.Single(first.Failed);
            Assert.Equal("NOPE-1", refused.WorkItemKey);
            Assert.Equal("Not a work item of this promotion", refused.Error);
        }

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            var rows = await db.WorkItemApprovals.AsNoTracking().OrderBy(a => a.WorkItemKey).ToListAsync();
            Assert.Equal(new[] { "K-1 qa Approved Regression suite green", "K-2 qa Approved Regression suite green" },
                rows.Select(r => $"{r.WorkItemKey} {r.ApproverEmail.Split('@')[0]} {r.Decision} {r.Comment}"));
            var candidate = await db.PromotionCandidates.AsNoTracking().SingleAsync(c => c.Id == id);
            Assert.Equal(PromotionStatus.Pending, candidate.Status);
        }

        using (var scope = factory.Services.CreateScope())
        {
            var svc = scope.ServiceProvider.GetRequiredService<WorkItemApprovalService>();
            var second = await svc.ApproveAllOnCandidateAsync(id, dryRun: false, keys: null, comment: null);
            Assert.Equal(new[] { "K-3" }, second.Approved.Select(r => r.WorkItemKey));
        }

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            var candidate = await db.PromotionCandidates.AsNoTracking().SingleAsync(c => c.Id == id);
            Assert.Equal(PromotionStatus.Approved, candidate.Status);
            // Each sign-off left the trail a click leaves.
            Assert.Equal(3, await db.AuditLog.CountAsync(a => a.Action == "promotion.ticket.approved"));
            Assert.Equal(3, await db.WorkItemComments.CountAsync(c => c.Decision == WorkItemDecision.Approved));
        }
    }

    [Fact]
    public async Task RequiresTheQaOrAdminRole()
    {
        await using var factory = QaFactory();
        factory.Current.RolesList = new() { "ReleaseApprovers" };
        Guid id;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            id = AddCandidate(db, Snapshot(requireAll: true), new[] { "K-1" }).Id;
            await db.SaveChangesAsync();
        }

        using var call = factory.Services.CreateScope();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            call.ServiceProvider.GetRequiredService<WorkItemApprovalService>()
                .ApproveAllOnCandidateAsync(id, dryRun: true, keys: null, comment: null));
    }

    /// <summary>An auto-approve promotion has no human gate, so there is nothing to sign off on it.</summary>
    [Fact]
    public async Task RefusesAnAutoApprovePromotion()
    {
        await using var factory = QaFactory();
        Guid id;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            var auto = JsonSerializer.Serialize(new ResolvedPolicySnapshot(null, null),
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            id = AddCandidate(db, auto, new[] { "K-1" }).Id;
            await db.SaveChangesAsync();
        }

        using var call = factory.Services.CreateScope();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            call.ServiceProvider.GetRequiredService<WorkItemApprovalService>()
                .ApproveAllOnCandidateAsync(id, dryRun: true, keys: null, comment: null));
    }

    /// <summary>The route the Work items card calls, end to end: preview, then apply the previewed keys.</summary>
    [Fact]
    public async Task POST_WorkItemApprovals_PreviewsThenApplies()
    {
        await using var factory = QaFactory();
        Guid id;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            id = AddCandidate(db, Snapshot(requireAll: true), new[] { "K-1", "K-2" }).Id;
            await db.SaveChangesAsync();
        }

        var client = factory.CreateAdminClient();
        var url = $"/api/promotions/{id}/work-items/approvals";

        var preview = await client.PostAsJsonAsync(url, new { dryRun = true });
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        using var previewBody = JsonDocument.Parse(await preview.Content.ReadAsStringAsync());
        Assert.True(previewBody.RootElement.GetProperty("dryRun").GetBoolean());
        var keys = previewBody.RootElement.GetProperty("approved").EnumerateArray()
            .Select(r => r.GetProperty("workItemKey").GetString()!).ToList();
        Assert.Equal(new[] { "K-1", "K-2" }, keys);
        Assert.Equal("Title of K-1",
            previewBody.RootElement.GetProperty("approved")[0].GetProperty("title").GetString());

        var apply = await client.PostAsJsonAsync(url, new { dryRun = false, keys });
        Assert.Equal(HttpStatusCode.OK, apply.StatusCode);
        using var applyBody = JsonDocument.Parse(await apply.Content.ReadAsStringAsync());
        Assert.Equal(2, applyBody.RootElement.GetProperty("approved").GetArrayLength());
        Assert.Equal(0, applyBody.RootElement.GetProperty("failed").GetArrayLength());

        var missing = await client.PostAsJsonAsync($"/api/promotions/{Guid.NewGuid()}/work-items/approvals",
            new { dryRun = true });
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    private static WorkItemApprovalTests.WorkItemTestFactory QaFactory()
    {
        var factory = NewFactory();
        factory.Current.Email = Qa;
        factory.Current.Name = "QA";
        factory.Current.RolesList = new() { "InfraPortal.QA" };
        return factory;
    }
}
