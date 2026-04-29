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
        factory.Current.RolesList = new() { "ReleaseApprovers" };

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

    [Fact]
    public async Task Approve_Throws_WhenNoPendingCandidateReferencesTicket()
    {
        await using var factory = new WorkItemTestFactory();
        factory.Current.Email = "approver@example.com";
        factory.Current.RolesList = new() { "ReleaseApprovers" };

        // No setup — no candidate carries FOO-999.
        using var scope = factory.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<WorkItemApprovalService>();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.ApproveAsync("FOO-999", "acme", "prod", null, default));
        Assert.Contains("Pending", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Approve_Throws_WhenAlreadyDecided()
    {
        await using var factory = new WorkItemTestFactory();
        factory.Current.Email = "approver@example.com";
        factory.Current.Name = "Approver User";
        factory.Current.RolesList = new() { "ReleaseApprovers" };

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

    [Fact]
    public async Task Approve_Throws_WhenUserIsExcludedByRole()
    {
        await using var factory = new WorkItemTestFactory();
        factory.Current.Email = "deployer@example.com";
        factory.Current.Name = "Deployer";
        factory.Current.RolesList = new() { "ReleaseApprovers" };

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            // Source event lists deployer@example.com under the excluded "deploy-author" role.
            var participants = new List<ParticipantDto>
            {
                new("deploy-author", "Deployer", "deployer@example.com"),
            };
            await SeedPolicyEventCandidateAsync(db, "FOO-1",
                approverGroup: "ReleaseApprovers",
                excludeRole: "deploy-author",
                participants: participants);
        }

        using (var scope = factory.Services.CreateScope())
        {
            var svc = scope.ServiceProvider.GetRequiredService<WorkItemApprovalService>();
            var ex = await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                svc.ApproveAsync("FOO-1", "acme", "prod", null, default));
            Assert.Contains("excluded", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
    }

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
            Assert.Contains("approver group", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Approve_Throws_WhenPolicyIsAutoApprove()
    {
        await using var factory = new WorkItemTestFactory();
        factory.Current.Email = "any@example.com";
        factory.Current.RolesList = new() { "ReleaseApprovers" };

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
        factory.Current.RolesList = new() { "ReleaseApprovers" };

        Guid newerCandidateId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            // Two candidates in (acme, prod). Older candidate's bundle includes FOO-1 via a
            // superseded source event; newer candidate's *own* SourceDeployEvent has FOO-1.
            // Pre-flight: seed the older candidate first, then the newer one.
            var (oldEvent, _, oldCandidate) = await SeedPolicyEventCandidateAsync(
                db, "FOO-1",
                approverGroup: "ReleaseApprovers",
                createdAt: DateTimeOffset.UtcNow.AddMinutes(-10));

            // Newer event also carries FOO-1; newer candidate inherits oldEvent in its
            // superseded list so both candidates' bundles match the ticket.
            var newEvent = NewDeployEvent(participants: null);
            db.DeployEvents.Add(newEvent);
            db.DeployEventWorkItems.Add(new DeployEventWorkItem
            {
                Id = Guid.NewGuid(),
                DeployEventId = newEvent.Id,
                WorkItemKey = "FOO-1",
                Product = "acme",
                CreatedAt = DateTimeOffset.UtcNow,
            });

            var newer = NewCandidate(newEvent.Id, approverGroup: "ReleaseApprovers");
            newer.CreatedAt = DateTimeOffset.UtcNow;
            newer.SupersededSourceEventIds = new() { oldEvent.Id };
            db.PromotionCandidates.Add(newer);
            await db.SaveChangesAsync();
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
        factory.Current.RolesList = new() { "ReleaseApprovers" };

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
        factory.Current.RolesList = new() { "ReleaseApprovers" };

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

    [Fact]
    public async Task GetTicketContext_BlockedReasonIsAlreadyDecided_WhenUserHasDecided()
    {
        await using var factory = new WorkItemTestFactory();
        factory.Current.Email = "me@example.com";
        factory.Current.RolesList = new() { "ReleaseApprovers" };

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
            Assert.False(ctx.CanApprove);
            Assert.Equal("Already decided", ctx.BlockedReason);
        }
    }

    [Fact]
    public async Task GetTicketContext_BlockedReasonIsNoPending_WhenNoCandidateCarriesTicket()
    {
        await using var factory = new WorkItemTestFactory();
        factory.Current.Email = "me@example.com";
        factory.Current.RolesList = new() { "ReleaseApprovers" };

        using var scope = factory.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<WorkItemApprovalService>();
        var ctx = await svc.GetTicketContextAsync("ZZZ-999", "acme", "prod", default);
        Assert.False(ctx.CanApprove);
        Assert.Equal("No pending promotion needs this ticket", ctx.BlockedReason);
        Assert.Null(ctx.PendingCandidateId);
        Assert.Empty(ctx.Approvals);
    }

    [Fact]
    public async Task GetPendingForCurrentUser_ReturnsTicketsUserCouldSignOff()
    {
        await using var factory = new WorkItemTestFactory();
        factory.Current.Email = "me@example.com";
        factory.Current.RolesList = new() { "ReleaseApprovers" };

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
    public async Task GetPendingForCurrentUser_ExcludesTicketsAlreadyDecided()
    {
        await using var factory = new WorkItemTestFactory();
        factory.Current.Email = "me@example.com";
        factory.Current.RolesList = new() { "ReleaseApprovers" };

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

    [Fact]
    public async Task GetPendingForCurrentUser_ExcludesTicketsWhereUserIsExcludedByRole()
    {
        await using var factory = new WorkItemTestFactory();
        factory.Current.Email = "deployer@example.com";
        factory.Current.RolesList = new() { "ReleaseApprovers" };

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            // FOO-1: source event lists me as deploy-author (excluded).
            await SeedPolicyEventCandidateAsync(db, "FOO-1",
                approverGroup: "ReleaseApprovers",
                excludeRole: "deploy-author",
                participants: new() { new("deploy-author", "Deployer", "deployer@example.com") },
                service: "a");

            // FOO-2: I'm not on the participant list.
            await SeedPolicyEventCandidateAsync(db, "FOO-2",
                approverGroup: "ReleaseApprovers",
                excludeRole: "deploy-author",
                participants: new() { new("deploy-author", "Other", "other@example.com") },
                service: "b");
        }

        using (var scope = factory.Services.CreateScope())
        {
            var svc = scope.ServiceProvider.GetRequiredService<WorkItemApprovalService>();
            var queue = await svc.GetPendingForCurrentUserAsync(default);
            var pending = queue.Tickets;
            Assert.Single(pending);
            Assert.Equal("FOO-2", pending[0].WorkItemKey);
        }
    }

    // ── Endpoint-level tests (HTTP) ─────────────────────────────────────────

    [Fact]
    public async Task POST_Approvals_Returns200_WithApprovalRowAndCandidateId()
    {
        await using var factory = new WorkItemTestFactory();
        factory.Current.Email = "approver@example.com";
        factory.Current.Name = "Approver";
        factory.Current.RolesList = new() { "ReleaseApprovers" };

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
    public async Task POST_Approvals_Returns400_WhenNoPendingCandidate()
    {
        await using var factory = new WorkItemTestFactory();
        factory.Current.Email = "approver@example.com";
        factory.Current.RolesList = new() { "ReleaseApprovers" };

        var client = factory.CreateAdminClient();
        var response = await client.PostAsJsonAsync("/api/work-items/NOPE-1/approvals",
            new { product = "acme", targetEnv = "prod" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var body = await Deserialize(response);
        Assert.Contains("Pending", body.GetProperty("error").GetString()!,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task POST_Approvals_Returns400_WhenAlreadyDecided()
    {
        await using var factory = new WorkItemTestFactory();
        factory.Current.Email = "approver@example.com";
        factory.Current.Name = "Approver";
        factory.Current.RolesList = new() { "ReleaseApprovers" };

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

    [Fact]
    public async Task POST_Approvals_Returns403_WhenExcludedRole()
    {
        await using var factory = new WorkItemTestFactory();
        factory.Current.Email = "deployer@example.com";
        factory.Current.RolesList = new() { "ReleaseApprovers" };

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            await SeedPolicyEventCandidateAsync(db, "FOO-1",
                approverGroup: "ReleaseApprovers",
                excludeRole: "deploy-author",
                participants: new() { new("deploy-author", "Deployer", "deployer@example.com") });
        }

        var client = factory.CreateAdminClient();
        var response = await client.PostAsJsonAsync("/api/work-items/FOO-1/approvals",
            new { product = "acme", targetEnv = "prod" });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

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
        factory.Current.RolesList = new() { "ReleaseApprovers" };

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
        factory.Current.RolesList = new() { "ReleaseApprovers" };

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
        factory.Current.RolesList = new() { "ReleaseApprovers" };

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
        factory.Current.RolesList = new() { "ReleaseApprovers" };

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
    private static async Task<(DeployEvent ev, DeployEventWorkItem wi, PromotionCandidate cand)>
        SeedPolicyEventCandidateAsync(
            PlatformDbContext db,
            string workItemKey,
            string? approverGroup,
            string? excludeRole = null,
            List<ParticipantDto>? participants = null,
            string product = "acme",
            string service = "api",
            string sourceEnv = "staging",
            string targetEnv = "prod",
            DateTimeOffset? createdAt = null)
    {
        var ev = NewDeployEvent(participants, product, service, sourceEnv);
        db.DeployEvents.Add(ev);

        var wi = new DeployEventWorkItem
        {
            Id = Guid.NewGuid(),
            DeployEventId = ev.Id,
            WorkItemKey = workItemKey,
            Product = product,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.DeployEventWorkItems.Add(wi);

        var cand = NewCandidate(ev.Id, approverGroup, excludeRole, product, service, sourceEnv, targetEnv);
        if (createdAt is not null) cand.CreatedAt = createdAt.Value;
        db.PromotionCandidates.Add(cand);

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
        Guid sourceEventId,
        string? approverGroup,
        string? excludeRole = null,
        string product = "acme",
        string service = "api",
        string sourceEnv = "staging",
        string targetEnv = "prod")
    {
        var snapshot = new ResolvedPolicySnapshot(
            PolicyId: approverGroup is null ? null : Guid.NewGuid(),
            ApproverGroup: approverGroup,
            Strategy: PromotionStrategy.Any,
            MinApprovers: 1,
            ExcludeRole: excludeRole,
            TimeoutHours: 24,
            EscalationGroup: null);

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
            SourceDeployEventId = sourceEventId,
            Status = PromotionStatus.Pending,
            PolicyId = snapshot.PolicyId,
            ResolvedPolicyJson = json,
            CreatedAt = DateTimeOffset.UtcNow,
            SupersededSourceEventIdsJson = "[]",
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
