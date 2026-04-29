using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Platform.Api.Features.Deployments.Models;
using Microsoft.Extensions.Options;
using Platform.Api.Features.Webhooks;
using Platform.Api.Features.Promotions.Models;
using Platform.Api.Infrastructure;
using Platform.Api.Infrastructure.Audit;
using Platform.Api.Infrastructure.Auth;
using Platform.Api.Infrastructure.Persistence;

namespace Platform.Api.Features.Promotions;

/// <summary>
/// Domain service for the promotion approval flow. Owns:
/// <list type="bullet">
///   <item>Candidate creation from ingested deploy events (with policy snapshot + supersede rules).</item>
///   <item>State machine enforcement: Pending → Approved → Deploying → Deployed, plus Rejected / Superseded off-ramps.</item>
///   <item>Approval recording and threshold evaluation (Any vs NOfM strategies).</item>
///   <item>Per-user capability checks (<see cref="CanUserApproveAsync"/>) so the UI can grey out buttons.</item>
/// </list>
///
/// <para>Endpoints and ingestion hooks live in separate files (P3.A / P3.B / P3.C); this class is
/// the pure-domain core.</para>
/// </summary>
public class PromotionService
{
    private readonly PlatformDbContext _db;
    private readonly PromotionPolicyResolver _resolver;
    private readonly PromotionApprovalAuthorizer _auth;
    private readonly ICurrentUser _currentUser;
    private readonly IAuditLogger _audit;
    private readonly IWebhookDispatcher _webhookDispatcher;
    private readonly IOptionsMonitor<NormalizationOptions> _normalization;
    private readonly ILogger<PromotionService> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public PromotionService(
        PlatformDbContext db,
        PromotionPolicyResolver resolver,
        PromotionApprovalAuthorizer auth,
        ICurrentUser currentUser,
        IAuditLogger audit,
        ILogger<PromotionService> logger,
        IWebhookDispatcher webhookDispatcher,
        IOptionsMonitor<NormalizationOptions> normalization)
    {
        _db = db;
        _resolver = resolver;
        _auth = auth;
        _currentUser = currentUser;
        _audit = audit;
        _webhookDispatcher = webhookDispatcher;
        _normalization = normalization;
        _logger = logger;
    }

    // ---------------------------------------------------------------------
    // Candidate creation
    // ---------------------------------------------------------------------

    /// <summary>
    /// Creates a new <see cref="PromotionCandidate"/> for the given source deploy event and target
    /// environment. Returns <c>null</c> when candidate creation is intentionally skipped:
    /// <list type="bullet">
    ///   <item>Source event is a rollback (<c>IsRollback = true</c>) — per design, we don't offer to
    ///         promote a rolled-back build forward.</item>
    ///   <item>Source event status is not "succeeded" — we only promote successful builds.</item>
    /// </list>
    ///
    /// <para>Supersedes any currently <c>Pending</c> candidate for the same
    /// <c>(Product, Service, SourceEnv, TargetEnv)</c> — a newer version in source always replaces
    /// an older still-pending one.</para>
    ///
    /// <para>If no promotion policy exists for the product × target-env combination, candidate
    /// creation is skipped entirely — the product is not enrolled in promotions for that edge.</para>
    ///
    /// <para>If the resolved policy is auto-approve (no gate), the candidate is created directly
    /// in <see cref="PromotionStatus.Approved"/> so downstream executor dispatch can pick it up.</para>
    /// </summary>
    public async Task<PromotionCandidate?> CreateCandidateAsync(
        DeployEvent source, string targetEnv, CancellationToken ct = default)
    {
        if (source.IsRollback)
        {
            _logger.LogInformation("Skipping promotion candidate for rollback event {EventId}", source.Id);
            return null;
        }

        if (!string.Equals(source.Status, "succeeded", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation("Skipping promotion candidate for non-succeeded event {EventId}", source.Id);
            return null;
        }

        // No policy → product is not enrolled in promotions for this edge.
        var policy = await _resolver.ResolveAsync(source.Product, source.Service, targetEnv, ct);
        if (policy is null)
        {
            _logger.LogDebug("No promotion policy found for event {EventId}; skipping candidate creation", source.Id);
            return null;
        }

        var snapshot = await _resolver.SnapshotAsync(source.Product, source.Service, targetEnv, ct);

        var deployer = ExtractDeployer(source);
        var now = DateTimeOffset.UtcNow;
        var candidate = new PromotionCandidate
        {
            Id = Guid.NewGuid(),
            Product = source.Product,
            Service = source.Service,
            SourceEnv = source.Environment,
            TargetEnv = targetEnv,
            Version = source.Version,
            SourceDeployEventId = source.Id,
            SourceDeployerName = deployer?.Name,
            SourceDeployerEmail = deployer?.Email,
            Status = snapshot.IsAutoApprove ? PromotionStatus.Approved : PromotionStatus.Pending,
            PolicyId = snapshot.PolicyId,
            ResolvedPolicyJson = JsonSerializer.Serialize(snapshot, JsonOptions),
            CreatedAt = now,
            ApprovedAt = snapshot.IsAutoApprove ? now : null,
        };

        // Supersede any still-pending candidate for the same service+edge — a newer version wins.
        // The fresh candidate inherits their source deploy-event IDs so the UI can surface work
        // items, PRs, and participants that were part of superseded predecessors.
        candidate.SupersededSourceEventIds = await SupersedeStalePendingAsync(candidate, ct);

        _db.PromotionCandidates.Add(candidate);

        // Auto-approve: record a synthetic approval row so the UI's approval trail renders it.
        if (snapshot.IsAutoApprove)
        {
            _db.PromotionApprovals.Add(new PromotionApproval
            {
                Id = Guid.NewGuid(),
                CandidateId = candidate.Id,
                ApproverEmail = "system",
                ApproverName = "System (auto-approve)",
                Decision = PromotionDecision.Approved,
                CreatedAt = now,
            });
        }

        await _db.SaveChangesAsync(ct);

        await _audit.Log(
            "promotions", "promotion.candidate.created",
            "system", "System", "system",
            "PromotionCandidate", candidate.Id, null,
            new
            {
                candidate.Product,
                candidate.Service,
                candidate.SourceEnv,
                candidate.TargetEnv,
                candidate.Version,
                candidate.Status,
                AutoApprove = snapshot.IsAutoApprove,
            });

        _logger.LogInformation(
            "Created promotion candidate {CandidateId} from event {EventId} ({Status})",
            candidate.Id, source.Id, candidate.Status);

        // If the candidate was born Approved (auto-approve policy), kick off execution right away.
        // Done *after* the initial SaveChangesAsync so the candidate is visible to queries even if
        // dispatch transiently fails.
        if (candidate.Status == PromotionStatus.Approved)
        {
            await _audit.Log(
                "promotions", "promotion.approved",
                "system", "System", "system",
                "PromotionCandidate", candidate.Id, null,
                new { autoApprove = true });

            await DispatchWebhookAsync(candidate, "promotion.approved", ct);
        }

        return candidate;
    }

    private async Task<List<Guid>> SupersedeStalePendingAsync(PromotionCandidate fresh, CancellationToken ct)
    {
        var stale = await _db.PromotionCandidates
            .Where(c => c.Product == fresh.Product
                     && c.Service == fresh.Service
                     && c.SourceEnv == fresh.SourceEnv
                     && c.TargetEnv == fresh.TargetEnv
                     && c.Status == PromotionStatus.Pending)
            .ToListAsync(ct);

        // Accumulated inheritance: each superseded candidate contributes its own source event
        // plus whatever it had already inherited. Deduplicate and exclude the fresh candidate's
        // own SourceDeployEventId (inheritance is for *other* events in the chain).
        var inherited = new HashSet<Guid>();
        foreach (var old in stale)
        {
            old.Status = PromotionStatus.Superseded;
            old.SupersededById = fresh.Id;
            inherited.Add(old.SourceDeployEventId);
            foreach (var id in old.SupersededSourceEventIds) inherited.Add(id);
        }
        inherited.Remove(fresh.SourceDeployEventId);

        if (stale.Count > 0)
            _logger.LogInformation(
                "Superseded {Count} pending candidates in favour of {CandidateId} (inherited {InheritedCount} events)",
                stale.Count, fresh.Id, inherited.Count);

        return inherited.ToList();
    }

    private record DeployerInfo(string? Name, string? Email);

    // Canonical role name CI senders use to identify who/what kicked off a pipeline run.
    // Named "triggered-by" because it's the run initiator (human, service principal, scheduler),
    // not necessarily the person who authored the code or approved the deploy.
    private const string TriggeredByRole = "triggered-by";

    private static DeployerInfo? ExtractDeployer(DeployEvent source)
    {
        // Participants JSON is a serialised List<ParticipantDto>. We look for the participant
        // tagged as the run initiator. Normalise each role at read time so the match works
        // whether or not ingest-time canonicalisation is enabled (senders may post "TriggeredBy",
        // "triggered_by", etc.). Best-effort parse — return null when the payload is malformed
        // or absent rather than fail candidate creation.
        if (string.IsNullOrWhiteSpace(source.ParticipantsJson)) return null;
        try
        {
            var parts = JsonSerializer.Deserialize<List<ParticipantDto>>(source.ParticipantsJson, JsonOptions);
            var deployer = parts?.FirstOrDefault(p =>
                RoleNormalizer.Normalize(p.Role) == TriggeredByRole);
            if (deployer is null) return null;
            return new DeployerInfo(deployer.DisplayName, deployer.Email);
        }
        catch
        {
            return null;
        }
    }

    // ---------------------------------------------------------------------
    // Queries
    // ---------------------------------------------------------------------

    /// <summary>
    /// Lists candidates with optional filters. Results are ordered newest-first so the UI can
    /// render "what needs attention now" at the top.
    /// <para>
    /// When no explicit status filter is provided, returns **all Pending** candidates plus up to
    /// <see cref="PromotionQuery.Limit"/> most-recent non-Pending ones — Pending is actionable
    /// work that should never be clipped; the resolved tail is just for context and grows without
    /// bound as the system runs, so we cap it.
    /// </para>
    /// </summary>
    public async Task<List<PromotionCandidate>> GetAsync(PromotionQuery query, CancellationToken ct = default)
    {
        var q = _db.PromotionCandidates.AsNoTracking().AsQueryable();
        if (query.Status is { } s) q = q.Where(c => c.Status == s);
        if (!string.IsNullOrEmpty(query.Product)) q = q.Where(c => c.Product == query.Product);
        if (!string.IsNullOrEmpty(query.TargetEnv)) q = q.Where(c => c.TargetEnv == query.TargetEnv);
        // Service filter is a substring match (case-insensitive) — services-per-product can be
        // large and users typically remember a fragment ("auth-api"), not the full name.
        if (!string.IsNullOrEmpty(query.Service))
        {
            var needle = query.Service.ToLower();
            q = q.Where(c => c.Service.ToLower().Contains(needle));
        }

        if (query.Status is not null)
        {
            // Explicit status → straight newest-first, honoring Limit as a safety cap.
            return await q.OrderByDescending(c => c.CreatedAt).Take(query.Limit).ToListAsync(ct);
        }

        // No status filter: load all Pending (never clipped) + newest N non-Pending.
        var pending = await q.Where(c => c.Status == PromotionStatus.Pending)
            .OrderByDescending(c => c.CreatedAt)
            .ToListAsync(ct);

        var resolved = await q.Where(c => c.Status != PromotionStatus.Pending)
            .OrderByDescending(c => c.CreatedAt)
            .Take(query.Limit)
            .ToListAsync(ct);

        return pending.Concat(resolved)
            .OrderByDescending(c => c.CreatedAt)
            .ToList();
    }

    public async Task<PromotionCandidate?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        return await _db.PromotionCandidates.FirstOrDefaultAsync(c => c.Id == id, ct);
    }

    public async Task<List<PromotionApproval>> GetApprovalsAsync(Guid candidateId, CancellationToken ct = default)
    {
        return await _db.PromotionApprovals.AsNoTracking()
            .Where(a => a.CandidateId == candidateId)
            .OrderBy(a => a.CreatedAt)
            .ToListAsync(ct);
    }

    // ---------------------------------------------------------------------
    // Participants (promotion-level, free-form roles)
    // ---------------------------------------------------------------------

    /// <summary>
    /// Adds or replaces a participant on the candidate keyed by role (case-insensitive). Raises
    /// <c>promotion.updated</c>. Role is trimmed; display casing is preserved on the stored record.
    /// </summary>
    public async Task<PromotionCandidate> UpsertParticipantAsync(
        Guid candidateId, PromotionParticipant participant, CancellationToken ct = default)
    {
        // Storage-time canonicalisation is opt-in via `Normalization:Roles` in appsettings.
        // Dedupe, however, is always done on the normalised key so that "QA" and "qa" don't
        // end up as two participants on the same candidate regardless of the policy.
        var storedRole = _normalization.CurrentValue.ApplyRole(participant.Role);
        if (string.IsNullOrEmpty(storedRole))
            throw new InvalidOperationException("Participant role is required");
        var canonicalKey = RoleNormalizer.Normalize(storedRole);

        var candidate = await _db.PromotionCandidates.FirstOrDefaultAsync(c => c.Id == candidateId, ct)
            ?? throw new KeyNotFoundException($"Promotion candidate {candidateId} not found");

        var list = candidate.Participants;
        var idx = list.FindIndex(p => RoleNormalizer.Normalize(p.Role) == canonicalKey);
        var entry = new PromotionParticipant(storedRole, participant.DisplayName, participant.Email);
        if (idx >= 0) list[idx] = entry; else list.Add(entry);
        candidate.Participants = list;

        await _db.SaveChangesAsync(ct);

        await _audit.Log(
            "promotions", "promotion.participant.upserted",
            _currentUser.Id, _currentUser.Name, "user",
            "PromotionCandidate", candidate.Id, null,
            new { role = storedRole, canonicalKey, entry.DisplayName, entry.Email });

        await DispatchWebhookAsync(candidate, "promotion.updated", ct,
            new { changeType = "participant.upserted", role = storedRole, canonicalKey, entry.DisplayName, entry.Email });

        return candidate;
    }

    /// <summary>Removes a participant by role (case-insensitive). No-op if the role isn't present.</summary>
    public async Task<PromotionCandidate> RemoveParticipantAsync(
        Guid candidateId, string role, CancellationToken ct = default)
    {
        // Match on the normalised key regardless of how roles are stored so the caller can
        // pass "QA", "qa", or "qa-lead" interchangeably.
        var canonicalKey = RoleNormalizer.Normalize(role);
        var candidate = await _db.PromotionCandidates.FirstOrDefaultAsync(c => c.Id == candidateId, ct)
            ?? throw new KeyNotFoundException($"Promotion candidate {candidateId} not found");

        var list = candidate.Participants;
        var before = list.Count;
        list.RemoveAll(p => RoleNormalizer.Normalize(p.Role) == canonicalKey);
        if (list.Count == before) return candidate;

        candidate.Participants = list;
        await _db.SaveChangesAsync(ct);

        await _audit.Log(
            "promotions", "promotion.participant.removed",
            _currentUser.Id, _currentUser.Name, "user",
            "PromotionCandidate", candidate.Id, null, new { role });

        await DispatchWebhookAsync(candidate, "promotion.updated", ct,
            new { changeType = "participant.removed", role });

        return candidate;
    }

    /// <summary>
    /// Returns distinct participant roles observed across deploy events and promotion candidates,
    /// ordered by frequency so the UI autocomplete surfaces the most common first.
    /// </summary>
    public async Task<List<string>> GetKnownRolesAsync(CancellationToken ct = default)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        // Deploy events carry role in ParticipantsJson as [{role,...}]. JSON query support varies by
        // provider — simplest and good enough: scan recent events and parse.
        var recentEvents = await _db.DeployEvents.AsNoTracking()
            .OrderByDescending(e => e.DeployedAt)
            .Select(e => e.ParticipantsJson)
            .Take(500)
            .ToListAsync(ct);

        foreach (var json in recentEvents) AccumulateRoles(json, counts);

        var promotionJson = await _db.PromotionCandidates.AsNoTracking()
            .OrderByDescending(c => c.CreatedAt)
            .Select(c => c.ParticipantsJson)
            .Take(500)
            .ToListAsync(ct);

        foreach (var json in promotionJson) AccumulateRoles(json, counts);

        return counts
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kv => kv.Key)
            .ToList();
    }

    private static void AccumulateRoles(string? json, Dictionary<string, int> counts)
    {
        if (string.IsNullOrWhiteSpace(json)) return;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return;
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                if (!item.TryGetProperty("role", out var roleProp)) continue;
                var canonical = RoleNormalizer.Normalize(roleProp.GetString());
                if (string.IsNullOrEmpty(canonical)) continue;
                counts[canonical] = counts.GetValueOrDefault(canonical) + 1;
            }
        }
        catch { /* ignore malformed entries */ }
    }

    // ---------------------------------------------------------------------
    // Comments
    // ---------------------------------------------------------------------

    public async Task<List<PromotionComment>> GetCommentsAsync(Guid candidateId, CancellationToken ct = default)
    {
        return await _db.PromotionComments.AsNoTracking()
            .Where(c => c.CandidateId == candidateId)
            .OrderBy(c => c.CreatedAt)
            .ToListAsync(ct);
    }

    public async Task<PromotionComment> AddCommentAsync(Guid candidateId, string body, CancellationToken ct = default)
    {
        var trimmed = (body ?? "").Trim();
        if (string.IsNullOrEmpty(trimmed))
            throw new InvalidOperationException("Comment body is required");

        var candidate = await _db.PromotionCandidates.FirstOrDefaultAsync(c => c.Id == candidateId, ct)
            ?? throw new KeyNotFoundException($"Promotion candidate {candidateId} not found");

        var comment = new PromotionComment
        {
            Id = Guid.NewGuid(),
            CandidateId = candidateId,
            AuthorEmail = _currentUser.Email,
            AuthorName = _currentUser.Name,
            Body = trimmed,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        _db.PromotionComments.Add(comment);
        await _db.SaveChangesAsync(ct);

        await _audit.Log(
            "promotions", "promotion.comment.added",
            _currentUser.Id, _currentUser.Name, "user",
            "PromotionCandidate", candidateId, null,
            new { comment.Id });

        await DispatchWebhookAsync(candidate, "promotion.updated", ct,
            new { changeType = "comment.added", commentId = comment.Id, comment.AuthorEmail });

        return comment;
    }

    public async Task<PromotionComment> UpdateCommentAsync(Guid commentId, string body, CancellationToken ct = default)
    {
        var trimmed = (body ?? "").Trim();
        if (string.IsNullOrEmpty(trimmed))
            throw new InvalidOperationException("Comment body is required");

        var comment = await _db.PromotionComments.FirstOrDefaultAsync(c => c.Id == commentId, ct)
            ?? throw new KeyNotFoundException($"Comment {commentId} not found");

        if (!string.Equals(comment.AuthorEmail, _currentUser.Email, StringComparison.OrdinalIgnoreCase)
            && !_currentUser.IsAdmin)
        {
            throw new UnauthorizedAccessException("Only the author (or an admin) can edit this comment");
        }

        comment.Body = trimmed;
        comment.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);

        var candidate = await _db.PromotionCandidates.FirstOrDefaultAsync(c => c.Id == comment.CandidateId, ct);
        if (candidate is not null)
            await DispatchWebhookAsync(candidate, "promotion.updated", ct,
                new { changeType = "comment.updated", commentId = comment.Id });

        return comment;
    }

    public async Task DeleteCommentAsync(Guid commentId, CancellationToken ct = default)
    {
        var comment = await _db.PromotionComments.FirstOrDefaultAsync(c => c.Id == commentId, ct)
            ?? throw new KeyNotFoundException($"Comment {commentId} not found");

        if (!string.Equals(comment.AuthorEmail, _currentUser.Email, StringComparison.OrdinalIgnoreCase)
            && !_currentUser.IsAdmin)
        {
            throw new UnauthorizedAccessException("Only the author (or an admin) can delete this comment");
        }

        var candidateId = comment.CandidateId;
        _db.PromotionComments.Remove(comment);
        await _db.SaveChangesAsync(ct);

        var candidate = await _db.PromotionCandidates.FirstOrDefaultAsync(c => c.Id == candidateId, ct);
        if (candidate is not null)
            await DispatchWebhookAsync(candidate, "promotion.updated", ct,
                new { changeType = "comment.deleted", commentId });
    }

    // ---------------------------------------------------------------------
    // Approval / rejection
    // ---------------------------------------------------------------------

    /// <summary>
    /// Records an approval from the current user. Enforces all gating rules:
    /// candidate must still be Pending, user must be in the approver group, user must not be the
    /// participant matching the policy's <c>ExcludeRole</c>, and the same user may not approve twice
    /// (also enforced by a DB-level unique index as belt-and-suspenders).
    ///
    /// <para>After persisting the approval row, delegates to <see cref="ReevaluateAsync"/> which
    /// runs the gate evaluator and transitions the candidate to <see cref="PromotionStatus.Approved"/>
    /// when satisfied. The split keeps the row-recording concerns here (granular audit, dup checks)
    /// separate from the candidate-level transition concerns owned by re-evaluation, which Phase 3B
    /// will also drive from the ticket-approval flow.</para>
    ///
    /// <para>For <see cref="PromotionGate.TicketsOnly"/> the candidate is not approvable through
    /// this flow at all — the only path forward is approving the underlying tickets — so we reject
    /// with a 400-friendly <see cref="InvalidOperationException"/>.</para>
    /// </summary>
    public async Task<PromotionCandidate> ApproveAsync(
        Guid candidateId, string? comment, CancellationToken ct = default)
    {
        var candidate = await LoadPendingAsync(candidateId, ct);
        var snapshot = ReadSnapshot(candidate);

        // TicketsOnly candidates auto-promote from ticket approvals; the manual approval surface
        // is intentionally inert — UNLESS the bundle has no tickets to gate on, in which case the
        // gate falls back to PromotionOnly evaluation and a manual signoff is the only way forward.
        if (snapshot.Gate == PromotionGate.TicketsOnly && await CandidateHasTicketsAsync(candidate, ct))
            throw new InvalidOperationException(
                "This candidate auto-promotes from ticket approvals; approve the tickets, not the promotion.");

        await EnsureUserCanApproveAsync(candidate, snapshot, ct);
        await EnsureNotAlreadyDecidedAsync(candidateId, _currentUser.Email, ct);

        var decision = new PromotionApproval
        {
            Id = Guid.NewGuid(),
            CandidateId = candidateId,
            ApproverEmail = _currentUser.Email,
            ApproverName = _currentUser.Name,
            Comment = comment,
            Decision = PromotionDecision.Approved,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        _db.PromotionApprovals.Add(decision);
        await _db.SaveChangesAsync(ct);

        // Granular per-row event: "this user signed off on the candidate". Coarse candidate-level
        // transition is emitted from ReevaluateAsync so a system-driven gate satisfaction (Phase 3B)
        // looks identical to one triggered directly by this user.
        await _audit.Log(
            "promotions", "promotion.approval.recorded",
            _currentUser.Id, _currentUser.Name, "user",
            "PromotionCandidate", candidate.Id, null,
            new { approvalId = decision.Id, comment });

        _logger.LogInformation(
            "Approval recorded on candidate {Id} by {Email}", candidate.Id, _currentUser.Email);

        // Re-evaluate the gate now that the new row is persisted. This may flip the candidate to
        // Approved (and emit the candidate-level audit + webhook) or leave it Pending if the
        // strategy threshold or ticket gates aren't yet satisfied.
        return await ReevaluateAsync(candidateId, ct);
    }

    /// <summary>
    /// Re-evaluates a Pending candidate against its policy gate and transitions it to
    /// <see cref="PromotionStatus.Approved"/> when the gate is satisfied. Idempotent and a no-op
    /// for candidates that are no longer Pending — safe to call from any path that may have
    /// affected gate satisfaction (a new <see cref="PromotionApproval"/> from
    /// <see cref="ApproveAsync"/>, or, in Phase 3B, a new <see cref="WorkItemApproval"/>).
    ///
    /// <para>The candidate-level audit entry is written with a <c>system</c> actor and a
    /// <c>trigger=gate-evaluator</c> marker so logs disambiguate "user X explicitly approved" from
    /// "the last ticket signoff caused the gate to satisfy and the system promoted the candidate".
    /// The granular per-user signoff (when present) lives on the corresponding
    /// <c>promotion.approval.recorded</c> entry written by the caller.</para>
    /// </summary>
    public async Task<PromotionCandidate> ReevaluateAsync(Guid candidateId, CancellationToken ct = default)
    {
        var candidate = await _db.PromotionCandidates.FirstOrDefaultAsync(c => c.Id == candidateId, ct)
            ?? throw new KeyNotFoundException($"Promotion candidate {candidateId} not found");

        // Re-evaluation is only meaningful for Pending candidates; everything else is a terminal
        // or in-flight state and must not be re-transitioned.
        if (candidate.Status != PromotionStatus.Pending) return candidate;

        var snapshot = ReadSnapshot(candidate);
        var gate = await EvaluateGateAsync(candidate, snapshot, ct);
        if (!gate.Satisfied) return candidate;

        candidate.Status = PromotionStatus.Approved;
        candidate.ApprovedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);

        await _audit.Log(
            "promotions", "promotion.approved",
            "system", "System (gate satisfied)", "system",
            "PromotionCandidate", candidate.Id, null,
            new { trigger = "gate-evaluator", mode = snapshot.Gate.ToString() });

        _logger.LogInformation(
            "Candidate {Id} → Approved via gate evaluator (mode={Mode})", candidate.Id, snapshot.Gate);

        await DispatchWebhookAsync(candidate, "promotion.approved", ct);

        return candidate;
    }

    /// <summary>
    /// Evaluates whether a Pending candidate's policy gate is satisfied. Pure(ish): reads
    /// <see cref="PromotionApproval"/> / <see cref="WorkItemApproval"/> / <see cref="DeployEventWorkItem"/>
    /// rows but never mutates state. Returned blockers are human-readable strings the UI can render
    /// directly when surfacing "what's missing on this candidate".
    /// </summary>
    /// <remarks>
    /// Behaviour by gate mode:
    /// <list type="bullet">
    ///   <item><see cref="PromotionGate.PromotionOnly"/> — counts <see cref="PromotionApproval"/>
    ///         rows with <see cref="PromotionDecision.Approved"/>; satisfied per the policy
    ///         <see cref="PromotionStrategy"/>.</item>
    ///   <item><see cref="PromotionGate.TicketsOnly"/> — every distinct work-item key in the
    ///         candidate's bundle (source event ∪ inherited superseded events) must have at least
    ///         one Approved <see cref="WorkItemApproval"/> row for
    ///         <c>(WorkItemKey, Product, TargetEnv)</c> and zero Rejected rows. A bundle with zero
    ///         work-items falls back to the <see cref="PromotionGate.PromotionOnly"/> rules — there's
    ///         nothing to gate on, so the only way forward is a manual signoff.</item>
    ///   <item><see cref="PromotionGate.TicketsAndManual"/> — TicketsOnly rules AND ≥1 Approved
    ///         <see cref="PromotionApproval"/> row.</item>
    /// </list>
    /// Auto-approve policies (no approver group) short-circuit to satisfied — though such candidates
    /// are never created Pending so this branch rarely runs.
    /// </remarks>
    internal async Task<GateResult> EvaluateGateAsync(
        PromotionCandidate candidate,
        ResolvedPolicySnapshot snapshot,
        CancellationToken ct)
    {
        if (snapshot.IsAutoApprove)
            return new GateResult(true, Array.Empty<string>());

        return snapshot.Gate switch
        {
            PromotionGate.TicketsOnly => await EvaluateTicketsGateAsync(candidate, snapshot, requireManual: false, ct),
            PromotionGate.TicketsAndManual => await EvaluateTicketsGateAsync(candidate, snapshot, requireManual: true, ct),
            _ => await EvaluatePromotionOnlyGateAsync(candidate, snapshot, ct),
        };
    }

    private async Task<GateResult> EvaluatePromotionOnlyGateAsync(
        PromotionCandidate candidate, ResolvedPolicySnapshot snapshot, CancellationToken ct)
    {
        var approvedCount = await _db.PromotionApprovals
            .CountAsync(a => a.CandidateId == candidate.Id && a.Decision == PromotionDecision.Approved, ct);

        var required = snapshot.Strategy switch
        {
            PromotionStrategy.Any => 1,
            PromotionStrategy.NOfM => Math.Max(1, snapshot.MinApprovers),
            _ => 1,
        };

        if (approvedCount >= required)
            return new GateResult(true, Array.Empty<string>());

        var missing = required - approvedCount;
        return new GateResult(false, new[] { $"{missing} more approval(s) required" });
    }

    private async Task<bool> CandidateHasTicketsAsync(PromotionCandidate candidate, CancellationToken ct)
    {
        var bundleEventIds = new HashSet<Guid>(candidate.SupersededSourceEventIds) { candidate.SourceDeployEventId };
        return await _db.DeployEventWorkItems.AsNoTracking()
            .AnyAsync(w => bundleEventIds.Contains(w.DeployEventId), ct);
    }

    private async Task<GateResult> EvaluateTicketsGateAsync(
        PromotionCandidate candidate, ResolvedPolicySnapshot snapshot, bool requireManual, CancellationToken ct)
    {
        // Bundle = the candidate's own source event plus everything it inherited from superseded
        // predecessors on the same edge. Ticket signoffs are scoped to (key, product, target_env)
        // so they survive a supersede chain.
        var bundleEventIds = new HashSet<Guid>(candidate.SupersededSourceEventIds) { candidate.SourceDeployEventId };

        var ticketKeys = await _db.DeployEventWorkItems.AsNoTracking()
            .Where(w => bundleEventIds.Contains(w.DeployEventId))
            .Select(w => w.WorkItemKey)
            .Distinct()
            .ToListAsync(ct);

        // Empty bundle → no tickets to gate on. Per design we fall back to PromotionOnly so a
        // human signoff (in TicketsAndManual or as a one-off in TicketsOnly) remains a viable
        // path forward; otherwise the candidate would be permanently un-promotable.
        if (ticketKeys.Count == 0)
            return await EvaluatePromotionOnlyGateAsync(candidate, snapshot, ct);

        var approvals = await _db.WorkItemApprovals.AsNoTracking()
            .Where(a => ticketKeys.Contains(a.WorkItemKey)
                     && a.Product == candidate.Product
                     && a.TargetEnv == candidate.TargetEnv)
            .ToListAsync(ct);

        var blockers = new List<string>();
        var pendingTickets = new List<string>();

        foreach (var key in ticketKeys)
        {
            var rows = approvals.Where(a => a.WorkItemKey == key).ToList();
            if (rows.Any(a => a.Decision == PromotionDecision.Rejected))
            {
                // A rejected ticket vetoes the candidate. Phase 3B turns this into a candidate
                // rejection cascade; for the evaluator alone, it's just an unsatisfiable blocker.
                blockers.Add($"Ticket {key} was rejected");
                continue;
            }
            if (!rows.Any(a => a.Decision == PromotionDecision.Approved))
                pendingTickets.Add(key);
        }

        // Cap pending-ticket blockers at 5 to keep error payloads bounded; tail collapses to
        // "...and N more" so callers can still see the magnitude.
        const int MaxPendingShown = 5;
        if (pendingTickets.Count > 0)
        {
            foreach (var key in pendingTickets.Take(MaxPendingShown))
                blockers.Add($"Awaiting signoff on {key}");
            if (pendingTickets.Count > MaxPendingShown)
                blockers.Add($"...and {pendingTickets.Count - MaxPendingShown} more");
        }

        if (requireManual)
        {
            var manualApproved = await _db.PromotionApprovals
                .AnyAsync(a => a.CandidateId == candidate.Id && a.Decision == PromotionDecision.Approved, ct);
            if (!manualApproved)
                blockers.Add("Manual release-manager approval required");
        }

        return new GateResult(blockers.Count == 0, blockers);
    }

    /// <summary>
    /// Rejects a pending candidate. One rejection from an authorized approver is enough to
    /// terminate the flow — consistent with treating rejection as an explicit veto.
    /// </summary>
    public async Task<PromotionCandidate> RejectAsync(
        Guid candidateId, string? comment, CancellationToken ct = default)
    {
        var candidate = await LoadPendingAsync(candidateId, ct);
        var snapshot = ReadSnapshot(candidate);

        await EnsureUserCanApproveAsync(candidate, snapshot, ct);
        await EnsureNotAlreadyDecidedAsync(candidateId, _currentUser.Email, ct);

        var decision = new PromotionApproval
        {
            Id = Guid.NewGuid(),
            CandidateId = candidateId,
            ApproverEmail = _currentUser.Email,
            ApproverName = _currentUser.Name,
            Comment = comment,
            Decision = PromotionDecision.Rejected,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        _db.PromotionApprovals.Add(decision);

        candidate.Status = PromotionStatus.Rejected;
        await _db.SaveChangesAsync(ct);

        await _audit.Log(
            "promotions", "promotion.rejected",
            _currentUser.Id, _currentUser.Name, "user",
            "PromotionCandidate", candidate.Id, null,
            new { comment });

        _logger.LogInformation(
            "Candidate {Id} rejected by {Email}", candidate.Id, _currentUser.Email);

        await DispatchWebhookAsync(candidate, "promotion.rejected", ct);

        return candidate;
    }

    // ---------------------------------------------------------------------
    // Execution transitions (called by P3.C / P3.D)
    // ---------------------------------------------------------------------

    /// <summary>Approved → Deploying. Called after the executor has accepted the dispatch.</summary>
    public async Task<PromotionCandidate> MarkDeployingAsync(
        Guid candidateId, string? externalRunUrl, CancellationToken ct = default)
    {
        var candidate = await _db.PromotionCandidates.FirstOrDefaultAsync(c => c.Id == candidateId, ct)
            ?? throw new KeyNotFoundException($"Promotion candidate {candidateId} not found");

        if (candidate.Status != PromotionStatus.Approved)
            throw new InvalidOperationException(
                $"Cannot transition {candidate.Status} → Deploying (id={candidateId})");

        candidate.Status = PromotionStatus.Deploying;
        candidate.ExternalRunUrl = externalRunUrl;
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Candidate {Id} → Deploying (run={Run})", candidateId, externalRunUrl);
        return candidate;
    }

    /// <summary>Deploying → Deployed. Called from the ingest completion hook once the target
    /// environment reports a deploy event matching this candidate's (product, service, target_env, version).</summary>
    public async Task<PromotionCandidate> MarkDeployedAsync(Guid candidateId, CancellationToken ct = default)
    {
        var candidate = await _db.PromotionCandidates.FirstOrDefaultAsync(c => c.Id == candidateId, ct)
            ?? throw new KeyNotFoundException($"Promotion candidate {candidateId} not found");

        if (candidate.Status is not (PromotionStatus.Deploying or PromotionStatus.Approved))
            throw new InvalidOperationException(
                $"Cannot transition {candidate.Status} → Deployed (id={candidateId})");

        candidate.Status = PromotionStatus.Deployed;
        candidate.DeployedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Candidate {Id} → Deployed", candidateId);

        await DispatchWebhookAsync(candidate, "promotion.deployed", ct);

        return candidate;
    }

    // ---------------------------------------------------------------------
    // Webhook dispatch (called after state transitions)
    // ---------------------------------------------------------------------

    /// <summary>
    /// Dispatches a webhook event for a promotion state change. Non-fatal: logs a warning on
    /// failure but never throws — the state transition has already been persisted.
    /// </summary>
    private async Task DispatchWebhookAsync(
        PromotionCandidate candidate, string eventType, CancellationToken ct, object? change = null)
    {
        try
        {
            var payload = new
            {
                candidateId = candidate.Id,
                candidate.Product,
                candidate.Service,
                candidate.SourceEnv,
                candidate.TargetEnv,
                candidate.Version,
                candidate.SourceDeployEventId,
                candidate.SourceDeployerEmail,
                status = candidate.Status.ToString(),
                candidate.ApprovedAt,
                participants = candidate.Participants,
                change,
            };

            var filters = new WebhookEventFilters(Product: candidate.Product, Environment: candidate.TargetEnv);

            await _webhookDispatcher.DispatchAsync(eventType, payload, filters);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Webhook dispatch '{EventType}' failed for candidate {Id}",
                eventType, candidate.Id);
        }
    }

    // ---------------------------------------------------------------------
    // Capability probe (for UI gating)
    // ---------------------------------------------------------------------

    /// <summary>
    /// Non-throwing version of the approval authorization check — used by endpoints to return a
    /// <c>canApprove</c> flag per candidate so the UI can disable buttons.
    /// </summary>
    public async Task<bool> CanUserApproveAsync(PromotionCandidate candidate, CancellationToken ct = default)
    {
        if (candidate.Status != PromotionStatus.Pending) return false;

        var snapshot = ReadSnapshot(candidate);
        if (snapshot.IsAutoApprove) return false; // nothing to approve

        if (await IsCurrentUserExcludedByRoleAsync(candidate, snapshot, ct))
            return false;

        // Already decided? Can't approve again.
        var already = await _db.PromotionApprovals.AsNoTracking()
            .AnyAsync(a => a.CandidateId == candidate.Id && a.ApproverEmail == _currentUser.Email, ct);
        if (already) return false;

        return await _auth.IsInApproverGroupAsync(snapshot.ApproverGroup!, ct);
    }

    /// <summary>
    /// Bulk capability probe — resolves the approver group once per distinct group, then tests
    /// each candidate against the cached membership set. This avoids N+1 Graph calls when the
    /// UI lists dozens of candidates.
    /// </summary>
    public async Task<IReadOnlyDictionary<Guid, bool>> CanUserApproveManyAsync(
        IEnumerable<PromotionCandidate> candidates, CancellationToken ct = default)
    {
        var result = new Dictionary<Guid, bool>();
        var list = candidates.ToList();

        // Precompute "already decided" set for the current user across all candidates in one query.
        var ids = list.Select(c => c.Id).ToList();
        var alreadyDecided = await _db.PromotionApprovals.AsNoTracking()
            .Where(a => ids.Contains(a.CandidateId) && a.ApproverEmail == _currentUser.Email)
            .Select(a => a.CandidateId)
            .ToListAsync(ct);
        var decidedSet = alreadyDecided.ToHashSet();

        // Batch-load source deploy event participants + references for role-based exclusion.
        // One query instead of N — important when the UI is rendering a long list of candidates.
        // References are needed for the reference-level participant lookup (two-level model).
        // Bundle = each candidate's own source event + any superseded predecessors so an override
        // (or excluded participant) on an inherited event still trips the gate.
        var bundleEventIds = list
            .SelectMany(c => new[] { c.SourceDeployEventId }.Concat(c.SupersededSourceEventIds))
            .Distinct()
            .ToList();
        var sourceJsonByEvent = await _db.DeployEvents.AsNoTracking()
            .Where(e => bundleEventIds.Contains(e.Id))
            .Select(e => new { e.Id, e.ParticipantsJson, e.ReferencesJson })
            .ToDictionaryAsync(e => e.Id, e => (e.ParticipantsJson, e.ReferencesJson), ct);

        var overridesRows = await _db.ReferenceParticipantOverrides.AsNoTracking()
            .Where(o => bundleEventIds.Contains(o.DeployEventId))
            .ToListAsync(ct);
        var overridesByEvent = overridesRows
            .GroupBy(o => o.DeployEventId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<ReferenceParticipantOverride>)g.ToList());

        // Cache group membership lookups: one call per unique approver group.
        var groupMembership = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        foreach (var c in list)
        {
            if (c.Status != PromotionStatus.Pending) { result[c.Id] = false; continue; }
            var snapshot = ReadSnapshot(c);
            if (snapshot.IsAutoApprove) { result[c.Id] = false; continue; }
            if (decidedSet.Contains(c.Id)) { result[c.Id] = false; continue; }

            if (!string.IsNullOrWhiteSpace(snapshot.ExcludeRole))
            {
                var bundle = new[] { c.SourceDeployEventId }.Concat(c.SupersededSourceEventIds);
                var excluded = false;
                foreach (var eid in bundle)
                {
                    var (partsJson, refsJson) = sourceJsonByEvent.GetValueOrDefault(eid);
                    var evOverrides = overridesByEvent.GetValueOrDefault(eid, Array.Empty<ReferenceParticipantOverride>());
                    if (PromotionApprovalAuthorizer.EmailMatchesExcludedRole(
                            partsJson, refsJson, evOverrides, snapshot.ExcludeRole, _currentUser.Email))
                    {
                        excluded = true; break;
                    }
                }
                if (excluded) { result[c.Id] = false; continue; }
            }

            var group = snapshot.ApproverGroup!;
            if (!groupMembership.TryGetValue(group, out var member))
            {
                member = await _auth.IsInApproverGroupAsync(group, ct);
                groupMembership[group] = member;
            }
            result[c.Id] = member;
        }

        return result;
    }

    // ---------------------------------------------------------------------
    // Private helpers
    // ---------------------------------------------------------------------

    private async Task<PromotionCandidate> LoadPendingAsync(Guid id, CancellationToken ct)
    {
        var candidate = await _db.PromotionCandidates.FirstOrDefaultAsync(c => c.Id == id, ct)
            ?? throw new KeyNotFoundException($"Promotion candidate {id} not found");
        if (candidate.Status != PromotionStatus.Pending)
            throw new InvalidOperationException(
                $"Candidate {id} is {candidate.Status}, no longer accepting decisions");
        return candidate;
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

    private async Task EnsureUserCanApproveAsync(
        PromotionCandidate candidate, ResolvedPolicySnapshot snapshot, CancellationToken ct)
    {
        if (snapshot.IsAutoApprove)
            throw new InvalidOperationException("This candidate does not require approval");

        if (await IsCurrentUserExcludedByRoleAsync(candidate, snapshot, ct))
        {
            throw new UnauthorizedAccessException(
                $"You cannot approve — the '{snapshot.ExcludeRole}' role is excluded from approving this promotion.");
        }

        if (!await _auth.IsInApproverGroupAsync(snapshot.ApproverGroup!, ct))
            throw new UnauthorizedAccessException("You are not in the approver group for this promotion");
    }

    /// <summary>
    /// Thin wrapper over <see cref="PromotionApprovalAuthorizer.IsEmailExcludedByRoleAsync"/>: the
    /// candidate already knows the source event id and we want the current user's email — pull
    /// both out and delegate. Kept so existing call-sites read naturally.
    /// </summary>
    private Task<bool> IsCurrentUserExcludedByRoleAsync(
        PromotionCandidate candidate, ResolvedPolicySnapshot snapshot, CancellationToken ct)
        => _auth.IsEmailExcludedByRoleAsync(
            snapshot, candidate.SourceDeployEventId, candidate.SupersededSourceEventIds, _currentUser.Email, ct);

    private async Task EnsureNotAlreadyDecidedAsync(Guid candidateId, string email, CancellationToken ct)
    {
        var dup = await _db.PromotionApprovals.AsNoTracking()
            .AnyAsync(a => a.CandidateId == candidateId && a.ApproverEmail == email, ct);
        if (dup)
            throw new InvalidOperationException("You have already made a decision on this promotion");
    }
}

/// <summary>
/// Filter args for <see cref="PromotionService.GetAsync"/>. All fields are optional; omitted
/// filters are treated as "don't narrow".
/// </summary>
public record PromotionQuery(
    PromotionStatus? Status = null,
    string? Product = null,
    string? Service = null,
    string? TargetEnv = null,
    int Limit = 200);

/// <summary>
/// Outcome of <see cref="PromotionService.EvaluateGateAsync"/>: whether the gate is currently
/// satisfied for a Pending candidate, plus a list of human-readable blockers when it isn't.
/// Blockers are intended for surfacing in error responses or the UI's "what's missing" panel —
/// not a structured machine-consumable shape.
/// </summary>
public record GateResult(bool Satisfied, IReadOnlyList<string> Blockers);
