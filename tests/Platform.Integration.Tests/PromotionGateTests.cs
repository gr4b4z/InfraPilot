using System.Data.Common;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Platform.Api.Features.Deployments.Models;
using Platform.Api.Features.Promotions;
using Platform.Api.Features.Promotions.Models;
using Platform.Api.Features.Webhooks;
using Platform.Api.Infrastructure.Audit;
using Platform.Api.Infrastructure.Auth;
using Platform.Api.Infrastructure.Identity;
using Platform.Api.Infrastructure.Persistence;

namespace Platform.Integration.Tests;

/// <summary>
/// Integration tests for Phase 3 of PR3: the gate evaluator on
/// <see cref="ResolvedPolicySnapshot.Gate"/>. Drives candidates through
/// <see cref="PromotionService.ApproveAsync"/>, <see cref="PromotionService.ReevaluateAsync"/>
/// and <see cref="WorkItemApprovalService.ApproveAsync"/> /
/// <see cref="WorkItemApprovalService.RejectAsync"/> and asserts the candidate-level transitions,
/// audit entries (legacy + ticket-level + coarse), and webhook dispatches that come out of them.
///
/// <para>Each test owns its own <see cref="GateTestFactory"/> for isolation. The factory mirrors
/// the existing <c>WorkItemTestFactory</c> setup (fake <see cref="ICurrentUser"/>, empty
/// <see cref="IIdentityService"/>, context-free <see cref="IAuditLogger"/>) and additionally
/// captures <see cref="IWebhookDispatcher"/> through an NSubstitute mock so tests can assert
/// dispatch.</para>
/// </summary>
public class PromotionGateTests
{
    // ── 1. TicketsOnly_SingleTicket_AutoPromotesWhenApproved ────────────────

    [Fact]
    public async Task TicketsOnly_SingleTicket_AutoPromotesWhenApproved()
    {
        await using var factory = new GateTestFactory();
        factory.Current.Email = "approver@example.com";
        factory.Current.Name = "Approver";
        factory.Current.RolesList = new() { "ReleaseApprovers" };

        Guid candidateId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            var (_, _, c) = await SeedAsync(db, gate: PromotionGate.TicketsOnly,
                workItemKeys: new[] { "FOO-1" });
            candidateId = c.Id;
        }

        using (var scope = factory.Services.CreateScope())
        {
            var svc = scope.ServiceProvider.GetRequiredService<WorkItemApprovalService>();
            await svc.ApproveAsync("FOO-1", "acme", "prod", "ship it", default);
        }

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();

            // Candidate transitioned to Approved.
            var candidate = await db.PromotionCandidates.AsNoTracking()
                .FirstAsync(c => c.Id == candidateId);
            Assert.Equal(PromotionStatus.Approved, candidate.Status);
            Assert.NotNull(candidate.ApprovedAt);

            // Coarse system-actor audit emitted for the gate-driven transition.
            var coarse = await db.AuditLog.AsNoTracking()
                .FirstAsync(a => a.Action == "promotion.approved" && a.EntityId == candidateId);
            Assert.Equal("system", coarse.ActorId);
            Assert.Equal("system", coarse.ActorType);
            // Trigger + mode live on AfterState (the audit logger's "what changed" payload).
            Assert.Contains("gate-evaluator", coarse.AfterState!);
            Assert.Contains("TicketsOnly", coarse.AfterState!);

            // Ticket-level audit emitted alongside.
            var ticket = await db.AuditLog.AsNoTracking()
                .Where(a => a.Action == "promotion.ticket.approved").ToListAsync();
            Assert.Single(ticket);
        }

        // Webhook dispatch for both ticket-level approval and the coarse promotion.approved.
        await factory.WebhookDispatcher.Received().DispatchAsync(
            "promotion.approved",
            Arg.Any<object>(),
            Arg.Any<WebhookEventFilters>());
        await factory.WebhookDispatcher.Received().DispatchAsync(
            "promotion.ticket.approved",
            Arg.Any<object>(),
            Arg.Any<WebhookEventFilters>());
    }

    // ── 2. TicketsOnly_TwoTickets_DoesNotPromoteUntilAllApproved ────────────

    [Fact]
    public async Task TicketsOnly_TwoTickets_DoesNotPromoteUntilAllApproved()
    {
        await using var factory = new GateTestFactory();
        factory.Current.Email = "approver@example.com";
        factory.Current.RolesList = new() { "ReleaseApprovers" };

        Guid candidateId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            var (_, _, c) = await SeedAsync(db, gate: PromotionGate.TicketsOnly,
                workItemKeys: new[] { "FOO-1", "FOO-2" });
            candidateId = c.Id;
        }

        // Approve only the first ticket — candidate should stay Pending.
        using (var scope = factory.Services.CreateScope())
        {
            var svc = scope.ServiceProvider.GetRequiredService<WorkItemApprovalService>();
            await svc.ApproveAsync("FOO-1", "acme", "prod", null, default);
        }

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            var candidate = await db.PromotionCandidates.AsNoTracking()
                .FirstAsync(c => c.Id == candidateId);
            Assert.Equal(PromotionStatus.Pending, candidate.Status);
        }

        // Approve the second ticket — gate now satisfied, candidate transitions.
        using (var scope = factory.Services.CreateScope())
        {
            var svc = scope.ServiceProvider.GetRequiredService<WorkItemApprovalService>();
            await svc.ApproveAsync("FOO-2", "acme", "prod", null, default);
        }

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            var candidate = await db.PromotionCandidates.AsNoTracking()
                .FirstAsync(c => c.Id == candidateId);
            Assert.Equal(PromotionStatus.Approved, candidate.Status);
        }
    }

    // ── 3. TicketsOnly_ManualApprove_ThrowsInvalidOperation ─────────────────

    [Fact]
    public async Task TicketsOnly_ManualApprove_ThrowsInvalidOperation()
    {
        await using var factory = new GateTestFactory();
        factory.Current.Email = "approver@example.com";
        factory.Current.RolesList = new() { "ReleaseApprovers" };

        Guid candidateId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            var (_, _, c) = await SeedAsync(db, gate: PromotionGate.TicketsOnly,
                workItemKeys: new[] { "FOO-1" });
            candidateId = c.Id;
        }

        using (var scope = factory.Services.CreateScope())
        {
            var svc = scope.ServiceProvider.GetRequiredService<PromotionService>();
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                svc.ApproveAsync(candidateId, "ship it", default));
            Assert.Contains("auto-promotes from ticket approvals", ex.Message,
                StringComparison.OrdinalIgnoreCase);
        }
    }

    // ── 4. TicketsOnly_NoTickets_FallsBackToPromotionOnlyEvaluation ─────────
    //
    // A TicketsOnly candidate with an empty bundle has no tickets to gate on, so the
    // gate falls back to PromotionOnly evaluation and ApproveAsync becomes a viable
    // path forward — otherwise such a candidate would be permanently un-promotable.

    [Fact]
    public async Task TicketsOnly_NoTickets_FallsBackToPromotionOnlyEvaluation()
    {
        await using var factory = new GateTestFactory();
        factory.Current.Email = "approver@example.com";
        factory.Current.Name = "Approver";
        factory.Current.RolesList = new() { "ReleaseApprovers" };

        Guid candidateId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            var (_, _, c) = await SeedAsync(db, gate: PromotionGate.TicketsOnly,
                workItemKeys: Array.Empty<string>());
            candidateId = c.Id;
        }

        using (var scope = factory.Services.CreateScope())
        {
            var svc = scope.ServiceProvider.GetRequiredService<PromotionService>();
            var result = await svc.ApproveAsync(candidateId, "shipping it", default);
            Assert.Equal(PromotionStatus.Approved, result.Status);
        }

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            var coarse = await db.AuditLog.AsNoTracking()
                .FirstAsync(a => a.Action == "promotion.approved" && a.EntityId == candidateId);
            Assert.Equal("system", coarse.ActorType);
        }
    }

    // ── 5. TicketsOnly_TicketRejected_VetoesCandidate ───────────────────────

    [Fact]
    public async Task TicketsOnly_TicketRejected_VetoesCandidate()
    {
        await using var factory = new GateTestFactory();
        factory.Current.Email = "rejector@example.com";
        factory.Current.Name = "Rejector";
        factory.Current.RolesList = new() { "ReleaseApprovers" };

        Guid candidateId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            var (_, _, c) = await SeedAsync(db, gate: PromotionGate.TicketsOnly,
                workItemKeys: new[] { "FOO-1", "FOO-2" });
            candidateId = c.Id;
        }

        // Reject one ticket — candidate should be terminated immediately, regardless of FOO-2.
        using (var scope = factory.Services.CreateScope())
        {
            var svc = scope.ServiceProvider.GetRequiredService<WorkItemApprovalService>();
            await svc.RejectAsync("FOO-1", "acme", "prod", "blocked", default);
        }

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            var candidate = await db.PromotionCandidates.AsNoTracking()
                .FirstAsync(c => c.Id == candidateId);
            Assert.Equal(PromotionStatus.Rejected, candidate.Status);

            // The rejecting user (not "system") owns the coarse promotion.rejected audit.
            var coarse = await db.AuditLog.AsNoTracking()
                .FirstAsync(a => a.Action == "promotion.rejected" && a.EntityId == candidateId);
            Assert.Equal("user", coarse.ActorType);
            Assert.NotEqual("system", coarse.ActorId);
            // Trigger marker is on AfterState (audit logger's payload arg).
            Assert.Contains("ticket-veto", coarse.AfterState!);

            // Ticket-level audit also emitted for the rejection.
            var ticket = await db.AuditLog.AsNoTracking()
                .Where(a => a.Action == "promotion.ticket.rejected").ToListAsync();
            Assert.Single(ticket);
        }

        await factory.WebhookDispatcher.Received().DispatchAsync(
            "promotion.rejected",
            Arg.Any<object>(),
            Arg.Any<WebhookEventFilters>());
    }

    // ── 6. TicketsAndManual_RequiresBothTicketsAndManualApproval ────────────

    [Fact]
    public async Task TicketsAndManual_RequiresBothTicketsAndManualApproval()
    {
        await using var factory = new GateTestFactory();
        factory.Current.Email = "approver@example.com";
        factory.Current.Name = "Approver";
        factory.Current.RolesList = new() { "ReleaseApprovers" };

        Guid candidateId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            var (_, _, c) = await SeedAsync(db, gate: PromotionGate.TicketsAndManual,
                workItemKeys: new[] { "FOO-1" });
            candidateId = c.Id;
        }

        // Step 1: ticket-approve. Manual still missing → Pending.
        using (var scope = factory.Services.CreateScope())
        {
            var svc = scope.ServiceProvider.GetRequiredService<WorkItemApprovalService>();
            await svc.ApproveAsync("FOO-1", "acme", "prod", null, default);
        }

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            var candidate = await db.PromotionCandidates.AsNoTracking()
                .FirstAsync(c => c.Id == candidateId);
            Assert.Equal(PromotionStatus.Pending, candidate.Status);
        }

        // Step 2: manual-approve. Both gates satisfied → Approved.
        using (var scope = factory.Services.CreateScope())
        {
            var svc = scope.ServiceProvider.GetRequiredService<PromotionService>();
            var result = await svc.ApproveAsync(candidateId, "ship it", default);
            Assert.Equal(PromotionStatus.Approved, result.Status);
        }

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();

            // Per-row signoff audit (granular).
            var granular = await db.AuditLog.AsNoTracking()
                .Where(a => a.Action == "promotion.approval.recorded").ToListAsync();
            Assert.Single(granular);

            // Coarse system audit fired exactly once for the gate-driven transition.
            var coarse = await db.AuditLog.AsNoTracking()
                .Where(a => a.Action == "promotion.approved" && a.EntityId == candidateId).ToListAsync();
            Assert.Single(coarse);
            Assert.Equal("system", coarse[0].ActorType);
        }
    }

    // ── 7. TicketsAndManual_ManualApproveAlone_DoesNotPromote ───────────────

    [Fact]
    public async Task TicketsAndManual_ManualApproveAlone_DoesNotPromote()
    {
        await using var factory = new GateTestFactory();
        factory.Current.Email = "approver@example.com";
        factory.Current.Name = "Approver";
        factory.Current.RolesList = new() { "ReleaseApprovers" };

        Guid candidateId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            var (_, _, c) = await SeedAsync(db, gate: PromotionGate.TicketsAndManual,
                workItemKeys: new[] { "FOO-1" });
            candidateId = c.Id;
        }

        using (var scope = factory.Services.CreateScope())
        {
            var svc = scope.ServiceProvider.GetRequiredService<PromotionService>();
            // Approve manually but no ticket signoff → still pending.
            var result = await svc.ApproveAsync(candidateId, "ship", default);
            Assert.Equal(PromotionStatus.Pending, result.Status);
        }

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            var candidate = await db.PromotionCandidates.AsNoTracking()
                .FirstAsync(c => c.Id == candidateId);
            Assert.Equal(PromotionStatus.Pending, candidate.Status);

            // The granular per-row signoff was recorded — manual signoff happened, gate just
            // wasn't satisfied because the ticket is still pending.
            Assert.Single(await db.AuditLog.AsNoTracking()
                .Where(a => a.Action == "promotion.approval.recorded").ToListAsync());

            // No coarse promotion.approved entry yet — gate isn't satisfied.
            var coarse = await db.AuditLog.AsNoTracking()
                .Where(a => a.Action == "promotion.approved" && a.EntityId == candidateId).ToListAsync();
            Assert.Empty(coarse);
        }

        // Now approve the ticket. The gate becomes satisfied (manual + ticket both done) and
        // the candidate transitions — proving the missing piece really was the ticket signoff.
        using (var scope = factory.Services.CreateScope())
        {
            var svc = scope.ServiceProvider.GetRequiredService<WorkItemApprovalService>();
            await svc.ApproveAsync("FOO-1", "acme", "prod", null, default);
        }

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            var candidate = await db.PromotionCandidates.AsNoTracking()
                .FirstAsync(c => c.Id == candidateId);
            Assert.Equal(PromotionStatus.Approved, candidate.Status);
        }
    }

    // ── 8. PromotionOnly_TicketApprovalDoesNotPromote ───────────────────────

    [Fact]
    public async Task PromotionOnly_TicketApprovalDoesNotPromote()
    {
        await using var factory = new GateTestFactory();
        factory.Current.Email = "approver@example.com";
        factory.Current.Name = "Approver";
        factory.Current.RolesList = new() { "ReleaseApprovers" };

        Guid candidateId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            var (_, _, c) = await SeedAsync(db, gate: PromotionGate.PromotionOnly,
                workItemKeys: new[] { "FOO-1" });
            candidateId = c.Id;
        }

        using (var scope = factory.Services.CreateScope())
        {
            var svc = scope.ServiceProvider.GetRequiredService<WorkItemApprovalService>();
            await svc.ApproveAsync("FOO-1", "acme", "prod", "noted", default);
        }

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            // Candidate stays Pending — PromotionOnly gate doesn't count ticket signoffs.
            var candidate = await db.PromotionCandidates.AsNoTracking()
                .FirstAsync(c => c.Id == candidateId);
            Assert.Equal(PromotionStatus.Pending, candidate.Status);

            // Ticket-level audit + legacy audit should still fire — these are independent of
            // the gate.
            Assert.Single(await db.AuditLog.AsNoTracking()
                .Where(a => a.Action == "promotion.ticket.approved").ToListAsync());
            Assert.Single(await db.AuditLog.AsNoTracking()
                .Where(a => a.Action == "work-item.approved").ToListAsync());
            // No coarse promotion.approved.
            Assert.Empty(await db.AuditLog.AsNoTracking()
                .Where(a => a.Action == "promotion.approved" && a.EntityId == candidateId).ToListAsync());
        }

        // Ticket-level webhook fires unconditionally.
        await factory.WebhookDispatcher.Received().DispatchAsync(
            "promotion.ticket.approved",
            Arg.Any<object>(),
            Arg.Any<WebhookEventFilters>());
    }

    // ── 9. SupersededCandidate_TicketsCarryForward_NewCandidateUsesAccumulatedTickets ──

    [Fact]
    public async Task SupersededCandidate_TicketsCarryForward_NewCandidateUsesAccumulatedTickets()
    {
        await using var factory = new GateTestFactory();
        factory.Current.Email = "approver@example.com";
        factory.Current.RolesList = new() { "ReleaseApprovers" };

        Guid newerCandidateId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();

            // Older candidate Ca with T1.
            var (oldEvent, _, oldCandidate) = await SeedAsync(db, gate: PromotionGate.TicketsOnly,
                workItemKeys: new[] { "T1" },
                createdAt: DateTimeOffset.UtcNow.AddMinutes(-10));

            // Simulate supersede: Cb on a new event with T2, inheriting the old event's id so its
            // bundle includes T1 too.
            oldCandidate.Status = PromotionStatus.Superseded;

            var newEvent = NewDeployEvent();
            db.DeployEvents.Add(newEvent);
            db.DeployEventWorkItems.Add(new DeployEventWorkItem
            {
                Id = Guid.NewGuid(),
                DeployEventId = newEvent.Id,
                WorkItemKey = "T2",
                Product = "acme",
                CreatedAt = DateTimeOffset.UtcNow,
            });

            var newer = NewCandidate(newEvent.Id, gate: PromotionGate.TicketsOnly);
            newer.SupersededSourceEventIds = new() { oldEvent.Id };
            db.PromotionCandidates.Add(newer);
            oldCandidate.SupersededById = newer.Id;
            await db.SaveChangesAsync();
            newerCandidateId = newer.Id;
        }

        // Approve T1 (was on superseded predecessor's event).
        using (var scope = factory.Services.CreateScope())
        {
            var svc = scope.ServiceProvider.GetRequiredService<WorkItemApprovalService>();
            await svc.ApproveAsync("T1", "acme", "prod", null, default);
        }

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            var candidate = await db.PromotionCandidates.AsNoTracking()
                .FirstAsync(c => c.Id == newerCandidateId);
            // Cb still pending — T2 hasn't been approved yet.
            Assert.Equal(PromotionStatus.Pending, candidate.Status);
        }

        // Approve T2 (on Cb's own source event).
        using (var scope = factory.Services.CreateScope())
        {
            var svc = scope.ServiceProvider.GetRequiredService<WorkItemApprovalService>();
            await svc.ApproveAsync("T2", "acme", "prod", null, default);
        }

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            var candidate = await db.PromotionCandidates.AsNoTracking()
                .FirstAsync(c => c.Id == newerCandidateId);
            Assert.Equal(PromotionStatus.Approved, candidate.Status);
        }
    }

    // ── 10. TicketsOnly_OrphanedTicketSignoff_NoActiveCandidate_RejectsRecord ──

    // Adjusted from spec: the spec asks us to verify against actual Phase 3B
    // behaviour. RecordAsync throws InvalidOperationException("No Pending promotion
    // candidate references this ticket") when no candidate carries the ticket — it
    // does NOT persist a row, NOT emit ticket audit, NOT emit webhook. We assert that.

    [Fact]
    public async Task TicketsOnly_OrphanedTicketSignoff_NoActiveCandidate_ThrowsAndPersistsNothing()
    {
        await using var factory = new GateTestFactory();
        factory.Current.Email = "approver@example.com";
        factory.Current.RolesList = new() { "ReleaseApprovers" };

        // Seed a deploy event + work-item but mark its candidate as already Approved so there's
        // nothing Pending to attach to.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            var (_, _, c) = await SeedAsync(db, gate: PromotionGate.TicketsOnly,
                workItemKeys: new[] { "ORPH-1" });
            c.Status = PromotionStatus.Approved;
            await db.SaveChangesAsync();
        }

        using (var scope = factory.Services.CreateScope())
        {
            var svc = scope.ServiceProvider.GetRequiredService<WorkItemApprovalService>();
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                svc.ApproveAsync("ORPH-1", "acme", "prod", null, default));
            Assert.Contains("Pending", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();

            // No WorkItemApproval row written.
            Assert.Empty(await db.WorkItemApprovals.AsNoTracking().ToListAsync());

            // No ticket-level audit (neither legacy nor new ticket-level event).
            Assert.Empty(await db.AuditLog.AsNoTracking()
                .Where(a => a.Action == "promotion.ticket.approved").ToListAsync());
            Assert.Empty(await db.AuditLog.AsNoTracking()
                .Where(a => a.Action == "work-item.approved").ToListAsync());
        }

        // No webhook dispatch for a ticket that wasn't recorded.
        await factory.WebhookDispatcher.DidNotReceive().DispatchAsync(
            "promotion.ticket.approved",
            Arg.Any<object>(),
            Arg.Any<WebhookEventFilters>());
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Seeds a Pending PromotionCandidate carrying zero or more ticket keys on its source
    /// DeployEvent. The snapshot's <see cref="ResolvedPolicySnapshot.Gate"/> is set to
    /// <paramref name="gate"/> so each test can dial in the mode under test.
    /// </summary>
    private static async Task<(DeployEvent ev, List<DeployEventWorkItem> workItems, PromotionCandidate cand)>
        SeedAsync(
            PlatformDbContext db,
            PromotionGate gate,
            IEnumerable<string> workItemKeys,
            string approverGroup = "ReleaseApprovers",
            string product = "acme",
            string service = "api",
            string sourceEnv = "staging",
            string targetEnv = "prod",
            DateTimeOffset? createdAt = null)
    {
        var ev = NewDeployEvent(product, service, sourceEnv);
        db.DeployEvents.Add(ev);

        var items = new List<DeployEventWorkItem>();
        foreach (var key in workItemKeys)
        {
            var wi = new DeployEventWorkItem
            {
                Id = Guid.NewGuid(),
                DeployEventId = ev.Id,
                WorkItemKey = key,
                Product = product,
                CreatedAt = DateTimeOffset.UtcNow,
            };
            items.Add(wi);
            db.DeployEventWorkItems.Add(wi);
        }

        var cand = NewCandidate(ev.Id, gate, approverGroup, product, service, sourceEnv, targetEnv);
        if (createdAt is not null) cand.CreatedAt = createdAt.Value;
        db.PromotionCandidates.Add(cand);

        await db.SaveChangesAsync();
        return (ev, items, cand);
    }

    private static DeployEvent NewDeployEvent(
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
            ParticipantsJson = "[]",
            MetadataJson = "{}",
            CreatedAt = DateTimeOffset.UtcNow,
        };
    }

    private static PromotionCandidate NewCandidate(
        Guid sourceEventId,
        PromotionGate gate,
        string approverGroup = "ReleaseApprovers",
        string product = "acme",
        string service = "api",
        string sourceEnv = "staging",
        string targetEnv = "prod")
    {
        var snapshot = new ResolvedPolicySnapshot(
            PolicyId: Guid.NewGuid(),
            ApproverGroup: approverGroup,
            Strategy: PromotionStrategy.Any,
            MinApprovers: 1,
            ExcludeRole: null,
            TimeoutHours: 24,
            EscalationGroup: null)
        {
            Gate = gate,
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
            SourceDeployEventId = sourceEventId,
            Status = PromotionStatus.Pending,
            PolicyId = snapshot.PolicyId,
            ResolvedPolicyJson = json,
            CreatedAt = DateTimeOffset.UtcNow,
            SupersededSourceEventIdsJson = "[]",
            ParticipantsJson = "[]",
        };
    }

    // ── Test factory ────────────────────────────────────────────────────────

    /// <summary>
    /// Test host for gate-evaluator tests. Adds an NSubstitute-captured
    /// <see cref="IWebhookDispatcher"/> on top of the same fake <see cref="ICurrentUser"/>,
    /// empty <see cref="IIdentityService"/>, and context-free <see cref="IAuditLogger"/> setup
    /// used by <c>WorkItemApprovalTests</c>. We define our own factory rather than reuse the
    /// nested one in WorkItemApprovalTests so we can register the webhook substitute (the other
    /// factory uses the real WebhookDispatcher, which is a no-op against an empty subscription
    /// table — fine for those tests, less observable here).
    /// </summary>
    public class GateTestFactory : WebApplicationFactory<Program>, IAsyncDisposable
    {
        public WorkItemApprovalTests.FakeCurrentUser Current { get; } = new();
        public IWebhookDispatcher WebhookDispatcher { get; } = Substitute.For<IWebhookDispatcher>();
        private readonly SqliteConnection _connection;

        public GateTestFactory()
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

                RemoveService<ICurrentUser>(services);
                services.AddSingleton<ICurrentUser>(Current);

                RemoveService<IIdentityService>(services);
                services.AddScoped<IIdentityService, WorkItemApprovalTests.EmptyIdentityService>();

                RemoveService<IAuditLogger>(services);
                services.AddScoped<IAuditLogger, WorkItemApprovalTests.ContextFreeAuditLogger>();

                RemoveService<IWebhookDispatcher>(services);
                services.AddSingleton(WebhookDispatcher);
            });
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
}
