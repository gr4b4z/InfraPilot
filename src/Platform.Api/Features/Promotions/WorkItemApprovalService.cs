using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Platform.Api.Features.Deployments;
using Platform.Api.Features.Deployments.Models;
using Platform.Api.Features.Promotions.Models;
using Platform.Api.Features.Users;
using Platform.Api.Features.Settings;
using Platform.Api.Features.Webhooks;
using Platform.Api.Infrastructure;
using Platform.Api.Infrastructure.Audit;
using Platform.Api.Infrastructure.Auth;
using Platform.Api.Infrastructure.Persistence;
using Platform.Api.Infrastructure.Realtime;

namespace Platform.Api.Features.Promotions;

/// <summary>
/// Records ticket-level (work-item) approvals. Persistent state only —
/// the gate evaluator that auto-promotes candidates when all tickets are
/// signed lives in PR3. Approvals carry across superseded builds because
/// they key on (workItemKey, product, targetEnv), not on the candidate.
///
/// <para>Authority: work-item sign-off is the QA role's jurisdiction (Admin included), on any work
/// item the platform has seen for that (product, targetEnv) — including one whose promotion has
/// died, since an orphaned item still needs resolving. The only refusal is an auto-approve policy,
/// where there is no human gate to sign against. One signoff per ticket per
/// (product, env, approver) — enforced by unique index plus an in-app duplicate check that returns
/// a friendly 400 instead of a DB exception.</para>
///
/// <para>No decision cascades to the promotion. Approve feeds the gate (and can auto-promote);
/// Issue and Block both simply leave the item unresolved, which stalls the gate without terminating
/// the candidate, and both are reversible. Vetoing a promotion is a candidate-level action
/// (<see cref="PromotionService.RejectAsync"/>), never something done to a single ticket.</para>
/// </summary>
public class WorkItemApprovalService
{
    private readonly PlatformDbContext _db;
    private readonly PromotionApprovalAuthorizer _auth;
    private readonly ICurrentUser _currentUser;
    private readonly IAuditLogger _audit;
    private readonly IWebhookDispatcher _webhookDispatcher;
    private readonly PromotionService _promotion;
    private readonly UserPreferencesService _userPrefs;
    private readonly ILogger<WorkItemApprovalService> _logger;
    private readonly IPlatformEventPublisher _events;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// How far back the orphan scan reaches when rebuilding the queue. Bounds the "promotion died,
    /// work item didn't" pass so an old graveyard of superseded candidates can't turn one inbox
    /// read into a full-table scan.
    /// </summary>
    private const int OrphanScanLimit = 500;

    // PromotionService is injected so a ticket approval can drive the candidate side
    // (re-evaluate the gate, which may auto-promote). The dependency is one-way:
    // PromotionService does NOT pull WorkItemApprovalService, so DI resolution is unambiguous.
    public WorkItemApprovalService(
        PlatformDbContext db,
        PromotionApprovalAuthorizer auth,
        ICurrentUser currentUser,
        IAuditLogger audit,
        IWebhookDispatcher webhookDispatcher,
        PromotionService promotion,
        UserPreferencesService userPrefs,
        ILogger<WorkItemApprovalService> logger,
        IPlatformEventPublisher events)
    {
        _db = db;
        _auth = auth;
        _currentUser = currentUser;
        _audit = audit;
        _webhookDispatcher = webhookDispatcher;
        _promotion = promotion;
        _userPrefs = userPrefs;
        _logger = logger;
        _events = events;
    }

    // ---------------------------------------------------------------------
    // Decision recording
    // ---------------------------------------------------------------------

    public Task<WorkItemApproval> ApproveAsync(
        string workItemKey, string product, string targetEnv, string? comment, CancellationToken ct = default)
        => RecordAsync(workItemKey, product, targetEnv, comment, WorkItemDecision.Approved, ct);

    /// <summary>
    /// Flags something wrong with the work item. The candidate stays Pending and the gate treats the
    /// item as unresolved; the same user can switch to Approved later. Mechanically identical to
    /// <see cref="BlockAsync"/> — the two differ only in what the reviewer is saying.
    /// </summary>
    public Task<WorkItemApproval> RaiseIssueAsync(
        string workItemKey, string product, string targetEnv, string? comment, CancellationToken ct = default)
        => RecordAsync(workItemKey, product, targetEnv, comment, WorkItemDecision.Issue, ct);

    /// <summary>
    /// Holds the work item back. Says more than <see cref="RaiseIssueAsync"/> and does exactly the
    /// same: no cascade to the promotion, and reversible. Note this is <i>not</i> a veto — that is
    /// <see cref="PromotionService.RejectAsync"/>, which terminates a candidate.
    /// </summary>
    public Task<WorkItemApproval> BlockAsync(
        string workItemKey, string product, string targetEnv, string? comment, CancellationToken ct = default)
        => RecordAsync(workItemKey, product, targetEnv, comment, WorkItemDecision.Blocked, ct);

    /// <summary>
    /// Records a ticket-level decision after authority checks, then drives the candidate side:
    /// an approval re-evaluates the gate (which may auto-promote), an Issue or Block does nothing
    /// to the candidate — it just leaves the item unresolved, which stalls the gate.
    ///
    /// <para>A user who already decided may change their mind: the existing row is updated in
    /// place (the unique index permits one row per approver) and <c>UpdatedAt</c> is stamped.
    /// Re-recording the <i>same</i> decision is a no-op error. Every decision also appends a
    /// decision entry to the work item's comment thread, so the discussion carries the full
    /// sequence of sign-offs inline.</para>
    ///
    /// <para>A missing Pending candidate is <i>not</i> a blocker: an orphaned work item (its
    /// promotion superseded or rejected) still needs resolving, and refusing the sign-off would
    /// strand it in the queue forever. The item does have to be one the platform has seen for that
    /// (product, targetEnv), so an arbitrary key can't seed rows.</para>
    ///
    /// <para>Throws <see cref="InvalidOperationException"/> for "unknown work item", "already
    /// recorded that decision", or "auto-approve policy" so endpoints map them to 400. Throws
    /// <see cref="UnauthorizedAccessException"/> for the missing QA/Admin role so endpoints map it
    /// to 403.</para>
    /// </summary>
    private async Task<WorkItemApproval> RecordAsync(
        string workItemKey, string product, string targetEnv,
        string? comment, WorkItemDecision decision, CancellationToken ct)
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

        var candidate = await FindPendingCandidateForTicketAsync(key, prod, env, ct);
        if (candidate is null)
        {
            // Orphaned sign-off — no live promotion needs the item. Allowed, but only for items the
            // platform actually knows about in this (product, env).
            if (!await IsKnownWorkItemAsync(key, prod, env, ct))
                throw new InvalidOperationException(
                    $"Work item '{key}' is not known for {prod}/{env}");
        }
        // Auto-approve has no human gate; a ticket signoff against it is meaningless.
        else if (ReadSnapshot(candidate).IsAutoApprove)
        {
            throw new InvalidOperationException("This promotion is auto-approve; ticket signoff is not applicable");
        }

        // Work-item sign-off is the QA role's jurisdiction (Admin included) — distinct from promotion
        // approval, which is governed by the policy's approver requirements. Any QA/Admin may decide
        // any candidate's tickets, regardless of whether they're an approver of the promotion itself.
        if (!(_currentUser.IsQA || _currentUser.IsAdmin))
            throw new UnauthorizedAccessException("Work-item sign-off requires the QA or Admin role");

        // The unique index holds one row per (ticket, product, env, approver). Load the caller's
        // row (tracked) so a change of mind updates it rather than colliding with the constraint.
        var existing = await _db.WorkItemApprovals
            .FirstOrDefaultAsync(a =>
                a.WorkItemKey == key &&
                a.Product == prod &&
                a.TargetEnv == env &&
                a.ApproverEmail == _currentUser.Email, ct);
        if (existing is not null && existing.Decision == decision)
            throw new InvalidOperationException(
                $"You have already recorded '{decision}' on this work item");

        WorkItemApproval row;
        if (existing is null)
        {
            row = new WorkItemApproval
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
        }
        else
        {
            existing.Decision = decision;
            existing.Comment = comment;
            existing.UpdatedAt = DateTimeOffset.UtcNow;
            row = existing;
        }

        // Mirror the decision into the comment thread. The decision trail already lists who decided
        // what, but the thread is where the conversation lives — a sign-off that doesn't appear
        // there reads as if nothing happened between two comments.
        _db.WorkItemComments.Add(new WorkItemComment
        {
            Id = Guid.NewGuid(),
            WorkItemKey = key,
            Product = prod,
            TargetEnv = env,
            AuthorEmail = _currentUser.Email,
            AuthorName = _currentUser.Name,
            Decision = decision,
            Body = DescribeDecision(decision, comment),
            CreatedAt = DateTimeOffset.UtcNow,
        });

        await _db.SaveChangesAsync(ct);

        // Legacy granular row-level audit kept for backward compatibility with existing callers
        // (dashboards, alerts, integration tests). The new ticket-level audit + webhook events
        // emitted below are the canonical events for downstream consumers.
        //
        // Note for anyone reading old rows: "work-item.blocked" used to mean what is now an issue,
        // and today's block used to be "work-item.rejected". Rows written before the rename keep
        // their original action names — the decision values in the tables were migrated, the audit
        // history was not (rewriting an audit trail to say something it didn't say is worse than a
        // documented discontinuity).
        var legacyAction = decision switch
        {
            WorkItemDecision.Approved => "work-item.approved",
            WorkItemDecision.Issue => "work-item.issue-raised",
            _ => "work-item.blocked",
        };
        await _audit.Log(
            "promotions", legacyAction,
            _currentUser.Id, _currentUser.Name, "user",
            "WorkItemApproval", row.Id, null,
            new { workItemKey = key, product = prod, targetEnv = env, candidateId = candidate?.Id, comment });

        // Ticket-level audit + webhook: attached to the live candidate when there is one, and emitted
        // with a null candidate id for an orphaned sign-off — the payload identifies the ticket by
        // (workItemKey, product, targetEnv) either way.
        await EmitTicketEventsAsync(decision, key, prod, env, candidate?.Id, comment, ct);

        _logger.LogInformation(
            "Work-item decision recorded: {Decision} on {Key} ({Product}/{Env}) by {Email}; candidate {CandidateId}",
            decision, LogSanitizer.Clean(key), LogSanitizer.Clean(prod), LogSanitizer.Clean(env),
            LogSanitizer.Clean(_currentUser.Email), candidate?.Id);

        // Drive the candidate side. Approve → re-evaluate the gate (may auto-promote when
        // WorkItemsOnly / WorkItemsAndManual conditions are met). Issue and Block → nothing at all:
        // neither cascades to the promotion; they simply leave the item unresolved, which is enough
        // to stall the gate until someone approves or the next version resets the decision. (A
        // decision that displaces an earlier approval needs no re-evaluation either: re-evaluation
        // only ever promotes.)
        if (decision == WorkItemDecision.Approved)
        {
            // A ticket approval is shared across every candidate carrying it (WorkItemApproval is
            // keyed by key+product+targetEnv, not by candidate), so re-evaluate ALL pending
            // candidates that reference this ticket — not just the one the row was attributed to —
            // so every gate the sign-off satisfies auto-promotes immediately. ReevaluateAsync is
            // idempotent and no-ops for candidates that aren't Pending or whose gate isn't met.
            var affected = await FindPendingCandidateIdsForTicketAsync(key, prod, env, ct);
            foreach (var affectedId in affected)
                await TryReevaluateCandidateAsync(affectedId, ct);
        }

        return row;
    }

    /// <summary>
    /// The comment-thread wording for a decision. The operator's own note is appended verbatim so
    /// the thread reads as one narrative rather than pointing at the decision trail for the detail.
    /// </summary>
    private static string DescribeDecision(WorkItemDecision decision, string? comment)
    {
        var headline = decision switch
        {
            WorkItemDecision.Approved => "Approved this work item.",
            WorkItemDecision.Issue => "Raised an issue on this work item.",
            _ => "Blocked this work item.",
        };
        var note = (comment ?? "").Trim();
        return note.Length == 0 ? headline : $"{headline}\n\n{note}";
    }

    /// <summary>
    /// Emits the ticket-level audit + webhook for a decision. Independent of the candidate, so the
    /// ticket signoff is always observable — including the orphaned path where no live candidate
    /// carries the item and <paramref name="candidateId"/> is null.
    /// </summary>
    private async Task EmitTicketEventsAsync(
        WorkItemDecision decision,
        string workItemKey, string product, string targetEnv,
        Guid? candidateId, string? comment, CancellationToken ct)
    {
        // Subscriber-visible contract. As with the audit actions above, the two non-approval names
        // shifted meaning in the rename: a subscriber that was matching promotion.ticket.blocked now
        // receives today's block (the stronger call) and needs promotion.ticket.issue-raised for what
        // it used to get. Both still mean "not approved, promotion still pending".
        var action = decision switch
        {
            WorkItemDecision.Approved => "promotion.ticket.approved",
            WorkItemDecision.Issue => "promotion.ticket.issue-raised",
            _ => "promotion.ticket.blocked",
        };

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
                action, LogSanitizer.Clean(workItemKey), LogSanitizer.Clean(product),
                LogSanitizer.Clean(targetEnv));
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
    /// to drive the UI button state. An absent pending candidate leaves
    /// <see cref="TicketContext.PendingCandidateId"/> null but does not block the sign-off: an
    /// orphaned item is still decidable, so <c>CanApprove</c> stays true for a QA/Admin as long as
    /// the platform knows the item.
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
        // what the user would see if they tried to act. An existing decision by the caller is NOT
        // a blocker — re-deciding is allowed (see RecordAsync) — it's surfaced as MyDecision so the
        // UI can render "you approved this; switch to Block?" rather than a dead end.
        var mine = approvals.FirstOrDefault(a =>
            string.Equals(a.ApproverEmail, _currentUser.Email, StringComparison.OrdinalIgnoreCase));

        bool canApprove = false;
        string? blockedReason = null;

        if (candidate is not null && ReadSnapshot(candidate).IsAutoApprove)
        {
            blockedReason = "Auto-approve policy";
        }
        else if (!(_currentUser.IsQA || _currentUser.IsAdmin))
        {
            blockedReason = "Work-item sign-off requires the QA or Admin role";
        }
        else if (candidate is null && !await IsKnownWorkItemAsync(key, prod, env, ct))
        {
            blockedReason = "This work item is not known for that product and environment";
        }
        else
        {
            canApprove = true;
        }

        return new TicketContext(
            WorkItemKey: key,
            Product: prod,
            TargetEnv: env,
            PendingCandidateId: candidate?.Id,
            CanApprove: canApprove,
            BlockedReason: blockedReason,
            Approvals: approvals,
            MyDecision: mine?.Decision.ToString());
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
    /// <para>The queue also carries <b>orphaned</b> work items: those whose promotion died
    /// (superseded without the replacement picking the ticket up, or rejected outright) and which
    /// nobody has approved. Dropping them would silently lose the work, so they stay — their row
    /// reports the dead candidate's status so the UI can flag it. The scan over dead candidates is
    /// capped at <see cref="OrphanScanLimit"/> newest-first; a Pending candidate always wins the
    /// row for a given ticket, because Pending is iterated first and rows dedupe on the triple.</para>
    ///
    /// <para>Returns the rendered ticket list along with the (email, role) → count assignee
    /// summary built from the authorized list <i>before</i> the person filter is applied.
    /// The summary feeds the front-end's person dropdown so the picker only ever surfaces choices
    /// the user can actually narrow to. Filtering first then collecting would hide every
    /// alternative — pre-filter is the correct anchor. It counts only people holding a
    /// <b>policy-required</b> role on the item: the queue's person filter matches against those
    /// roles (<c>roleRequirement=assigned</c>), so anyone else would be a pick that returns
    /// nothing.</para>
    ///
    /// <para>Person narrowing reads <b>the work item's own participants</b> — the people on its
    /// work-item reference, plus the promotion-level fallback — which is exactly the set the row
    /// displays. It deliberately does not consider the participants of the promotion's other
    /// references (commits, pull requests): a commit author on an unrelated change in the same build
    /// is not an assignee of this ticket, and counting them made "assigned to &lt;person&gt;" return
    /// work items that person had nothing to do with.</para>
    ///
    /// <para><paramref name="roleRequirement"/> narrows by the promotion policy's work-item role
    /// requirement (<see cref="WorkItemRoleRequirements"/>) — the roles that make somebody answerable
    /// for an item: <see cref="WorkItemRoleRequirementFilter.Assigned"/> answers "which items is this
    /// person <i>responsible</i> for" by matching the person only against the roles the policy
    /// requires (the queue's person filter and the "Assigned to me" tab), and
    /// <see cref="WorkItemRoleRequirementFilter.Missing"/> answers "which items has nobody been put on"
    /// — the "Not assigned" tab. Both are per-item: the required roles come from the candidate's
    /// own policy snapshot, so two rows in the same list can be judged against different roles.</para>
    /// </summary>
    public async Task<PendingQueueResult> GetPendingForCurrentUserAsync(
        CancellationToken ct = default,
        string? assigneeFilter = null,
        WorkItemRoleRequirementFilter roleRequirement = WorkItemRoleRequirementFilter.Any)
    {
        // The viewer's hidden products drop out of the queue entirely — including the assignee
        // rollup below, which is computed from these rows, so the "who has work" dropdown never
        // offers a person whose only items are on a hidden product.
        var hidden = await _userPrefs.GetHiddenProductsAsync(ct);

        // Retired services drop out on the same principle: the queue is a list of work to do, and
        // nobody is signing off tickets for a component that has been migrated away.
        var pending = await _db.PromotionCandidates.AsNoTracking()
            .Where(c => c.Status == PromotionStatus.Pending)
            .Where(c => !hidden.Contains(c.Product))
            .ExcludingDeletedServices(_db)
            .OrderByDescending(c => c.CreatedAt)
            .ToListAsync(ct);

        // Dead candidates whose work items may have been stranded. Iterated after the Pending set so
        // a ticket still carried by a live promotion always renders from that promotion instead.
        var stranded = await _db.PromotionCandidates.AsNoTracking()
            .Where(c => c.Status == PromotionStatus.Superseded || c.Status == PromotionStatus.Rejected)
            .Where(c => !hidden.Contains(c.Product))
            .ExcludingDeletedServices(_db)
            .OrderByDescending(c => c.CreatedAt)
            .Take(OrphanScanLimit)
            .ToListAsync(ct);

        var queueCandidates = pending.Concat(stranded).ToList();

        if (queueCandidates.Count == 0)
        {
            return new PendingQueueResult(new(), new());
        }

        // Filter input. `assigneeFilter` narrows to a specific email or to "unassigned" — see the
        // matrix documented on WorkItemEndpoints.
        var trimmedAssignee = assigneeFilter?.Trim();
        var assigneeFilterActive = !string.IsNullOrEmpty(trimmedAssignee);
        var assigneeIsUnassigned = assigneeFilterActive
            && string.Equals(trimmedAssignee, "unassigned", StringComparison.OrdinalIgnoreCase);
        var assigneeEmail = (assigneeFilterActive && !assigneeIsUnassigned)
            ? trimmedAssignee!.ToLowerInvariant()
            : null;

        // Candidate-scoped work-item index — the candidate is self-contained, so its tickets come
        // from PromotionWorkItem by candidate id (not from deploy-event bundles).
        var candidateIds = queueCandidates.Select(c => c.Id).ToList();
        var workItems = await _db.PromotionWorkItems.AsNoTracking()
            .Where(w => candidateIds.Contains(w.CandidateId))
            .ToListAsync(ct);
        if (workItems.Count == 0)
        {
            return new PendingQueueResult(new(), new());
        }

        // Group user's existing decisions: (key, product, env) tuples to skip.
        var decided = await _db.WorkItemApprovals.AsNoTracking()
            .Where(a => a.ApproverEmail == _currentUser.Email)
            .Select(a => new { a.WorkItemKey, a.Product, a.TargetEnv })
            .ToListAsync(ct);
        var decidedSet = decided
            .Select(d => (d.WorkItemKey, d.Product, d.TargetEnv))
            .ToHashSet();

        // Every decision anyone has recorded, and the approvals among them. Two uses:
        //  - approved-by-anyone retires orphans: an item whose promotion is dead but which someone
        //    already signed off is finished, not stranded. Rows still carried by a Pending candidate
        //    keep the existing behaviour (visible until *this* user decides).
        //  - decided-by-anyone suppresses role completeness (see WorkItemRoleRequirements): a ruled-on
        //    item isn't waiting for an assignment, so it reports no missing roles and drops out of the
        //    "Not assigned" narrowing below.
        var allDecisions = await _db.WorkItemApprovals.AsNoTracking()
            .Select(a => new { a.WorkItemKey, a.Product, a.TargetEnv, a.Decision })
            .ToListAsync(ct);
        var approvedByAnyone = allDecisions
            .Where(a => a.Decision == WorkItemDecision.Approved)
            .Select(a => (a.WorkItemKey, a.Product, a.TargetEnv))
            .ToHashSet();
        var decidedByAnyone = allDecisions
            .Select(a => (a.WorkItemKey, a.Product, a.TargetEnv))
            .ToHashSet();

        // Work-item management (view / assign / sign off) is the QA role's jurisdiction (Admin
        // included) — independent of the promotion's approver requirements. A user without it has no
        // work-item queue at all, so bail before the remaining lookups.
        if (!(_currentUser.IsQA || _currentUser.IsAdmin))
        {
            return new PendingQueueResult(new(), new());
        }

        // Where each candidate's version actually landed. Resolved once for the whole queue so the
        // rows can answer "which environments can I test this in?" without a query per row.
        var deployedEnvironments = await ResolveDeployedEnvironmentsAsync(
            queueCandidates.Select(c => new DeployedVersionKey(c.Product, c.Service, c.Version)), ct);

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
        // Dedup by (key, product, targetEnv) — the grain at which a work-item sign-off actually
        // happens (WorkItemApproval is keyed the same way). One shared decision ⇒ one row, even when
        // the ticket backs several Pending candidates; the row's BlockingPromotions surfaces the count.
        var emitted = new HashSet<(string Key, string Product, string Env)>();

        // (email, role) → count + best displayName seen. Counts feed the assignee summary;
        // displayName is taken from the first non-empty value seen.
        var assigneeAccumulator = new Dictionary<(string Email, string Role), AssigneeAccumulator>();

        // Pending candidates first, each set newest-first, so the most recent live candidate "owns"
        // the inbox row when the same ticket appears in several — keeps the list deterministic and
        // surfaces the freshest version/promotion to the approver. Dead candidates trail behind and
        // only ever contribute rows for tickets no live promotion claimed.
        foreach (var c in queueCandidates)
        {
            var isOrphanSource = c.Status != PromotionStatus.Pending;
            var snapshot = ReadSnapshot(c);
            if (snapshot.IsAutoApprove) continue;

            // Roles the policy says every work item on this candidate must have somebody in. Read from
            // the snapshot we already deserialised, so the per-row completeness check costs nothing.
            var requiredRoles = WorkItemRoleRequirements.RequiredRoles(snapshot);
            var requiredRoleSet = requiredRoles.Count == 0
                ? null
                : new HashSet<string>(requiredRoles, StringComparer.OrdinalIgnoreCase);

            // Distinct work items on this candidate.
            var bundleItems = (workItemsByCandidate.GetValueOrDefault(c.Id) ?? new())
                .GroupBy(w => w.WorkItemKey)
                .Select(g => g.First());

            var environments = deployedEnvironments.GetValueOrDefault(
                new DeployedVersionKey(c.Product, c.Service, c.Version)) ?? new();

            foreach (var w in bundleItems)
            {
                var tup = (w.WorkItemKey, c.Product, c.TargetEnv);
                if (decidedSet.Contains(tup)) continue;
                // An orphan someone already approved is resolved, not stranded — retire it.
                if (isOrphanSource && approvedByAnyone.Contains(tup)) continue;
                // Dedupe BEFORE the person/role narrowing so the newest candidate always owns the
                // row, and the participants the filter reads are the ones the row will display. The
                // other order would let an older candidate's copy of the ticket answer the filter
                // while the row still rendered the newest one's people.
                if (!emitted.Add(tup)) continue;

                // The people on THIS work item — its own reference participants, with the
                // promotion-level fallback — which is exactly what the row shows.
                var ticketParticipants = GetWorkItemParticipants(c, w.WorkItemKey);
                // Any role counts as an assignment — see AssignableParticipants.
                var ticketAssignees = AssignableParticipants(ticketParticipants, roleSet: null);

                // Update the assignee summary BEFORE narrowing — computed against the unfiltered
                // authorized list. Only people in a role this item's policy REQUIRES count: the
                // person dropdown this feeds narrows with roleRequirement=assigned, so a name in
                // any other role would be a choice that filters to nothing. An item whose policy
                // requires no roles contributes nobody. Dedupe per (email, role) within the item.
                var rollupAssignees = requiredRoleSet is null
                    ? new List<MergedParticipant>()
                    : AssignableParticipants(ticketParticipants, requiredRoleSet);
                var seenOnItem = new HashSet<(string Email, string Role)>();
                foreach (var p in rollupAssignees)
                {
                    var key = (p.Email, p.Role);
                    if (!seenOnItem.Add(key)) continue;
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

                // Policy-required roles nobody holds on this item — what makes it "incomplete". Always
                // computed (the row reports it), and the basis of the two roleRequirement narrowings.
                // An item somebody has already ruled on reports none: the warning asks for an
                // assignment, and the sign-off it was waiting for has happened.
                var missingRoles = WorkItemRoleRequirements.MissingRoles(
                    ticketParticipants, requiredRoles, decided: decidedByAnyone.Contains(tup));

                // "Not assigned": at least one role the policy requires has nobody in it. Decided
                // items are excluded by the empty missingRoles above.
                if (roleRequirement == WorkItemRoleRequirementFilter.Missing && missingRoles.Count == 0)
                    continue;

                // Which roles the person filter is matched against. Any role normally; under
                // roleRequirement=Assigned it's the policy-required roles instead — being named as,
                // say, the reporter of a ticket is not being made answerable for it. An item whose
                // policy requires nothing therefore matches nobody in that mode.
                HashSet<string>? matchRoleSet = null;
                if (roleRequirement == WorkItemRoleRequirementFilter.Assigned)
                {
                    if (requiredRoleSet is null) continue;
                    matchRoleSet = requiredRoleSet;
                }

                // Apply person narrowing. No participant in the match role set =>
                // "unassigned" by definition (legacy data, no participants, all tombstoned).
                if (assigneeFilterActive || matchRoleSet is not null)
                {
                    // Under roleRequirement=Assigned, narrow to the policy-required roles; otherwise
                    // every named person counts (ticketAssignees is already the any-role set).
                    var inMatchRoles = matchRoleSet is not null
                        ? AssignableParticipants(ticketParticipants, matchRoleSet)
                        : ticketAssignees;

                    bool keep;
                    if (assigneeIsUnassigned)
                    {
                        // No participant whose role ∈ matchRoleSet exists.
                        keep = inMatchRoles.Count == 0;
                    }
                    else if (assigneeFilterActive)
                    {
                        // Specific email narrows.
                        keep = inMatchRoles.Any(p =>
                            string.Equals(p.Email, assigneeEmail, StringComparison.OrdinalIgnoreCase));
                    }
                    else
                    {
                        // roleRequirement=assigned with no person — keep work items with at least
                        // one participant in a required role.
                        keep = inMatchRoles.Count > 0;
                    }

                    if (!keep) continue;
                }

                result.Add(new PendingTicketView(
                    WorkItemKey: w.WorkItemKey,
                    Product: c.Product,
                    TargetEnv: c.TargetEnv,
                    Provider: w.Provider,
                    Url: w.Url,
                    Title: w.Title,
                    SubTitle: w.SubTitle,
                    CandidateId: c.Id,
                    Service: c.Service,
                    Version: c.Version,
                    Environments: environments,
                    BlockingPromotions: blockingCount.GetValueOrDefault(tup, 1),
                    Participants: ticketParticipants,
                    // "Pending" for a live promotion; the dead candidate's status for an orphan, which
                    // is what tells the UI to render it as stranded rather than actionable-as-usual.
                    CandidateStatus: c.Status.ToString(),
                    RequiredRoles: requiredRoles,
                    MissingRoles: missingRoles));
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

        return new PendingQueueResult(result, assigneeRows);
    }

    /// <summary>
    /// Returns rows representing recent ticket decisions across the platform — both approvals
    /// and non-approvals by anyone. Use <paramref name="decision"/> to narrow to a single
    /// decision; pass <c>null</c> for all of them. <paramref name="since"/> caps the query to recent
    /// decisions (recommended — full history is unbounded).
    ///
    /// <para>For each <see cref="WorkItemApproval"/>, picks the most recent candidate that carries
    /// the ticket (any candidate status — including Approved, Deployed, Rejected, Superseded). The
    /// returned <see cref="PendingQueueResult.Assignees"/> carries the decider rollup rather than
    /// work-item participants — the decided view's "who decided" dropdown.</para>
    /// </summary>
    public async Task<PendingQueueResult> GetDecidedAsync(
        WorkItemDecision? decision,
        DateTimeOffset? since,
        string? decidedBy = null,
        CancellationToken ct = default)
    {
        var query = _db.WorkItemApprovals.AsNoTracking().AsQueryable();

        var hidden = await _userPrefs.GetHiddenProductsAsync(ct);
        if (hidden.Count > 0) query = query.Where(a => !hidden.Contains(a.Product));

        if (decision is { } d) query = query.Where(a => a.Decision == d);
        if (since is { } cutoff) query = query.Where(a => a.CreatedAt >= cutoff);

        var approvals = await query
            .OrderByDescending(a => a.CreatedAt)
            .ToListAsync(ct);
        if (approvals.Count == 0)
            return new PendingQueueResult(new(), new());

        // Decider rollup — computed BEFORE the decidedBy narrowing (mirrors the pending path's
        // pre-narrow assignee summary) so the front-end "who decided" dropdown never offers a
        // zero-result person. Deciders carry no role, so Role is left empty. One row per email;
        // the display name is the first non-empty one seen (approvals are newest-first).
        var deciderAccumulator = new Dictionary<string, AssigneeAccumulator>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in approvals)
        {
            if (string.IsNullOrEmpty(a.ApproverEmail)) continue;
            if (!deciderAccumulator.TryGetValue(a.ApproverEmail, out var acc))
                acc = new AssigneeAccumulator(a.ApproverName, 0);
            else if (string.IsNullOrEmpty(acc.DisplayName) && !string.IsNullOrEmpty(a.ApproverName))
                acc = acc with { DisplayName = a.ApproverName };
            deciderAccumulator[a.ApproverEmail] = acc with { Count = acc.Count + 1 };
        }
        var deciderRows = deciderAccumulator
            .Select(kv => new PendingAssigneeView(
                Email: kv.Key,
                DisplayName: string.IsNullOrEmpty(kv.Value.DisplayName) ? kv.Key : kv.Value.DisplayName!,
                Role: "",
                Count: kv.Value.Count))
            .OrderByDescending(a => a.Count)
            .ThenBy(a => a.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Narrow to a single decider when requested. Case-insensitive, matching the pending
        // path's email comparison.
        var trimmedDecider = decidedBy?.Trim();
        if (!string.IsNullOrEmpty(trimmedDecider))
        {
            approvals = approvals
                .Where(a => string.Equals(a.ApproverEmail, trimmedDecider, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (approvals.Count == 0)
                return new PendingQueueResult(new(), deciderRows);
        }

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

        var deployedEnvironments = await ResolveDeployedEnvironmentsAsync(
            candidatesById.Values.Select(c => new DeployedVersionKey(c.Product, c.Service, c.Version)), ct);

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

            // The roles the item's policy asks for, for the row's own reporting. No missing ones are
            // reported on this path: every row here IS a decision, and a ruled-on item is not waiting
            // for anybody to be assigned (see WorkItemRoleRequirements).
            var requiredRoles = c2 is null
                ? Array.Empty<string>()
                : WorkItemRoleRequirements.RequiredRoles(c2);

            result.Add(new PendingTicketView(
                WorkItemKey: a.WorkItemKey,
                Product: a.Product,
                TargetEnv: a.TargetEnv,
                Provider: wi?.Provider,
                Url: wi?.Url,
                Title: wi?.Title,
                SubTitle: wi?.SubTitle,
                CandidateId: c2?.Id ?? Guid.Empty,
                Service: c2?.Service ?? "",
                Version: c2?.Version ?? "",
                Environments: c2 is null
                    ? new List<WorkItemEnvironmentView>()
                    : deployedEnvironments.GetValueOrDefault(
                          new DeployedVersionKey(c2.Product, c2.Service, c2.Version)) ?? new(),
                BlockingPromotions: 0,
                Participants: ticketParticipants,
                CandidateStatus: c2?.Status.ToString() ?? "Unknown",
                RequiredRoles: requiredRoles,
                MissingRoles: WorkItemRoleRequirements.MissingRoles(
                    ticketParticipants, requiredRoles, decided: true),
                Decision: a.Decision.ToString(),
                DecidedAt: a.CreatedAt,
                DecidedByEmail: a.ApproverEmail,
                DecidedByName: a.ApproverName,
                DecisionComment: a.Comment));
        }

        return new PendingQueueResult(result, deciderRows);
    }

    /// <summary>
    /// Everything the work-item detail page renders for one <c>(key, product, targetEnv)</c>:
    /// display fields, the people assigned to it, the decision trail, the comment thread, and every
    /// promotion candidate that carries it. Returns <c>null</c> when no candidate has ever carried
    /// the ticket in that product/env — the caller maps that to 404.
    ///
    /// <para>The <i>primary</i> candidate is the newest Pending one, falling back to the newest that
    /// isn't superseded, then to the newest of any status. It's the candidate that participant
    /// assignments write to (participants live on a candidate's reference, not on the ticket) and the
    /// one whose reference supplies title/url.</para>
    ///
    /// <para>Superseded candidates are left out of the returned <see cref="WorkItemDetail.Candidates"/>
    /// list: a build that was replaced before it shipped is noise on a page about the work item, and
    /// the promotion that replaced it is in the list anyway. They still participate in primary
    /// resolution, so a ticket whose only candidates are superseded keeps a write target for people.</para>
    /// </summary>
    public async Task<WorkItemDetail?> GetDetailAsync(
        string workItemKey, string product, string targetEnv, CancellationToken ct = default)
    {
        var key = (workItemKey ?? "").Trim();
        var prod = (product ?? "").Trim();
        var env = (targetEnv ?? "").Trim();
        if (key.Length == 0 || prod.Length == 0 || env.Length == 0) return null;

        var rows = await _db.PromotionWorkItems.AsNoTracking()
            .Where(w => w.WorkItemKey == key && w.Product == prod && w.TargetEnv == env)
            .ToListAsync(ct);
        if (rows.Count == 0) return null;

        var candidateIds = rows.Select(w => w.CandidateId).Distinct().ToList();
        var candidates = await _db.PromotionCandidates.AsNoTracking()
            .Where(c => candidateIds.Contains(c.Id))
            .ToListAsync(ct);
        if (candidates.Count == 0) return null;

        var ordered = candidates.OrderByDescending(c => c.CreatedAt).ToList();
        var primary = ordered.FirstOrDefault(c => c.Status == PromotionStatus.Pending)
            ?? ordered.FirstOrDefault(c => c.Status != PromotionStatus.Superseded)
            ?? ordered[0];

        // Display fields: prefer the primary candidate's own row, then any row that has them —
        // an older candidate may carry a title the newest ingest omitted.
        var primaryRow = rows.FirstOrDefault(w => w.CandidateId == primary.Id);
        var title = primaryRow?.Title ?? rows.Select(w => w.Title).FirstOrDefault(t => !string.IsNullOrEmpty(t));
        var subTitle = primaryRow?.SubTitle ?? rows.Select(w => w.SubTitle).FirstOrDefault(s => !string.IsNullOrEmpty(s));
        var content = primaryRow?.Content ?? rows.Select(w => w.Content).FirstOrDefault(c => !string.IsNullOrEmpty(c));
        var url = primaryRow?.Url ?? rows.Select(w => w.Url).FirstOrDefault(u => !string.IsNullOrEmpty(u));
        var provider = primaryRow?.Provider ?? rows.Select(w => w.Provider).FirstOrDefault(p => !string.IsNullOrEmpty(p));

        var ctx = await GetTicketContextAsync(key, prod, env, ct);
        var comments = await GetCommentsAsync(key, prod, env, ct);

        // The change that carried this ticket. Resolved from the primary candidate, falling back to
        // the newest candidate that actually records commits for the ticket — same "prefer primary,
        // else whoever has the data" rule the display fields above use, because an older ingest may
        // have supplied commits that a later one omitted.
        var changeSource = ResolvesCommits(primary, key)
            ? primary
            : ordered.FirstOrDefault(c => ResolvesCommits(c, key)) ?? primary;
        var (commits, pullRequests) = ResolveChangeSet(changeSource, key);

        // Environments the change is actually running in, unioned across every version that carried
        // the ticket — superseded builds included, because an environment may still be sitting on one
        // of them, and that's where someone would go to exercise the change.
        var environments = (await ResolveDeployedEnvironmentsAsync(
                ordered.Select(c => new DeployedVersionKey(c.Product, c.Service, c.Version)), ct))
            .Values
            .SelectMany(v => v)
            .GroupBy(v => v.Environment, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(v => v.DeployedAt).First())
            .OrderByDescending(v => v.DeployedAt)
            .ToList();

        // Role completeness is judged against the primary candidate — the same one whose reference
        // supplies the display fields and receives participant writes, so what the page asks for is
        // what the assign control can actually fill. Once anybody has ruled on the item there is
        // nothing to ask for: the page keeps its Assign controls but stops warning
        // (see WorkItemRoleRequirements).
        var participants = WorkItemRoleRequirements.ResolveParticipants(primary, key);
        var requiredRoles = WorkItemRoleRequirements.RequiredRoles(primary);
        var decided = ctx.Approvals.Count > 0;

        return new WorkItemDetail(
            WorkItemKey: key,
            Product: prod,
            TargetEnv: env,
            Environments: environments,
            Title: title,
            // The rows already went through WorkItemDisplay, which drops a repeat — but the two
            // values can come from different rows here, so the guard is re-applied.
            SubTitle: string.Equals(subTitle, title, StringComparison.Ordinal) ? null : subTitle,
            // Blank-to-null so the client has one emptiness check ("no content") rather than two.
            Content: string.IsNullOrWhiteSpace(content) ? null : content,
            Url: url,
            Provider: provider,
            PendingCandidateId: ctx.PendingCandidateId,
            PrimaryCandidateId: primary.Id,
            CanApprove: ctx.CanApprove,
            CanManage: _currentUser.IsQA || _currentUser.IsAdmin,
            BlockedReason: ctx.BlockedReason,
            MyDecision: ctx.MyDecision,
            Participants: participants,
            RequiredRoles: requiredRoles,
            MissingRoles: WorkItemRoleRequirements.MissingRoles(participants, requiredRoles, decided),
            Approvals: ctx.Approvals,
            Comments: comments,
            Commits: commits,
            PullRequests: pullRequests,
            Candidates: ordered
                .Where(c => c.Status != PromotionStatus.Superseded)
                .Select(c => new WorkItemCandidateRef(
                    c.Id, c.Service, c.Version, c.SourceEnv, c.TargetEnv,
                    c.Status.ToString(), c.CreatedAt, c.Id == primary.Id))
                .ToList());
    }

    // ---------------------------------------------------------------------
    // Maintenance: stranded work items ("No live promotion")
    // ---------------------------------------------------------------------

    /// <summary>
    /// Signs off every work item stranded in the "No live promotion" state — the queue rows whose
    /// only promotions were superseded or rejected without a replacement picking the ticket up.
    /// Nothing resolves them on its own (no future gate needs them, and no deploy retires them), so
    /// they accumulate as permanently pending work; this is the sweep that clears them.
    ///
    /// <para>The scan mirrors the orphan branch of <see cref="GetPendingForCurrentUserAsync"/> —
    /// dead candidate, live service, human gate — with two deliberate differences. It is
    /// <b>global</b>, not narrowed by the caller's hidden products: a maintenance pass repairs the
    /// install, and one admin's view preference is not a scope. And it skips any item that already
    /// carries <i>any</i> decision by anyone, not just an approval: an Issue or a Block is a
    /// deliberate human hold, and a bulk sweep has no business overruling one.</para>
    ///
    /// <para>Each item is signed off through the ordinary <see cref="ApproveAsync"/> path, so every
    /// one gets its audit row, its <c>promotion.ticket.approved</c> webhook and its entry in the
    /// work item's comment thread — a bulk repair should leave the same trail as the clicks it
    /// replaces. Gate re-evaluation is a no-op by construction: nothing Pending carries these items.
    /// A single failure is recorded on its row and the sweep continues.</para>
    ///
    /// <para><paramref name="dryRun"/> reports the list without writing. The scan is capped at
    /// <see cref="OrphanScanLimit"/> dead candidates, the same ceiling the queue reads.</para>
    /// </summary>
    public async Task<OrphanedWorkItemSweepResult> ApproveOrphanedWorkItemsAsync(
        bool dryRun, CancellationToken ct = default)
    {
        // Same jurisdiction as a single sign-off. Checked up front rather than per item so an
        // unauthorized caller gets one clear refusal instead of N identical row-level failures.
        if (!(_currentUser.IsQA || _currentUser.IsAdmin))
            throw new UnauthorizedAccessException("Work-item sign-off requires the QA or Admin role");

        var items = await FindOrphanedWorkItemsAsync(ct);
        if (dryRun || items.Count == 0)
            return new OrphanedWorkItemSweepResult(items.Count, 0, 0, DryRun: dryRun, Items: items);

        var results = new List<OrphanedWorkItemView>(items.Count);
        var approved = 0;
        var failed = 0;
        foreach (var item in items)
        {
            try
            {
                await ApproveAsync(item.WorkItemKey, item.Product, item.TargetEnv, SweepComment, ct);
                approved++;
                results.Add(item);
            }
            catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException)
            {
                // Row-level refusal — the item raced into a state the sign-off will not accept
                // (someone decided it, a new promotion picked it up). Reported, not fatal.
                failed++;
                results.Add(item with { Error = ex.Message });
                _logger.LogWarning(ex,
                    "Orphan sweep could not sign off {Key} ({Product}/{Env})",
                    LogSanitizer.Clean(item.WorkItemKey), LogSanitizer.Clean(item.Product),
                    LogSanitizer.Clean(item.TargetEnv));
            }
        }

        await _audit.Log(
            "promotions", "work-item.orphans-swept",
            _currentUser.Id, _currentUser.Name, "user",
            "WorkItemApproval", null, null,
            new { examined = items.Count, approved, failed });

        _logger.LogInformation(
            "Orphaned work-item sweep by {Email}: {Approved} approved, {Failed} failed of {Examined}",
            LogSanitizer.Clean(_currentUser.Email), approved, failed, items.Count);

        return new OrphanedWorkItemSweepResult(items.Count, approved, failed, DryRun: false, Items: results);
    }

    /// <summary>The note attached to a swept sign-off, in the approval row and the comment thread.</summary>
    private const string SweepComment =
        "Approved by maintenance sweep — no live promotion carries this work item.";

    /// <summary>
    /// The undecided work items whose only promotions are dead. Shared by the preview and the apply
    /// halves of <see cref="ApproveOrphanedWorkItemsAsync"/> so the reviewed list is the applied one.
    /// Ordered newest dead candidate first, matching the queue.
    /// </summary>
    private async Task<List<OrphanedWorkItemView>> FindOrphanedWorkItemsAsync(CancellationToken ct)
    {
        // Dead candidates, newest first — the newest one owns the row when several carry the ticket.
        // Retired services drop out on the queue's principle: nobody signs off tickets for a
        // component that has been migrated away, and those rows are in nobody's queue to begin with.
        var stranded = await _db.PromotionCandidates.AsNoTracking()
            .Where(c => c.Status == PromotionStatus.Superseded || c.Status == PromotionStatus.Rejected)
            .ExcludingDeletedServices(_db)
            .OrderByDescending(c => c.CreatedAt)
            .Take(OrphanScanLimit)
            .ToListAsync(ct);
        if (stranded.Count == 0) return new();

        var strandedIds = stranded.Select(c => c.Id).ToList();
        var workItems = await _db.PromotionWorkItems.AsNoTracking()
            .Where(w => strandedIds.Contains(w.CandidateId))
            .ToListAsync(ct);
        if (workItems.Count == 0) return new();

        // Tuples a live promotion still carries. Those items are ordinary pending work — a sweep
        // must not touch them, and signing one off would feed a gate that could auto-promote.
        var liveIds = await _db.PromotionCandidates.AsNoTracking()
            .Where(c => c.Status == PromotionStatus.Pending)
            .Select(c => c.Id)
            .ToListAsync(ct);
        var live = (await _db.PromotionWorkItems.AsNoTracking()
                .Where(w => liveIds.Contains(w.CandidateId))
                .Select(w => new { w.WorkItemKey, w.Product, w.TargetEnv })
                .ToListAsync(ct))
            .Select(w => (w.WorkItemKey, w.Product, w.TargetEnv))
            .ToHashSet();

        // Any decision at all — an approval means resolved, an Issue or Block means someone is
        // deliberately holding the item. Both are left alone.
        var decided = (await _db.WorkItemApprovals.AsNoTracking()
                .Select(a => new { a.WorkItemKey, a.Product, a.TargetEnv })
                .ToListAsync(ct))
            .Select(a => (a.WorkItemKey, a.Product, a.TargetEnv))
            .ToHashSet();

        var workItemsByCandidate = workItems
            .GroupBy(w => w.CandidateId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var emitted = new HashSet<(string Key, string Product, string Env)>();
        var result = new List<OrphanedWorkItemView>();
        foreach (var c in stranded)
        {
            // Auto-approve has no human gate, so its tickets are not sign-off work — the queue
            // skips them and they never show as "No live promotion".
            if (ReadSnapshot(c).IsAutoApprove) continue;

            foreach (var w in workItemsByCandidate.GetValueOrDefault(c.Id) ?? new())
            {
                var tup = (w.WorkItemKey, c.Product, c.TargetEnv);
                if (live.Contains(tup)) continue;
                if (decided.Contains(tup)) continue;
                if (!emitted.Add(tup)) continue;

                result.Add(new OrphanedWorkItemView(
                    WorkItemKey: w.WorkItemKey,
                    Title: w.Title,
                    Product: c.Product,
                    TargetEnv: c.TargetEnv,
                    Service: c.Service,
                    Version: c.Version,
                    CandidateStatus: c.Status.ToString()));
            }
        }

        return result;
    }

    // ---------------------------------------------------------------------
    // Comments
    //
    // Keyed by (workItemKey, product, targetEnv) — the same grain as the decision rows — so the
    // thread outlives the candidate that happened to be live when it started.
    // ---------------------------------------------------------------------

    public async Task<List<WorkItemComment>> GetCommentsAsync(
        string workItemKey, string product, string targetEnv, CancellationToken ct = default)
    {
        var key = (workItemKey ?? "").Trim();
        var prod = (product ?? "").Trim();
        var env = (targetEnv ?? "").Trim();
        if (key.Length == 0 || prod.Length == 0 || env.Length == 0) return new();

        return await _db.WorkItemComments.AsNoTracking()
            .Where(c => c.WorkItemKey == key && c.Product == prod && c.TargetEnv == env)
            .OrderBy(c => c.CreatedAt)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Posts a comment on a work item. Unlike a decision this needs no live Pending candidate —
    /// discussing a ticket whose promotion already shipped (or was superseded) is legitimate — but
    /// the ticket must be one the platform has actually seen, so an arbitrary key can't seed rows.
    /// </summary>
    public async Task<WorkItemComment> AddCommentAsync(
        string workItemKey, string product, string targetEnv, string body, CancellationToken ct = default)
    {
        var key = (workItemKey ?? "").Trim();
        var prod = (product ?? "").Trim();
        var env = (targetEnv ?? "").Trim();
        var trimmed = (body ?? "").Trim();
        if (key.Length == 0) throw new InvalidOperationException("workItemKey is required");
        if (prod.Length == 0) throw new InvalidOperationException("product is required");
        if (env.Length == 0) throw new InvalidOperationException("targetEnv is required");
        if (trimmed.Length == 0) throw new InvalidOperationException("Comment body is required");

        if (!await IsKnownWorkItemAsync(key, prod, env, ct))
            throw new KeyNotFoundException($"Work item '{key}' is not known for {prod}/{env}");

        var comment = new WorkItemComment
        {
            Id = Guid.NewGuid(),
            WorkItemKey = key,
            Product = prod,
            TargetEnv = env,
            AuthorEmail = _currentUser.Email,
            AuthorName = _currentUser.Name,
            Body = trimmed,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        _db.WorkItemComments.Add(comment);
        await _db.SaveChangesAsync(ct);

        await _audit.Log(
            "promotions", "work-item.comment.added",
            _currentUser.Id, _currentUser.Name, "user",
            "WorkItemComment", comment.Id, null,
            new { workItemKey = key, product = prod, targetEnv = env });

        // Comments are conversation, not a subscriber-facing contract event — realtime only,
        // so an open work-item view shows the new entry without a reload.
        await _events.PublishEntityChanged(new EntityChangedEvent
        {
            Entity = "work-item", Action = "commented",
            Key = key, Product = prod, Environment = env,
        });

        return comment;
    }

    public async Task<WorkItemComment> UpdateCommentAsync(
        Guid commentId, string body, CancellationToken ct = default)
    {
        var trimmed = (body ?? "").Trim();
        if (trimmed.Length == 0) throw new InvalidOperationException("Comment body is required");

        var comment = await _db.WorkItemComments.FirstOrDefaultAsync(c => c.Id == commentId, ct)
            ?? throw new KeyNotFoundException($"Comment {commentId} not found");
        EnsureCommentEditable(comment, "edit");

        comment.Body = trimmed;
        comment.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);

        await _audit.Log(
            "promotions", "work-item.comment.updated",
            _currentUser.Id, _currentUser.Name, "user",
            "WorkItemComment", comment.Id, null,
            new { comment.WorkItemKey, comment.Product, comment.TargetEnv });

        await _events.PublishEntityChanged(new EntityChangedEvent
        {
            Entity = "work-item", Action = "commented",
            Key = comment.WorkItemKey, Product = comment.Product, Environment = comment.TargetEnv,
        });

        return comment;
    }

    public async Task DeleteCommentAsync(Guid commentId, CancellationToken ct = default)
    {
        var comment = await _db.WorkItemComments.FirstOrDefaultAsync(c => c.Id == commentId, ct)
            ?? throw new KeyNotFoundException($"Comment {commentId} not found");
        EnsureCommentEditable(comment, "delete");

        _db.WorkItemComments.Remove(comment);
        await _db.SaveChangesAsync(ct);

        await _audit.Log(
            "promotions", "work-item.comment.deleted",
            _currentUser.Id, _currentUser.Name, "user",
            "WorkItemComment", commentId, null,
            new { comment.WorkItemKey, comment.Product, comment.TargetEnv });

        await _events.PublishEntityChanged(new EntityChangedEvent
        {
            Entity = "work-item", Action = "commented",
            Key = comment.WorkItemKey, Product = comment.Product, Environment = comment.TargetEnv,
        });
    }

    private void EnsureCommentEditable(WorkItemComment comment, string verb)
    {
        // Decision entries are the record of a sign-off, and system entries record what the platform
        // did — neither is discussion, so nobody edits them, admin included. The way to change a
        // decision is to record a new one.
        if (comment.Decision is not null
            || string.Equals(comment.AuthorEmail, WorkItemComment.SystemAuthor, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"This entry is a system record and cannot be {verb}d");
        if (string.Equals(comment.AuthorEmail, _currentUser.Email, StringComparison.OrdinalIgnoreCase)) return;
        if (_currentUser.IsAdmin) return;
        throw new UnauthorizedAccessException($"Only the author (or an admin) can {verb} this comment");
    }

    /// <summary>
    /// Whether the platform has ever seen this work item on a promotion for that (product, env).
    /// The gate on writing anything — a decision or a comment — against an arbitrary key.
    /// </summary>
    private async Task<bool> IsKnownWorkItemAsync(
        string workItemKey, string product, string targetEnv, CancellationToken ct)
        => await _db.PromotionWorkItems.AsNoTracking()
            .AnyAsync(w => w.WorkItemKey == workItemKey && w.Product == product && w.TargetEnv == targetEnv, ct);

    // ---------------------------------------------------------------------
    // Private helpers
    // ---------------------------------------------------------------------

    /// <summary>
    /// Picks the candidate whose policy will gate the decision. A ticket can appear on multiple
    /// Pending candidates (different services or envs); we pick the most recently created Pending
    /// candidate in <c>(product, targetEnv)</c> whose <see cref="PromotionWorkItem"/> rows include
    /// the ticket. Most-recent because it represents the freshest state of the world.
    /// </summary>
    /// <summary>
    /// All Pending candidates (in the ticket's product/targetEnv) that carry this work item. A ticket
    /// can back several promotions at once, and one shared approval counts for all of them — this is
    /// the fan-out used to re-evaluate every affected gate after a sign-off.
    /// </summary>
    private async Task<IReadOnlyList<Guid>> FindPendingCandidateIdsForTicketAsync(
        string workItemKey, string product, string targetEnv, CancellationToken ct)
    {
        var candidateIds = await _db.PromotionWorkItems.AsNoTracking()
            .Where(w => w.WorkItemKey == workItemKey && w.Product == product && w.TargetEnv == targetEnv)
            .Select(w => w.CandidateId)
            .Distinct()
            .ToListAsync(ct);
        if (candidateIds.Count == 0) return Array.Empty<Guid>();

        return await _db.PromotionCandidates.AsNoTracking()
            .Where(c => candidateIds.Contains(c.Id)
                     && c.Product == product
                     && c.TargetEnv == targetEnv
                     && c.Status == PromotionStatus.Pending)
            .Select(c => c.Id)
            .ToListAsync(ct);
    }

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

    /// <summary>Whether the candidate records any source commits for this work item.</summary>
    private static bool ResolvesCommits(PromotionCandidate candidate, string workItemKey)
        => FindWorkItemReference(candidate, workItemKey)?.Commits is { Count: > 0 };

    private static ReferenceDto? FindWorkItemReference(PromotionCandidate candidate, string workItemKey)
        => candidate.References.FirstOrDefault(r =>
            string.Equals(r.Key, workItemKey, StringComparison.OrdinalIgnoreCase)
            && string.Equals(r.Type, "work-item", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Resolves the change set behind a work item: the commits whose messages referenced the ticket,
    /// and the pull requests those commits merged.
    ///
    /// <para>The producer supplies the linkage as bare hashes on the work-item reference's
    /// <see cref="ReferenceDto.Commits"/>. This walks the candidate's other references to hydrate
    /// them: a <c>commit</c> reference matches on <see cref="ReferenceDto.Key"/>, and a
    /// <c>pull-request</c> reference matches on <see cref="ReferenceDto.Revision"/> — the merge commit
    /// it produced. That indirection (ticket → commit → PR) is why the PR list is derived rather than
    /// declared: the payload never states which PR belongs to which ticket, only which commit does.</para>
    ///
    /// <para>A hash with no matching <c>commit</c> reference still yields a row carrying just the hash.
    /// The hash is real information — the producer saw that commit — and silently dropping it would
    /// make the change set look smaller than it is. Output order follows the declared hash order so
    /// it stays stable and mirrors the payload.</para>
    /// </summary>
    private static (List<WorkItemCommitRef> Commits, List<WorkItemPullRequestRef> PullRequests)
        ResolveChangeSet(PromotionCandidate candidate, string workItemKey)
    {
        var commits = new List<WorkItemCommitRef>();
        var pullRequests = new List<WorkItemPullRequestRef>();

        var declared = FindWorkItemReference(candidate, workItemKey)?.Commits;
        if (declared is not { Count: > 0 }) return (commits, pullRequests);

        var references = candidate.References;
        var seenCommits = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenPrs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var raw in declared)
        {
            var hash = (raw ?? "").Trim();
            if (hash.Length == 0 || !seenCommits.Add(hash)) continue;

            var commitRef = references.FirstOrDefault(r =>
                string.Equals(r.Type, "commit", StringComparison.OrdinalIgnoreCase)
                && GitHash.Matches(r.Key, hash));

            commits.Add(new WorkItemCommitRef(
                Hash: hash,
                Title: commitRef?.Title,
                Url: commitRef?.Url,
                Provider: commitRef?.Provider,
                Participants: commitRef?.Participants ?? Array.Empty<ParticipantDto>()));

            foreach (var pr in references.Where(r =>
                string.Equals(r.Type, "pull-request", StringComparison.OrdinalIgnoreCase)
                && GitHash.Matches(r.Revision, hash)))
            {
                // Key is a PR number and the natural identity; fall back to the URL for a producer
                // that omitted it, so an unkeyed PR still renders once instead of on every commit.
                var prIdentity = string.IsNullOrWhiteSpace(pr.Key) ? pr.Url ?? "" : pr.Key;
                if (prIdentity.Length == 0 || !seenPrs.Add(prIdentity)) continue;
                pullRequests.Add(new WorkItemPullRequestRef(
                    Key: pr.Key ?? "",
                    Title: pr.Title,
                    Url: pr.Url,
                    Provider: pr.Provider,
                    Revision: pr.Revision,
                    Participants: pr.Participants ?? Array.Empty<ParticipantDto>()));
            }
        }

        return (commits, pullRequests);
    }

    /// <summary>
    /// The effective participant list for <paramref name="workItemKey"/> on the candidate — see
    /// <see cref="WorkItemRoleRequirements.ResolveParticipants"/>. Kept as a thin alias because it is
    /// read on every row of the queue and the shared name reads less clearly at those call sites.
    /// </summary>
    private static IReadOnlyList<ParticipantDto> GetWorkItemParticipants(
        PromotionCandidate candidate, string workItemKey)
        => WorkItemRoleRequirements.ResolveParticipants(candidate, workItemKey);

    /// <summary>
    /// Narrows a work item's effective participants (see
    /// <see cref="GetWorkItemParticipants"/>) to the ones that can be treated as an assignment:
    /// a non-empty email and, when <paramref name="roleSet"/> is supplied, a role that canonicalises
    /// into it. This — not the candidate's full participant graph — is what the person filter
    /// matches against, so filtering and display can't disagree about who a work item is assigned to.
    ///
    /// <para>Pass <c>null</c> for <paramref name="roleSet"/> to accept <b>any</b> role. That is what
    /// a plain <c>assignee</c> filter means: being named on a work item at all matches, whether you
    /// are its assignee, its QA owner, or its reporter. Restricting that to a privileged subset of
    /// roles hid items from the very people recorded against them.</para>
    ///
    /// <para>"Any role" excludes <see cref="PipelineMetadataRoles"/> — see there for why. An explicit
    /// role set (the policy-required roles, under <c>roleRequirement=assigned</c>) still honours
    /// them: a policy that requires a role is asking for somebody in it, whatever it is called.</para>
    /// </summary>
    private static List<MergedParticipant> AssignableParticipants(
        IReadOnlyList<ParticipantDto> participants, HashSet<string>? roleSet)
    {
        var result = new List<MergedParticipant>();

        foreach (var p in participants)
        {
            var canon = RoleNormalizer.Normalize(p.Role);
            if (canon.Length == 0) continue;
            if (roleSet is null ? PipelineMetadataRoles.Contains(canon) : !roleSet.Contains(canon)) continue;
            if (string.IsNullOrEmpty(p.Email)) continue;
            result.Add(new MergedParticipant(canon, p.Email!.Trim().ToLowerInvariant(), p.DisplayName));
        }

        return result;
    }

    /// <summary>
    /// Roles that record how a build happened rather than who is answerable for the change in it. They
    /// are the one exception to "any role counts as an assignment": whoever clicked run on a pipeline
    /// would otherwise become the assignee of every work item that build carried, and the
    /// "nobody assigned" filter — which exists to find unowned work — would never match anything.
    /// </summary>
    private static readonly HashSet<string> PipelineMetadataRoles =
        new(new[] { RoleNormalizer.Normalize("triggered-by") }, StringComparer.OrdinalIgnoreCase);

    private readonly record struct MergedParticipant(string Role, string Email, string? DisplayName);

    /// <summary>
    /// The environments a given (product, service, version) has actually been deployed to, keyed by
    /// that triple. This is the answer to "where can someone see and test this work item?" — a
    /// question the promotion's source/target env never answered: the target env is where the build
    /// is <i>asking</i> to go, not where the change is running.
    ///
    /// <para>Only succeeded deploys count, and each environment appears once with its most recent
    /// deploy of that version (a version can be redeployed, or rolled back to).</para>
    ///
    /// <para>One query for the whole batch: the <c>Contains</c> predicates span the cross product of
    /// the distinct products / services / versions asked for, and the exact triples are re-checked in
    /// memory. That over-fetches rows for combinations that happen to share a version string across
    /// services, which is cheap and bounded — a query per row would not be.</para>
    /// </summary>
    private async Task<Dictionary<DeployedVersionKey, List<WorkItemEnvironmentView>>>
        ResolveDeployedEnvironmentsAsync(
            IEnumerable<DeployedVersionKey> versions, CancellationToken ct)
    {
        var wanted = versions
            .Where(v => v.Product.Length > 0 && v.Service.Length > 0 && v.Version.Length > 0)
            .ToHashSet();
        if (wanted.Count == 0) return new();

        var products = wanted.Select(v => v.Product).Distinct().ToList();
        var services = wanted.Select(v => v.Service).Distinct().ToList();
        var versionStrings = wanted.Select(v => v.Version).Distinct().ToList();

        var events = await _db.DeployEvents.AsNoTracking()
            .Where(e => e.Status == "succeeded"
                     && products.Contains(e.Product)
                     && services.Contains(e.Service)
                     && versionStrings.Contains(e.Version))
            .Select(e => new { e.Product, e.Service, e.Environment, e.Version, e.DeployedAt })
            .ToListAsync(ct);

        var byVersion = new Dictionary<DeployedVersionKey, Dictionary<string, WorkItemEnvironmentView>>();
        foreach (var e in events)
        {
            var key = new DeployedVersionKey(e.Product, e.Service, e.Version);
            if (!wanted.Contains(key)) continue;

            if (!byVersion.TryGetValue(key, out var envs))
                byVersion[key] = envs = new Dictionary<string, WorkItemEnvironmentView>(StringComparer.OrdinalIgnoreCase);
            if (envs.TryGetValue(e.Environment, out var seen) && seen.DeployedAt >= e.DeployedAt) continue;
            envs[e.Environment] = new WorkItemEnvironmentView(
                Environment: e.Environment,
                Service: e.Service,
                Version: e.Version,
                DeployedAt: e.DeployedAt);
        }

        return byVersion.ToDictionary(
            kv => kv.Key,
            kv => kv.Value.Values.OrderByDescending(v => v.DeployedAt).ToList());
    }

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
/// Narrowing of the work-item queue by the promotion policy's work-item role requirement — the two
/// tabs that ask about responsibility rather than about sign-off state. See
/// <see cref="WorkItemRoleRequirements"/> for what "required" means.
/// </summary>
public enum WorkItemRoleRequirementFilter
{
    /// <summary>No narrowing: the person filter behaves exactly as it always has.</summary>
    Any,

    /// <summary>
    /// Only work items where the person filter matches somebody in a role the item's policy
    /// <i>requires</i> — "assigned to me" in the sense of being answerable for it. Items whose policy
    /// requires no role can never match.
    /// </summary>
    Assigned,

    /// <summary>
    /// Only work items missing somebody in at least one policy-required role — the "Not assigned" tab.
    /// </summary>
    Missing,
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
    List<WorkItemApproval> Approvals,
    /// <summary>The current user's own decision ("Approved" / "Rejected" / "Blocked"), or null.</summary>
    string? MyDecision = null);

/// <summary>
/// Full state of one work item for the detail page. <see cref="PrimaryCandidateId"/> is the
/// candidate participant assignments write to; <see cref="Candidates"/> lists every promotion the
/// ticket appears on so the page can link out to each.
/// </summary>
public record WorkItemDetail(
    string WorkItemKey,
    string Product,
    /// <summary>
    /// The promotion edge this sign-off gates. Part of the work item's identity (decisions and
    /// comments key on it) but not something the page presents as a property of the work item —
    /// <see cref="Environments"/> is what tells a reviewer where the change can be exercised.
    /// </summary>
    string TargetEnv,
    /// <summary>Environments the change is deployed to, newest deploy first. See
    /// <see cref="WorkItemEnvironmentView"/>.</summary>
    IReadOnlyList<WorkItemEnvironmentView> Environments,
    string? Title,
    /// <summary>
    /// Secondary display line under <see cref="Title"/>: the messages of every commit this item was
    /// carried by, joined (see <c>Deployments.WorkItemDisplay</c>). <see cref="Title"/> names the
    /// ticket, this says what changed. Null when no commit message is known, or when the item's one
    /// commit is named the same thing as the ticket.
    /// </summary>
    string? SubTitle,
    /// <summary>
    /// The work item's body as the producer sent it — the Jira description, PR description, or
    /// commit message body. Null (or blank) when the payload carried none, in which case the
    /// detail page shows no Content section at all rather than an empty one.
    /// </summary>
    string? Content,
    string? Url,
    string? Provider,
    Guid? PendingCandidateId,
    Guid? PrimaryCandidateId,
    bool CanApprove,
    /// <summary>Whether the caller may assign/remove people (QA or Admin).</summary>
    bool CanManage,
    string? BlockedReason,
    string? MyDecision,
    IReadOnlyList<ParticipantDto> Participants,
    /// <summary>
    /// Participant roles the primary candidate's policy requires somebody in on this work item
    /// (<see cref="WorkItemRoleRequirements"/>). Empty when the policy asks for none.
    /// </summary>
    IReadOnlyList<string> RequiredRoles,
    /// <summary>
    /// The subset of <see cref="RequiredRoles"/> nobody holds — the work item is incomplete until they
    /// are filled, and the page asks for someone to be put on each.
    /// </summary>
    IReadOnlyList<string> MissingRoles,
    List<WorkItemApproval> Approvals,
    List<WorkItemComment> Comments,
    /// <summary>The commits whose messages referenced this ticket, newest-declared-first.</summary>
    IReadOnlyList<WorkItemCommitRef> Commits,
    /// <summary>The pull requests those commits merged. Derived via commit → PR revision.</summary>
    IReadOnlyList<WorkItemPullRequestRef> PullRequests,
    IReadOnlyList<WorkItemCandidateRef> Candidates);

/// <summary>
/// One commit that carried a work item. <see cref="Hash"/> is always present — it's what the producer
/// declared. Everything else is hydrated from the matching <c>commit</c> reference and is null when
/// the payload didn't include one, in which case the row is hash-only.
/// </summary>
public record WorkItemCommitRef(
    string Hash,
    string? Title,
    string? Url,
    string? Provider,
    IReadOnlyList<ParticipantDto> Participants);

/// <summary>One pull request behind a work item, reached through the commit it merged.</summary>
public record WorkItemPullRequestRef(
    string Key,
    string? Title,
    string? Url,
    string? Provider,
    /// <summary>The merge commit that tied this PR to the work item.</summary>
    string? Revision,
    IReadOnlyList<ParticipantDto> Participants);

/// <summary>
/// One environment a work item's change is deployed to, resolved from the deploy events that shipped
/// the carrying version rather than from the promotion edge. <see cref="DeployedAt"/> is the most
/// recent succeeded deploy of that version in that environment.
/// </summary>
public record WorkItemEnvironmentView(
    string Environment,
    string Service,
    string Version,
    DateTimeOffset DeployedAt);

/// <summary>Identity of a shipped build — the grain deploy environments are resolved at.</summary>
public readonly record struct DeployedVersionKey(string Product, string Service, string Version);

/// <summary>One promotion candidate carrying a work item, as listed on the detail page.</summary>
public record WorkItemCandidateRef(
    Guid Id,
    string Service,
    string Version,
    string SourceEnv,
    string TargetEnv,
    string Status,
    DateTimeOffset CreatedAt,
    bool IsPrimary);

/// <summary>
/// One row of the "tickets I can sign off right now" inbox. Includes the work-item display
/// fields plus the candidate context the UI uses to build a deep link, plus a count of
/// distinct Pending candidates referencing the ticket so heavily-shared tickets can be flagged.
/// </summary>
public record PendingTicketView(
    string WorkItemKey,
    string Product,
    /// <summary>The promotion edge the sign-off gates — identity, not display. See
    /// <see cref="WorkItemDetail.TargetEnv"/>.</summary>
    string TargetEnv,
    string? Provider,
    string? Url,
    string? Title,
    /// <summary>Secondary display line — see <see cref="WorkItemDetail.SubTitle"/>.</summary>
    string? SubTitle,
    Guid CandidateId,
    string Service,
    string Version,
    /// <summary>Environments the carrying version is deployed to, newest deploy first — where the
    /// work item can actually be seen and tested.</summary>
    IReadOnlyList<WorkItemEnvironmentView> Environments,
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
    string? DecisionComment = null,
    /// <summary>
    /// Participant roles the carrying promotion's policy requires somebody in on this work item
    /// (<see cref="WorkItemRoleRequirements"/>). Empty when the policy asks for none.
    /// </summary>
    IReadOnlyList<string>? RequiredRoles = null,
    /// <summary>
    /// The subset of <see cref="RequiredRoles"/> nobody holds. Non-empty ⇒ the work item is incomplete
    /// and the UI asks for someone to be put on those roles.
    /// </summary>
    IReadOnlyList<string>? MissingRoles = null);

/// <summary>
/// One row of the assignee summary for the My-queue endpoint. Aggregated by (email, role)
/// across the user's authorized list <i>before</i> the person filter is applied, so the
/// front-end always knows the full set of choices the user can narrow to. On the pending path
/// the role is always one the item's policy requires — the only assignments the queue's person
/// filter matches. <see cref="Count"/> is the number of distinct candidates the (email, role)
/// pair appears on.
/// </summary>
public record PendingAssigneeView(
    string Email,
    string DisplayName,
    string Role,
    int Count);

/// <summary>
/// Composite return for <c>GET /api/work-items/me/pending</c>. Carries the rendered ticket
/// list plus the person dropdown's contents, so it can be populated without a second call.
/// </summary>
public record PendingQueueResult(
    List<PendingTicketView> Tickets,
    /// <summary>Unfiltered (email, required-role) rollup — the person dropdown's contents. On the
    /// decided path this is the decider rollup instead (role is empty there).</summary>
    List<PendingAssigneeView> Assignees);

/// <summary>
/// One work item the "No live promotion" sweep found, or acted on. Carries the dead promotion's
/// coordinates because that is what the queue row shows the reviewer — the service and version the
/// item was last riding, and which terminal state stranded it.
/// </summary>
public record OrphanedWorkItemView(
    string WorkItemKey,
    string? Title,
    string Product,
    /// <summary>The promotion edge the sign-off is keyed to. See <see cref="WorkItemDetail.TargetEnv"/>.</summary>
    string TargetEnv,
    string Service,
    string Version,
    /// <summary>"Superseded" or "Rejected" — the terminal state of the promotion that left it stranded.</summary>
    string CandidateStatus,
    /// <summary>Why this row was not signed off. Null on a preview and on every successful sweep row.</summary>
    string? Error = null);

/// <summary>
/// Outcome of <see cref="WorkItemApprovalService.ApproveOrphanedWorkItemsAsync"/>.
/// <see cref="Examined"/> is what the scan found; on a dry run nothing else moves. On an apply,
/// <c>Approved + Failed == Examined</c>, and the per-row <see cref="OrphanedWorkItemView.Error"/>
/// says what went wrong on each of the failures.
/// </summary>
public record OrphanedWorkItemSweepResult(
    int Examined,
    int Approved,
    int Failed,
    bool DryRun,
    List<OrphanedWorkItemView> Items);
