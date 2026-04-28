using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Platform.Api.Features.Deployments.Models;
using Platform.Api.Features.Promotions.Models;
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
    private readonly ILogger<WorkItemApprovalService> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public WorkItemApprovalService(
        PlatformDbContext db,
        PromotionApprovalAuthorizer auth,
        ICurrentUser currentUser,
        IAuditLogger audit,
        ILogger<WorkItemApprovalService> logger)
    {
        _db = db;
        _auth = auth;
        _currentUser = currentUser;
        _audit = audit;
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

        if (await _auth.IsEmailExcludedByRoleAsync(snapshot, candidate.SourceDeployEventId, _currentUser.Email, ct))
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

        var action = decision == PromotionDecision.Approved ? "work-item.approved" : "work-item.rejected";
        await _audit.Log(
            "promotions", action,
            _currentUser.Id, _currentUser.Name, "user",
            "WorkItemApproval", row.Id, null,
            new { workItemKey = key, product = prod, targetEnv = env, candidateId = candidate.Id, comment });

        _logger.LogInformation(
            "Work-item decision recorded: {Decision} on {Key} ({Product}/{Env}) by {Email}; candidate {CandidateId}",
            decision, key, prod, env, _currentUser.Email, candidate.Id);

        return row;
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
            else if (await _auth.IsEmailExcludedByRoleAsync(snapshot, candidate.SourceDeployEventId, _currentUser.Email, ct))
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
    public async Task<List<PendingTicketView>> GetPendingForCurrentUserAsync(CancellationToken ct = default)
    {
        var pending = await _db.PromotionCandidates.AsNoTracking()
            .Where(c => c.Status == PromotionStatus.Pending)
            .ToListAsync(ct);
        if (pending.Count == 0) return new();

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

        // Source-event participants for excluded-role checks. Pulled once for all Pending sources.
        var sourceParticipantsByEvent = await _db.DeployEvents.AsNoTracking()
            .Where(e => allEventIds.Contains(e.Id))
            .ToDictionaryAsync(e => e.Id, e => e.ParticipantsJson, ct);

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

            // Excluded-role: precomputed JSON dict so this is in-memory only.
            if (!string.IsNullOrWhiteSpace(snapshot.ExcludeRole))
            {
                var json = sourceParticipantsByEvent.GetValueOrDefault(c.SourceDeployEventId);
                if (PromotionApprovalAuthorizer.EmailMatchesExcludedRole(json, snapshot.ExcludeRole, _currentUser.Email))
                    continue;
            }

            var bundle = new[] { c.SourceDeployEventId }.Concat(c.SupersededSourceEventIds);
            // Distinct work items in the bundle (a ticket may appear on multiple events in the chain).
            var bundleItems = bundle
                .SelectMany(eid => workItemsByEvent.GetValueOrDefault(eid) ?? new())
                .Where(w => string.Equals(w.Product, c.Product, StringComparison.Ordinal))
                .GroupBy(w => w.WorkItemKey)
                .Select(g => g.First());

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
