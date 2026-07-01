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
///   <item>Approval recording and gate evaluation (per-requirement, distinct-person matching).</item>
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
    // Candidate creation (external / push-only)
    // ---------------------------------------------------------------------

    /// <summary>
    /// Creates a <see cref="PromotionCandidate"/> from an external create-promotion request. The
    /// external system (CI, which has SCM access) computes the authoritative net change set and
    /// POSTs it; the tool records exactly what it's told — it does not infer anything.
    ///
    /// <para>Returns <c>null</c> when no promotion policy resolves for
    /// <c>(product, service, targetEnv)</c> — the product is not enrolled in promotions for that
    /// edge (the endpoint maps this to 422). With topology dropped (D19) this is the only edge
    /// guard; <c>sourceEnv</c> is recorded for display but not validated.</para>
    ///
    /// <para>Identity is the natural key <c>(product, service, sourceEnv, targetEnv, version)</c>.
    /// If a non-terminal candidate already exists for it, this <b>updates</b> that candidate's
    /// references/revisions and re-evaluates it instead of duplicating — a repeat for the same
    /// version is a legitimate update (the external may have recomputed the net set after another
    /// revert). A concurrent double-create is caught via the post-save reuse path.</para>
    ///
    /// <para>Supersede is a <b>pure state flip</b> (D2): any still-<c>Pending</c> candidate on the
    /// same <c>(product, service, sourceEnv, targetEnv)</c> is marked <c>Superseded</c> with
    /// <c>SupersededById</c> set. No inheritance, no event-id copying — each candidate is
    /// self-contained.</para>
    ///
    /// <para>If the resolved policy is auto-approve (or AutoApproveWhenNoWorkItems applies and the
    /// payload carries no work-item refs), the candidate is created directly in
    /// <see cref="PromotionStatus.Approved"/>.</para>
    /// </summary>
    public async Task<PromotionCandidate?> CreateExternalCandidateAsync(
        CreatePromotionDto dto, CancellationToken ct = default)
    {
        var product = dto.Product.Trim();
        var service = dto.Service.Trim();
        var sourceEnv = dto.SourceEnv.Trim();
        var targetEnv = dto.TargetEnv.Trim();
        var version = dto.Version.Trim();
        var references = dto.References ?? new List<ReferenceDto>();
        var participants = CanonicaliseParticipants(dto.Participants);

        // No policy → product is not enrolled in promotions for this source→target edge (→ 422).
        var policy = await _resolver.ResolveAsync(product, service, sourceEnv, targetEnv, ct);
        if (policy is null)
        {
            _logger.LogDebug(
                "No promotion policy for {Product}/{Service} {SourceEnv} → {TargetEnv}; rejecting external create",
                LogSanitizer.Clean(product), LogSanitizer.Clean(service),
                LogSanitizer.Clean(sourceEnv), LogSanitizer.Clean(targetEnv));
            return null;
        }

        // Ground the promotion in real source state: the exact version must have a succeeded deploy
        // in the source environment. Blocks promotions from an unknown / never-shipped source.
        var sourceDeployed = await _db.DeployEvents.AsNoTracking().AnyAsync(e =>
            e.Product == product && e.Service == service && e.Environment == sourceEnv
            && e.Version == version && e.Status == "succeeded", ct);
        if (!sourceDeployed)
            throw new SourceDeploymentNotFoundException(product, service, sourceEnv, version);

        // Natural-key reuse-and-update (D15): a non-terminal candidate for this exact edge+version
        // is updated in place rather than duplicated.
        var existing = await _db.PromotionCandidates.FirstOrDefaultAsync(c =>
            c.Product == product && c.Service == service
            && c.SourceEnv == sourceEnv && c.TargetEnv == targetEnv && c.Version == version
            && (c.Status == PromotionStatus.Pending || c.Status == PromotionStatus.Approved
                || c.Status == PromotionStatus.Deploying), ct);
        if (existing is not null)
            return await UpdateExistingCandidateAsync(existing, dto, references, participants, ct);

        var snapshot = await _resolver.SnapshotAsync(product, service, sourceEnv, targetEnv, ct);

        // AutoApproveWhenNoWorkItems: probe reads the PAYLOAD references (not DeployEventWorkItems) —
        // the candidate is self-contained, so "no work items" means the payload carries none.
        var payloadWorkItems = ExtractWorkItemReferences(references);
        var autoApproveNoWorkItems = !snapshot.IsAutoApprove
            && snapshot.AutoApproveWhenNoWorkItems
            && payloadWorkItems.Count == 0;
        var effectiveAutoApprove = snapshot.IsAutoApprove || autoApproveNoWorkItems;

        var now = DateTimeOffset.UtcNow;
        var candidate = new PromotionCandidate
        {
            Id = Guid.NewGuid(),
            Product = product,
            Service = service,
            SourceEnv = sourceEnv,
            TargetEnv = targetEnv,
            Version = version,
            FromRevision = dto.FromRevision,
            ToRevision = dto.ToRevision,
            References = references,
            Participants = participants,
            Status = effectiveAutoApprove ? PromotionStatus.Approved : PromotionStatus.Pending,
            PolicyId = snapshot.PolicyId,
            ResolvedPolicyJson = JsonSerializer.Serialize(snapshot, JsonOptions),
            CreatedAt = now,
            ApprovedAt = effectiveAutoApprove ? now : null,
        };

        // Supersede = pure state flip (D2): no inheritance, no event-id copying.
        await SupersedeStalePendingAsync(candidate, ct);

        _db.PromotionCandidates.Add(candidate);

        // Populate the candidate-scoped work-item index from the payload's work-item references.
        SyncWorkItems(candidate, payloadWorkItems);

        // Auto-approve: record a synthetic approval row so the UI's approval trail renders it.
        if (effectiveAutoApprove)
        {
            _db.PromotionApprovals.Add(new PromotionApproval
            {
                Id = Guid.NewGuid(),
                CandidateId = candidate.Id,
                ApproverEmail = "system",
                ApproverName = autoApproveNoWorkItems
                    ? "System (auto-approve — no work items)"
                    : "System (auto-approve)",
                Decision = PromotionDecision.Approved,
                CreatedAt = now,
            });
        }

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // A concurrent double-create may have lost the race on the natural key. If a reusable
            // candidate now exists, treat this as reuse: detach our staged inserts and update the
            // winner instead. Otherwise the failure is genuine — rethrow.
            _db.ChangeTracker.Clear();
            var raced = await FindReusableCandidateAsync(product, service, sourceEnv, targetEnv, version, ct);
            if (raced is null) throw;
            _logger.LogInformation(
                "Concurrent external create for {Product}/{Service} {Version} → reusing candidate {CandidateId}",
                LogSanitizer.Clean(product), LogSanitizer.Clean(service), LogSanitizer.Clean(version), raced.Id);
            return await UpdateExistingCandidateAsync(raced, dto, references, participants, ct);
        }

        await _audit.Log(
            "promotions", "promotion.candidate.created",
            "system", "System", "system",
            "PromotionCandidate", candidate.Id, null,
            new
            {
                source = "external",
                candidate.Product,
                candidate.Service,
                candidate.SourceEnv,
                candidate.TargetEnv,
                candidate.Version,
                candidate.Status,
                AutoApprove = effectiveAutoApprove,
                AutoApproveReason = snapshot.IsAutoApprove ? "policy" : autoApproveNoWorkItems ? "no-work-items" : null,
            });

        _logger.LogInformation(
            "Created external promotion candidate {CandidateId} for {Product}/{Service} {Version} ({Status})",
            candidate.Id, LogSanitizer.Clean(product), LogSanitizer.Clean(service),
            LogSanitizer.Clean(version), candidate.Status);

        // If born Approved, kick off execution right away — after the initial save so the candidate
        // is visible to queries even if dispatch transiently fails.
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

    /// <summary>
    /// Update-in-place path for a repeat create on the same non-terminal natural key (D15): refresh
    /// the references/revisions, re-sync the candidate's work-item index, re-evaluate the gate
    /// (a Pending candidate may now satisfy or no longer satisfy it), and return it.
    /// </summary>
    private async Task<PromotionCandidate> UpdateExistingCandidateAsync(
        PromotionCandidate existing, CreatePromotionDto dto,
        List<ReferenceDto> references, List<PromotionParticipant> participants, CancellationToken ct)
    {
        existing.References = references;
        existing.FromRevision = dto.FromRevision;
        existing.ToRevision = dto.ToRevision;
        if (participants.Count > 0) existing.Participants = participants;

        // Re-sync the candidate-scoped work-item index to match the new references.
        var stale = await _db.PromotionWorkItems.Where(w => w.CandidateId == existing.Id).ToListAsync(ct);
        _db.PromotionWorkItems.RemoveRange(stale);
        SyncWorkItems(existing, ExtractWorkItemReferences(references));

        await _db.SaveChangesAsync(ct);

        await _audit.Log(
            "promotions", "promotion.candidate.updated",
            "system", "System", "system",
            "PromotionCandidate", existing.Id, null,
            new { source = "external", existing.Version, refCount = references.Count });

        _logger.LogInformation(
            "Updated existing candidate {CandidateId} from external create (refs={Count})",
            existing.Id, references.Count);

        if (existing.Status == PromotionStatus.Pending) return await ReevaluateAsync(existing.Id, ct);
        return existing;
    }

    private async Task<PromotionCandidate?> FindReusableCandidateAsync(
        string product, string service, string sourceEnv, string targetEnv, string version, CancellationToken ct)
        => await _db.PromotionCandidates.FirstOrDefaultAsync(c =>
            c.Product == product && c.Service == service
            && c.SourceEnv == sourceEnv && c.TargetEnv == targetEnv && c.Version == version
            && (c.Status == PromotionStatus.Pending || c.Status == PromotionStatus.Approved
                || c.Status == PromotionStatus.Deploying), ct);

    /// <summary>
    /// Pure state flip (D2): mark every still-<c>Pending</c> candidate on the same edge as
    /// <c>Superseded</c> and point its <c>SupersededById</c> at the fresh candidate. No inheritance,
    /// no event-id copying — the fresh candidate is self-contained.
    /// </summary>
    private async Task SupersedeStalePendingAsync(PromotionCandidate fresh, CancellationToken ct)
    {
        var stale = await _db.PromotionCandidates
            .Where(c => c.Product == fresh.Product
                     && c.Service == fresh.Service
                     && c.SourceEnv == fresh.SourceEnv
                     && c.TargetEnv == fresh.TargetEnv
                     && c.Status == PromotionStatus.Pending)
            .ToListAsync(ct);

        foreach (var old in stale)
        {
            old.Status = PromotionStatus.Superseded;
            old.SupersededById = fresh.Id;
        }

        if (stale.Count > 0)
            _logger.LogInformation(
                "Superseded {Count} pending candidate(s) in favour of {CandidateId}",
                stale.Count, fresh.Id);
    }

    /// <summary>
    /// Stages <see cref="PromotionWorkItem"/> inserts for the candidate from its work-item
    /// references. Deduped by key (case-insensitive), mirroring <c>WorkItemSyncService</c>.
    /// </summary>
    private void SyncWorkItems(PromotionCandidate candidate, IReadOnlyList<ReferenceDto> workItemRefs)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var r in workItemRefs)
        {
            _db.PromotionWorkItems.Add(new PromotionWorkItem
            {
                Id = Guid.NewGuid(),
                CandidateId = candidate.Id,
                WorkItemKey = r.Key!,
                Product = candidate.Product,
                TargetEnv = candidate.TargetEnv,
                Provider = r.Provider,
                Url = r.Url,
                Title = r.Title,
                Revision = r.Revision,
                CreatedAt = now,
            });
        }
    }

    /// <summary>Distinct <c>work-item</c> references (by key, case-insensitive) with a non-blank key.</summary>
    private static List<ReferenceDto> ExtractWorkItemReferences(IEnumerable<ReferenceDto> references)
        => references
            .Where(r => string.Equals(r.Type, "work-item", StringComparison.OrdinalIgnoreCase)
                     && !string.IsNullOrWhiteSpace(r.Key))
            .GroupBy(r => r.Key!, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

    /// <summary>
    /// Canonicalises participant roles the same way the participant-upsert path does (storage-time
    /// role normalisation via <c>Normalization:Roles</c>, dedupe on the canonical key). Drops
    /// entries with a blank role.
    /// </summary>
    private List<PromotionParticipant> CanonicaliseParticipants(IEnumerable<ParticipantDto>? participants)
    {
        var result = new List<PromotionParticipant>();
        if (participants is null) return result;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in participants)
        {
            var storedRole = _normalization.CurrentValue.ApplyRole(p.Role);
            if (string.IsNullOrEmpty(storedRole)) continue;
            var canonicalKey = RoleNormalizer.Normalize(storedRole);
            if (!seen.Add(canonicalKey)) continue;
            result.Add(new PromotionParticipant(storedRole, p.DisplayName, p.Email));
        }
        return result;
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
    /// candidate must still be Pending, user must be in the approver group, and the same user may
    /// not approve twice (also enforced by a DB-level unique index as belt-and-suspenders).
    ///
    /// <para>After persisting the approval row, delegates to <see cref="ReevaluateAsync"/> which
    /// runs the gate evaluator and transitions the candidate to <see cref="PromotionStatus.Approved"/>
    /// when satisfied. The split keeps the row-recording concerns here (granular audit, dup checks)
    /// separate from the candidate-level transition concerns owned by re-evaluation, which Phase 3B
    /// will also drive from the work item-approval flow.</para>
    ///
    /// <para>When a policy has no human approver requirements there is nothing to manually approve;
    /// that path is handled by eligibility (an empty requirement tree yields no eligible
    /// requirements) rather than a dedicated guard here.</para>
    /// </summary>
    public async Task<PromotionCandidate> ApproveAsync(
        Guid candidateId, string? comment,
        string? stepName = null, string? requirementName = null, CancellationToken ct = default)
    {
        var candidate = await LoadPendingAsync(candidateId, ct);
        var snapshot = ReadSnapshot(candidate);

        // RequireAllWorkItemsApproved: the policy says every work item must be signed off before a
        // human release manager can approve the promotion. When the bundle has work items and at least
        // one is still pending (or rejected), reject the attempt with an actionable message.
        if (snapshot.RequireAllWorkItemsApproved && await CandidateHasWorkItemsAsync(candidate, ct))
        {
            if (!await AreAllWorkItemsApprovedAsync(candidate, ct))
                throw new InvalidOperationException(
                    "All work items must be approved before this promotion can be approved. " +
                    "Check the work items queue for pending sign-offs.");
        }

        await EnsureUserCanApproveAsync(candidate, snapshot, ct);
        await EnsureNotAlreadyDecidedAsync(candidateId, _currentUser.Email, ct);

        // Resolve which requirement this approval is attributed to. The approver may explicitly pin
        // a (stepName, requirementName); otherwise we auto-pick when exactly one open requirement is
        // available, or ask the caller to choose when more than one is.
        var eligible = await GetEligibleRequirementsAsync(candidate, ct);
        RequirementRef? target = null;
        var hasExplicit = !string.IsNullOrWhiteSpace(stepName) || !string.IsNullOrWhiteSpace(requirementName);
        if (hasExplicit)
        {
            var match = eligible.FirstOrDefault(r =>
                string.Equals(r.StepName, stepName ?? "", StringComparison.OrdinalIgnoreCase)
                && string.Equals(r.RequirementName, requirementName ?? "", StringComparison.OrdinalIgnoreCase));
            if (match is null)
            {
                // Distinguish "not eligible" from "already satisfied" so the caller can pick a status.
                var existsInTree = snapshot.ApprovalSteps.Any(s =>
                    string.Equals(s.Name ?? "", stepName ?? "", StringComparison.OrdinalIgnoreCase)
                    && s.Requirements.Any(rq =>
                        string.Equals(rq.Name ?? "", requirementName ?? "", StringComparison.OrdinalIgnoreCase)));
                var authorized = false;
                if (existsInTree)
                {
                    var req = snapshot.ApprovalSteps
                        .First(s => string.Equals(s.Name ?? "", stepName ?? "", StringComparison.OrdinalIgnoreCase))
                        .Requirements
                        .First(rq => string.Equals(rq.Name ?? "", requirementName ?? "", StringComparison.OrdinalIgnoreCase));
                    authorized = await _auth.IsAuthorizedForRequirementAsync(req, _currentUser.Email, ct);
                }

                if (existsInTree && authorized)
                    throw new RequirementAlreadySatisfiedException(
                        $"Requirement '{requirementName}' is already satisfied — nothing to approve there.");
                throw new UnauthorizedAccessException("You are not eligible for that requirement.");
            }
            target = match;
        }
        else
        {
            if (eligible.Count == 1) target = eligible[0];
            else if (eligible.Count > 1) throw new MultipleEligibleRequirementsException(eligible);
            // eligible.Count == 0: leave target null — EnsureUserCanApproveAsync already passed, so
            // the user is authorized for the tree but every requirement they match is satisfied.
            // Record an unattributed row (back-compat) so the matcher's surplus handling applies.
        }

        var decision = new PromotionApproval
        {
            Id = Guid.NewGuid(),
            CandidateId = candidateId,
            ApproverEmail = _currentUser.Email,
            ApproverName = _currentUser.Name,
            Comment = comment,
            Decision = PromotionDecision.Approved,
            StepName = target?.StepName,
            RequirementName = target?.RequirementName,
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
            new { approvalId = decision.Id, comment, decision.StepName, decision.RequirementName });

        _logger.LogInformation(
            "Approval recorded on candidate {Id} by {Email}", candidate.Id, _currentUser.Email);

        // Re-evaluate the gate now that the new row is persisted. This may flip the candidate to
        // Approved (and emit the candidate-level audit + webhook) or leave it Pending if the
        // strategy threshold or work item gates aren't yet satisfied.
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
    /// "the last work item signoff caused the gate to satisfy and the system promoted the candidate".
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
            new { trigger = "gate-evaluator" });

        _logger.LogInformation(
            "Candidate {Id} → Approved via gate evaluator", candidate.Id);

        await DispatchWebhookAsync(candidate, "promotion.approved", ct);

        return candidate;
    }

    /// <summary>
    /// Evaluates whether a Pending candidate's policy gate is satisfied. Pure(ish): reads
    /// <see cref="PromotionApproval"/> / <see cref="WorkItemApproval"/> / <see cref="PromotionWorkItem"/>
    /// rows but never mutates state. Returned blockers are human-readable strings the UI can render
    /// directly when surfacing "what's missing on this candidate".
    /// </summary>
    /// <remarks>
    /// Evaluated in order, using two orthogonal signals — the human approver tree
    /// (<see cref="ResolvedPolicySnapshot.ApprovalSteps"/>) and the work-item flags:
    /// <list type="number">
    ///   <item>If <see cref="ResolvedPolicySnapshot.RequireAllWorkItemsApproved"/> and the bundle has
    ///         work items that are not all approved → blocked.</item>
    ///   <item>If <see cref="ResolvedPolicySnapshot.AutoApproveOnAllWorkItemsApproved"/> and every
    ///         work item is approved → satisfied, regardless of any manual approver requirements.</item>
    ///   <item>If there are no human approver requirements (empty requirement tree) → satisfied.</item>
    ///   <item>Otherwise, Approved <see cref="PromotionApproval"/> rows are matched against the
    ///         requirement tree; satisfied when every requirement has enough distinct eligible
    ///         approvers (see <see cref="ApprovalMatcher"/>).</item>
    /// </list>
    /// A work item counts as approved when it has at least one Approved <see cref="WorkItemApproval"/>
    /// row for <c>(WorkItemKey, Product, TargetEnv)</c> and zero Rejected rows.
    /// </remarks>
    internal async Task<GateResult> EvaluateGateAsync(
        PromotionCandidate candidate,
        ResolvedPolicySnapshot snapshot,
        CancellationToken ct)
    {
        // Source-drift invariant: a candidate is only promotable while its source environment is
        // still running the candidate's version. If the source was rolled back (or otherwise moved
        // off this version), block — promoting would push a version no live env runs. This clears
        // automatically once the source is redeployed to the version (see idempotent reactivation
        // in CreateCandidateAsync). Checked before auto-approve so even auto policies can't promote
        // a drifted version.
        var sourceCurrent = await _db.DeployEvents.AsNoTracking()
            .Where(e => e.Product == candidate.Product && e.Service == candidate.Service
                     && e.Environment == candidate.SourceEnv)
            .OrderByDescending(e => e.DeployedAt)
            .Select(e => e.Version)
            .FirstOrDefaultAsync(ct);
        // Only block on positive evidence of drift: a source deploy exists and runs a *different*
        // version. No source history (null) means we can't conclude drift, so don't block.
        if (sourceCurrent is not null
            && !string.Equals(sourceCurrent, candidate.Version, StringComparison.OrdinalIgnoreCase))
            return new GateResult(false, new[]
            {
                $"Source environment '{candidate.SourceEnv}' no longer runs {candidate.Version} " +
                $"(now {sourceCurrent}) — promotion is stale until redeployed",
            });

        // 1. Work items REQUIRED but not all approved → blocked.
        if (snapshot.RequireAllWorkItemsApproved
            && await CandidateHasWorkItemsAsync(candidate, ct)
            && !await AreAllWorkItemsApprovedAsync(candidate, ct))
        {
            return new GateResult(false, new[] { "All work items must be approved before this promotion can proceed" });
        }

        // 2. Accelerator: all work items approved auto-promotes, regardless of manual steps.
        if (snapshot.AutoApproveOnAllWorkItemsApproved
            && await CandidateHasWorkItemsAsync(candidate, ct)
            && await AreAllWorkItemsApprovedAsync(candidate, ct))
        {
            return new GateResult(true, Array.Empty<string>());
        }

        // 3. No human approver requirements → satisfied (any required work-item gate already passed above).
        if (snapshot.IsAutoApprove)
            return new GateResult(true, Array.Empty<string>());

        // 4. Human approver requirements must be satisfied.
        return await EvaluatePromotionOnlyGateAsync(candidate, snapshot, ct);
    }

    private async Task<GateResult> EvaluatePromotionOnlyGateAsync(
        PromotionCandidate candidate, ResolvedPolicySnapshot snapshot, CancellationToken ct)
    {
        // The manual gate is satisfied when EVERY requirement across EVERY step is satisfied by a
        // distinct set of approvers (parallel AND over the flattened requirement set, D9). The
        // matcher assigns each distinct approver to at most one requirement, most-constrained first.
        var requirements = snapshot.AllRequirements;
        if (requirements.Count == 0)
            return new GateResult(true, Array.Empty<string>()); // no human gate

        var match = await EvaluateRequirementMatchAsync(candidate, requirements, ct);
        if (match.AllSatisfied)
            return new GateResult(true, Array.Empty<string>());

        var blockers = match.Requirements
            .Where(o => !o.Satisfied)
            .Select(o =>
            {
                var label = string.IsNullOrEmpty(o.Requirement.Name) ? "approval" : o.Requirement.Name;
                var missing = o.Required - o.Matched;
                return $"{missing} more approval(s) required for '{label}'";
            })
            .ToArray();
        return new GateResult(false, blockers);
    }

    /// <summary>
    /// Loads the candidate's distinct Approved approver emails and runs the
    /// <see cref="ApprovalMatcher"/> against the requirement set, using the authorizer to decide
    /// eligibility. Eligibility for the current user is resolved live (group membership); for any
    /// other recorded approver, eligibility is determined by the requirement's explicit user list
    /// (group membership for non-current users can't be answered by the identity service). This is
    /// adequate because, in practice, distinct group memberships are validated at record time via
    /// <see cref="EnsureUserCanApproveAsync"/>.
    /// </summary>
    private async Task<MatchResult> EvaluateRequirementMatchAsync(
        PromotionCandidate candidate, IReadOnlyList<ApproverRequirement> requirements, CancellationToken ct)
    {
        // Load the full Approved rows so we can resolve each approver's pinned requirement (if any)
        // from its (StepName, RequirementName) attribution.
        var approvedRows = await _db.PromotionApprovals.AsNoTracking()
            .Where(a => a.CandidateId == candidate.Id && a.Decision == PromotionDecision.Approved)
            .Select(a => new { a.ApproverEmail, a.StepName, a.RequirementName })
            .ToListAsync(ct);

        // Map (StepName, RequirementName) → flattened requirement index for resolving pinned rows.
        // Built by walking the steps in the same flatten order AllRequirements uses.
        var snapshot = ReadSnapshot(candidate);
        var indexByName = new Dictionary<(string Step, string Req), int>();
        {
            var cursor = 0;
            foreach (var step in snapshot.ApprovalSteps)
            {
                foreach (var req in step.Requirements)
                {
                    indexByName[(step.Name ?? "", req.Name ?? "")] = cursor;
                    cursor++;
                }
            }
        }

        // Collapse to one decision per distinct approver. A pinned attribution (resolvable to a
        // requirement index) wins; otherwise the row is unpinned and auto-attributed by the matcher.
        var decisionByEmail = new Dictionary<string, ApproverDecision>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in approvedRows)
        {
            if (string.IsNullOrEmpty(row.ApproverEmail)) continue;
            int? pinned = null;
            if (!string.IsNullOrEmpty(row.RequirementName)
                && indexByName.TryGetValue((row.StepName ?? "", row.RequirementName), out var idx))
            {
                pinned = idx;
            }

            // Keep the strongest signal: a pinned decision should not be overwritten by a later
            // unpinned dup (shouldn't happen given the unique constraint, but be defensive).
            if (decisionByEmail.TryGetValue(row.ApproverEmail, out var existing) && existing.PinnedRequirementIndex is not null)
                continue;
            decisionByEmail[row.ApproverEmail] = new ApproverDecision(row.ApproverEmail, pinned);
        }

        var decisions = decisionByEmail.Values.ToList();

        // Pre-resolve eligibility for the UNPINNED approvers so the (synchronous) matcher stays pure.
        // Pinned rows are trusted (eligibility was validated at record time) so they're attributed
        // directly by the matcher without re-checking group membership here. For the current user we
        // consult the authorizer (role/group/Graph + admin bootstrap); for everyone else we honour
        // the requirement's explicit user list (or the legacy "has groups" approximation). Keyed by
        // (email, requirement-index) so requirements that share a Name don't collide.
        var eligibility = new HashSet<(string Email, int ReqIndex)>();
        for (var ri = 0; ri < requirements.Count; ri++)
        {
            var req = requirements[ri];
            foreach (var decision in decisions)
            {
                if (decision.PinnedRequirementIndex is not null) continue; // trusted, attributed directly
                var email = decision.Approver;
                var isCurrent = string.Equals(email, _currentUser.Email, StringComparison.OrdinalIgnoreCase);
                bool eligible;
                if (isCurrent)
                {
                    // Live check for the current user: role/group/Graph + admin bootstrap + user list.
                    eligible = await _auth.IsAuthorizedForRequirementAsync(req, email, ct);
                }
                else
                {
                    // We can't resolve another user's live group membership. They match a requirement
                    // if listed explicitly, OR — since every recorded approval was authorized for some
                    // requirement at record time — if the requirement carries groups (membership
                    // can't be disproven). This mirrors the legacy "count any approved row" behaviour
                    // for single-group policies while still letting the matcher honour user-only
                    // requirements (plan §8.4).
                    eligible = req.Users.Any(u => string.Equals(u, email, StringComparison.OrdinalIgnoreCase))
                               || req.Groups.Count > 0;
                }
                if (eligible) eligibility.Add((email, ri));
            }
        }

        // Index lookup for the eligibility closure. ReferenceEquals is safe: the matcher passes back
        // the same ApproverRequirement instances we handed it.
        var indexOf = new Dictionary<ApproverRequirement, int>(ReferenceEqualityComparer.Instance);
        for (var ri = 0; ri < requirements.Count; ri++) indexOf[requirements[ri]] = ri;

        return ApprovalMatcher.Match(
            requirements,
            decisions,
            (email, req) => eligibility.Contains((email, indexOf[req])));
    }

    /// <summary>
    /// Surfaces the live approval gate as a per-step / per-requirement progress structure for the
    /// detail view. Reuses the same matcher path as <see cref="EvaluateGateAsync"/> so the panel
    /// always reflects the real gate — it never recomputes progress independently.
    ///
    /// <para>Auto-approve candidates (or any with no requirements) return
    /// <see cref="ApprovalProgress.RequiresApproval"/> = false with empty steps, so the UI can hide
    /// the panel. The flattened <see cref="MatchResult.Requirements"/> outcomes are index-aligned to
    /// <see cref="ResolvedPolicySnapshot.AllRequirements"/> (steps' requirements flattened in order),
    /// so walking the steps in order consumes the outcomes 1:1.</para>
    /// </summary>
    public async Task<ApprovalProgress> GetApprovalProgressAsync(
        PromotionCandidate candidate, CancellationToken ct = default)
    {
        var snapshot = ReadSnapshot(candidate);

        // The work item-resolution gate (policy's "all work items must be resolved" condition), if any.
        var workItems = await GetWorkItemGateAsync(candidate, snapshot, ct);

        if (snapshot.IsAutoApprove || snapshot.AllRequirements.Count == 0)
            // No manual approver requirements — but the candidate may still gate on work items, in which
            // case we surface the panel so the work item condition (and its fulfilment) stays visible.
            return new ApprovalProgress(
                RequiresApproval: workItems != null,
                AllSatisfied: workItems?.Satisfied ?? true,
                TotalRequired: 0, TotalApproved: 0,
                Steps: Array.Empty<StepProgress>(),
                WorkItems: workItems);

        var match = await EvaluateRequirementMatchAsync(candidate, snapshot.AllRequirements, ct);

        // The matcher returns one outcome per AllRequirements entry, in order. Walk the steps in the
        // same order to map outcomes back to their step. Guard the invariant.
        if (match.Requirements.Count != snapshot.AllRequirements.Count)
            throw new InvalidOperationException(
                $"Match outcome count ({match.Requirements.Count}) does not align with requirement " +
                $"count ({snapshot.AllRequirements.Count}) for candidate {candidate.Id}");

        var steps = new List<StepProgress>(snapshot.ApprovalSteps.Count);
        var totalRequired = 0;
        var totalApproved = 0;
        var cursor = 0;
        foreach (var step in snapshot.ApprovalSteps)
        {
            var reqs = new List<RequirementProgress>(step.Requirements.Count);
            var stepSatisfied = true;
            foreach (var req in step.Requirements)
            {
                var outcome = match.Requirements[cursor++];
                var label = string.IsNullOrEmpty(req.Name) ? "Approval" : req.Name;
                reqs.Add(new RequirementProgress(
                    label, outcome.Required, outcome.Matched, outcome.Satisfied,
                    req.Groups, req.Users));
                totalRequired += outcome.Required;
                totalApproved += outcome.Matched;
                if (!outcome.Satisfied) stepSatisfied = false;
            }

            var stepName = string.IsNullOrEmpty(step.Name) ? "Approval" : step.Name;
            steps.Add(new StepProgress(stepName, stepSatisfied, reqs));
        }

        return new ApprovalProgress(
            RequiresApproval: true,
            // Overall is met only when the human sign-offs AND the work item gate (if any) are satisfied.
            AllSatisfied: match.AllSatisfied && (workItems?.Satisfied ?? true),
            TotalRequired: totalRequired,
            TotalApproved: totalApproved,
            Steps: steps,
            WorkItems: workItems);
    }

    /// <summary>
    /// Computes the "all work items resolved" gate condition for a candidate, or <c>null</c> when the
    /// policy doesn't gate on work items (neither <see cref="ResolvedPolicySnapshot.RequireAllWorkItemsApproved"/>
    /// nor <see cref="ResolvedPolicySnapshot.AutoApproveOnAllWorkItemsApproved"/>) or the candidate carries no
    /// work items. A work item counts as resolved when it has an Approved <see cref="WorkItemApproval"/> and no
    /// Rejected one.
    /// </summary>
    private async Task<WorkItemGateProgress?> GetWorkItemGateAsync(
        PromotionCandidate candidate, ResolvedPolicySnapshot snapshot, CancellationToken ct)
    {
        var gatesOnWorkItems = snapshot.RequireAllWorkItemsApproved
            || snapshot.AutoApproveOnAllWorkItemsApproved;
        if (!gatesOnWorkItems) return null;

        // Whether resolving all work items auto-promotes the candidate (no human sign-off needed):
        // the explicit AutoApproveOnAllWorkItemsApproved flag promotes from work-item approvals alone.
        var autoApprove = snapshot.AutoApproveOnAllWorkItemsApproved;

        var workItemKeys = await _db.PromotionWorkItems.AsNoTracking()
            .Where(w => w.CandidateId == candidate.Id)
            .Select(w => w.WorkItemKey)
            .Distinct()
            .ToListAsync(ct);
        if (workItemKeys.Count == 0) return null; // nothing to gate on

        var approvals = await _db.WorkItemApprovals.AsNoTracking()
            .Where(a => workItemKeys.Contains(a.WorkItemKey)
                     && a.Product == candidate.Product
                     && a.TargetEnv == candidate.TargetEnv)
            .ToListAsync(ct);

        var approved = 0;
        foreach (var key in workItemKeys)
        {
            var rows = approvals.Where(a => a.WorkItemKey == key).ToList();
            if (rows.Any(a => a.Decision == PromotionDecision.Rejected)) continue;
            if (rows.Any(a => a.Decision == PromotionDecision.Approved)) approved++;
        }

        return new WorkItemGateProgress(
            Required: true, Total: workItemKeys.Count, Approved: approved,
            Satisfied: approved == workItemKeys.Count, AutoApprove: autoApprove);
    }

    /// <summary>
    /// The set of OPEN requirements the current user may approve as: those they
    /// <see cref="PromotionApprovalAuthorizer.IsAuthorizedForRequirementAsync"/> AND that are not yet
    /// satisfied by the live matcher outcome. Returns an empty list when the candidate isn't Pending,
    /// is auto-approve, the user has already decided, or the user is eligible for none.
    ///
    /// <para>When the user is eligible for more than one open requirement, all of them are returned so
    /// the caller (endpoint / UI) can prompt the approver to choose which one they approve as.</para>
    /// </summary>
    public async Task<IReadOnlyList<RequirementRef>> GetEligibleRequirementsAsync(
        PromotionCandidate candidate, CancellationToken ct = default)
    {
        if (candidate.Status != PromotionStatus.Pending) return Array.Empty<RequirementRef>();

        var snapshot = ReadSnapshot(candidate);
        if (snapshot.IsAutoApprove || snapshot.AllRequirements.Count == 0) return Array.Empty<RequirementRef>();

        // Already decided? Nothing further to offer.
        var already = await _db.PromotionApprovals.AsNoTracking()
            .AnyAsync(a => a.CandidateId == candidate.Id && a.ApproverEmail == _currentUser.Email, ct);
        if (already) return Array.Empty<RequirementRef>();

        // Which requirements are still OPEN (not yet satisfied) per the live matcher.
        var match = await EvaluateRequirementMatchAsync(candidate, snapshot.AllRequirements, ct);
        var openByIndex = new bool[snapshot.AllRequirements.Count];
        for (var i = 0; i < match.Requirements.Count; i++) openByIndex[i] = !match.Requirements[i].Satisfied;

        // Walk steps in flatten order, offering each (step, requirement) the user is authorized for
        // and that is still open.
        var result = new List<RequirementRef>();
        var cursor = 0;
        foreach (var step in snapshot.ApprovalSteps)
        {
            foreach (var req in step.Requirements)
            {
                var idx = cursor++;
                if (!openByIndex[idx]) continue;
                if (await _auth.IsAuthorizedForRequirementAsync(req, _currentUser.Email, ct))
                    result.Add(new RequirementRef(step.Name ?? "", req.Name ?? ""));
            }
        }

        return result;
    }

    private async Task<bool> CandidateHasWorkItemsAsync(PromotionCandidate candidate, CancellationToken ct)
    {
        return await _db.PromotionWorkItems.AsNoTracking()
            .AnyAsync(w => w.CandidateId == candidate.Id, ct);
    }

    /// <summary>
    /// Returns <c>true</c> when every distinct work-item key on the candidate has at least one
    /// <see cref="PromotionDecision.Approved"/> <see cref="WorkItemApproval"/> row and zero
    /// <see cref="PromotionDecision.Rejected"/> rows. Returns <c>true</c> vacuously when the
    /// candidate has no work items — callers should guard with <see cref="CandidateHasWorkItemsAsync"/>
    /// first when they want "no work items" to be treated differently.
    /// </summary>
    private async Task<bool> AreAllWorkItemsApprovedAsync(PromotionCandidate candidate, CancellationToken ct)
    {
        var workItemKeys = await _db.PromotionWorkItems.AsNoTracking()
            .Where(w => w.CandidateId == candidate.Id)
            .Select(w => w.WorkItemKey)
            .Distinct()
            .ToListAsync(ct);

        if (workItemKeys.Count == 0) return true;

        var approvals = await _db.WorkItemApprovals.AsNoTracking()
            .Where(a => workItemKeys.Contains(a.WorkItemKey)
                     && a.Product == candidate.Product
                     && a.TargetEnv == candidate.TargetEnv)
            .ToListAsync(ct);

        foreach (var key in workItemKeys)
        {
            var rows = approvals.Where(a => a.WorkItemKey == key).ToList();
            if (rows.Any(a => a.Decision == PromotionDecision.Rejected)) return false;
            if (!rows.Any(a => a.Decision == PromotionDecision.Approved)) return false;
        }

        return true;
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
            // The candidate is self-contained: its References are the authoritative net change set,
            // so the webhook reads them directly rather than re-aggregating from deploy events.
            var payload = new
            {
                candidateId = candidate.Id,
                candidate.Product,
                candidate.Service,
                candidate.SourceEnv,
                candidate.TargetEnv,
                candidate.Version,
                candidate.FromRevision,
                candidate.ToRevision,
                status = candidate.Status.ToString(),
                candidate.ApprovedAt,
                // Promotion-level participants (manually assigned QA/reviewer etc.)
                participants = candidate.Participants,
                // The candidate's own net change set (work items / PRs / repository refs).
                references = candidate.References,
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

        // Already decided? Can't approve again.
        var already = await _db.PromotionApprovals.AsNoTracking()
            .AnyAsync(a => a.CandidateId == candidate.Id && a.ApproverEmail == _currentUser.Email, ct);
        if (already) return false;

        // Authorized for some requirement of this candidate's rule tree.
        return await _auth.IsAuthorizedForAnyRequirementAsync(snapshot, _currentUser.Email, ct);
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

        // Cache group membership lookups: one Graph call per unique approver group across all
        // candidates' requirement trees. The current user matches a requirement when they're in any
        // of its groups OR listed in its users — so a candidate is approvable when ≥1 requirement
        // matches.
        var groupMembership = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var email = _currentUser.Email;

        foreach (var c in list)
        {
            if (c.Status != PromotionStatus.Pending) { result[c.Id] = false; continue; }
            var snapshot = ReadSnapshot(c);
            if (snapshot.IsAutoApprove) { result[c.Id] = false; continue; }
            if (decidedSet.Contains(c.Id)) { result[c.Id] = false; continue; }

            var canApprove = false;
            foreach (var req in snapshot.AllRequirements)
            {
                // User-list match is free; check it first.
                if (req.Users.Any(u => string.Equals(u, email, StringComparison.OrdinalIgnoreCase)))
                {
                    canApprove = true;
                    break;
                }
                foreach (var group in req.Groups)
                {
                    if (!groupMembership.TryGetValue(group.Id, out var member))
                    {
                        member = await _auth.IsInApproverGroupAsync(group, ct);
                        groupMembership[group.Id] = member;
                    }
                    if (member) { canApprove = true; break; }
                }
                if (canApprove) break;
            }
            result[c.Id] = canApprove;
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

        // A user may approve if they're authorized for at least one (still-relevant) requirement.
        // We don't pre-assign them to a specific requirement here — recording the row is enough; the
        // matcher attributes it at evaluation time (most-constrained first). Separation-of-duties
        // (ExcludeRole) was removed (D17): anyone authorized for the promotion may approve it.
        if (!await _auth.IsAuthorizedForAnyRequirementAsync(snapshot, _currentUser.Email, ct))
            throw new UnauthorizedAccessException("You are not authorized to approve this promotion");
    }

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

/// <summary>
/// Structured approval progress for the detail view, produced by
/// <see cref="PromotionService.GetApprovalProgressAsync"/>. Unlike <see cref="GateResult"/>'s
/// human-readable blockers, this is a machine-consumable shape the UI renders as a progress panel.
/// <see cref="RequiresApproval"/> is false for auto-approve candidates (panel hidden); the counts
/// and per-step breakdown reflect the live matcher outcome.
/// </summary>
public record ApprovalProgress(
    bool RequiresApproval,
    bool AllSatisfied,
    int TotalRequired,
    int TotalApproved,
    IReadOnlyList<StepProgress> Steps,
    // The work item-resolution gate (policy's "all work items must be resolved" condition), when the
    // policy gates on it and the candidate has work items. Null otherwise. Surfaced so the approver can
    // see whether that condition is fulfilled, not just the human sign-offs.
    WorkItemGateProgress? WorkItems = null);

/// <summary>
/// Progress of the "all work items resolved/approved" gate condition for a candidate:
/// <paramref name="Approved"/> of <paramref name="Total"/> distinct work items signed off.
/// </summary>
public record WorkItemGateProgress(bool Required, int Total, int Approved, bool Satisfied, bool AutoApprove = false);

/// <summary>One approval step's progress: satisfied once all its requirements are.</summary>
public record StepProgress(string Name, bool Satisfied, IReadOnlyList<RequirementProgress> Requirements);

/// <summary>One requirement's progress: how many distinct eligible approvals are in vs. required.</summary>
public record RequirementProgress(
    string Name, int Required, int Approved, bool Satisfied,
    // Who can satisfy this requirement: the configured groups (id+name) plus any explicitly
    // listed user emails. Surfaced so a waiting approver can see "who do I chase for this".
    IReadOnlyList<GroupRef> Groups, IReadOnlyList<string> Users);

/// <summary>
/// Identifies a single requirement by its unique (step name, requirement name) pair — the attribution
/// recorded on a <see cref="PromotionApproval"/> and the choice an approver can pin when approving.
/// </summary>
public record RequirementRef(string StepName, string RequirementName);

/// <summary>
/// Thrown by <see cref="PromotionService.ApproveAsync"/> when the caller did not specify which
/// requirement they approve as but is eligible for more than one open requirement. Carries the
/// candidate list so the endpoint can surface the choices and the UI can prompt.
/// </summary>
public class MultipleEligibleRequirementsException : InvalidOperationException
{
    public IReadOnlyList<RequirementRef> Options { get; }

    public MultipleEligibleRequirementsException(IReadOnlyList<RequirementRef> options)
        : base("Multiple requirements available — specify which one you are approving as.")
    {
        Options = options;
    }
}

/// <summary>
/// Thrown by <see cref="PromotionService.ApproveAsync"/> when the caller pinned a requirement they
/// are authorized for but which is already satisfied (a 409-shaped condition).
/// </summary>
public class RequirementAlreadySatisfiedException : InvalidOperationException
{
    public RequirementAlreadySatisfiedException(string message) : base(message) { }
}

/// <summary>
/// Thrown by <see cref="PromotionService.CreateExternalCandidateAsync"/> when the promotion's
/// (product, service, source env, version) does not correspond to a succeeded deployment already
/// ingested — i.e. an attempt to promote a version that never shipped to the source env. Maps to a
/// 422 at the endpoint.
/// </summary>
public class SourceDeploymentNotFoundException : InvalidOperationException
{
    public SourceDeploymentNotFoundException(string product, string service, string sourceEnv, string version)
        : base($"No succeeded deployment of {version} found in {sourceEnv} for {product}/{service} — "
             + "cannot promote an unknown source.")
    {
    }
}
