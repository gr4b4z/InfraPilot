using System.Data.Common;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Platform.Api.Features.Deployments.Models;
using Platform.Api.Features.Promotions;
using Platform.Api.Features.Promotions.Models;
using Platform.Api.Infrastructure.Audit;
using Platform.Api.Infrastructure.Auth;
using Platform.Api.Infrastructure.Identity;
using Platform.Api.Infrastructure.Persistence;

namespace Platform.Integration.Tests;

/// <summary>
/// Integration tests for the ticket-level (work-item) approval surface added in PR2:
/// <see cref="WorkItemApprovalService"/> and <see cref="WorkItemEndpoints"/>. Each test
/// owns a fresh <see cref="WorkItemTestFactory"/> so the in-memory SQLite database, the
/// fake <see cref="ICurrentUser"/>, and the empty <see cref="IIdentityService"/> override
/// are all isolated.
///
/// <para><b>Why a fake <c>ICurrentUser</c></b>: service-level tests run via
/// <c>factory.Services.CreateScope()</c>, where there's no live HTTP context to read
/// claims from. The fake gives each test a small dial it can turn (email, roles, group
/// membership) without spinning up a JWT and request-pipeline round-trip.</para>
///
/// <para><b>Why an empty <see cref="IIdentityService"/></b>: the production
/// <see cref="StubIdentityService"/> returns every local user for any group lookup,
/// which would mask the "not in approver group" failure path. We override with a
/// stub that returns no members so authority can only come from explicit role/group
/// claims.</para>
/// </summary>
public class WorkItemApprovalTests
{
    // ── Service-level tests (via factory.Services.CreateScope()) ────────────

    [Fact]
    public async Task Approve_RecordsRow_WhenPendingCandidateCarriesTicket()
    {
        await using var factory = new WorkItemTestFactory();
        factory.Current.Email = "approver@example.com";
        factory.Current.Name = "Approver User";
        factory.Current.RolesList = new() { "ReleaseApprovers", "InfraPortal.QA" };

        Guid candidateId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            var (_, _, c) = await SeedPolicyEventCandidateAsync(db, "FOO-123",
                approverGroup: "ReleaseApprovers");
            candidateId = c.Id;
        }

        using (var scope = factory.Services.CreateScope())
        {
            var svc = scope.ServiceProvider.GetRequiredService<WorkItemApprovalService>();
            var row = await svc.ApproveAsync("FOO-123", "acme", "prod", "looks good", default);
            Assert.NotEqual(Guid.Empty, row.Id);
            Assert.Equal("FOO-123", row.WorkItemKey);
            Assert.Equal("acme", row.Product);
            Assert.Equal("prod", row.TargetEnv);
            Assert.Equal("approver@example.com", row.ApproverEmail);
            Assert.Equal(WorkItemDecision.Approved, row.Decision);
            Assert.Equal("looks good", row.Comment);
        }

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            // Row persisted.
            var rows = await db.WorkItemApprovals.AsNoTracking()
                .Where(a => a.WorkItemKey == "FOO-123").ToListAsync();
            Assert.Single(rows);

            // Audit entry.
            var audit = await db.AuditLog.AsNoTracking()
                .Where(a => a.Action == "work-item.approved")
                .ToListAsync();
            Assert.Single(audit);

            // Candidate must NOT be transitioned by ticket approval (PR3 owns gating).
            var candidate = await db.PromotionCandidates.AsNoTracking()
                .FirstAsync(c => c.Id == candidateId);
            Assert.Equal(PromotionStatus.Pending, candidate.Status);
        }
    }

    /// <summary>
    /// A missing Pending candidate no longer blocks a sign-off (an orphaned item still needs
    /// resolving) — but a key the platform has never seen does, so nothing can seed rows for a
    /// ticket that was never promoted.
    /// </summary>
    [Fact]
    public async Task Approve_Throws_WhenTicketIsUnknown()
    {
        await using var factory = new WorkItemTestFactory();
        factory.Current.Email = "approver@example.com";
        factory.Current.RolesList = new() { "ReleaseApprovers", "InfraPortal.QA" };

        // No setup — no candidate has ever carried FOO-999.
        using var scope = factory.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<WorkItemApprovalService>();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.ApproveAsync("FOO-999", "acme", "prod", null, default));
        Assert.Contains("not known", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Approve_Throws_WhenAlreadyDecided()
    {
        await using var factory = new WorkItemTestFactory();
        factory.Current.Email = "approver@example.com";
        factory.Current.Name = "Approver User";
        factory.Current.RolesList = new() { "ReleaseApprovers", "InfraPortal.QA" };

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            await SeedPolicyEventCandidateAsync(db, "FOO-1", approverGroup: "ReleaseApprovers");

            // Pre-insert a decision for the same approver.
            db.WorkItemApprovals.Add(new WorkItemApproval
            {
                Id = Guid.NewGuid(),
                WorkItemKey = "FOO-1",
                Product = "acme",
                TargetEnv = "prod",
                ApproverEmail = "approver@example.com",
                ApproverName = "Approver User",
                Decision = WorkItemDecision.Approved,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        using (var scope = factory.Services.CreateScope())
        {
            var svc = scope.ServiceProvider.GetRequiredService<WorkItemApprovalService>();
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                svc.ApproveAsync("FOO-1", "acme", "prod", null, default));
            Assert.Contains("already", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
    }

    // NOTE: the separation-of-duties "excluded by role" path was removed (D17) along with
    // PromotionPolicy.ExcludeRole / candidate source-event linkage — anyone authorized for the
    // promotion may now decide on its tickets. The former Approve_Throws_WhenUserIsExcludedByRole
    // test exercised behaviour that no longer exists, so it was dropped.

    [Fact]
    public async Task Approve_Throws_WhenUserNotInApproverGroup()
    {
        await using var factory = new WorkItemTestFactory();
        // No matching role/group claim — and the empty IIdentityService has no members either.
        factory.Current.Email = "outsider@example.com";
        factory.Current.Name = "Outsider";
        factory.Current.RolesList = new() { "InfraPortal.User" };

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            await SeedPolicyEventCandidateAsync(db, "FOO-1", approverGroup: "ReleaseApprovers");
        }

        using (var scope = factory.Services.CreateScope())
        {
            var svc = scope.ServiceProvider.GetRequiredService<WorkItemApprovalService>();
            var ex = await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                svc.ApproveAsync("FOO-1", "acme", "prod", null, default));
            // Work-item sign-off is the QA role's jurisdiction: a user without QA/Admin (here just
            // InfraPortal.User) is refused, regardless of promotion approver-group membership.
            Assert.Contains("QA", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Approve_Throws_WhenPolicyIsAutoApprove()
    {
        await using var factory = new WorkItemTestFactory();
        factory.Current.Email = "any@example.com";
        factory.Current.RolesList = new() { "ReleaseApprovers", "InfraPortal.QA" };

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            // approverGroup = null → auto-approve. Candidate stays Pending in our seed because
            // we bypass the live ingest path; the JSON snapshot is still IsAutoApprove=true.
            await SeedPolicyEventCandidateAsync(db, "FOO-1", approverGroup: null);
        }

        using (var scope = factory.Services.CreateScope())
        {
            var svc = scope.ServiceProvider.GetRequiredService<WorkItemApprovalService>();
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                svc.ApproveAsync("FOO-1", "acme", "prod", null, default));
            Assert.Contains("auto-approve", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Approve_PicksMostRecentPendingCandidate()
    {
        await using var factory = new WorkItemTestFactory();
        factory.Current.Email = "approver@example.com";
        factory.Current.Name = "Approver";
        factory.Current.RolesList = new() { "ReleaseApprovers", "InfraPortal.QA" };

        Guid newerCandidateId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            // Two Pending candidates in (acme, prod), each self-contained and each carrying FOO-1 on
            // its own work-item index. The lookup must pick the most recently created one.
            await SeedPolicyEventCandidateAsync(
                db, "FOO-1",
                approverGroup: "ReleaseApprovers",
                createdAt: DateTimeOffset.UtcNow.AddMinutes(-10));

            var (_, _, newer) = await SeedPolicyEventCandidateAsync(
                db, "FOO-1",
                approverGroup: "ReleaseApprovers",
                createdAt: DateTimeOffset.UtcNow);
            newerCandidateId = newer.Id;
        }

        using (var scope = factory.Services.CreateScope())
        {
            var svc = scope.ServiceProvider.GetRequiredService<WorkItemApprovalService>();
            await svc.ApproveAsync("FOO-1", "acme", "prod", null, default);
        }

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            // Audit "afterState" carries the candidateId we picked. Most-recent → newer.
            var audit = await db.AuditLog.AsNoTracking()
                .FirstAsync(a => a.Action == "work-item.approved");
            Assert.NotNull(audit.AfterState);
            Assert.Contains(newerCandidateId.ToString(), audit.AfterState!,
                StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Block_RecordsRowWithBlockedDecision()
    {
        await using var factory = new WorkItemTestFactory();
        factory.Current.Email = "approver@example.com";
        factory.Current.Name = "Approver";
        factory.Current.RolesList = new() { "ReleaseApprovers", "InfraPortal.QA" };

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            await SeedPolicyEventCandidateAsync(db, "FOO-1", approverGroup: "ReleaseApprovers");
        }

        using (var scope = factory.Services.CreateScope())
        {
            var svc = scope.ServiceProvider.GetRequiredService<WorkItemApprovalService>();
            var row = await svc.BlockAsync("FOO-1", "acme", "prod", "not going out", default);
            Assert.Equal(WorkItemDecision.Blocked, row.Decision);
        }

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            var audit = await db.AuditLog.AsNoTracking()
                .Where(a => a.Action == "work-item.blocked").ToListAsync();
            Assert.Single(audit);
        }
    }

    [Fact]
    public async Task GetTicketContext_ReturnsApprovalsAndCanApproveFlag()
    {
        await using var factory = new WorkItemTestFactory();
        factory.Current.Email = "me@example.com";
        factory.Current.Name = "Me";
        factory.Current.RolesList = new() { "ReleaseApprovers", "InfraPortal.QA" };

        Guid candidateId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            var (_, _, c) = await SeedPolicyEventCandidateAsync(db, "FOO-1",
                approverGroup: "ReleaseApprovers");
            candidateId = c.Id;

            // Two prior approvals from other approvers.
            db.WorkItemApprovals.AddRange(
                new WorkItemApproval
                {
                    Id = Guid.NewGuid(),
                    WorkItemKey = "FOO-1", Product = "acme", TargetEnv = "prod",
                    ApproverEmail = "alice@example.com", ApproverName = "Alice",
                    Decision = WorkItemDecision.Approved,
                    CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-2),
                },
                new WorkItemApproval
                {
                    Id = Guid.NewGuid(),
                    WorkItemKey = "FOO-1", Product = "acme", TargetEnv = "prod",
                    ApproverEmail = "bob@example.com", ApproverName = "Bob",
                    Decision = WorkItemDecision.Approved,
                    CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
                });
            await db.SaveChangesAsync();
        }

        using (var scope = factory.Services.CreateScope())
        {
            var svc = scope.ServiceProvider.GetRequiredService<WorkItemApprovalService>();
            var ctx = await svc.GetTicketContextAsync("FOO-1", "acme", "prod", default);
            Assert.Equal(2, ctx.Approvals.Count);
            Assert.True(ctx.CanApprove);
            Assert.Null(ctx.BlockedReason);
            Assert.Equal(candidateId, ctx.PendingCandidateId);
        }
    }

    /// <summary>
    /// An existing decision by the caller is not a blocker — re-deciding (Approve ↔ Block ↔ Reject)
    /// is allowed, which is what makes Block usable as a reversible hold. The context surfaces the
    /// caller's current decision so the UI can offer the states they can move to.
    /// </summary>
    [Fact]
    public async Task GetTicketContext_SurfacesMyDecisionAndStaysActionable_WhenUserHasDecided()
    {
        await using var factory = new WorkItemTestFactory();
        factory.Current.Email = "me@example.com";
        factory.Current.RolesList = new() { "ReleaseApprovers", "InfraPortal.QA" };

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            await SeedPolicyEventCandidateAsync(db, "FOO-1", approverGroup: "ReleaseApprovers");

            db.WorkItemApprovals.Add(new WorkItemApproval
            {
                Id = Guid.NewGuid(),
                WorkItemKey = "FOO-1", Product = "acme", TargetEnv = "prod",
                ApproverEmail = "me@example.com", ApproverName = "Me",
                Decision = WorkItemDecision.Approved,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        using (var scope = factory.Services.CreateScope())
        {
            var svc = scope.ServiceProvider.GetRequiredService<WorkItemApprovalService>();
            var ctx = await svc.GetTicketContextAsync("FOO-1", "acme", "prod", default);
            Assert.True(ctx.CanApprove);
            Assert.Null(ctx.BlockedReason);
            Assert.Equal("Approved", ctx.MyDecision);
        }
    }

    // ── Block decision ──────────────────────────────────────────────────────

    /// <summary>
    /// A block records the decision and leaves the candidate Pending — that's the whole difference
    /// from a rejection, which vetoes and terminates it.
    /// </summary>
    [Fact]
    public async Task Issue_RecordsRow_AndLeavesCandidatePending()
    {
        await using var factory = new WorkItemTestFactory();
        factory.Current.Email = "qa@example.com";
        factory.Current.Name = "QA User";
        factory.Current.RolesList = new() { "ReleaseApprovers", "InfraPortal.QA" };

        Guid candidateId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            var (_, _, c) = await SeedPolicyEventCandidateAsync(db, "FOO-1",
                approverGroup: "ReleaseApprovers");
            candidateId = c.Id;
        }

        using (var scope = factory.Services.CreateScope())
        {
            var svc = scope.ServiceProvider.GetRequiredService<WorkItemApprovalService>();
            var row = await svc.RaiseIssueAsync("FOO-1", "acme", "prod", "waiting on test data", default);
            Assert.Equal(WorkItemDecision.Issue, row.Decision);
            Assert.Null(row.UpdatedAt);
        }

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            var candidate = await db.PromotionCandidates.AsNoTracking()
                .FirstAsync(c => c.Id == candidateId);
            Assert.Equal(PromotionStatus.Pending, candidate.Status);
        }
    }

    /// <summary>
    /// Changing one's mind updates the single row the unique index allows rather than colliding with
    /// it, stamping UpdatedAt and preserving CreatedAt.
    /// </summary>
    [Fact]
    public async Task Approve_AfterIssue_UpdatesExistingRowInPlace()
    {
        await using var factory = new WorkItemTestFactory();
        factory.Current.Email = "qa@example.com";
        factory.Current.Name = "QA User";
        factory.Current.RolesList = new() { "ReleaseApprovers", "InfraPortal.QA" };

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            await SeedPolicyEventCandidateAsync(db, "FOO-1", approverGroup: "ReleaseApprovers");
        }

        Guid rowId;
        DateTimeOffset createdAt;
        using (var scope = factory.Services.CreateScope())
        {
            var svc = scope.ServiceProvider.GetRequiredService<WorkItemApprovalService>();
            var raised = await svc.RaiseIssueAsync("FOO-1", "acme", "prod", "needs a fix", default);
            rowId = raised.Id;

            // Read CreatedAt back from storage rather than trusting the in-memory value: SQLite
            // round-trips DateTimeOffset at lower precision, so comparing the two would fail on
            // sub-tick digits that have nothing to do with the behaviour under test.
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            createdAt = (await db.WorkItemApprovals.AsNoTracking().FirstAsync(a => a.Id == rowId)).CreatedAt;
        }

        using (var scope = factory.Services.CreateScope())
        {
            var svc = scope.ServiceProvider.GetRequiredService<WorkItemApprovalService>();
            var approved = await svc.ApproveAsync("FOO-1", "acme", "prod", "unblocked", default);
            Assert.Equal(rowId, approved.Id);
            Assert.Equal(WorkItemDecision.Approved, approved.Decision);
            Assert.Equal("unblocked", approved.Comment);
            Assert.NotNull(approved.UpdatedAt);
        }

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            // One row per approver, still — and the original creation time survived the update.
            var rows = await db.WorkItemApprovals.AsNoTracking()
                .Where(a => a.WorkItemKey == "FOO-1").ToListAsync();
            Assert.Single(rows);
            Assert.Equal(createdAt, rows[0].CreatedAt);
        }
    }

    [Fact]
    public async Task Issue_Throws_WhenAlreadyRaisedByTheSameUser()
    {
        await using var factory = new WorkItemTestFactory();
        factory.Current.Email = "qa@example.com";
        factory.Current.RolesList = new() { "ReleaseApprovers", "InfraPortal.QA" };

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            await SeedPolicyEventCandidateAsync(db, "FOO-1", approverGroup: "ReleaseApprovers");
        }

        using (var scope = factory.Services.CreateScope())
        {
            var svc = scope.ServiceProvider.GetRequiredService<WorkItemApprovalService>();
            await svc.RaiseIssueAsync("FOO-1", "acme", "prod", null, default);
        }

        using (var scope = factory.Services.CreateScope())
        {
            var svc = scope.ServiceProvider.GetRequiredService<WorkItemApprovalService>();
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                svc.RaiseIssueAsync("FOO-1", "acme", "prod", null, default));
            Assert.Contains("already", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task POST_Blocks_Returns200_AndRecordsBlockedDecision()
    {
        await using var factory = new WorkItemTestFactory();
        factory.Current.Email = "admin@localhost";
        factory.Current.RolesList = new() { "InfraPortal.Admin", "InfraPortal.QA" };

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            await SeedPolicyEventCandidateAsync(db, "FOO-1", approverGroup: "ReleaseApprovers");
        }

        var client = factory.CreateAdminClient();
        var response = await client.PostAsJsonAsync("/api/work-items/FOO-1/blocks",
            new { product = "acme", targetEnv = "prod", comment = "on hold" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await Deserialize(response);
        Assert.Equal("Blocked", body.GetProperty("decision").GetString());
    }

    // ── Comments ────────────────────────────────────────────────────────────

    [Fact]
    public async Task AddComment_Throws_WhenWorkItemIsUnknown()
    {
        await using var factory = new WorkItemTestFactory();
        factory.Current.Email = "qa@example.com";
        factory.Current.RolesList = new() { "InfraPortal.QA" };

        using var scope = factory.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<WorkItemApprovalService>();
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            svc.AddCommentAsync("NOPE-1", "acme", "prod", "hello", default));
    }

    [Fact]
    public async Task Comments_RoundTrip_ScopedToKeyProductAndEnv()
    {
        await using var factory = new WorkItemTestFactory();
        factory.Current.Email = "qa@example.com";
        factory.Current.Name = "QA User";
        factory.Current.RolesList = new() { "InfraPortal.QA" };

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            await SeedPolicyEventCandidateAsync(db, "FOO-1", approverGroup: "ReleaseApprovers");
        }

        Guid commentId;
        using (var scope = factory.Services.CreateScope())
        {
            var svc = scope.ServiceProvider.GetRequiredService<WorkItemApprovalService>();
            var created = await svc.AddCommentAsync("FOO-1", "acme", "prod", "  needs a retest  ", default);
            commentId = created.Id;
            Assert.Equal("needs a retest", created.Body);
            Assert.Equal("qa@example.com", created.AuthorEmail);
            Assert.Null(created.UpdatedAt);
        }

        using (var scope = factory.Services.CreateScope())
        {
            var svc = scope.ServiceProvider.GetRequiredService<WorkItemApprovalService>();
            var updated = await svc.UpdateCommentAsync(commentId, "retested, fine", default);
            Assert.Equal("retested, fine", updated.Body);
            Assert.NotNull(updated.UpdatedAt);

            var thread = await svc.GetCommentsAsync("FOO-1", "acme", "prod", default);
            Assert.Single(thread);

            // A different env is a different thread.
            Assert.Empty(await svc.GetCommentsAsync("FOO-1", "acme", "staging", default));
        }

        using (var scope = factory.Services.CreateScope())
        {
            var svc = scope.ServiceProvider.GetRequiredService<WorkItemApprovalService>();
            await svc.DeleteCommentAsync(commentId, default);
            Assert.Empty(await svc.GetCommentsAsync("FOO-1", "acme", "prod", default));
        }
    }

    [Fact]
    public async Task UpdateComment_Throws_WhenNotAuthorOrAdmin()
    {
        await using var factory = new WorkItemTestFactory();
        factory.Current.Email = "author@example.com";
        factory.Current.RolesList = new() { "InfraPortal.QA" };

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            await SeedPolicyEventCandidateAsync(db, "FOO-1", approverGroup: "ReleaseApprovers");
        }

        Guid commentId;
        using (var scope = factory.Services.CreateScope())
        {
            var svc = scope.ServiceProvider.GetRequiredService<WorkItemApprovalService>();
            commentId = (await svc.AddCommentAsync("FOO-1", "acme", "prod", "mine", default)).Id;
        }

        factory.Current.Email = "someone-else@example.com";
        using (var scope = factory.Services.CreateScope())
        {
            var svc = scope.ServiceProvider.GetRequiredService<WorkItemApprovalService>();
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                svc.UpdateCommentAsync(commentId, "hijacked", default));
        }
    }

    // ── Detail projection ───────────────────────────────────────────────────

    [Fact]
    public async Task GetDetail_ReturnsNull_WhenWorkItemIsUnknown()
    {
        await using var factory = new WorkItemTestFactory();
        factory.Current.RolesList = new() { "InfraPortal.QA" };

        using var scope = factory.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<WorkItemApprovalService>();
        Assert.Null(await svc.GetDetailAsync("NOPE-1", "acme", "prod", default));
    }

    /// <summary>
    /// The detail projection lists every candidate carrying the ticket and picks the newest Pending
    /// one as primary — that's the candidate participant assignments are written to.
    /// </summary>
    [Fact]
    public async Task GetDetail_PicksNewestPendingCandidateAsPrimary_AndListsAll()
    {
        await using var factory = new WorkItemTestFactory();
        factory.Current.Email = "qa@example.com";
        factory.Current.RolesList = new() { "InfraPortal.QA" };

        Guid newerId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            await SeedPolicyEventCandidateAsync(db, "FOO-1", approverGroup: "ReleaseApprovers",
                service: "api", createdAt: DateTimeOffset.UtcNow.AddHours(-2));
            var (_, _, newer) = await SeedPolicyEventCandidateAsync(db, "FOO-1",
                approverGroup: "ReleaseApprovers", service: "web",
                createdAt: DateTimeOffset.UtcNow);
            newerId = newer.Id;
        }

        using (var scope = factory.Services.CreateScope())
        {
            var svc = scope.ServiceProvider.GetRequiredService<WorkItemApprovalService>();
            var detail = await svc.GetDetailAsync("FOO-1", "acme", "prod", default);
            Assert.NotNull(detail);
            Assert.Equal(2, detail!.Candidates.Count);
            Assert.Equal(newerId, detail.PrimaryCandidateId);
            Assert.True(detail.CanManage);
            Assert.Single(detail.Candidates.Where(c => c.IsPrimary));
        }
    }

    /// <summary>
    /// A superseded build is a promotion that was replaced before it shipped — noise on a page about
    /// the work item, so it's left out of the list. It still resolves as primary when it's all the
    /// ticket has, so people assignments keep a write target.
    /// </summary>
    [Fact]
    public async Task GetDetail_OmitsSupersededCandidates()
    {
        await using var factory = new WorkItemTestFactory();
        factory.Current.Email = "qa@example.com";
        factory.Current.RolesList = new() { "InfraPortal.QA" };

        Guid supersededId;
        Guid liveId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            var (_, _, old) = await SeedPolicyEventCandidateAsync(db, "FOO-1",
                approverGroup: "ReleaseApprovers", service: "api",
                createdAt: DateTimeOffset.UtcNow.AddHours(-2));
            old.Status = PromotionStatus.Superseded;
            supersededId = old.Id;
            var (_, _, live) = await SeedPolicyEventCandidateAsync(db, "FOO-1",
                approverGroup: "ReleaseApprovers", service: "api",
                createdAt: DateTimeOffset.UtcNow);
            liveId = live.Id;
            await db.SaveChangesAsync();
        }

        using (var scope = factory.Services.CreateScope())
        {
            var svc = scope.ServiceProvider.GetRequiredService<WorkItemApprovalService>();
            var detail = await svc.GetDetailAsync("FOO-1", "acme", "prod", default);
            Assert.NotNull(detail);
            Assert.Equal(new[] { liveId }, detail!.Candidates.Select(c => c.Id).ToArray());
            Assert.Equal(liveId, detail.PrimaryCandidateId);
            Assert.DoesNotContain(supersededId, detail.Candidates.Select(c => c.Id));
        }
    }

    /// <summary>
    /// The body the producer sent on the work-item reference reaches the detail page verbatim,
    /// newlines and all — it's shown as-is, so nothing may normalise it on the way out.
    /// </summary>
    [Fact]
    public async Task GetDetail_ReturnsContentVerbatim()
    {
        await using var factory = new WorkItemTestFactory();
        factory.Current.Email = "qa@example.com";
        factory.Current.RolesList = new() { "InfraPortal.QA" };

        const string body = "Checkout double-charges on retry.\n\nRepro:\n1. Submit\n2. Retry";

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            await SeedPolicyEventCandidateAsync(db, "FOO-1", approverGroup: "ReleaseApprovers",
                content: body);
        }

        using (var scope = factory.Services.CreateScope())
        {
            var svc = scope.ServiceProvider.GetRequiredService<WorkItemApprovalService>();
            var detail = await svc.GetDetailAsync("FOO-1", "acme", "prod", default);
            Assert.NotNull(detail);
            Assert.Equal(body, detail!.Content);
        }
    }

    /// <summary>
    /// Content follows the same "prefer primary, else whoever has it" rule as title and url: a later
    /// ingest that omitted the description shouldn't blank out a body an earlier one supplied.
    /// </summary>
    [Fact]
    public async Task GetDetail_FallsBackToAnotherCandidatesContent_WhenPrimaryRowHasNone()
    {
        await using var factory = new WorkItemTestFactory();
        factory.Current.Email = "qa@example.com";
        factory.Current.RolesList = new() { "InfraPortal.QA" };

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            // Older candidate carries the description; the newer (primary) one doesn't.
            await SeedPolicyEventCandidateAsync(db, "FOO-1", approverGroup: "ReleaseApprovers",
                service: "api", createdAt: DateTimeOffset.UtcNow.AddHours(-2),
                content: "the original description");
            await SeedPolicyEventCandidateAsync(db, "FOO-1", approverGroup: "ReleaseApprovers",
                service: "web", createdAt: DateTimeOffset.UtcNow, content: null);
        }

        using (var scope = factory.Services.CreateScope())
        {
            var svc = scope.ServiceProvider.GetRequiredService<WorkItemApprovalService>();
            var detail = await svc.GetDetailAsync("FOO-1", "acme", "prod", default);
            Assert.NotNull(detail);
            Assert.Equal("the original description", detail!.Content);
        }
    }

    /// <summary>
    /// A whitespace-only body is no body. Normalising it to null server-side leaves the client one
    /// emptiness check, and keeps it from rendering a Content section with nothing in it.
    /// </summary>
    [Fact]
    public async Task GetDetail_ReturnsNullContent_WhenBodyIsBlank()
    {
        await using var factory = new WorkItemTestFactory();
        factory.Current.Email = "qa@example.com";
        factory.Current.RolesList = new() { "InfraPortal.QA" };

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            await SeedPolicyEventCandidateAsync(db, "FOO-1", approverGroup: "ReleaseApprovers",
                content: "   \n  ");
        }

        using (var scope = factory.Services.CreateScope())
        {
            var svc = scope.ServiceProvider.GetRequiredService<WorkItemApprovalService>();
            var detail = await svc.GetDetailAsync("FOO-1", "acme", "prod", default);
            Assert.NotNull(detail);
            Assert.Null(detail!.Content);
        }
    }

    /// <summary>
    /// Both display lines reach the detail page as the projection resolved them: the ticket's own name
    /// as the title, its commit messages underneath. A subtitle that merely repeats the title is
    /// dropped server-side — a second line saying the same thing is noise, not information.
    /// </summary>
    [Fact]
    public async Task GetDetail_ReturnsSubTitle_AndDropsItWhenItRepeatsTheTitle()
    {
        await using var factory = new WorkItemTestFactory();
        factory.Current.Email = "qa@example.com";
        factory.Current.RolesList = new() { "InfraPortal.QA" };

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            await SeedPolicyEventCandidateAsync(db, "FOO-1", approverGroup: "ReleaseApprovers",
                title: "Fix retry",
                subTitle: "fix: send an idempotency key with the retry • test: cover the duplicate submit");
            await SeedPolicyEventCandidateAsync(db, "BAR-1", approverGroup: "ReleaseApprovers",
                title: "Fix retry", subTitle: "Fix retry");
        }

        using (var scope = factory.Services.CreateScope())
        {
            var svc = scope.ServiceProvider.GetRequiredService<WorkItemApprovalService>();

            var detail = await svc.GetDetailAsync("FOO-1", "acme", "prod", default);
            Assert.NotNull(detail);
            Assert.Equal("Fix retry", detail!.Title);
            Assert.Equal(
                "fix: send an idempotency key with the retry • test: cover the duplicate submit",
                detail.SubTitle);

            var duplicate = await svc.GetDetailAsync("BAR-1", "acme", "prod", default);
            Assert.NotNull(duplicate);
            Assert.Equal("Fix retry", duplicate!.Title);
            Assert.Null(duplicate.SubTitle);
        }
    }

    [Fact]
    public async Task GetTicketContext_CannotApprove_WhenTicketIsUnknown()
    {
        await using var factory = new WorkItemTestFactory();
        factory.Current.Email = "me@example.com";
        factory.Current.RolesList = new() { "ReleaseApprovers", "InfraPortal.QA" };

        using var scope = factory.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<WorkItemApprovalService>();
        var ctx = await svc.GetTicketContextAsync("ZZZ-999", "acme", "prod", default);
        Assert.False(ctx.CanApprove);
        Assert.Equal("This work item is not known for that product and environment", ctx.BlockedReason);
        Assert.Null(ctx.PendingCandidateId);
        Assert.Empty(ctx.Approvals);
    }

    /// <summary>
    /// A known work item whose promotion has moved on is orphaned, not undecidable: the context must
    /// still offer the sign-off, otherwise the item can never be closed out and sits in the queue.
    /// </summary>
    [Fact]
    public async Task GetTicketContext_CanApprove_WhenTicketIsOrphaned()
    {
        await using var factory = new WorkItemTestFactory();
        factory.Current.Email = "me@example.com";
        factory.Current.RolesList = new() { "ReleaseApprovers", "InfraPortal.QA" };

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            var (_, _, c) = await SeedPolicyEventCandidateAsync(db, "ORPH-1",
                approverGroup: "ReleaseApprovers");
            c.Status = PromotionStatus.Superseded;
            await db.SaveChangesAsync();
        }

        using (var scope = factory.Services.CreateScope())
        {
            var svc = scope.ServiceProvider.GetRequiredService<WorkItemApprovalService>();
            var ctx = await svc.GetTicketContextAsync("ORPH-1", "acme", "prod", default);
            Assert.True(ctx.CanApprove);
            Assert.Null(ctx.BlockedReason);
            Assert.Null(ctx.PendingCandidateId);
        }
    }

    [Fact]
    public async Task GetPendingForCurrentUser_ReturnsTicketsUserCouldSignOff()
    {
        await using var factory = new WorkItemTestFactory();
        factory.Current.Email = "me@example.com";
        factory.Current.RolesList = new() { "ReleaseApprovers", "InfraPortal.QA" };

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            await SeedPolicyEventCandidateAsync(db, "FOO-1", approverGroup: "ReleaseApprovers");
            await SeedPolicyEventCandidateAsync(db, "FOO-2", approverGroup: "ReleaseApprovers",
                service: "api2");
        }

        using (var scope = factory.Services.CreateScope())
        {
            var svc = scope.ServiceProvider.GetRequiredService<WorkItemApprovalService>();
            var queue = await svc.GetPendingForCurrentUserAsync(default);
            var pending = queue.Tickets;
            Assert.Equal(2, pending.Count);
            var keys = pending.Select(p => p.WorkItemKey).OrderBy(k => k).ToList();
            Assert.Equal(new[] { "FOO-1", "FOO-2" }, keys);
            Assert.All(pending, p => Assert.Equal("acme", p.Product));
            Assert.All(pending, p => Assert.Equal("prod", p.TargetEnv));
        }
    }

    [Fact]
    public async Task GetPendingForCurrentUser_SameTicketOnMultipleCandidates_EmittedOnceWithCount()
    {
        // The same ticket backs two Pending candidates (same product/targetEnv, different services).
        // A sign-off is shared across them, so the queue shows the ticket ONCE, with BlockingPromotions
        // reflecting how many promotions it unblocks.
        await using var factory = new WorkItemTestFactory();
        factory.Current.Email = "qa@example.com";
        factory.Current.RolesList = new() { "InfraPortal.QA" };

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            await SeedPolicyEventCandidateAsync(db, "FOO-1", approverGroup: "ReleaseApprovers", service: "a");
            await SeedPolicyEventCandidateAsync(db, "FOO-1", approverGroup: "ReleaseApprovers", service: "b");
        }

        using (var scope = factory.Services.CreateScope())
        {
            var svc = scope.ServiceProvider.GetRequiredService<WorkItemApprovalService>();
            var queue = await svc.GetPendingForCurrentUserAsync(default);

            var row = Assert.Single(queue.Tickets);
            Assert.Equal("FOO-1", row.WorkItemKey);
            Assert.Equal(2, row.BlockingPromotions);
        }
    }

    [Fact]
    public async Task GetPendingForCurrentUser_ExcludesTicketsAlreadyDecided()
    {
        await using var factory = new WorkItemTestFactory();
        factory.Current.Email = "me@example.com";
        factory.Current.RolesList = new() { "ReleaseApprovers", "InfraPortal.QA" };

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            await SeedPolicyEventCandidateAsync(db, "FOO-1", approverGroup: "ReleaseApprovers", service: "a");
            await SeedPolicyEventCandidateAsync(db, "FOO-2", approverGroup: "ReleaseApprovers", service: "b");
            await SeedPolicyEventCandidateAsync(db, "FOO-3", approverGroup: "ReleaseApprovers", service: "c");

            // Already decided FOO-2.
            db.WorkItemApprovals.Add(new WorkItemApproval
            {
                Id = Guid.NewGuid(),
                WorkItemKey = "FOO-2", Product = "acme", TargetEnv = "prod",
                ApproverEmail = "me@example.com", ApproverName = "Me",
                Decision = WorkItemDecision.Approved,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        using (var scope = factory.Services.CreateScope())
        {
            var svc = scope.ServiceProvider.GetRequiredService<WorkItemApprovalService>();
            var queue = await svc.GetPendingForCurrentUserAsync(default);
            var pending = queue.Tickets;
            var keys = pending.Select(p => p.WorkItemKey).OrderBy(k => k).ToList();
            Assert.Equal(new[] { "FOO-1", "FOO-3" }, keys);
        }
    }

    // NOTE: GetPendingForCurrentUser_ExcludesTicketsWhereUserIsExcludedByRole was dropped — the
    // excluded-role (separation-of-duties) filtering it asserted was removed (D17).

    // ── Deployed environments ───────────────────────────────────────────────
    // A work item reports where its change is actually running, resolved from the deploy events that
    // shipped the carrying version. It deliberately does NOT report the promotion's source/target
    // edge: the target env is where the build is asking to go, which is the one place the change
    // can't be tested yet.

    [Fact]
    public async Task GetPendingForCurrentUser_ReportsEnvironmentsTheVersionIsDeployedTo()
    {
        await using var factory = new WorkItemTestFactory();
        factory.Current.Email = "qa@example.com";
        factory.Current.RolesList = new() { "InfraPortal.QA" };

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            // Candidate is acme/api v1.0.0 staging → prod; the seed also lands v1.0.0 in staging.
            await SeedPolicyEventCandidateAsync(db, "ENV-1", approverGroup: "ReleaseApprovers");

            db.DeployEvents.AddRange(
                // Same version, another environment — belongs on the row.
                NewDeployEventFor("acme", "api", "dev", "v1.0.0"),
                // A different version of the same service — not this work item's change.
                NewDeployEventFor("acme", "api", "prod", "v0.9.0"),
                // Right version, failed deploy — nothing to test there.
                NewDeployEventFor("acme", "api", "uat", "v1.0.0", status: "failed"),
                // Another service that happens to share the version string.
                NewDeployEventFor("acme", "other", "qa", "v1.0.0"));
            await db.SaveChangesAsync();
        }

        using (var scope = factory.Services.CreateScope())
        {
            var svc = scope.ServiceProvider.GetRequiredService<WorkItemApprovalService>();
            var row = Assert.Single((await svc.GetPendingForCurrentUserAsync(default)).Tickets);

            Assert.Equal(
                new[] { "dev", "staging" },
                row.Environments.Select(e => e.Environment).OrderBy(e => e).ToArray());
            Assert.All(row.Environments, e => Assert.Equal("v1.0.0", e.Version));
            Assert.All(row.Environments, e => Assert.Equal("api", e.Service));
        }
    }

    [Fact]
    public async Task GetDetail_UnionsEnvironmentsAcrossEveryVersionThatCarriedTheItem()
    {
        // An environment may still be sitting on a build that was superseded before it shipped, and
        // that build carried the ticket too — so it's a place the change can be exercised.
        await using var factory = new WorkItemTestFactory();
        factory.Current.Email = "qa@example.com";
        factory.Current.RolesList = new() { "InfraPortal.QA" };

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            await SeedPolicyEventCandidateAsync(db, "ENVD-1", approverGroup: "ReleaseApprovers");

            var older = NewCandidate("ReleaseApprovers");
            older.Version = "v0.9.0";
            older.Status = PromotionStatus.Superseded;
            older.CreatedAt = DateTimeOffset.UtcNow.AddHours(-2);
            db.PromotionCandidates.Add(older);
            db.PromotionWorkItems.Add(new PromotionWorkItem
            {
                Id = Guid.NewGuid(),
                CandidateId = older.Id,
                WorkItemKey = "ENVD-1",
                Product = "acme",
                TargetEnv = "prod",
                CreatedAt = DateTimeOffset.UtcNow.AddHours(-2),
            });
            db.DeployEvents.Add(NewDeployEventFor("acme", "api", "dev", "v0.9.0"));
            await db.SaveChangesAsync();
        }

        using (var scope = factory.Services.CreateScope())
        {
            var svc = scope.ServiceProvider.GetRequiredService<WorkItemApprovalService>();
            var detail = await svc.GetDetailAsync("ENVD-1", "acme", "prod", default);
            Assert.NotNull(detail);

            var byEnv = detail!.Environments.ToDictionary(e => e.Environment, e => e.Version);
            Assert.Equal("v1.0.0", byEnv["staging"]);
            Assert.Equal("v0.9.0", byEnv["dev"]);
            Assert.Equal(2, byEnv.Count);
        }
    }

    // ── Decided-history tests (GetDecidedAsync) ─────────────────────────────

    [Fact]
    public async Task GetDecided_WithNullSince_ReturnsDecisionsOlderThanOneDay()
    {
        // Regression: the "All time" time-frame sends no `since`; the endpoint used to coerce a
        // missing `since` into UtcNow.AddDays(-1), so anything decided > 24h ago silently vanished.
        // GetDecidedAsync must treat since=null as "no cutoff".
        await using var factory = new WorkItemTestFactory();

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            await SeedPolicyEventCandidateAsync(db, "OLD-1", approverGroup: "ReleaseApprovers", service: "a");
            await SeedPolicyEventCandidateAsync(db, "NEW-1", approverGroup: "ReleaseApprovers", service: "b");
            db.WorkItemApprovals.AddRange(
                new WorkItemApproval
                {
                    Id = Guid.NewGuid(),
                    WorkItemKey = "OLD-1", Product = "acme", TargetEnv = "prod",
                    ApproverEmail = "me@example.com", ApproverName = "Me",
                    Decision = WorkItemDecision.Approved,
                    CreatedAt = DateTimeOffset.UtcNow.AddDays(-10),
                },
                new WorkItemApproval
                {
                    Id = Guid.NewGuid(),
                    WorkItemKey = "NEW-1", Product = "acme", TargetEnv = "prod",
                    ApproverEmail = "me@example.com", ApproverName = "Me",
                    Decision = WorkItemDecision.Blocked,
                    CreatedAt = DateTimeOffset.UtcNow,
                });
            await db.SaveChangesAsync();
        }

        using (var scope = factory.Services.CreateScope())
        {
            var svc = scope.ServiceProvider.GetRequiredService<WorkItemApprovalService>();

            // null cutoff → both, including the 10-day-old decision.
            var all = await svc.GetDecidedAsync(decision: null, since: null);
            var allKeys = all.Tickets.Select(t => t.WorkItemKey).OrderBy(k => k).ToList();
            Assert.Equal(new[] { "NEW-1", "OLD-1" }, allKeys);

            // 24h cutoff → only the recent one (proves the cutoff still works, so null != default).
            var recent = await svc.GetDecidedAsync(decision: null, since: DateTimeOffset.UtcNow.AddDays(-1));
            Assert.Equal(new[] { "NEW-1" }, recent.Tickets.Select(t => t.WorkItemKey).ToList());
        }
    }

    [Fact]
    public async Task GetDecided_DecidedBy_NarrowsToDecider_AndReturnsFullDeciderRollup()
    {
        await using var factory = new WorkItemTestFactory();

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            await SeedPolicyEventCandidateAsync(db, "K-1", approverGroup: "ReleaseApprovers", service: "a");
            await SeedPolicyEventCandidateAsync(db, "K-2", approverGroup: "ReleaseApprovers", service: "b");
            await SeedPolicyEventCandidateAsync(db, "K-3", approverGroup: "ReleaseApprovers", service: "c");
            db.WorkItemApprovals.AddRange(
                Approval("K-1", "alice@example.com", "Alice", WorkItemDecision.Approved),
                Approval("K-2", "alice@example.com", "Alice", WorkItemDecision.Blocked),
                Approval("K-3", "bob@example.com", "Bob", WorkItemDecision.Approved));
            await db.SaveChangesAsync();
        }

        using (var scope = factory.Services.CreateScope())
        {
            var svc = scope.ServiceProvider.GetRequiredService<WorkItemApprovalService>();

            // Narrow by Alice (mixed case → case-insensitive match).
            var aliceOnly = await svc.GetDecidedAsync(decision: null, since: null, decidedBy: "ALICE@example.com");
            Assert.Equal(new[] { "K-1", "K-2" }, aliceOnly.Tickets.Select(t => t.WorkItemKey).OrderBy(k => k).ToList());

            // The decider rollup is computed BEFORE narrowing, so it still lists Bob too — that's
            // what keeps the "Decided by" dropdown from hiding people once a pick is active.
            var rollup = aliceOnly.Assignees.ToDictionary(a => a.Email, a => a.Count, StringComparer.OrdinalIgnoreCase);
            Assert.Equal(2, rollup["alice@example.com"]);
            Assert.Equal(1, rollup["bob@example.com"]);
            Assert.All(aliceOnly.Assignees, a => Assert.Equal("", a.Role));

            // No decidedBy → all three decisions.
            var everyone = await svc.GetDecidedAsync(decision: null, since: null);
            Assert.Equal(3, everyone.Tickets.Count);
        }
    }

    // ── Endpoint-level tests (HTTP) ─────────────────────────────────────────

    [Fact]
    public async Task POST_Approvals_Returns200_WithApprovalRowAndCandidateId()
    {
        await using var factory = new WorkItemTestFactory();
        factory.Current.Email = "approver@example.com";
        factory.Current.Name = "Approver";
        factory.Current.RolesList = new() { "ReleaseApprovers", "InfraPortal.QA" };

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            await SeedPolicyEventCandidateAsync(db, "FOO-1", approverGroup: "ReleaseApprovers");
        }

        var client = factory.CreateAdminClient();
        // Admin client is needed only to satisfy the [Authorize] CanApprove pipeline.
        // Authority is checked server-side against the fake ICurrentUser.
        var response = await client.PostAsJsonAsync("/api/work-items/FOO-1/approvals",
            new { product = "acme", targetEnv = "prod", comment = "ship it" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await Deserialize(response);
        Assert.Equal("FOO-1", body.GetProperty("workItemKey").GetString());
        Assert.Equal("acme", body.GetProperty("product").GetString());
        Assert.Equal("prod", body.GetProperty("targetEnv").GetString());
        Assert.Equal("Approved", body.GetProperty("decision").GetString());
        Assert.Equal("approver@example.com", body.GetProperty("approverEmail").GetString());
        Assert.Equal("ship it", body.GetProperty("comment").GetString());
    }

    [Fact]
    public async Task POST_Approvals_Returns400_WhenTicketIsUnknown()
    {
        await using var factory = new WorkItemTestFactory();
        factory.Current.Email = "approver@example.com";
        factory.Current.RolesList = new() { "ReleaseApprovers", "InfraPortal.QA" };

        var client = factory.CreateAdminClient();
        var response = await client.PostAsJsonAsync("/api/work-items/NOPE-1/approvals",
            new { product = "acme", targetEnv = "prod" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var body = await Deserialize(response);
        Assert.Contains("not known", body.GetProperty("error").GetString()!,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task POST_Approvals_Returns400_WhenAlreadyDecided()
    {
        await using var factory = new WorkItemTestFactory();
        factory.Current.Email = "approver@example.com";
        factory.Current.Name = "Approver";
        factory.Current.RolesList = new() { "ReleaseApprovers", "InfraPortal.QA" };

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            await SeedPolicyEventCandidateAsync(db, "FOO-1", approverGroup: "ReleaseApprovers");
            db.WorkItemApprovals.Add(new WorkItemApproval
            {
                Id = Guid.NewGuid(),
                WorkItemKey = "FOO-1", Product = "acme", TargetEnv = "prod",
                ApproverEmail = "approver@example.com", ApproverName = "Approver",
                Decision = WorkItemDecision.Approved,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var client = factory.CreateAdminClient();
        var response = await client.PostAsJsonAsync("/api/work-items/FOO-1/approvals",
            new { product = "acme", targetEnv = "prod" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await Deserialize(response);
        Assert.Contains("already", body.GetProperty("error").GetString()!,
            StringComparison.OrdinalIgnoreCase);
    }

    // NOTE: POST_Approvals_Returns403_WhenExcludedRole was dropped — the excluded-role (D17)
    // separation-of-duties path it covered no longer exists.

    [Fact]
    public async Task POST_Approvals_Returns403_WhenNotInApproverGroup()
    {
        await using var factory = new WorkItemTestFactory();
        factory.Current.Email = "outsider@example.com";
        factory.Current.RolesList = new() { "InfraPortal.User" };

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            await SeedPolicyEventCandidateAsync(db, "FOO-1", approverGroup: "ReleaseApprovers");
        }

        var client = factory.CreateAdminClient();
        var response = await client.PostAsJsonAsync("/api/work-items/FOO-1/approvals",
            new { product = "acme", targetEnv = "prod" });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task POST_Issues_Returns200_AndRecordsIssue()
    {
        await using var factory = new WorkItemTestFactory();
        factory.Current.Email = "approver@example.com";
        factory.Current.Name = "Approver";
        factory.Current.RolesList = new() { "ReleaseApprovers", "InfraPortal.QA" };

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            await SeedPolicyEventCandidateAsync(db, "FOO-1", approverGroup: "ReleaseApprovers");
        }

        var client = factory.CreateAdminClient();
        var response = await client.PostAsJsonAsync("/api/work-items/FOO-1/issues",
            new { product = "acme", targetEnv = "prod", comment = "found a regression" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await Deserialize(response);
        Assert.Equal("Issue", body.GetProperty("decision").GetString());

        using var scope2 = factory.Services.CreateScope();
        var db2 = scope2.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var rows = await db2.WorkItemApprovals.AsNoTracking().ToListAsync();
        Assert.Single(rows);
        Assert.Equal(WorkItemDecision.Issue, rows[0].Decision);
    }

    [Fact]
    public async Task GET_TicketContext_Returns200_WithExpectedShape()
    {
        await using var factory = new WorkItemTestFactory();
        factory.Current.Email = "me@example.com";
        factory.Current.RolesList = new() { "ReleaseApprovers", "InfraPortal.QA" };

        Guid candidateId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            var (_, _, c) = await SeedPolicyEventCandidateAsync(db, "FOO-1",
                approverGroup: "ReleaseApprovers");
            candidateId = c.Id;
        }

        var client = factory.CreateAdminClient();
        var response = await client.GetAsync("/api/work-items/FOO-1?product=acme&targetEnv=prod");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await Deserialize(response);
        Assert.Equal("FOO-1", body.GetProperty("workItemKey").GetString());
        Assert.Equal("acme", body.GetProperty("product").GetString());
        Assert.Equal("prod", body.GetProperty("targetEnv").GetString());
        Assert.Equal(candidateId.ToString(), body.GetProperty("pendingCandidateId").GetString());
        Assert.True(body.GetProperty("canApprove").GetBoolean());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("blockedReason").ValueKind);
        Assert.Equal(JsonValueKind.Array, body.GetProperty("approvals").ValueKind);
    }

    [Fact]
    public async Task GET_MePending_ReturnsArrayOfPendingTicketViews()
    {
        await using var factory = new WorkItemTestFactory();
        factory.Current.Email = "me@example.com";
        factory.Current.RolesList = new() { "ReleaseApprovers", "InfraPortal.QA" };

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            await SeedPolicyEventCandidateAsync(db, "FOO-1", approverGroup: "ReleaseApprovers");
        }

        var client = factory.CreateAdminClient();
        var response = await client.GetAsync("/api/work-items/me/pending");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await Deserialize(response);
        var tickets = body.GetProperty("tickets");
        Assert.Equal(JsonValueKind.Array, tickets.ValueKind);
        Assert.Equal(1, tickets.GetArrayLength());
        var t = tickets[0];
        Assert.Equal("FOO-1", t.GetProperty("workItemKey").GetString());
        Assert.Equal("acme", t.GetProperty("product").GetString());
        Assert.Equal("prod", t.GetProperty("targetEnv").GetString());
    }

    [Fact]
    public async Task GET_MePending_DoesNotReturn_AlreadyDecidedTickets()
    {
        await using var factory = new WorkItemTestFactory();
        factory.Current.Email = "me@example.com";
        factory.Current.RolesList = new() { "ReleaseApprovers", "InfraPortal.QA" };

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            await SeedPolicyEventCandidateAsync(db, "FOO-1", approverGroup: "ReleaseApprovers", service: "a");
            await SeedPolicyEventCandidateAsync(db, "FOO-2", approverGroup: "ReleaseApprovers", service: "b");
            db.WorkItemApprovals.Add(new WorkItemApproval
            {
                Id = Guid.NewGuid(),
                WorkItemKey = "FOO-1", Product = "acme", TargetEnv = "prod",
                ApproverEmail = "me@example.com", ApproverName = "Me",
                Decision = WorkItemDecision.Approved,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var client = factory.CreateAdminClient();
        var response = await client.GetAsync("/api/work-items/me/pending");
        var body = await Deserialize(response);
        var tickets = body.GetProperty("tickets");
        Assert.Equal(1, tickets.GetArrayLength());
        Assert.Equal("FOO-2", tickets[0].GetProperty("workItemKey").GetString());
    }

    // ── Maintenance: the "No live promotion" sweep ──────────────────────────

    /// <summary>
    /// The dry run is a report: it lists what the apply would sign off and writes nothing at all.
    /// </summary>
    [Fact]
    public async Task ApproveOrphaned_DryRun_ListsStrandedItems_AndWritesNothing()
    {
        await using var factory = new WorkItemTestFactory();
        factory.Current.Email = "admin@example.com";
        factory.Current.RolesList = new() { "InfraPortal.Admin" };

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            var (_, _, dead) = await SeedPolicyEventCandidateAsync(db, "ORPH-1",
                approverGroup: "ReleaseApprovers", title: "Stranded ticket");
            dead.Status = PromotionStatus.Superseded;
            await db.SaveChangesAsync();
        }

        using (var scope = factory.Services.CreateScope())
        {
            var svc = scope.ServiceProvider.GetRequiredService<WorkItemApprovalService>();
            var result = await svc.ApproveOrphanedWorkItemsAsync(dryRun: true);

            Assert.True(result.DryRun);
            Assert.Equal(1, result.Examined);
            Assert.Equal(0, result.Approved);
            Assert.Equal(0, result.Failed);
            var item = Assert.Single(result.Items);
            Assert.Equal("ORPH-1", item.WorkItemKey);
            Assert.Equal("Stranded ticket", item.Title);
            Assert.Equal("acme", item.Product);
            Assert.Equal("prod", item.TargetEnv);
            Assert.Equal("api", item.Service);
            Assert.Equal("Superseded", item.CandidateStatus);
            Assert.Null(item.Error);
        }

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            Assert.Empty(await db.WorkItemApprovals.AsNoTracking().ToListAsync());
            Assert.Empty(await db.WorkItemComments.AsNoTracking().ToListAsync());
        }
    }

    /// <summary>
    /// The apply signs each item off on the caller's name, through the ordinary decision path — so
    /// the approval row, the audit entry and the comment-thread entry all appear.
    /// </summary>
    [Fact]
    public async Task ApproveOrphaned_SignsOffStrandedItems()
    {
        await using var factory = new WorkItemTestFactory();
        factory.Current.Email = "sweeper@example.com";
        factory.Current.Name = "Sweeper";
        factory.Current.RolesList = new() { "InfraPortal.Admin" };

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            var (_, _, superseded) = await SeedPolicyEventCandidateAsync(db, "ORPH-1",
                approverGroup: "ReleaseApprovers", service: "api");
            var (_, _, rejected) = await SeedPolicyEventCandidateAsync(db, "ORPH-2",
                approverGroup: "ReleaseApprovers", service: "web");
            superseded.Status = PromotionStatus.Superseded;
            rejected.Status = PromotionStatus.Rejected;
            await db.SaveChangesAsync();
        }

        using (var scope = factory.Services.CreateScope())
        {
            var svc = scope.ServiceProvider.GetRequiredService<WorkItemApprovalService>();
            var result = await svc.ApproveOrphanedWorkItemsAsync(dryRun: false);

            Assert.False(result.DryRun);
            Assert.Equal(2, result.Examined);
            Assert.Equal(2, result.Approved);
            Assert.Equal(0, result.Failed);
            Assert.All(result.Items, i => Assert.Null(i.Error));
        }

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            var rows = await db.WorkItemApprovals.AsNoTracking()
                .OrderBy(a => a.WorkItemKey).ToListAsync();
            Assert.Equal(new[] { "ORPH-1", "ORPH-2" }, rows.Select(r => r.WorkItemKey));
            Assert.All(rows, r =>
            {
                Assert.Equal(WorkItemDecision.Approved, r.Decision);
                Assert.Equal("sweeper@example.com", r.ApproverEmail);
                Assert.Contains("maintenance sweep", r.Comment);
            });

            // The decision is mirrored into each thread, exactly as a clicked sign-off would be.
            var comments = await db.WorkItemComments.AsNoTracking().ToListAsync();
            Assert.Equal(2, comments.Count);
            Assert.All(comments, c => Assert.Equal(WorkItemDecision.Approved, c.Decision));

            // Per-item audit plus one entry for the sweep itself.
            var actions = await db.AuditLog.AsNoTracking().Select(a => a.Action).ToListAsync();
            Assert.Equal(2, actions.Count(a => a == "work-item.approved"));
            Assert.Single(actions, a => a == "work-item.orphans-swept");
        }
    }

    /// <summary>
    /// The sweep is for stranded items only. A ticket a live promotion still carries is ordinary
    /// pending work; a ticket somebody already decided is either resolved or deliberately held, and
    /// an issue or a block is not something a bulk repair gets to overrule.
    /// </summary>
    [Fact]
    public async Task ApproveOrphaned_LeavesLiveAndDecidedItemsAlone()
    {
        await using var factory = new WorkItemTestFactory();
        factory.Current.Email = "admin@example.com";
        factory.Current.RolesList = new() { "InfraPortal.Admin" };

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();

            // Stranded and undecided — the only one the sweep should touch.
            var (_, _, stranded) = await SeedPolicyEventCandidateAsync(db, "SWEEP-ME",
                approverGroup: "ReleaseApprovers", service: "api");
            stranded.Status = PromotionStatus.Superseded;

            // Same ticket on a dead AND a live candidate: still live work, so hands off.
            var (_, _, deadCopy) = await SeedPolicyEventCandidateAsync(db, "ALSO-LIVE",
                approverGroup: "ReleaseApprovers", service: "old");
            deadCopy.Status = PromotionStatus.Superseded;
            await SeedPolicyEventCandidateAsync(db, "ALSO-LIVE",
                approverGroup: "ReleaseApprovers", service: "new");

            // Stranded, but somebody raised an issue on it — a deliberate hold.
            var (_, _, held) = await SeedPolicyEventCandidateAsync(db, "ON-HOLD",
                approverGroup: "ReleaseApprovers", service: "held");
            held.Status = PromotionStatus.Rejected;
            db.WorkItemApprovals.Add(
                Approval("ON-HOLD", "qa@example.com", "QA", WorkItemDecision.Issue));

            await db.SaveChangesAsync();
        }

        using (var scope = factory.Services.CreateScope())
        {
            var svc = scope.ServiceProvider.GetRequiredService<WorkItemApprovalService>();
            var result = await svc.ApproveOrphanedWorkItemsAsync(dryRun: false);

            Assert.Equal(1, result.Examined);
            Assert.Equal(1, result.Approved);
            var item = Assert.Single(result.Items);
            Assert.Equal("SWEEP-ME", item.WorkItemKey);
        }

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            var approved = await db.WorkItemApprovals.AsNoTracking()
                .Where(a => a.Decision == WorkItemDecision.Approved)
                .Select(a => a.WorkItemKey)
                .ToListAsync();
            Assert.Equal(new[] { "SWEEP-ME" }, approved);

            // The held item keeps its issue — untouched, not flipped.
            var hold = await db.WorkItemApprovals.AsNoTracking()
                .SingleAsync(a => a.WorkItemKey == "ON-HOLD");
            Assert.Equal(WorkItemDecision.Issue, hold.Decision);
        }
    }

    /// <summary>
    /// An auto-approve promotion has no human gate, so its tickets were never sign-off work — they
    /// never show as "No live promotion" in the queue, and the sweep must not invent decisions for
    /// them either.
    /// </summary>
    [Fact]
    public async Task ApproveOrphaned_SkipsAutoApproveCandidates()
    {
        await using var factory = new WorkItemTestFactory();
        factory.Current.Email = "admin@example.com";
        factory.Current.RolesList = new() { "InfraPortal.Admin" };

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            var (_, _, auto) = await SeedPolicyEventCandidateAsync(db, "AUTO-1",
                approverGroup: null);
            auto.Status = PromotionStatus.Superseded;
            await db.SaveChangesAsync();
        }

        using (var scope = factory.Services.CreateScope())
        {
            var svc = scope.ServiceProvider.GetRequiredService<WorkItemApprovalService>();
            var result = await svc.ApproveOrphanedWorkItemsAsync(dryRun: false);
            Assert.Equal(0, result.Examined);
            Assert.Empty(result.Items);
        }
    }

    /// <summary>
    /// Retired services drop out on the queue's principle: nobody signs off tickets for a component
    /// that has been migrated away, and those rows are in nobody's queue to begin with.
    /// </summary>
    [Fact]
    public async Task ApproveOrphaned_SkipsRetiredServices()
    {
        await using var factory = new WorkItemTestFactory();
        factory.Current.Email = "admin@example.com";
        factory.Current.RolesList = new() { "InfraPortal.Admin" };

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            var (_, _, dead) = await SeedPolicyEventCandidateAsync(db, "GONE-1",
                approverGroup: "ReleaseApprovers", service: "retired");
            dead.Status = PromotionStatus.Superseded;
            db.DeletedServices.Add(new DeletedService
            {
                Id = Guid.NewGuid(),
                Product = "acme",
                Service = "retired",
                DeletedAt = DateTimeOffset.UtcNow,
                DeletedById = "admin",
                DeletedByName = "Admin",
            });
            await db.SaveChangesAsync();
        }

        using (var scope = factory.Services.CreateScope())
        {
            var svc = scope.ServiceProvider.GetRequiredService<WorkItemApprovalService>();
            var result = await svc.ApproveOrphanedWorkItemsAsync(dryRun: true);
            Assert.Equal(0, result.Examined);
        }
    }

    /// <summary>Same jurisdiction as a single sign-off, refused up front rather than per row.</summary>
    [Fact]
    public async Task ApproveOrphaned_RequiresQaOrAdmin()
    {
        await using var factory = new WorkItemTestFactory();
        factory.Current.Email = "nobody@example.com";
        factory.Current.RolesList = new();

        using var scope = factory.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<WorkItemApprovalService>();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.ApproveOrphanedWorkItemsAsync(dryRun: true));
    }

    /// <summary>The admin route the Maintenance card calls, end to end.</summary>
    [Fact]
    public async Task POST_ApproveOrphaned_SweepsViaAdminRoute()
    {
        await using var factory = new WorkItemTestFactory();
        factory.Current.Email = "admin@example.com";
        factory.Current.RolesList = new() { "InfraPortal.Admin" };

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            var (_, _, dead) = await SeedPolicyEventCandidateAsync(db, "ORPH-9",
                approverGroup: "ReleaseApprovers");
            dead.Status = PromotionStatus.Superseded;
            await db.SaveChangesAsync();
        }

        var client = factory.CreateAdminClient();

        var previewResponse = await client.PostAsJsonAsync(
            "/api/promotions/admin/work-items/approve-orphaned", new { dryRun = true });
        Assert.Equal(HttpStatusCode.OK, previewResponse.StatusCode);
        var previewBody = await Deserialize(previewResponse);
        Assert.True(previewBody.GetProperty("dryRun").GetBoolean());
        Assert.Equal(1, previewBody.GetProperty("examined").GetInt32());
        Assert.Equal(0, previewBody.GetProperty("approved").GetInt32());
        Assert.Equal("ORPH-9",
            previewBody.GetProperty("items")[0].GetProperty("workItemKey").GetString());

        var applyResponse = await client.PostAsJsonAsync(
            "/api/promotions/admin/work-items/approve-orphaned", new { dryRun = false });
        Assert.Equal(HttpStatusCode.OK, applyResponse.StatusCode);
        var applyBody = await Deserialize(applyResponse);
        Assert.Equal(1, applyBody.GetProperty("approved").GetInt32());
        Assert.Equal(0, applyBody.GetProperty("failed").GetInt32());

        // Second apply finds nothing — the sweep is self-clearing, not repeatable damage.
        var againResponse = await client.PostAsJsonAsync(
            "/api/promotions/admin/work-items/approve-orphaned", new { dryRun = false });
        var againBody = await Deserialize(againResponse);
        Assert.Equal(0, againBody.GetProperty("examined").GetInt32());
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Seeds the canonical "Pending candidate carries ticket K" graph: a DeployEvent, a
    /// DeployEventWorkItem, and a Pending PromotionCandidate keyed on (acme, prod). Returns
    /// all three so callers can layer extra setup on top.
    /// </summary>
    /// <summary>Convenience factory for a <see cref="WorkItemApproval"/> row (created now).</summary>
    private static WorkItemApproval Approval(
        string workItemKey, string approverEmail, string approverName, WorkItemDecision decision,
        string product = "acme", string targetEnv = "prod")
        => new()
        {
            Id = Guid.NewGuid(),
            WorkItemKey = workItemKey,
            Product = product,
            TargetEnv = targetEnv,
            ApproverEmail = approverEmail,
            ApproverName = approverName,
            Decision = decision,
            CreatedAt = DateTimeOffset.UtcNow,
        };

    private static async Task<(DeployEvent ev, PromotionWorkItem wi, PromotionCandidate cand)>
        SeedPolicyEventCandidateAsync(
            PlatformDbContext db,
            string workItemKey,
            string? approverGroup,
            List<ParticipantDto>? participants = null,
            string product = "acme",
            string service = "api",
            string sourceEnv = "staging",
            string targetEnv = "prod",
            DateTimeOffset? createdAt = null,
            string? content = null,
            string? title = null,
            string? subTitle = null)
    {
        var ev = NewDeployEvent(participants, product, service, sourceEnv);
        db.DeployEvents.Add(ev);

        var cand = NewCandidate(approverGroup, product, service, sourceEnv, targetEnv);
        if (createdAt is not null) cand.CreatedAt = createdAt.Value;
        db.PromotionCandidates.Add(cand);

        // The candidate carries its own ticket via the PromotionWorkItem index (keyed on
        // CandidateId) — this is what the ticket-approval lookup reads to find the candidate.
        var wi = new PromotionWorkItem
        {
            Id = Guid.NewGuid(),
            CandidateId = cand.Id,
            WorkItemKey = workItemKey,
            Product = product,
            TargetEnv = targetEnv,
            Title = title,
            SubTitle = subTitle,
            Content = content,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.PromotionWorkItems.Add(wi);

        await db.SaveChangesAsync();
        return (ev, wi, cand);
    }

    private static DeployEvent NewDeployEvent(
        List<ParticipantDto>? participants,
        string product = "acme",
        string service = "api",
        string sourceEnv = "staging")
    {
        return new DeployEvent
        {
            Id = Guid.NewGuid(),
            Product = product,
            Service = service,
            Environment = sourceEnv,
            Version = "v1.0.0",
            Source = "ci",
            Status = "succeeded",
            DeployedAt = DateTimeOffset.UtcNow,
            ReferencesJson = "[]",
            ParticipantsJson = JsonSerializer.Serialize(
                participants ?? new(),
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }),
            MetadataJson = "{}",
            CreatedAt = DateTimeOffset.UtcNow,
        };
    }

    /// <summary>
    /// A bare deploy event for a specific (product, service, environment, version). Used by the
    /// deployed-environments tests, which care about the deploy coordinates and nothing else.
    /// </summary>
    private static DeployEvent NewDeployEventFor(
        string product, string service, string environment, string version,
        string status = "succeeded", DateTimeOffset? deployedAt = null)
        => new()
        {
            Id = Guid.NewGuid(),
            Product = product,
            Service = service,
            Environment = environment,
            Version = version,
            Source = "ci",
            Status = status,
            DeployedAt = deployedAt ?? DateTimeOffset.UtcNow,
            ReferencesJson = "[]",
            ParticipantsJson = "[]",
            MetadataJson = "{}",
            CreatedAt = DateTimeOffset.UtcNow,
        };

    private static PromotionCandidate NewCandidate(
        string? approverGroup,
        string product = "acme",
        string service = "api",
        string sourceEnv = "staging",
        string targetEnv = "prod")
    {
        // approverGroup == null ⇒ auto-approve: an empty rule tree (no requirements anywhere).
        // Otherwise a single "any one member of <approverGroup>" requirement — the §8 rule-tree
        // equivalent of the legacy single-group / Strategy.Any / MinApprovers=1 policy.
        var snapshot = new ResolvedPolicySnapshot(
            PolicyId: approverGroup is null ? null : Guid.NewGuid(),
            EscalationGroup: null)
        {
            ApprovalSteps = approverGroup is null
                ? new()
                : new()
                {
                    new ApprovalStep("Approval", new()
                    {
                        new ApproverRequirement("Approvers", new() { new GroupRef(approverGroup, approverGroup) }, new(), 1),
                    }),
                },
        };

        var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        });

        return new PromotionCandidate
        {
            Id = Guid.NewGuid(),
            Product = product,
            Service = service,
            SourceEnv = sourceEnv,
            TargetEnv = targetEnv,
            Version = "v1.0.0",
            Status = PromotionStatus.Pending,
            PolicyId = snapshot.PolicyId,
            ResolvedPolicyJson = json,
            CreatedAt = DateTimeOffset.UtcNow,
            ParticipantsJson = "[]",
        };
    }

    private static async Task<JsonElement> Deserialize(HttpResponseMessage response)
    {
        var stream = await response.Content.ReadAsStreamAsync();
        var doc = await JsonDocument.ParseAsync(stream);
        return doc.RootElement;
    }

    // ── Test factory ────────────────────────────────────────────────────────

    /// <summary>
    /// Test host factory specialised for ticket-approval tests. Three things on top of the
    /// shared <see cref="TestFactory"/>:
    /// <list type="bullet">
    ///   <item>Replaces <see cref="ICurrentUser"/> with a mutable <see cref="FakeCurrentUser"/>
    ///         exposed as <see cref="Current"/> so tests can dial in roles/email/etc.</item>
    ///   <item>Replaces <see cref="IIdentityService"/> with <see cref="EmptyIdentityService"/> to
    ///         neutralise <c>StubIdentityService</c>'s "every local user is in every group" behaviour
    ///         that would otherwise mask the not-in-approver-group failure path.</item>
    ///   <item>Replaces <see cref="IAuditLogger"/> with one that doesn't require an HttpContext
    ///         for the correlation id, so service-level tests can drive the service from a plain
    ///         scope.</item>
    /// </list>
    /// </summary>
    public class WorkItemTestFactory : WebApplicationFactory<Program>, IAsyncDisposable
    {
        public FakeCurrentUser Current { get; } = new();
        private readonly SqliteConnection _connection;

        public WorkItemTestFactory()
        {
            _connection = new SqliteConnection("DataSource=:memory:");
            _connection.Open();

            var options = new DbContextOptionsBuilder<SqliteTestDbContext>()
                .UseSqlite(_connection)
                .Options;
            using var db = new SqliteTestDbContext(options);
            db.Database.EnsureCreated();
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");

            builder.ConfigureServices(services =>
            {
                RemoveService<DbContextOptions<PostgresPlatformDbContext>>(services);
                RemoveService<DbContextOptions<SqlServerPlatformDbContext>>(services);
                RemoveService<DbContextOptions<PlatformDbContext>>(services);
                RemoveService<PostgresPlatformDbContext>(services);
                RemoveService<SqlServerPlatformDbContext>(services);
                RemoveService<PlatformDbContext>(services);

                services.AddSingleton<DbConnection>(_connection);
                services.AddDbContext<PlatformDbContext, SqliteTestDbContext>((sp, options) =>
                    options.UseSqlite(sp.GetRequiredService<DbConnection>()));

                // Replace ICurrentUser with a mutable singleton fake.
                RemoveService<ICurrentUser>(services);
                services.AddSingleton<ICurrentUser>(Current);

                // Replace IIdentityService with one that returns no group members, so authority
                // can only flow through claims (admin/QA shortcut, role claim, group claim).
                RemoveService<IIdentityService>(services);
                services.AddScoped<IIdentityService, EmptyIdentityService>();

                // Replace IAuditLogger with a context-free implementation. The default
                // AuditLogger reads HttpContext.Items["CorrelationId"]; calling from a plain
                // service scope returns null, so we just shortcut to a fresh GUID.
                RemoveService<IAuditLogger>(services);
                services.AddScoped<IAuditLogger, ContextFreeAuditLogger>();
            });
        }

        public HttpClient CreateAdminClient()
        {
            var client = CreateClient();
            var loginResponse = client.PostAsJsonAsync("/api/auth/login",
                new { email = "admin@localhost", password = "admin123" })
                .GetAwaiter().GetResult();
            loginResponse.EnsureSuccessStatusCode();
            var stream = loginResponse.Content.ReadAsStreamAsync().GetAwaiter().GetResult();
            var doc = JsonDocument.Parse(stream);
            var token = doc.RootElement.GetProperty("token").GetString()!;
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return client;
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing) _connection.Dispose();
        }

        public new async ValueTask DisposeAsync()
        {
            await base.DisposeAsync();
            _connection.Dispose();
        }

        private static void RemoveService<T>(IServiceCollection services)
        {
            var descriptors = services.Where(d => d.ServiceType == typeof(T)).ToList();
            foreach (var d in descriptors) services.Remove(d);
        }
    }

    /// <summary>
    /// Mutable test double for <see cref="ICurrentUser"/>. Tests dial in <see cref="Email"/>
    /// and <see cref="RolesList"/> before exercising the service; <see cref="IsAdmin"/> /
    /// <see cref="IsQA"/> derive from roles to mirror the real implementation.
    /// </summary>
    public class FakeCurrentUser : ICurrentUser
    {
        public string Id { get; set; } = "test-user-id";
        public string Name { get; set; } = "Test User";
        public string Email { get; set; } = "test@example.com";
        public List<string> RolesList { get; set; } = new();
        public List<string> GroupsList { get; set; } = new();
        public IReadOnlyList<string> Roles => RolesList;
        public IReadOnlyList<string> Groups => GroupsList;
        public bool IsAdmin => Roles.Contains("InfraPortal.Admin", StringComparer.OrdinalIgnoreCase);
        public bool IsQA => Roles.Contains("InfraPortal.QA", StringComparer.OrdinalIgnoreCase);
        public bool IsInGroup(string groupId) => Groups.Contains(groupId, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Returns no group members. Replaces <see cref="StubIdentityService"/> in tests so the
    /// approver-group check doesn't trivially pass via the live-Graph fallback. Tests that need
    /// a user to be in a group should set the corresponding role/group claim on
    /// <see cref="FakeCurrentUser"/>.
    /// </summary>
    public class EmptyIdentityService : IIdentityService
    {
        public Task<IReadOnlyList<UserInfo>> GetGroupMembers(string groupId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<UserInfo>>(Array.Empty<UserInfo>());

        public Task<UserInfo?> GetUser(string userId, CancellationToken ct = default)
            => Task.FromResult<UserInfo?>(null);

        public Task<IReadOnlyList<UserInfo>> SearchUsers(string query, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<UserInfo>>(Array.Empty<UserInfo>());

        public Task<IReadOnlyList<GroupInfo>> SearchGroups(string query, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<GroupInfo>>(Array.Empty<GroupInfo>());
    }

    /// <summary>
    /// HttpContext-free <see cref="IAuditLogger"/>. The production implementation reads
    /// <c>HttpContext.Items["CorrelationId"]</c>; calling from a plain DI scope means there is
    /// no HttpContext, so this implementation just generates a fresh correlation id.
    /// </summary>
    public class ContextFreeAuditLogger : IAuditLogger
    {
        private readonly PlatformDbContext _db;

        public ContextFreeAuditLogger(PlatformDbContext db) { _db = db; }

        public async Task Log(
            string module, string action,
            string actorId, string actorName, string actorType,
            string entityType, Guid? entityId,
            object? beforeState = null,
            object? afterState = null,
            object? metadata = null)
        {
            var entry = new AuditEntry
            {
                Id = Guid.NewGuid(),
                Timestamp = DateTimeOffset.UtcNow,
                CorrelationId = Guid.NewGuid(),
                Module = module,
                Action = action,
                ActorId = actorId,
                ActorName = actorName,
                ActorType = actorType,
                EntityType = entityType,
                EntityId = entityId,
                BeforeState = beforeState is not null ? JsonSerializer.Serialize(beforeState) : null,
                AfterState = afterState is not null ? JsonSerializer.Serialize(afterState) : null,
                Metadata = metadata is not null ? JsonSerializer.Serialize(metadata) : null,
            };
            _db.AuditLog.Add(entry);
            await _db.SaveChangesAsync();
        }
    }
}
