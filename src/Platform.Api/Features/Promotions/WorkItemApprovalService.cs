using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Platform.Api.Features.Deployments.Models;
using Platform.Api.Features.Promotions.Models;
using Platform.Api.Features.Webhooks;
using Platform.Api.Infrastructure;
using Platform.Api.Infrastructure.Audit;
using Platform.Api.Infrastructure.Auth;
using Platform.Api.Infrastructure.Persistence;

namespace Platform.Api.Features.Promotions;

/// <summary>
/// Records ticket-level (work-item) approvals. Persistent state only —
/// the gate evaluator that auto-promotes candidates when all tickets are
/// signed lives in PR3. Approvals carry across superseded builds because
/// they key on (workItemKey, product, targetEnv), not on the candidate.
///
/// <para>Authority: a user can decide on a ticket if a Pending PromotionCandidate
/// in the same (product, targetEnv) carries the ticket and the user
/// satisfies that candidate's policy snapshot — i.e. they're in the
/// approver group and not on the excluded role for the source event.
/// One signoff per ticket per (product, env, approver) — enforced by
/// unique index plus an in-app duplicate check that returns a friendly
/// 400 instead of a DB exception.</para>
/// </summary>
public class WorkItemApprovalService
{
    private readonly PlatformDbContext _db;
    private readonly PromotionApprovalAuthorizer _auth;
    private readonly ICurrentUser _currentUser;
    private readonly IAuditLogger _audit;
    private readonly IWebhookDispatcher _webhookDispatcher;
    private readonly PromotionService _promotion;
    private readonly PromotionAssigneeRoleSettings _assigneeRoles;
    private readonly ILogger<WorkItemApprovalService> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    // PromotionService is injected so ticket signoffs can drive candidate state transitions
    // (auto-promote on approve, veto-cascade on reject). The dependency is one-way:
    // PromotionService does NOT pull WorkItemApprovalService, so DI resolution is unambiguous.
    public WorkItemApprovalService(
        PlatformDbContext db,
        PromotionApprovalAuthorizer auth,
        ICurrentUser currentUser,
        IAuditLogger audit,
        IWebhookDispatcher webhookDispatcher,
        PromotionService promotion,
        PromotionAssigneeRoleSettings assigneeRoles,
        ILogger<WorkItemApprovalService> logger)
    {
        _db = db;
        _auth = auth;
        _currentUser = currentUser;
        _audit = audit;
        _webhookDispatcher = webhookDispatcher;
        _promotion = promotion;
        _assigneeRoles = assigneeRoles;
        _logger = logger;
    }

    // ---------------------------------------------------------------------
    // Decision recording
    // ---------------------------------------------------------------------

    public Task<WorkItemApproval> ApproveAsync(
        string workItemKey, string product, string targetEnv, string? comment, CancellationToken ct = default)
        => RecordAsync(workItemKey, product, targetEnv, comment, PromotionDecision.Approved, ct);

    public Task<WorkItemApproval> RejectAsync(
        string workItemKey, string product, string targetEnv, string? comment, CancellationToken ct = default)
        => RecordAsync(workItemKey, product, targetEnv, comment, PromotionDecision.Rejected, ct);

    /// <summary>
    /// Records a ticket-level decision after authority + duplicate checks. Does not transition
    /// any candidate — that's PR3's gate evaluator.
    ///
    /// <para>Throws <see cref="InvalidOperationException"/> for "no pending candidate carries
    /// this ticket", "already decided", or "auto-approve policy" so endpoints map them to 400.
    /// Throws <see cref="UnauthorizedAccessException"/> for excluded role / not in approver
    /// group so endpoints map them to 403.</para>
    /// </summary>
    private async Task<WorkItemApproval> RecordAsync(
        string workItemKey, string product, string targetEnv,
        string? comment, PromotionDecision decision, CancellationToken ct)
    {
        var key = (workItemKey ?? "").Trim();
        var prod = (product ?? "").Trim();
        var env = (targetEnv ?? "").Trim();
        if (string.IsNullOrEmpty(key))
            throw new InvalidOperationException("workItemKey is required");
        if (string.IsNullOrEmpty(prod))
            throw new InvalidOperationException("product is required");
        if (string.IsNullOrEmpty(env))
            throw new InvalidOperationException("targetEnv is required");

        var candidate = await FindPendingCandidateForTicketAsync(key, prod, env, ct)
            ?? throw new InvalidOperationException("No Pending promotion candidate references this ticket");

        var snapshot = ReadSnapshot(candidate);

        // Auto-approve has no human gate; a ticket signoff against it is meaningless.
        if (snapshot.IsAutoApprove)
            throw new InvalidOperationException("This promotion is auto-approve; ticket signoff is not applicable");

        if (await _auth.IsEmailExcludedByRoleAsync(
                snapshot, candidate.SourceDeployEventId, candidate.SupersededSourceEventIds, _currentUser.Email, ct))
        {
            throw new UnauthorizedAccessException(
                $"You cannot decide on this ticket — the '{snapshot.ExcludeRole}' role is excluded from approving this promotion.");
        }

        if (!await _auth.IsInApproverGroupAsync(snapshot.ApproverGroup!, ct))
            throw new UnauthorizedAccessException("You are not in the approver group for this promotion");

        // Duplicate guard. The unique index enforces this at the DB; the in-app check turns the
        // race-loser case into a clean 400 instead of a 500 with an obscure constraint message.
        var alreadyDecided = await _db.WorkItemApprovals.AsNoTracking()
            .AnyAsync(a =>
                a.WorkItemKey == key &&
                a.Product == prod &&
                a.TargetEnv == env &&
                a.ApproverEmail == _currentUser.Email, ct);
        if (alreadyDecided)
            throw new InvalidOperationException("You have already made a decision on this ticket");

        var row = new WorkItemApproval
        {
            Id = Guid.NewGuid(),
            WorkItemKey = key,
            Product = prod,
            TargetEnv = env,
            ApproverEmail = _currentUser.Email,
            ApproverName = _currentUser.Name,
            Decision = decision,
            Comment = comment,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        _db.WorkItemApprovals.Add(row);
        await _db.SaveChangesAsync(ct);

        // Legacy granular row-level audit kept for backward compatibility with existing callers
        // (dashboards, alerts, integration tests). The new ticket-level audit + webhook events
        // emitted below are the canonical events for downstream consumers.
        var legacyAction = decision == PromotionDecision.Approved ? "work-item.approved" : "work-item.rejected";
        await _audit.Log(
            "promotions", legacyAction,
            _currentUser.Id, _currentUser.Name, "user",
            "WorkItemApproval", row.Id, null,
            new { workItemKey = key, product = prod, targetEnv = env, candidateId = candidate.Id, comment });

        // Ticket-level audit + webhook: independent of any candidate cascade. We emit even when
        // there's no live candidate carrying the ticket — but in this path RecordAsync requires
        // a Pending candidate, so candidateId is always non-null here. The orphan-signoff case
        // (separate from RecordAsync) doesn't exist today; if it ever does, the helper accepts a
        // null candidateId.
        await EmitTicketEventsAsync(decision, key, prod, env, candidate.Id, comment, ct);

        _logger.LogInformation(
            "Work-item decision recorded: {Decision} on {Key} ({Product}/{Env}) by {Email}; candidate {CandidateId}",
            decision, key, prod, env, _currentUser.Email, candidate.Id);

        // Drive the candidate side. Approve → re-evaluate the gate (may auto-promote when
        // TicketsOnly / TicketsAndManual conditions are met). Reject → veto cascade: terminate
        // the candidate directly with the rejecting user as the actor.
        if (decision == PromotionDecision.Approved)
        {
            await TryReevaluateCandidateAsync(candidate.Id, ct);
        }
        else
        {
            await CascadeRejectCandidateAsync(candidate, comment, ct);
        }

        return row;
    }

    /// <summary>
    /// Emits the ticket-level audit + webhook for an Approve / Reject. Independent of any
    /// candidate cascade so the ticket signoff itself is always observable, even if the
    /// candidate is no longer live (orphaned signoff path — RecordAsync rejects this today
    /// but the shape is here for future use).
    /// </summary>
    private async Task EmitTicketEventsAsync(
        PromotionDecision decision,
        string workItemKey, string product, string targetEnv,
        Guid? candidateId, string? comment, CancellationToken ct)
    {
        var action = decision == PromotionDecision.Approved
            ? "promotion.ticket.approved"
            : "promotion.ticket.rejected";

        // No dedicated ticket entity exists; the audit row attaches to the candidate when one
        // is known so the UI can deep-link. When the cascade has no live candidate (future),
        // entityType remains "PromotionCandidate" with a null entity id — the payload still
        // identifies the ticket via workItemKey + product + targetEnv.
        await _audit.Log(
            "promotions", action,
            _currentUser.Id, _currentUser.Name, "user",
            "PromotionCandidate", candidateId, null,
            new
            {
                workItemKey,
                product,
                targetEnv,
                candidateId,
                approver = _currentUser.Email,
                comment,
            });

        try
        {
            var payload = new
            {
                workItemKey,
                product,
                targetEnv,
                candidateId,
                approver = _currentUser.Email,
                comment,
            };
            var filters = new WebhookEventFilters(Product: product, Environment: targetEnv);
            await _webhookDispatcher.DispatchAsync(action, payload, filters);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Webhook dispatch '{EventType}' failed for ticket {Key} ({Product}/{Env})",
                action, workItemKey, product, targetEnv);
        }
    }

    /// <summary>
    /// Auto-promote hook: re-evaluates the candidate's gate after a ticket approval. Idempotent
    /// — <see cref="PromotionService.ReevaluateAsync"/> no-ops when the candidate is no longer
    /// Pending or the gate isn't satisfied. Failures are logged but never propagated: the
    /// ticket-level approval has already been persisted and is meaningful on its own.
    /// </summary>
    private async Task TryReevaluateCandidateAsync(Guid candidateId, CancellationToken ct)
    {
        try
        {
            await _promotion.ReevaluateAsync(candidateId, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Gate re-evaluation failed for candidate {CandidateId} after ticket approval",
                candidateId);
        }
    }

    /// <summary>
    /// Veto cascade: a ticket rejection terminates the candidate directly without going through
    /// the gate evaluator. The rejecting user is the actor on the resulting
    /// <c>promotion.rejected</c> audit + webhook so the audit chain stays attributable.
    /// Idempotent in the soft sense: if the candidate has already moved on (Approved, Rejected,
    /// Superseded, etc.) we skip the cascade rather than fight a state machine.
    /// </summary>
    private async Task CascadeRejectCandidateAsync(
        PromotionCandidate candidate, string? comment, CancellationToken ct)
    {
        // Reload tracked. RecordAsync's `candidate` was fetched from the same DbContext but as
        // part of a query that may or may not be tracked; safer to fetch a tracked instance.
        var tracked = await _db.PromotionCandidates.FirstOrDefaultAsync(c => c.Id == candidate.Id, ct);
        if (tracked is null) return;
        if (tracked.Status != PromotionStatus.Pending) return;

        // PromotionCandidate doesn't carry a generic "decided at" timestamp; the existing
        // PromotionService.RejectAsync flow only flips Status. Mirror that here so the rejection
        // shape matches the manual-reject path. (CreatedAt remains the audit anchor; the audit
        // entry timestamp is the canonical "when did this rejection happen".)
        tracked.Status = PromotionStatus.Rejected;
        await _db.SaveChangesAsync(ct);

        await _audit.Log(
            "promotions", "promotion.rejected",
            _currentUser.Id, _currentUser.Name, "user",
            "PromotionCandidate", tracked.Id, null,
            new { trigger = "ticket-veto", comment });

        _logger.LogInformation(
            "Candidate {Id} rejected via ticket-veto cascade by {Email}",
            tracked.Id, _currentUser.Email);

        try
        {
            var payload = new
            {
                candidateId = tracked.Id,
                tracked.Product,
                tracked.Service,
                tracked.SourceEnv,
                tracked.TargetEnv,
                tracked.Version,
                tracked.SourceDeployEventId,
                tracked.SourceDeployerEmail,
                status = tracked.Status.ToString(),
                tracked.ApprovedAt,
                participants = tracked.Participants,
                change = new { trigger = "ticket-veto" },
            };
            var filters = new WebhookEventFilters(Product: tracked.Product, Environment: tracked.TargetEnv);
            await _webhookDispatcher.DispatchAsync("promotion.rejected", payload, filters);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Webhook dispatch 'promotion.rejected' failed for candidate {Id}", tracked.Id);
        }
    }

    // ---------------------------------------------------------------------
    // Queries
    // ---------------------------------------------------------------------

    public async Task<List<WorkItemApproval>> GetForKeyAsync(
        string workItemKey, string product, string targetEnv, CancellationToken ct = default)
    {
        return await _db.WorkItemApprovals.AsNoTracking()
            .Where(a => a.WorkItemKey == workItemKey && a.Product == product && a.TargetEnv == targetEnv)
            .OrderBy(a => a.CreatedAt)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Snapshot of a ticket's authority state for the current user — used by the GET endpoint
    /// to drive the UI button state. Returns <c>null</c> only when input is malformed; an absent
    /// pending candidate is represented by <see cref="TicketContext.PendingCandidateId"/> being
    /// null with <c>BlockedReason = "No pending promotion needs this ticket"</c>.
    /// </summary>
    public async Task<TicketContext> GetTicketContextAsync(
        string workItemKey, string product, string targetEnv, CancellationToken ct = default)
    {
        var key = (workItemKey ?? "").Trim();
        var prod = (product ?? "").Trim();
        var env = (targetEnv ?? "").Trim();

        var approvals = (key.Length == 0 || prod.Length == 0 || env.Length == 0)
            ? new List<WorkItemApproval>()
            : await GetForKeyAsync(key, prod, env, ct);

        var candidate = (key.Length == 0 || prod.Length == 0 || env.Length == 0)
            ? null
            : await FindPendingCandidateForTicketAsync(key, prod, env, ct);

        // Build BlockedReason in the same order as the throwing path so the UI message matches
        // what the user would see if they tried to act.
        var alreadyDecidedByMe = approvals.Any(a =>
            string.Equals(a.ApproverEmail, _currentUser.Email, StringComparison.OrdinalIgnoreCase));

        bool canApprove = false;
        string? blockedReason = null;

        if (candidate is null)
        {
            blockedReason = "No pending promotion needs this ticket";
        }
        else
        {
            var snapshot = ReadSnapshot(candidate);
            if (snapshot.IsAutoApprove)
            {
                blockedReason = "Auto-approve policy";
            }
            else if (alreadyDecidedByMe)
            {
                blockedReason = "Already decided";
            }
            else if (await _auth.IsEmailExcludedByRoleAsync(
                snapshot, candidate.SourceDeployEventId, candidate.SupersededSourceEventIds, _currentUser.Email, ct))
            {
                blockedReason = "Excluded role";
            }
            else if (!await _auth.IsInApproverGroupAsync(snapshot.ApproverGroup!, ct))
            {
                blockedReason = "Not in approver group";
            }
            else
            {
                canApprove = true;
            }
        }

        return new TicketContext(
            WorkItemKey: key,
            Product: prod,
            TargetEnv: env,
            PendingCandidateId: candidate?.Id,
            CanApprove: canApprove,
            BlockedReason: blockedReason,
            Approvals: approvals);
    }

    /// <summary>
    /// Builds the inbox list — tickets the current user could sign off on right now (no decision
    /// yet, in approver group, not excluded). One row per (ticket × candidate) with a
    /// <c>BlockingPromotions</c> count when the same ticket appears across multiple Pending edges.
    ///
    /// <para>Strategy: load all Pending candidates, group their bundles' work-items, then filter
    /// in-memory after caching one approver-group lookup per distinct group. This is O(N) over
    /// Pending candidates × tickets-per-candidate which is small in practice (the Pending queue
    /// is bounded by the number of services × envs, not historical events). The distinct group
    /// cache mirrors <see cref="PromotionService.CanUserApproveManyAsync"/>.</para>
    /// </summary>
    public async Task<List<PendingTicketView>> GetPendingForCurrentUserAsync(
        CancellationToken ct = default,
        string? assigneeFilter = null)
    {
        var pending = await _db.PromotionCandidates.AsNoTracking()
            .Where(c => c.Status == PromotionStatus.Pending)
            .ToListAsync(ct);
        if (pending.Count == 0) return new();

        // Resolve the assignee-role set up front when a filter is set; skip when null since
        // the post-filter pass below short-circuits and we'd be doing unnecessary work.
        var trimmedAssignee = assigneeFilter?.Trim();
        var assigneeFilterActive = !string.IsNullOrEmpty(trimmedAssignee);
        var assigneeIsUnassigned = assigneeFilterActive
            && string.Equals(trimmedAssignee, "unassigned", StringComparison.OrdinalIgnoreCase);
        var assigneeEmail = (assigneeFilterActive && !assigneeIsUnassigned)
            ? trimmedAssignee!.ToLowerInvariant()
            : null;
        IReadOnlyList<string> assigneeRoles = assigneeFilterActive
            ? await _assigneeRoles.GetAsync(ct)
            : Array.Empty<string>();
        var assigneeRoleSet = new HashSet<string>(assigneeRoles, StringComparer.OrdinalIgnoreCase);

        // Bundle → all event ids that contribute work-items to a candidate.
        var allEventIds = pending
            .SelectMany(c => new[] { c.SourceDeployEventId }.Concat(c.SupersededSourceEventIds))
            .Distinct()
            .ToList();

        var workItems = await _db.DeployEventWorkItems.AsNoTracking()
            .Where(w => allEventIds.Contains(w.DeployEventId))
            .ToListAsync(ct);
        if (workItems.Count == 0) return new();

        // Group user's existing decisions: (key, product, env) tuples to skip.
        var decided = await _db.WorkItemApprovals.AsNoTracking()
            .Where(a => a.ApproverEmail == _currentUser.Email)
            .Select(a => new { a.WorkItemKey, a.Product, a.TargetEnv })
            .ToListAsync(ct);
        var decidedSet = decided
            .Select(d => (d.WorkItemKey, d.Product, d.TargetEnv))
            .ToHashSet();

        // Source-event participants + references for excluded-role checks (two-level lookup).
        // Pulled once for all Pending sources to avoid N+1.
        var sourceJsonByEvent = await _db.DeployEvents.AsNoTracking()
            .Where(e => allEventIds.Contains(e.Id))
            .Select(e => new { e.Id, e.ParticipantsJson, e.ReferencesJson })
            .ToDictionaryAsync(e => e.Id, e => (e.ParticipantsJson, e.ReferencesJson), ct);

        // Operator overrides for those same events — same batch shape so the per-candidate
        // exclusion check below can consult overrides without an extra query per row.
        var overridesRows = await _db.ReferenceParticipantOverrides.AsNoTracking()
            .Where(o => allEventIds.Contains(o.DeployEventId))
            .ToListAsync(ct);
        var overridesByEvent = overridesRows
            .GroupBy(o => o.DeployEventId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<ReferenceParticipantOverride>)g.ToList());

        // Cache approver-group membership across distinct groups.
        var groupMembership = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        // Index work-items by their event for fast lookup.
        var workItemsByEvent = workItems
            .GroupBy(w => w.DeployEventId)
            .ToDictionary(g => g.Key, g => g.ToList());

        // Build (key, product, targetEnv) -> count of Pending candidates whose bundle carries it.
        var blockingCount = new Dictionary<(string Key, string Product, string Env), int>();
        foreach (var c in pending)
        {
            var bundle = new[] { c.SourceDeployEventId }.Concat(c.SupersededSourceEventIds);
            var keys = bundle
                .SelectMany(eid => workItemsByEvent.GetValueOrDefault(eid) ?? new())
                .Where(w => string.Equals(w.Product, c.Product, StringComparison.Ordinal))
                .Select(w => w.WorkItemKey)
                .Distinct();
            foreach (var k in keys)
            {
                var tup = (k, c.Product, c.TargetEnv);
                blockingCount[tup] = blockingCount.GetValueOrDefault(tup) + 1;
            }
        }

        var result = new List<PendingTicketView>();
        var emitted = new HashSet<(string Key, Guid CandidateId)>();

        // Order Pending candidates newest-first so the most recent candidate "owns" the inbox row
        // when the same ticket appears in multiple — keeps the list deterministic and surfaces
        // the freshest version/promotion to the approver.
        foreach (var c in pending.OrderByDescending(p => p.CreatedAt))
        {
            var snapshot = ReadSnapshot(c);
            if (snapshot.IsAutoApprove) continue;

            // Group membership cached per distinct group.
            var group = snapshot.ApproverGroup!;
            if (!groupMembership.TryGetValue(group, out var member))
            {
                member = await _auth.IsInApproverGroupAsync(group, ct);
                groupMembership[group] = member;
            }
            if (!member) continue;

            // Excluded-role: precomputed JSON dict so this is in-memory only. Pass both
            // event-level participants and references so reference-level roles count too.
            // Walk the supersede bundle so an excluded participant on an inherited event also trips.
            if (!string.IsNullOrWhiteSpace(snapshot.ExcludeRole))
            {
                var exclBundle = new[] { c.SourceDeployEventId }.Concat(c.SupersededSourceEventIds);
                var excluded = false;
                foreach (var eid in exclBundle)
                {
                    var (partsJson, refsJson) = sourceJsonByEvent.GetValueOrDefault(eid);
                    var evOverrides = overridesByEvent.GetValueOrDefault(eid, Array.Empty<ReferenceParticipantOverride>());
                    if (PromotionApprovalAuthorizer.EmailMatchesExcludedRole(
                            partsJson, refsJson, evOverrides, snapshot.ExcludeRole, _currentUser.Email))
                    {
                        excluded = true; break;
                    }
                }
                if (excluded) continue;
            }

            var bundle = new[] { c.SourceDeployEventId }.Concat(c.SupersededSourceEventIds);
            // Distinct work items in the bundle (a ticket may appear on multiple events in the chain).
            var bundleItems = bundle
                .SelectMany(eid => workItemsByEvent.GetValueOrDefault(eid) ?? new())
                .Where(w => string.Equals(w.Product, c.Product, StringComparison.Ordinal))
                .GroupBy(w => w.WorkItemKey)
                .Select(g => g.First());

            // Build the merged participant view across the candidate's full bundle once per
            // candidate (only when the assignee filter is active — pure narrowing, doesn't
            // touch authorisation). Tombstones are honoured via FindByRoleWithOverrides so
            // suppressed roles don't count as "assigned".
            List<MergedParticipant>? bundleAssignees = null;
            if (assigneeFilterActive)
            {
                bundleAssignees = BuildMergedAssignees(
                    new[] { c.SourceDeployEventId }.Concat(c.SupersededSourceEventIds),
                    sourceJsonByEvent, overridesByEvent, assigneeRoleSet);

                if (assigneeIsUnassigned)
                {
                    // No participant in the merged set has a role in the assignee role set.
                    if (bundleAssignees.Count > 0) continue;
                }
                else
                {
                    // Some participant has email match (case-insensitive) AND role ∈ assigneeRoles.
                    var anyMatch = bundleAssignees.Any(p =>
                        !string.IsNullOrEmpty(p.Email)
                        && string.Equals(p.Email, assigneeEmail, StringComparison.OrdinalIgnoreCase));
                    if (!anyMatch) continue;
                }
            }

            foreach (var w in bundleItems)
            {
                var tup = (w.WorkItemKey, c.Product, c.TargetEnv);
                if (decidedSet.Contains(tup)) continue;
                if (!emitted.Add((w.WorkItemKey, c.Id))) continue;

                result.Add(new PendingTicketView(
                    WorkItemKey: w.WorkItemKey,
                    Product: c.Product,
                    TargetEnv: c.TargetEnv,
                    Provider: w.Provider,
                    Url: w.Url,
                    Title: w.Title,
                    CandidateId: c.Id,
                    Service: c.Service,
                    Version: c.Version,
                    SourceEnv: c.SourceEnv,
                    BlockingPromotions: blockingCount.GetValueOrDefault(tup, 1)));
            }
        }

        return result;
    }

    // ---------------------------------------------------------------------
    // Private helpers
    // ---------------------------------------------------------------------

    /// <summary>
    /// Picks the candidate whose policy will gate the decision. A ticket can appear in multiple
    /// Pending candidates (different services or envs); we pick the most recently created
    /// candidate in <c>(product, targetEnv)</c> whose bundle includes the ticket. Most-recent
    /// because it represents the freshest state of the world — a newer Pending candidate either
    /// superseded an older one on the same edge, or it's a parallel edge whose approver group
    /// is the same anyway in the common case (per-product policy).
    /// </summary>
    private async Task<PromotionCandidate?> FindPendingCandidateForTicketAsync(
        string workItemKey, string product, string targetEnv, CancellationToken ct)
    {
        // 1. Find all events that carry this ticket in this product.
        var eventIds = await _db.DeployEventWorkItems.AsNoTracking()
            .Where(w => w.WorkItemKey == workItemKey && w.Product == product)
            .Select(w => w.DeployEventId)
            .Distinct()
            .ToListAsync(ct);
        if (eventIds.Count == 0) return null;

        // 2. Pending candidates in (product, targetEnv) — small set, fetch in full and match
        //    bundle membership client-side (SupersededSourceEventIds is JSON, can't filter in SQL
        //    portably).
        var pending = await _db.PromotionCandidates.AsNoTracking()
            .Where(c => c.Product == product
                     && c.TargetEnv == targetEnv
                     && c.Status == PromotionStatus.Pending)
            .OrderByDescending(c => c.CreatedAt)
            .ToListAsync(ct);

        var eventIdSet = eventIds.ToHashSet();
        return pending.FirstOrDefault(c =>
            eventIdSet.Contains(c.SourceDeployEventId)
            || c.SupersededSourceEventIds.Any(eventIdSet.Contains));
    }

    /// <summary>
    /// Walks every event in the candidate's bundle and emits the merged participant view —
    /// override layer first (with reference scoping + tombstones suppressing fall-through),
    /// then reference-level participants for any reference whose (key, role) wasn't already
    /// resolved by an override, then event-level participants for any role not already covered.
    ///
    /// <para>Only participants whose role canonicalises into <paramref name="assigneeRoleSet"/>
    /// are returned — the filter cares about a specific subset of roles and we don't need to
    /// surface anything else.</para>
    /// </summary>
    private static List<MergedParticipant> BuildMergedAssignees(
        IEnumerable<Guid> bundleEventIds,
        Dictionary<Guid, (string ParticipantsJson, string ReferencesJson)> sourceJsonByEvent,
        Dictionary<Guid, IReadOnlyList<ReferenceParticipantOverride>> overridesByEvent,
        HashSet<string> assigneeRoleSet)
    {
        var result = new List<MergedParticipant>();
        if (assigneeRoleSet.Count == 0) return result;

        foreach (var eid in bundleEventIds.Distinct())
        {
            var (partsJson, refsJson) = sourceJsonByEvent.GetValueOrDefault(eid);
            var evOverrides = overridesByEvent.GetValueOrDefault(eid, Array.Empty<ReferenceParticipantOverride>());

            // Walk every (referenceKey, role-in-set) cell — ParticipantResolver handles override
            // precedence + tombstones for us. The set of reference keys to walk is the union of
            // ReferencesJson keys and any override-only reference keys.
            var refs = ParticipantResolver.GetReferenceParticipants(refsJson);
            var refKeys = refs
                .Select(rp => rp.Ref.Key ?? "")
                .Where(k => !string.IsNullOrEmpty(k))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var o in evOverrides)
            {
                if (!string.IsNullOrEmpty(o.ReferenceKey)) refKeys.Add(o.ReferenceKey);
            }

            foreach (var refKey in refKeys)
            {
                foreach (var role in assigneeRoleSet)
                {
                    var lookup = ParticipantResolver.FindByRoleWithOverrides(
                        partsJson, refsJson, evOverrides, role, refKey);
                    // Tombstone => slot is explicitly empty for this (refKey, role); skip.
                    if (lookup.Suppressed) continue;
                    if (lookup.Found is { } p && !string.IsNullOrEmpty(p.Email))
                    {
                        result.Add(new MergedParticipant(
                            Role: RoleNormalizer.Normalize(p.Role),
                            Email: p.Email!.Trim().ToLowerInvariant()));
                    }
                }
            }

            // Event-level participants: surface any whose role is in the set. These are not
            // reference-scoped so overrides (which always carry a reference key) don't displace
            // them — that mirrors the existing excluded-role check.
            foreach (var p in ParticipantResolver.GetEventParticipants(partsJson))
            {
                var canon = RoleNormalizer.Normalize(p.Role);
                if (canon.Length == 0) continue;
                if (!assigneeRoleSet.Contains(canon)) continue;
                if (string.IsNullOrEmpty(p.Email)) continue;
                result.Add(new MergedParticipant(
                    Role: canon,
                    Email: p.Email!.Trim().ToLowerInvariant()));
            }
        }

        return result;
    }

    private readonly record struct MergedParticipant(string Role, string Email);

    private static ResolvedPolicySnapshot ReadSnapshot(PromotionCandidate candidate)
    {
        if (string.IsNullOrEmpty(candidate.ResolvedPolicyJson))
            throw new InvalidOperationException(
                $"Candidate {candidate.Id} has no policy snapshot — data corruption?");
        return JsonSerializer.Deserialize<ResolvedPolicySnapshot>(candidate.ResolvedPolicyJson, JsonOptions)
            ?? throw new InvalidOperationException(
                $"Failed to deserialize policy snapshot for candidate {candidate.Id}");
    }
}

/// <summary>
/// Authority + history snapshot for a single ticket × (product, targetEnv) pair.
/// <para><c>BlockedReason</c> mirrors the failure modes of the throwing decision path so the
/// UI can surface the same wording it would see on a failed POST.</para>
/// </summary>
public record TicketContext(
    string WorkItemKey,
    string Product,
    string TargetEnv,
    Guid? PendingCandidateId,
    bool CanApprove,
    string? BlockedReason,
    List<WorkItemApproval> Approvals);

/// <summary>
/// One row of the "tickets I can sign off right now" inbox. Includes the work-item display
/// fields plus the candidate context the UI uses to build a deep link, plus a count of
/// distinct Pending candidates referencing the ticket so heavily-shared tickets can be flagged.
/// </summary>
public record PendingTicketView(
    string WorkItemKey,
    string Product,
    string TargetEnv,
    string? Provider,
    string? Url,
    string? Title,
    Guid CandidateId,
    string Service,
    string Version,
    string SourceEnv,
    int BlockingPromotions);
