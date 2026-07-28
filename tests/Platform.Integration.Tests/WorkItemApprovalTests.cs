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
            Assert.Equal(PromotionDecision.Approved, row.Decision);
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
                Decision = PromotionDecision.Approved,
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
    public async Task Reject_RecordsRowWithRejectedDecision()
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
            var row = await svc.RejectAsync("FOO-1", "acme", "prod", "blocked", default);
            Assert.Equal(PromotionDecision.Rejected, row.Decision);
        }

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            var audit = await db.AuditLog.AsNoTracking()
                .Where(a => a.Action == "work-item.rejected").ToListAsync();
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
                    Decision = PromotionDecision.Approved,
                    CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-2),
                },
                new WorkItemApproval
                {
                    Id = Guid.NewGuid(),
                    WorkItemKey = "FOO-1", Product = "acme", TargetEnv = "prod",
                    ApproverEmail = "bob@example.com", ApproverName = "Bob",
                    Decision = PromotionDecision.Approved,
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
                Decision = PromotionDecision.Approved,
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
    public async Task Block_RecordsRow_AndLeavesCandidatePending()
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
            var row = await svc.BlockAsync("FOO-1", "acme", "prod", "waiting on test data", default);
            Assert.Equal(PromotionDecision.Blocked, row.Decision);
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
    public async Task Approve_AfterBlock_UpdatesExistingRowInPlace()
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
            var blocked = await svc.BlockAsync("FOO-1", "acme", "prod", "blocked", default);
            rowId = blocked.Id;

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
            Assert.Equal(PromotionDecision.Approved, approved.Decision);
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
    public async Task Block_Throws_WhenAlreadyBlockedByTheSameUser()
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
            await svc.BlockAsync("FOO-1", "acme", "prod", null, default);
        }

        using (var scope = factory.Services.CreateScope())
        {
            var svc = scope.ServiceProvider.GetRequiredService<WorkItemApprovalService>();
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                svc.BlockAsync("FOO-1", "acme", "prod", null, default));
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
                Decision = PromotionDecision.Approved,
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
                    Decision = PromotionDecision.Approved,
                    CreatedAt = DateTimeOffset.UtcNow.AddDays(-10),
                },
                new WorkItemApproval
                {
                    Id = Guid.NewGuid(),
                    WorkItemKey = "NEW-1", Product = "acme", TargetEnv = "prod",
                    ApproverEmail = "me@example.com", ApproverName = "Me",
                    Decision = PromotionDecision.Rejected,
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
                Approval("K-1", "alice@example.com", "Alice", PromotionDecision.Approved),
                Approval("K-2", "alice@example.com", "Alice", PromotionDecision.Rejected),
                Approval("K-3", "bob@example.com", "Bob", PromotionDecision.Approved));
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
                Decision = PromotionDecision.Approved,
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
    public async Task POST_Rejections_Returns200_AndRecordsRejected()
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
        var response = await client.PostAsJsonAsync("/api/work-items/FOO-1/rejections",
            new { product = "acme", targetEnv = "prod", comment = "blocked" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await Deserialize(response);
        Assert.Equal("Rejected", body.GetProperty("decision").GetString());

        using var scope2 = factory.Services.CreateScope();
        var db2 = scope2.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var rows = await db2.WorkItemApprovals.AsNoTracking().ToListAsync();
        Assert.Single(rows);
        Assert.Equal(PromotionDecision.Rejected, rows[0].Decision);
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
                Decision = PromotionDecision.Approved,
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

    // ── Helpers ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Seeds the canonical "Pending candidate carries ticket K" graph: a DeployEvent, a
    /// DeployEventWorkItem, and a Pending PromotionCandidate keyed on (acme, prod). Returns
    /// all three so callers can layer extra setup on top.
    /// </summary>
    /// <summary>Convenience factory for a <see cref="WorkItemApproval"/> row (created now).</summary>
    private static WorkItemApproval Approval(
        string workItemKey, string approverEmail, string approverName, PromotionDecision decision,
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
            DateTimeOffset? createdAt = null)
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
            TimeoutHours: 24,
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
