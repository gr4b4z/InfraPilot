using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Platform.Api.Features.Deployments;
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

        // Separation-of-duties (ExcludeRole) removed (D17): anyone authorized for any requirement on
        // the promotion's rule tree may decide on its tickets.
        if (!await _auth.IsAuthorizedForAnyRequirementAsync(snapshot, _currentUser.Email, ct))
            throw new UnauthorizedAccessException("You are not authorized to approve this promotion");

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
        // WorkItemsOnly / WorkItemsAndManual conditions are met). Reject → veto cascade: terminate
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
                tracked.FromRevision,
                tracked.ToRevision,
                status = tracked.Status.ToString(),
                tracked.ApprovedAt,
                participants = tracked.Participants,
                references = tracked.References,
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
            else if (!await _auth.IsAuthorizedForAnyRequirementAsync(snapshot, _currentUser.Email, ct))
            {
                blockedReason = "Not authorized to approve this promotion";
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
    ///
    /// <para>Returns the rendered ticket list along with the (email, role) → count assignee
    /// summary built from the authorized list <i>before</i> the role/person filter is applied.
    /// The summary feeds the front-end's role + person dropdowns so the picker only ever
    /// surfaces choices the user can actually narrow to. Filtering first then collecting would
    /// hide every alternative — pre-filter is the correct anchor.</para>
    /// </summary>
    public async Task<PendingQueueResult> GetPendingForCurrentUserAsync(
        CancellationToken ct = default,
        string? assigneeFilter = null,
        string? roleFilter = null)
    {
        var pending = await _db.PromotionCandidates.AsNoTracking()
            .Where(c => c.Status == PromotionStatus.Pending)
            .ToListAsync(ct);

        // Always resolve the canonical assignee-role set — the response surfaces it directly so
        // the front-end can populate the role dropdown without an extra round trip, even when
        // the caller didn't pass a filter.
        var assigneeRoles = await _assigneeRoles.GetAsync(ct);
        var assigneeRoleSet = new HashSet<string>(assigneeRoles, StringComparer.OrdinalIgnoreCase);

        if (pending.Count == 0)
        {
            return new PendingQueueResult(new(), new(), assigneeRoles);
        }

        // Filter inputs. `roleFilter` narrows to a single canonicalised role; `assigneeFilter`
        // narrows to a specific email or to "unassigned". Both are optional and combine per the
        // matrix documented on WorkItemEndpoints.
        var trimmedAssignee = assigneeFilter?.Trim();
        var assigneeFilterActive = !string.IsNullOrEmpty(trimmedAssignee);
        var assigneeIsUnassigned = assigneeFilterActive
            && string.Equals(trimmedAssignee, "unassigned", StringComparison.OrdinalIgnoreCase);
        var assigneeEmail = (assigneeFilterActive && !assigneeIsUnassigned)
            ? trimmedAssignee!.ToLowerInvariant()
            : null;

        var canonicalRoleFilter = string.IsNullOrWhiteSpace(roleFilter)
            ? null
            : RoleNormalizer.Normalize(roleFilter);
        var roleFilterActive = !string.IsNullOrEmpty(canonicalRoleFilter);

        // Effective role set used for matching — single role when role-filter is active,
        // otherwise the full assignee-role set (the "any role" semantics).
        HashSet<string> effectiveRoleSet = roleFilterActive
            ? new HashSet<string>(new[] { canonicalRoleFilter! }, StringComparer.OrdinalIgnoreCase)
            : assigneeRoleSet;

        // Candidate-scoped work-item index — the candidate is self-contained, so its tickets come
        // from PromotionWorkItem by candidate id (not from deploy-event bundles).
        var candidateIds = pending.Select(c => c.Id).ToList();
        var workItems = await _db.PromotionWorkItems.AsNoTracking()
            .Where(w => candidateIds.Contains(w.CandidateId))
            .ToListAsync(ct);
        if (workItems.Count == 0)
        {
            return new PendingQueueResult(new(), new(), assigneeRoles);
        }

        // Group user's existing decisions: (key, product, env) tuples to skip.
        var decided = await _db.WorkItemApprovals.AsNoTracking()
            .Where(a => a.ApproverEmail == _currentUser.Email)
            .Select(a => new { a.WorkItemKey, a.Product, a.TargetEnv })
            .ToListAsync(ct);
        var decidedSet = decided
            .Select(d => (d.WorkItemKey, d.Product, d.TargetEnv))
            .ToHashSet();

        // Cache approver-group membership across distinct groups.
        var groupMembership = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        // Index work-items by their candidate for fast lookup.
        var workItemsByCandidate = workItems
            .GroupBy(w => w.CandidateId)
            .ToDictionary(g => g.Key, g => g.ToList());

        // Build (key, product, targetEnv) -> count of Pending candidates carrying it.
        var blockingCount = new Dictionary<(string Key, string Product, string Env), int>();
        foreach (var c in pending)
        {
            var keys = (workItemsByCandidate.GetValueOrDefault(c.Id) ?? new())
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

        // (email, role) → count + best displayName seen. Counts feed the assignee summary;
        // displayName is taken from the first non-empty value seen.
        var assigneeAccumulator = new Dictionary<(string Email, string Role), AssigneeAccumulator>();

        // Order Pending candidates newest-first so the most recent candidate "owns" the inbox row
        // when the same ticket appears in multiple — keeps the list deterministic and surfaces
        // the freshest version/promotion to the approver.
        foreach (var c in pending.OrderByDescending(p => p.CreatedAt))
        {
            var snapshot = ReadSnapshot(c);
            if (snapshot.IsAutoApprove) continue;

            // Authorized for at least one requirement on this candidate's rule tree? Group membership
            // is cached per distinct group across all candidates to avoid N+1 Graph calls; the
            // explicit user-list check is free.
            var authorized = false;
            foreach (var req in snapshot.AllRequirements)
            {
                if (req.Users.Any(u => string.Equals(u, _currentUser.Email, StringComparison.OrdinalIgnoreCase)))
                {
                    authorized = true;
                    break;
                }
                foreach (var group in req.Groups)
                {
                    if (!groupMembership.TryGetValue(group.Id, out var member))
                    {
                        member = await _auth.IsInApproverGroupAsync(group, ct);
                        groupMembership[group.Id] = member;
                    }
                    if (member) { authorized = true; break; }
                }
                if (authorized) break;
            }
            if (!authorized) continue;

            // Separation-of-duties (ExcludeRole) removed (D17) — no per-candidate exclusion check.

            // Distinct work items on this candidate.
            var bundleItems = (workItemsByCandidate.GetValueOrDefault(c.Id) ?? new())
                .GroupBy(w => w.WorkItemKey)
                .Select(g => g.First());

            // Merged participant view comes from the candidate's own data: reference-level
            // participants nested under each reference, plus promotion-level participants.
            var bundleAssignees = BuildMergedAssignees(c, assigneeRoleSet);

            // Update the assignee summary BEFORE narrowing — computed against the unfiltered
            // authorized list. Dedupe per (email, role) within a candidate.
            var seenInCandidate = new HashSet<(string Email, string Role)>();
            foreach (var p in bundleAssignees)
            {
                var key = (p.Email, p.Role);
                if (!seenInCandidate.Add(key)) continue;
                if (!assigneeAccumulator.TryGetValue(key, out var acc))
                {
                    acc = new AssigneeAccumulator(p.DisplayName, 0);
                }
                else if (string.IsNullOrEmpty(acc.DisplayName) && !string.IsNullOrEmpty(p.DisplayName))
                {
                    // Prefer the first non-empty displayName we encounter; once set, keep it.
                    acc = acc with { DisplayName = p.DisplayName };
                }
                acc = acc with { Count = acc.Count + 1 };
                assigneeAccumulator[key] = acc;
            }

            // Apply role/person matrix narrowing. Empty merged view => "unassigned" by
            // definition (legacy data, no participants, all tombstoned). The unassigned branch
            // keys off the merged-view subset matching the effective role set.
            if (assigneeFilterActive || roleFilterActive)
            {
                bool keep;
                var inEffectiveRole = bundleAssignees
                    .Where(p => effectiveRoleSet.Contains(p.Role))
                    .ToList();

                if (assigneeIsUnassigned)
                {
                    // No participant whose role ∈ effectiveRoleSet exists.
                    keep = inEffectiveRole.Count == 0;
                }
                else if (assigneeFilterActive)
                {
                    // Specific email + role narrows.
                    keep = inEffectiveRole.Any(p =>
                        !string.IsNullOrEmpty(p.Email)
                        && string.Equals(p.Email, assigneeEmail, StringComparison.OrdinalIgnoreCase));
                }
                else
                {
                    // role only (no person filter) — keep candidates with at least one
                    // participant in the role.
                    keep = inEffectiveRole.Count > 0;
                }

                if (!keep) continue;
            }

            foreach (var w in bundleItems)
            {
                var tup = (w.WorkItemKey, c.Product, c.TargetEnv);
                if (decidedSet.Contains(tup)) continue;
                if (!emitted.Add((w.WorkItemKey, c.Id))) continue;

                var ticketParticipants = GetWorkItemParticipants(c, w.WorkItemKey);

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
                    BlockingPromotions: blockingCount.GetValueOrDefault(tup, 1),
                    Participants: ticketParticipants));
            }
        }

        // Sort: count desc, then displayName asc (case-insensitive). DisplayName falls back to
        // email when missing so the secondary sort is always meaningful.
        var assigneeRows = assigneeAccumulator
            .Select(kv => new PendingAssigneeView(
                Email: kv.Key.Email,
                DisplayName: string.IsNullOrEmpty(kv.Value.DisplayName) ? kv.Key.Email : kv.Value.DisplayName!,
                Role: kv.Key.Role,
                Count: kv.Value.Count))
            .OrderByDescending(a => a.Count)
            .ThenBy(a => a.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new PendingQueueResult(result, assigneeRows, assigneeRoles);
    }

    /// <summary>
    /// Returns rows representing recent ticket decisions across the platform — both approvals
    /// and rejections by anyone. Use <paramref name="decision"/> to narrow to Approved or
    /// Rejected; pass <c>null</c> for both. <paramref name="since"/> caps the query to recent
    /// decisions (recommended — full history is unbounded).
    ///
    /// <para>For each <see cref="WorkItemApproval"/>, picks the most recent candidate that carries
    /// the ticket (any candidate status — including Approved, Deployed, Rejected, Superseded). The
    /// returned <see cref="PendingQueueResult.Assignees"/> and <see cref="PendingQueueResult.Roles"/>
    /// are empty — those dropdowns only narrow the pending inbox.</para>
    /// </summary>
    public async Task<PendingQueueResult> GetDecidedAsync(
        PromotionDecision? decision,
        DateTimeOffset? since,
        CancellationToken ct = default)
    {
        var query = _db.WorkItemApprovals.AsNoTracking().AsQueryable();
        if (decision is { } d) query = query.Where(a => a.Decision == d);
        if (since is { } cutoff) query = query.Where(a => a.CreatedAt >= cutoff);

        var approvals = await query
            .OrderByDescending(a => a.CreatedAt)
            .ToListAsync(ct);
        if (approvals.Count == 0)
            return new PendingQueueResult(new(), new(), Array.Empty<string>());

        // Candidate-scoped work-item rows for every (key, product, targetEnv) the decisions touch.
        var keys = approvals.Select(a => a.WorkItemKey).Distinct().ToList();
        var products = approvals.Select(a => a.Product).Distinct().ToList();
        var envs = approvals.Select(a => a.TargetEnv).Distinct().ToList();
        var workItems = await _db.PromotionWorkItems.AsNoTracking()
            .Where(w => keys.Contains(w.WorkItemKey) && products.Contains(w.Product) && envs.Contains(w.TargetEnv))
            .ToListAsync(ct);

        // The candidates referenced by those rows — pulled in full (small set) so we can pick the
        // most recent one carrying each (key, product, env) regardless of status.
        var candidateIds = workItems.Select(w => w.CandidateId).Distinct().ToList();
        var candidatesById = candidateIds.Count == 0
            ? new Dictionary<Guid, PromotionCandidate>()
            : (await _db.PromotionCandidates.AsNoTracking()
                .Where(c => candidateIds.Contains(c.Id))
                .ToListAsync(ct))
              .ToDictionary(c => c.Id);

        var result = new List<PendingTicketView>();
        foreach (var a in approvals)
        {
            // Candidate rows carrying this exact (key, product, env), newest candidate first.
            var rows = workItems
                .Where(w => string.Equals(w.WorkItemKey, a.WorkItemKey, StringComparison.OrdinalIgnoreCase)
                         && string.Equals(w.Product, a.Product, StringComparison.Ordinal)
                         && string.Equals(w.TargetEnv, a.TargetEnv, StringComparison.Ordinal))
                .Select(w => (Row: w, Candidate: candidatesById.GetValueOrDefault(w.CandidateId)))
                .Where(t => t.Candidate is not null)
                .OrderByDescending(t => t.Candidate!.CreatedAt)
                .ToList();

            var c2 = rows.FirstOrDefault().Candidate;
            var wi = rows.FirstOrDefault().Row;

            // Participants (best-effort — only meaningful when we have a candidate).
            IReadOnlyList<ParticipantDto> ticketParticipants = c2 is null
                ? Array.Empty<ParticipantDto>()
                : GetWorkItemParticipants(c2, a.WorkItemKey);

            result.Add(new PendingTicketView(
                WorkItemKey: a.WorkItemKey,
                Product: a.Product,
                TargetEnv: a.TargetEnv,
                Provider: wi?.Provider,
                Url: wi?.Url,
                Title: wi?.Title,
                CandidateId: c2?.Id ?? Guid.Empty,
                Service: c2?.Service ?? "",
                Version: c2?.Version ?? "",
                SourceEnv: c2?.SourceEnv ?? "",
                BlockingPromotions: 0,
                Participants: ticketParticipants,
                CandidateStatus: c2?.Status.ToString() ?? "Unknown",
                Decision: a.Decision.ToString(),
                DecidedAt: a.CreatedAt,
                DecidedByEmail: a.ApproverEmail,
                DecidedByName: a.ApproverName,
                DecisionComment: a.Comment));
        }

        return new PendingQueueResult(result, new(), Array.Empty<string>());
    }

    // ---------------------------------------------------------------------
    // Private helpers
    // ---------------------------------------------------------------------

    /// <summary>
    /// Picks the candidate whose policy will gate the decision. A ticket can appear on multiple
    /// Pending candidates (different services or envs); we pick the most recently created Pending
    /// candidate in <c>(product, targetEnv)</c> whose <see cref="PromotionWorkItem"/> rows include
    /// the ticket. Most-recent because it represents the freshest state of the world.
    /// </summary>
    private async Task<PromotionCandidate?> FindPendingCandidateForTicketAsync(
        string workItemKey, string product, string targetEnv, CancellationToken ct)
    {
        // 1. Candidate ids whose work-item index carries this ticket for (product, targetEnv).
        var candidateIds = await _db.PromotionWorkItems.AsNoTracking()
            .Where(w => w.WorkItemKey == workItemKey && w.Product == product && w.TargetEnv == targetEnv)
            .Select(w => w.CandidateId)
            .Distinct()
            .ToListAsync(ct);
        if (candidateIds.Count == 0) return null;

        // 2. Among those, the most recently created Pending candidate in (product, targetEnv).
        return await _db.PromotionCandidates.AsNoTracking()
            .Where(c => candidateIds.Contains(c.Id)
                     && c.Product == product
                     && c.TargetEnv == targetEnv
                     && c.Status == PromotionStatus.Pending)
            .OrderByDescending(c => c.CreatedAt)
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>
    /// Returns the effective participant list for <paramref name="workItemKey"/> on the candidate.
    /// The candidate is self-contained, so people come from its own data (no deploy-event join, no
    /// operator overrides):
    /// <list type="bullet">
    ///   <item>Reference-level participants nested in the matching work-item entry of
    ///         <see cref="PromotionCandidate.References"/>.</item>
    ///   <item>Promotion-level participants (<see cref="PromotionCandidate.Participants"/>) — for
    ///         any role not already resolved by the reference-level layer.</item>
    /// </list>
    /// Each canonical role appears at most once.
    /// </summary>
    private static IReadOnlyList<ParticipantDto> GetWorkItemParticipants(
        PromotionCandidate candidate, string workItemKey)
    {
        var merged = new List<ParticipantDto>();
        var seenCanonical = new HashSet<string>(StringComparer.Ordinal);

        // ── Layer 1: reference-level participants on the matching work-item reference ──
        var matchedRef = candidate.References.FirstOrDefault(r =>
            string.Equals(r.Key, workItemKey, StringComparison.OrdinalIgnoreCase)
            && string.Equals(r.Type, "work-item", StringComparison.OrdinalIgnoreCase));
        if (matchedRef?.Participants is { Count: > 0 } refParticipants)
        {
            foreach (var p in refParticipants)
            {
                var canonical = RoleNormalizer.Normalize(p.Role);
                if (canonical.Length == 0 || !seenCanonical.Add(canonical)) continue;
                merged.Add(p);
            }
        }

        // ── Layer 2: promotion-level participants for any role not yet covered ──
        foreach (var p in candidate.Participants)
        {
            var canonical = RoleNormalizer.Normalize(p.Role);
            if (canonical.Length == 0 || !seenCanonical.Add(canonical)) continue;
            merged.Add(new ParticipantDto(p.Role, p.DisplayName, p.Email));
        }

        return merged;
    }

    /// <summary>
    /// Builds the merged assignee view for a candidate from its own data: reference-level
    /// participants nested under each work-item reference, plus promotion-level participants. Only
    /// participants whose role canonicalises into <paramref name="assigneeRoleSet"/> are returned.
    /// </summary>
    private static List<MergedParticipant> BuildMergedAssignees(
        PromotionCandidate candidate, HashSet<string> assigneeRoleSet)
    {
        var result = new List<MergedParticipant>();
        if (assigneeRoleSet.Count == 0) return result;

        void Add(ParticipantDto p)
        {
            var canon = RoleNormalizer.Normalize(p.Role);
            if (canon.Length == 0 || !assigneeRoleSet.Contains(canon)) return;
            if (string.IsNullOrEmpty(p.Email)) return;
            result.Add(new MergedParticipant(canon, p.Email!.Trim().ToLowerInvariant(), p.DisplayName));
        }

        // Reference-level participants nested under each reference.
        foreach (var r in candidate.References)
        {
            if (r.Participants is not { Count: > 0 } refParticipants) continue;
            foreach (var p in refParticipants) Add(p);
        }

        // Promotion-level participants.
        foreach (var p in candidate.Participants)
            Add(new ParticipantDto(p.Role, p.DisplayName, p.Email));

        return result;
    }

    private readonly record struct MergedParticipant(string Role, string Email, string? DisplayName);

    private record struct AssigneeAccumulator(string? DisplayName, int Count);

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
    int BlockingPromotions,
    IReadOnlyList<ParticipantDto> Participants,
    // Status of the candidate this row represents. "Pending" for the inbox; for decision-history
    // rows the candidate may have moved on (Approved / Deploying / Deployed / Rejected /
    // Superseded).
    string CandidateStatus = "Pending",
    // Decision metadata — null on the pending inbox, populated on the decision-history view.
    // Stringified so the JSON response is self-describing.
    string? Decision = null,
    DateTimeOffset? DecidedAt = null,
    string? DecidedByEmail = null,
    string? DecidedByName = null,
    string? DecisionComment = null);

/// <summary>
/// One row of the assignee summary for the My-queue endpoint. Aggregated by (email, role)
/// across the user's authorized list <i>before</i> the role/person filter is applied, so the
/// front-end always knows the full set of choices the user can narrow to. <see cref="Count"/>
/// is the number of distinct candidates the (email, role) pair appears on.
/// </summary>
public record PendingAssigneeView(
    string Email,
    string DisplayName,
    string Role,
    int Count);

/// <summary>
/// Composite return for <c>GET /api/work-items/me/pending</c>. Carries the rendered ticket
/// list plus the unfiltered (email, role) summary and the canonical assignee-role set so
/// the front-end's role + person dropdowns can be populated without a second call.
/// </summary>
public record PendingQueueResult(
    List<PendingTicketView> Tickets,
    List<PendingAssigneeView> Assignees,
    IReadOnlyList<string> Roles);
