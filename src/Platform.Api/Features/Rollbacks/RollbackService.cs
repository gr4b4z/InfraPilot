using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Platform.Api.Features.Deployments.Models;
using Platform.Api.Features.Promotions;
using Platform.Api.Features.Promotions.Models;
using Platform.Api.Features.Rollbacks.Models;
using Platform.Api.Features.Users;
using Platform.Api.Features.Webhooks;
using Platform.Api.Infrastructure;
using Platform.Api.Infrastructure.Audit;
using Platform.Api.Infrastructure.Auth;
using Platform.Api.Infrastructure.Features;
using Platform.Api.Infrastructure.Persistence;

namespace Platform.Api.Features.Rollbacks;

/// <summary>
/// Domain service for rollbacks — reverting one or more services in an environment to an earlier,
/// previously-deployed version. Rollback is the inverse of promotion and reuses the promotion
/// approval <i>machinery</i> (rule tree → distinct-person matching → gate), but is governed by its
/// own <see cref="RollbackPolicy"/> rather than by whichever promotion policy guards the target
/// environment. The differences are: an extra safety rule (target version must have run in the env
/// before), in-place (no topology), and a per-product creator allowlist.
///
/// <para>Authorization has three parts:</para>
/// <list type="number">
///   <item><b>Create</b> — the resolved policy's <see cref="RollbackPolicy.Creators"/> set, or admin.
///     No policy for the environment ⇒ admins only (see <see cref="CanCreateAsync"/>).</item>
///   <item><b>Approve</b> — the policy's approval tree, matched <b>without</b> the implicit
///     "admins are in every group" shortcut promotions use, so an admin clearing a rollback gate is
///     always a visible override rather than an ordinary-looking approval.</item>
///   <item><b>Override</b> — an admin-only, reason-carrying bypass
///     (<see cref="OverrideApprovalAsync"/>).</item>
/// </list>
///
/// <para>Completion is detected from the deploy event the operator/executor emits when the target
/// version lands — there is no trusted callback (see <see cref="MatchCompletionAsync"/>).</para>
/// </summary>
public class RollbackService
{
    /// <summary>
    /// The retired per-product enrollment setting. Enrollment is now the existence of a
    /// <see cref="RollbackPolicy"/> row; this key is kept only so
    /// <see cref="RollbackPolicySeeder"/> can find and migrate an existing install's value.
    /// </summary>
    public const string EnabledProductsKey = "rollback.enabledProducts";

    private readonly PlatformDbContext _db;
    private readonly RollbackPolicyResolver _policies;
    private readonly PromotionApprovalAuthorizer _auth;
    private readonly ICurrentUser _user;
    private readonly IAuditLogger _audit;
    private readonly IWebhookDispatcher _webhooks;
    private readonly IFeatureFlags _flags;
    private readonly UserPreferencesService _userPrefs;
    private readonly ILogger<RollbackService> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public RollbackService(
        PlatformDbContext db,
        RollbackPolicyResolver policies,
        PromotionApprovalAuthorizer auth,
        ICurrentUser user,
        IAuditLogger audit,
        IWebhookDispatcher webhooks,
        IFeatureFlags flags,
        UserPreferencesService userPrefs,
        ILogger<RollbackService> logger)
    {
        _db = db;
        _policies = policies;
        _auth = auth;
        _user = user;
        _audit = audit;
        _webhooks = webhooks;
        _flags = flags;
        _userPrefs = userPrefs;
        _logger = logger;
    }

    // ---------------------------------------------------------------------
    // Enrollment + create permission (on top of the global features.rollbacks flag)
    // ---------------------------------------------------------------------

    /// <summary>
    /// Products with at least one <see cref="RollbackPolicy"/> — i.e. configured for rollbacks. This
    /// is enrollment: it replaced the <c>rollback.enabledProducts</c> setting, so a product is enrolled
    /// exactly when someone has said who may create and who must approve.
    /// </summary>
    public async Task<List<string>> GetEnabledProductsAsync(CancellationToken ct = default)
        => await _db.RollbackPolicies.AsNoTracking()
            .Select(p => p.Product)
            .Distinct()
            .OrderBy(p => p)
            .ToListAsync(ct);

    /// <summary>Whether any policy covers this product (the environment-agnostic enrollment probe).</summary>
    public async Task<bool> IsProductEnabledAsync(string product, CancellationToken ct = default)
    {
        if (!await _flags.IsEnabled(FeatureFlagKeys.Rollbacks, ct)) return false;
        return await _db.RollbackPolicies.AsNoTracking().AnyAsync(p => p.Product == product, ct);
    }

    /// <summary>
    /// Products the current user may raise a rollback for — what the create picker offers. Admins get
    /// every product with deploy history (they can roll back an unconfigured product, subject to
    /// overriding the gate, so restricting their picker to configured products would hide the very
    /// case the override exists for). Everyone else gets the products whose policies name them as a
    /// creator.
    /// </summary>
    public async Task<List<string>> GetCreatableProductsAsync(CancellationToken ct = default)
    {
        if (!await _flags.IsEnabled(FeatureFlagKeys.Rollbacks, ct)) return new();

        if (_user.IsAdmin)
        {
            var known = await _db.DeployEvents.AsNoTracking().Select(e => e.Product).Distinct().ToListAsync(ct);
            var configured = await GetEnabledProductsAsync(ct);
            return known.Concat(configured).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(p => p).ToList();
        }

        var policies = await _db.RollbackPolicies.AsNoTracking().ToListAsync(ct);
        var allowed = new List<string>();
        foreach (var group in policies.GroupBy(p => p.Product, StringComparer.OrdinalIgnoreCase))
        {
            foreach (var policy in group)
            {
                if (await IsCreatorAsync(policy, ct)) { allowed.Add(group.Key); break; }
            }
        }
        return allowed.OrderBy(p => p).ToList();
    }

    /// <summary>
    /// Whether the current user may create a rollback for (<paramref name="product"/>,
    /// <paramref name="targetEnv"/>), and if not, why not — the message is surfaced verbatim, so it
    /// has to explain the fix.
    ///
    /// <para>Admins always may. Otherwise a policy must cover the environment and must name the user
    /// in its creator set; an unconfigured environment is closed rather than open, so enabling the
    /// feature flag can never by itself expose production to arbitrary authenticated callers (which is
    /// what the previous "any authenticated user" create path did).</para>
    /// </summary>
    public async Task<(bool Allowed, string? Reason)> CanCreateAsync(
        string product, string targetEnv, CancellationToken ct = default)
    {
        if (!await _flags.IsEnabled(FeatureFlagKeys.Rollbacks, ct))
            return (false, "Rollbacks are not enabled on this platform");

        if (_user.IsAdmin) return (true, null);

        var policy = await _policies.ResolveAsync(product, targetEnv, ct);
        if (policy is null)
            return (false, $"Rollbacks are not configured for '{product}' in {targetEnv} — " +
                           "an admin must add a rollback policy for this environment");

        if (!await IsCreatorAsync(policy, ct))
            return (false, $"You are not allowed to create rollbacks for '{product}' in {targetEnv}");

        return (true, null);
    }

    // Creator membership for one policy. The admin shortcut is switched off here because admins are
    // handled explicitly by the callers — leaving it on would make every policy's creator set look
    // like it contained the admin, which the settings UI would then misreport.
    private async Task<bool> IsCreatorAsync(RollbackPolicy policy, CancellationToken ct)
    {
        var creators = policy.Creators;
        if (creators.IsEmpty) return false; // empty grants nobody, never everybody
        return await _auth.IsInPrincipalSetAsync(
            creators.Groups, creators.Users, _user.Email, ct, allowAdminShortcut: false);
    }

    // ---------------------------------------------------------------------
    // Item resolution (manual + align) with the "version must have run here" safety rule
    // ---------------------------------------------------------------------

    /// <summary>
    /// Resolves the concrete (service, from→to) items for a request, marking each eligible or
    /// skipped with a reason. Used by both create (eligible-only are persisted) and the dry-run
    /// preview the UI shows before submitting a bulk "align".
    /// </summary>
    public async Task<List<ResolvedRollbackItem>> ResolveItemsAsync(
        CreateRollbackRequestDto dto, RollbackMode mode, CancellationToken ct = default)
    {
        var result = new List<ResolvedRollbackItem>();

        if (mode == RollbackMode.Manual)
        {
            foreach (var input in dto.Items ?? new())
            {
                var current = await CurrentVersionAsync(dto.Product, input.Service, dto.TargetEnv, ct);
                var history = await VersionHistoryAsync(dto.Product, input.Service, dto.TargetEnv, ct);
                var (eligible, reason) = EvaluateTarget(input.ToVersion, current, history);
                result.Add(new ResolvedRollbackItem(input.Service, current ?? "", input.ToVersion, eligible, reason));
            }
            return result;
        }

        // Align: derive items from the diff between the target env and a reference env.
        if (string.IsNullOrWhiteSpace(dto.ReferenceEnv))
            throw new InvalidOperationException("'referenceEnv' is required for align mode");

        var exclude = new HashSet<string>(dto.Exclude ?? new(), StringComparer.OrdinalIgnoreCase);
        var services = await ServicesInEnvAsync(dto.Product, dto.TargetEnv, ct);

        foreach (var service in services)
        {
            var current = await CurrentVersionAsync(dto.Product, service, dto.TargetEnv, ct);
            var refVersion = await CurrentVersionAsync(dto.Product, service, dto.ReferenceEnv, ct);

            if (exclude.Contains(service))
            {
                result.Add(new ResolvedRollbackItem(service, current ?? "", refVersion ?? "", false, "excluded"));
                continue;
            }
            if (refVersion is null)
            {
                result.Add(new ResolvedRollbackItem(service, current ?? "", "", false, $"not present in {dto.ReferenceEnv}"));
                continue;
            }
            if (string.Equals(refVersion, current, StringComparison.OrdinalIgnoreCase))
            {
                result.Add(new ResolvedRollbackItem(service, current ?? "", refVersion, false, "already matching"));
                continue;
            }
            var history = await VersionHistoryAsync(dto.Product, service, dto.TargetEnv, ct);
            var (eligible, reason) = EvaluateTarget(refVersion, current, history);
            result.Add(new ResolvedRollbackItem(service, current ?? "", refVersion, eligible, reason));
        }

        return result;
    }

    // The safety rule: target must be a version that previously ran in this env, and not equal to
    // what's already running (that would be a no-op).
    private static (bool eligible, string? reason) EvaluateTarget(string toVersion, string? current, HashSet<string> history)
    {
        if (string.IsNullOrWhiteSpace(toVersion)) return (false, "no target version");
        if (string.Equals(toVersion, current, StringComparison.OrdinalIgnoreCase)) return (false, "already running this version");
        if (!history.Contains(toVersion)) return (false, "version never ran in this environment");
        return (true, null);
    }

    /// <summary>
    /// Dry run behind the same create permission as the real thing. The preview is read-only but it
    /// reports each service's current and candidate version across two environments, so leaving it open
    /// to callers who cannot create would just relocate the disclosure.
    /// </summary>
    public async Task<RollbackPreview> PreviewAsync(CreateRollbackRequestDto dto, CancellationToken ct = default)
    {
        var (allowed, reason) = await CanCreateAsync(dto.Product, dto.TargetEnv, ct);
        if (!allowed) throw new UnauthorizedAccessException(reason!);

        var mode = ParseMode(dto.Mode);
        var items = await ResolveItemsAsync(dto, mode, ct);
        return new RollbackPreview(dto.Product, dto.TargetEnv, mode.ToString(), dto.ReferenceEnv, items);
    }

    // ---------------------------------------------------------------------
    // Create
    // ---------------------------------------------------------------------

    public async Task<RollbackRequest> CreateAsync(CreateRollbackRequestDto dto, CancellationToken ct = default)
    {
        var (allowed, reason) = await CanCreateAsync(dto.Product, dto.TargetEnv, ct);
        if (!allowed) throw new UnauthorizedAccessException(reason!);

        var mode = ParseMode(dto.Mode);
        var resolved = await ResolveItemsAsync(dto, mode, ct);
        var eligible = resolved.Where(r => r.Eligible).ToList();
        if (eligible.Count == 0)
            throw new InvalidOperationException("No eligible services to roll back");

        // One request-level gate resolved from the (product, target env) rollback policy:
        // env-specific row → product-default row → none. Per-service policy divergence within one
        // request is deliberately not modelled — a rollback is one decision about one environment.
        var snapshot = await _policies.SnapshotAsync(dto.Product, dto.TargetEnv, ct);

        // Three outcomes, and only the middle one skips a human. An unconfigured environment
        // (PolicyId null) must NOT auto-approve just because it has no requirements to satisfy —
        // it lands Pending and waits for an admin override. Conflating the two is exactly how the
        // previous implementation let a product with no promotion policy revert prod ungated.
        var autoApprove = snapshot.PolicyId is not null && snapshot.IsAutoApprove;

        var now = DateTimeOffset.UtcNow;
        var request = new RollbackRequest
        {
            Id = Guid.NewGuid(),
            Product = dto.Product,
            TargetEnv = dto.TargetEnv,
            Mode = mode,
            ReferenceEnv = mode == RollbackMode.Align ? dto.ReferenceEnv : null,
            Exclusions = dto.Exclude ?? new(),
            Reason = dto.Reason,
            Status = autoApprove ? RollbackStatus.Approved : RollbackStatus.Pending,
            PolicyId = snapshot.PolicyId,
            ResolvedPolicyJson = JsonSerializer.Serialize(snapshot, JsonOptions),
            CreatedBy = _user.Email,
            CreatedByName = _user.Name,
            CreatedAt = now,
            ApprovedAt = autoApprove ? now : null,
            Items = eligible.Select(r => new RollbackItem
            {
                Id = Guid.NewGuid(),
                Service = r.Service,
                FromVersion = r.FromVersion,
                ToVersion = r.ToVersion,
                Status = RollbackItemStatus.Pending,
                CreatedAt = now,
            }).ToList(),
        };
        _db.RollbackRequests.Add(request);

        if (autoApprove)
            _db.RollbackApprovals.Add(new RollbackApproval
            {
                Id = Guid.NewGuid(),
                RequestId = request.Id,
                ApproverEmail = "system",
                ApproverName = "System (auto-approve)",
                Decision = PromotionDecision.Approved,
                CreatedAt = now,
            });

        await _db.SaveChangesAsync(ct);

        await _audit.Log("rollbacks", "rollback.request.created",
            _user.Id, _user.Name, "user", "RollbackRequest", request.Id, null,
            new
            {
                request.Product, request.TargetEnv, mode = mode.ToString(),
                itemCount = request.Items.Count, autoApprove,
                policyId = snapshot.PolicyId,
                // Records that this request was raised against an unconfigured environment, so the
                // override it will need is traceable back to the gap rather than looking arbitrary.
                unconfigured = snapshot.PolicyId is null,
            });

        _logger.LogInformation("Created rollback request {Id} for {Product}/{Env} ({Count} items, {Status})",
            request.Id, LogSanitizer.Clean(request.Product), LogSanitizer.Clean(request.TargetEnv), request.Items.Count, request.Status);

        if (request.Status == RollbackStatus.Approved)
            await DispatchWebhookAsync(request, "rollback.approved", ct);

        return request;
    }

    // ---------------------------------------------------------------------
    // Approval / rejection / override / cancel
    // ---------------------------------------------------------------------

    /// <summary>
    /// Records the current user's approval, then re-evaluates the gate. Membership is checked
    /// <b>without</b> the admin shortcut: an admin who is not genuinely named by a requirement must use
    /// <see cref="OverrideApprovalAsync"/>, so bypasses stay legible in the history.
    /// </summary>
    public async Task<RollbackRequest> ApproveAsync(Guid id, string? comment, CancellationToken ct = default)
    {
        var request = await LoadPendingAsync(id, ct);
        var snapshot = ReadSnapshot(request);
        EnsureGateIsApprovable(snapshot);
        if (!await _auth.IsAuthorizedForAnyRequirementAsync(snapshot, _user.Email, ct, allowAdminShortcut: false))
            throw new UnauthorizedAccessException("You are not authorized to approve this rollback");
        if (await _db.RollbackApprovals.AnyAsync(a => a.RequestId == id && a.ApproverEmail == _user.Email, ct))
            throw new InvalidOperationException("You have already made a decision on this rollback");

        _db.RollbackApprovals.Add(new RollbackApproval
        {
            Id = Guid.NewGuid(),
            RequestId = id,
            ApproverEmail = _user.Email,
            ApproverName = _user.Name,
            Comment = comment,
            Decision = PromotionDecision.Approved,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await _db.SaveChangesAsync(ct);

        await _audit.Log("rollbacks", "rollback.approval.recorded",
            _user.Id, _user.Name, "user", "RollbackRequest", id, null, new { comment });

        return await ReevaluateAsync(id, ct);
    }

    public async Task<RollbackRequest> ReevaluateAsync(Guid id, CancellationToken ct = default)
    {
        var request = await _db.RollbackRequests.FirstOrDefaultAsync(r => r.Id == id, ct)
            ?? throw new KeyNotFoundException($"Rollback request {id} not found");
        if (request.Status != RollbackStatus.Pending) return request;

        var snapshot = ReadSnapshot(request);
        var requirements = snapshot.AllRequirements;
        if (requirements.Count == 0) return request; // auto-approve never reaches Pending here

        // Override rows are excluded: an override forces the status directly and is not a claim that
        // some requirement was met, so counting it here would let one admin's bypass masquerade as
        // progress toward an N-of-M gate.
        var approverEmails = await _db.RollbackApprovals.AsNoTracking()
            .Where(a => a.RequestId == id && a.Decision == PromotionDecision.Approved && !a.IsOverride)
            .Select(a => a.ApproverEmail)
            .ToListAsync(ct);
        var distinct = approverEmails
            .Where(e => !string.IsNullOrEmpty(e))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Same distinct-person, per-requirement matching as promotions. We can't resolve recorded
        // approvers' live group membership, but each was authorized for some requirement at record
        // time — so an approver matches a requirement if listed explicitly OR the requirement carries
        // groups (membership can't be disproven). Preserves legacy single-group NOfM counting while
        // honouring user-only requirements.
        var match = ApprovalMatcher.Match(requirements, distinct, (email, req) =>
            req.Users.Any(u => string.Equals(u, email, StringComparison.OrdinalIgnoreCase))
            || req.Groups.Count > 0);
        if (!match.AllSatisfied) return request;

        request.Status = RollbackStatus.Approved;
        request.ApprovedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);

        await _audit.Log("rollbacks", "rollback.approved",
            "system", "System (gate satisfied)", "system", "RollbackRequest", id, null,
            new { approvedCount = distinct.Count, requirements = requirements.Count });
        _logger.LogInformation("Rollback request {Id} → Approved", id);

        await DispatchWebhookAsync(request, "rollback.approved", ct);
        return request;
    }

    /// <summary>
    /// Records a rejection, which is terminal. Admins may reject regardless of the approver tree —
    /// unlike approving, refusing a change needs no bypass ceremony, and an unconfigured request has
    /// no approvers at all, so somebody has to be able to close it out.
    /// </summary>
    public async Task<RollbackRequest> RejectAsync(Guid id, string? comment, CancellationToken ct = default)
    {
        var request = await LoadPendingAsync(id, ct);
        var snapshot = ReadSnapshot(request);
        if (!_user.IsAdmin
            && !await _auth.IsAuthorizedForAnyRequirementAsync(snapshot, _user.Email, ct, allowAdminShortcut: false))
            throw new UnauthorizedAccessException("You are not authorized to decide on this rollback");

        _db.RollbackApprovals.Add(new RollbackApproval
        {
            Id = Guid.NewGuid(),
            RequestId = id,
            ApproverEmail = _user.Email,
            ApproverName = _user.Name,
            Comment = comment,
            Decision = PromotionDecision.Rejected,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        request.Status = RollbackStatus.Rejected;
        await _db.SaveChangesAsync(ct);

        await _audit.Log("rollbacks", "rollback.rejected",
            _user.Id, _user.Name, "user", "RollbackRequest", id, null, new { comment });
        await DispatchWebhookAsync(request, "rollback.rejected", ct);
        return request;
    }

    /// <summary>
    /// Admin escape hatch: forces a Pending request past its approval gate without satisfying it.
    /// A non-empty <paramref name="reason"/> is required and is stored on the flagged approval row.
    ///
    /// <para>This is the <i>only</i> way an admin clears a rollback gate they are not genuinely named
    /// in — <see cref="ApproveAsync"/> deliberately declines to treat them as a member of every group.
    /// It is also the only way an unconfigured environment's rollback can proceed, which is what makes
    /// "locked down until configured" workable rather than a dead end during an incident.</para>
    ///
    /// <para>The normal <c>rollback.approved</c> webhook still fires, so the executor is unchanged.</para>
    /// </summary>
    public async Task<RollbackRequest> OverrideApprovalAsync(Guid id, string reason, CancellationToken ct = default)
    {
        if (!_user.IsAdmin)
            throw new UnauthorizedAccessException("Only an admin can override the rollback approval gate");
        if (string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException("A reason is required to override the approval gate", nameof(reason));

        var request = await LoadPendingAsync(id, ct);
        var snapshot = ReadSnapshot(request);
        // Nothing to override on an explicitly ungated scope — it never reaches Pending, but a caller
        // racing a policy edit could still get here.
        if (snapshot.PolicyId is not null && snapshot.IsAutoApprove)
            throw new InvalidOperationException("This rollback does not require approval");

        var now = DateTimeOffset.UtcNow;
        var trimmed = reason.Trim();

        // Reuse the approver row rather than adding a parallel table: the decision history stays in one
        // place and the (RequestId, ApproverEmail) unique index keeps a double-override honest. An admin
        // who already recorded a normal decision cannot then override — they are no longer neutral.
        if (await _db.RollbackApprovals.AnyAsync(a => a.RequestId == id && a.ApproverEmail == _user.Email, ct))
            throw new InvalidOperationException("You have already made a decision on this rollback");

        _db.RollbackApprovals.Add(new RollbackApproval
        {
            Id = Guid.NewGuid(),
            RequestId = id,
            ApproverEmail = _user.Email,
            ApproverName = _user.Name,
            Comment = trimmed,
            Decision = PromotionDecision.Approved,
            IsOverride = true,
            CreatedAt = now,
        });

        request.Status = RollbackStatus.Approved;
        request.ApprovedAt = now;
        request.ApprovalOverridden = true;
        await _db.SaveChangesAsync(ct);

        await _audit.Log("rollbacks", "rollback.approval.overridden",
            _user.Id, _user.Name, "user", "RollbackRequest", id, null,
            new
            {
                reason = trimmed,
                request.Product,
                request.TargetEnv,
                policyId = snapshot.PolicyId,
                unconfigured = snapshot.PolicyId is null,
                bypassedRequirements = snapshot.AllRequirements.Count,
            });

        _logger.LogWarning("Rollback request {Id} approval gate overridden by {Actor} ({Requirements} requirements bypassed)",
            id, LogSanitizer.Clean(_user.Email), snapshot.AllRequirements.Count);

        await DispatchWebhookAsync(request, "rollback.approved", ct);
        return request;
    }

    public async Task<RollbackRequest> CancelAsync(Guid id, CancellationToken ct = default)
    {
        var request = await _db.RollbackRequests.FirstOrDefaultAsync(r => r.Id == id, ct)
            ?? throw new KeyNotFoundException($"Rollback request {id} not found");
        // Only Pending requests can be cancelled. Once Approved, the rollback.approved webhook has
        // already fired and the executor may be acting — cancelling here would change our record
        // without stopping reality, which is worse than not offering it.
        if (request.Status != RollbackStatus.Pending)
            throw new InvalidOperationException(
                $"Rollback request is {request.Status}; only Pending requests can be cancelled " +
                "(an approved rollback has already been dispatched).");
        if (!string.Equals(request.CreatedBy, _user.Email, StringComparison.OrdinalIgnoreCase) && !_user.IsAdmin)
            throw new UnauthorizedAccessException("Only the creator or an admin can cancel this rollback");

        request.Status = RollbackStatus.Cancelled;
        await _db.SaveChangesAsync(ct);
        await _audit.Log("rollbacks", "rollback.cancelled",
            _user.Id, _user.Name, "user", "RollbackRequest", id, null, null);
        await DispatchWebhookAsync(request, "rollback.cancelled", ct);
        return request;
    }

    // ---------------------------------------------------------------------
    // Completion matching — the deploy event closes the loop (no trusted callback)
    // ---------------------------------------------------------------------

    /// <summary>
    /// Called from the deployment ingest hook for every event. If the event matches an open rollback
    /// item — same (product, service, env), version == the item's target, and after the request was
    /// approved — marks the item RolledBack and, when all items are terminal, the request RolledBack.
    /// Returns <c>true</c> if any item matched, so the caller can suppress forward-promotion of this
    /// (older) version. The <c>IsRollback</c> flag is treated as corroboration, not a requirement —
    /// a human-triggered rollback often won't set it.
    /// </summary>
    public async Task<bool> MatchCompletionAsync(DeployEvent landing, CancellationToken ct = default)
    {
        var openRequests = await _db.RollbackRequests
            .Where(r => r.Product == landing.Product
                     && r.TargetEnv == landing.Environment
                     && (r.Status == RollbackStatus.Approved || r.Status == RollbackStatus.RollingBack))
            .ToListAsync(ct);
        if (openRequests.Count == 0) return false;

        var requestIds = openRequests.Select(r => r.Id).ToList();
        var approvedAtById = openRequests.ToDictionary(r => r.Id, r => r.ApprovedAt);

        var items = await _db.RollbackItems
            .Where(i => requestIds.Contains(i.RequestId)
                     && i.Service == landing.Service
                     && i.ToVersion == landing.Version
                     && (i.Status == RollbackItemStatus.Pending || i.Status == RollbackItemStatus.RollingBack))
            .ToListAsync(ct);
        // Only count events that landed after the relevant request was approved.
        items = items.Where(i => approvedAtById.GetValueOrDefault(i.RequestId) is { } a && landing.DeployedAt >= a).ToList();
        if (items.Count == 0) return false;

        var now = DateTimeOffset.UtcNow;
        var touchedRequestIds = new HashSet<Guid>();
        foreach (var item in items)
        {
            item.Status = RollbackItemStatus.RolledBack;
            item.CompletedDeployEventId = landing.Id;
            item.CompletedAt = now;
            touchedRequestIds.Add(item.RequestId);
        }
        await _db.SaveChangesAsync(ct);

        // Flip a request to RollingBack/RolledBack based on its items' aggregate state.
        foreach (var reqId in touchedRequestIds)
        {
            var req = openRequests.First(r => r.Id == reqId);
            var allItems = await _db.RollbackItems.Where(i => i.RequestId == reqId).ToListAsync(ct);
            var allTerminal = allItems.All(i => i.Status is RollbackItemStatus.RolledBack
                or RollbackItemStatus.Failed or RollbackItemStatus.Skipped);
            if (allTerminal)
            {
                req.Status = RollbackStatus.RolledBack;
                req.CompletedAt = now;
            }
            else if (req.Status == RollbackStatus.Approved)
            {
                req.Status = RollbackStatus.RollingBack;
            }
            await _db.SaveChangesAsync(ct);

            _logger.LogInformation("Rollback request {Id} → {Status} (item for {Service} landed)",
                reqId, req.Status, LogSanitizer.Clean(landing.Service));
            if (req.Status == RollbackStatus.RolledBack)
                await DispatchWebhookAsync(req, "rollback.deployed", ct);
        }

        return true;
    }

    // ---------------------------------------------------------------------
    // Queries
    // ---------------------------------------------------------------------

    public async Task<List<RollbackRequest>> GetAsync(RollbackQuery query, CancellationToken ct = default)
    {
        var q = _db.RollbackRequests.AsNoTracking().Include(r => r.Items).AsQueryable();

        // Before Take(limit) below, so a hidden product can't quietly shorten the page.
        var hidden = await _userPrefs.GetHiddenProductsAsync(ct);
        if (hidden.Count > 0) q = q.Where(r => !hidden.Contains(r.Product));

        if (query.Status is { } s) q = q.Where(r => r.Status == s);
        if (!string.IsNullOrEmpty(query.Product)) q = q.Where(r => r.Product == query.Product);
        if (!string.IsNullOrEmpty(query.TargetEnv)) q = q.Where(r => r.TargetEnv == query.TargetEnv);
        return await q.OrderByDescending(r => r.CreatedAt).Take(query.Limit).ToListAsync(ct);
    }

    public async Task<RollbackRequest?> GetByIdAsync(Guid id, CancellationToken ct = default)
        => await _db.RollbackRequests.AsNoTracking().Include(r => r.Items).FirstOrDefaultAsync(r => r.Id == id, ct);

    public async Task<List<RollbackApproval>> GetApprovalsAsync(Guid id, CancellationToken ct = default)
        => await _db.RollbackApprovals.AsNoTracking().Where(a => a.RequestId == id).OrderBy(a => a.CreatedAt).ToListAsync(ct);

    public async Task<bool> CanUserApproveAsync(RollbackRequest request, CancellationToken ct = default)
    {
        if (request.Status != RollbackStatus.Pending) return false;
        var snapshot = ReadSnapshot(request);
        if (snapshot.AllRequirements.Count == 0) return false;
        if (await _db.RollbackApprovals.AsNoTracking().AnyAsync(a => a.RequestId == request.Id && a.ApproverEmail == _user.Email, ct))
            return false;
        return await _auth.IsAuthorizedForAnyRequirementAsync(snapshot, _user.Email, ct, allowAdminShortcut: false);
    }

    /// <summary>
    /// Whether the current user may override this request's gate — admin, still Pending, actually
    /// gated, and hasn't already decided. Drives the UI affordance so the override button only appears
    /// where the call would succeed.
    /// </summary>
    public async Task<bool> CanUserOverrideAsync(RollbackRequest request, CancellationToken ct = default)
    {
        if (!_user.IsAdmin || request.Status != RollbackStatus.Pending) return false;
        var snapshot = ReadSnapshot(request);
        if (snapshot.PolicyId is not null && snapshot.IsAutoApprove) return false;
        return !await _db.RollbackApprovals.AsNoTracking()
            .AnyAsync(a => a.RequestId == request.Id && a.ApproverEmail == _user.Email, ct);
    }

    /// <summary>
    /// The gate as the UI should describe it: the requirement tree the request was snapshotted under,
    /// each requirement paired with how many distinct eligible approvals it has so far. Lets the detail
    /// view render "Platform leads 1/2" instead of an opaque Pending.
    /// </summary>
    public async Task<(bool Unconfigured, IReadOnlyList<RequirementOutcome> Requirements)> GetGateAsync(
        RollbackRequest request, CancellationToken ct = default)
    {
        var snapshot = ReadSnapshot(request);
        var requirements = snapshot.AllRequirements;
        if (requirements.Count == 0)
            return (snapshot.PolicyId is null, Array.Empty<RequirementOutcome>());

        var approvers = await _db.RollbackApprovals.AsNoTracking()
            .Where(a => a.RequestId == request.Id && a.Decision == PromotionDecision.Approved && !a.IsOverride)
            .Select(a => a.ApproverEmail)
            .ToListAsync(ct);
        var distinct = approvers
            .Where(e => !string.IsNullOrEmpty(e))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var match = ApprovalMatcher.Match(requirements, distinct, (email, req) =>
            req.Users.Any(u => string.Equals(u, email, StringComparison.OrdinalIgnoreCase))
            || req.Groups.Count > 0);
        return (false, match.Requirements);
    }

    // ---------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------

    private async Task<string?> CurrentVersionAsync(string product, string service, string env, CancellationToken ct)
        => await _db.DeployEvents.AsNoTracking()
            .Where(e => e.Product == product && e.Service == service && e.Environment == env)
            .OrderByDescending(e => e.DeployedAt)
            .Select(e => e.Version)
            .FirstOrDefaultAsync(ct);

    private async Task<HashSet<string>> VersionHistoryAsync(string product, string service, string env, CancellationToken ct)
    {
        var versions = await _db.DeployEvents.AsNoTracking()
            .Where(e => e.Product == product && e.Service == service && e.Environment == env)
            .Select(e => e.Version)
            .Distinct()
            .ToListAsync(ct);
        return new HashSet<string>(versions, StringComparer.OrdinalIgnoreCase);
    }

    private async Task<List<string>> ServicesInEnvAsync(string product, string env, CancellationToken ct)
        => await _db.DeployEvents.AsNoTracking()
            .Where(e => e.Product == product && e.Environment == env)
            .Select(e => e.Service)
            .Distinct()
            .OrderBy(s => s)
            .ToListAsync(ct);

    private static RollbackMode ParseMode(string mode)
        => Enum.TryParse<RollbackMode>(mode, ignoreCase: true, out var m)
            ? m
            : throw new InvalidOperationException($"Unknown rollback mode '{mode}' (expected 'manual' or 'align')");

    private async Task<RollbackRequest> LoadPendingAsync(Guid id, CancellationToken ct)
    {
        var request = await _db.RollbackRequests.FirstOrDefaultAsync(r => r.Id == id, ct)
            ?? throw new KeyNotFoundException($"Rollback request {id} not found");
        if (request.Status != RollbackStatus.Pending)
            throw new InvalidOperationException($"Rollback request {id} is {request.Status}, no longer accepting decisions");
        return request;
    }

    /// <summary>
    /// Rejects the two states where a normal approval is meaningless, with messages that name the fix.
    /// Both were previously collapsed into "does not require approval", which was actively misleading
    /// for the unconfigured case — the request very much did require a decision, just not one anybody
    /// was authorized to give.
    /// </summary>
    private static void EnsureGateIsApprovable(ResolvedPolicySnapshot snapshot)
    {
        if (snapshot.PolicyId is null)
            throw new InvalidOperationException(
                "No rollback policy governs this environment, so there are no approvers to satisfy — " +
                "an admin must override the approval gate, or add a policy and recreate the request");
        if (snapshot.IsAutoApprove)
            throw new InvalidOperationException("This rollback does not require approval");
    }

    private static ResolvedPolicySnapshot ReadSnapshot(RollbackRequest request)
    {
        if (string.IsNullOrEmpty(request.ResolvedPolicyJson))
            throw new InvalidOperationException($"Rollback request {request.Id} has no policy snapshot");
        return JsonSerializer.Deserialize<ResolvedPolicySnapshot>(request.ResolvedPolicyJson, JsonOptions)
            ?? throw new InvalidOperationException($"Failed to deserialize policy snapshot for rollback {request.Id}");
    }

    private async Task DispatchWebhookAsync(RollbackRequest request, string eventType, CancellationToken ct)
    {
        try
        {
            var items = await _db.RollbackItems.AsNoTracking()
                .Where(i => i.RequestId == request.Id)
                .Select(i => new { i.Service, i.FromVersion, i.ToVersion, status = i.Status.ToString() })
                .ToListAsync(ct);

            var payload = new
            {
                rollbackId = request.Id,
                request.Product,
                request.TargetEnv,
                mode = request.Mode.ToString(),
                request.ReferenceEnv,
                status = request.Status.ToString(),
                request.Reason,
                request.ApprovedAt,
                items,
            };
            var filters = new WebhookEventFilters(Product: request.Product, Environment: request.TargetEnv);
            await _webhooks.DispatchAsync(eventType, payload, filters);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Webhook dispatch '{EventType}' failed for rollback {Id}", eventType, request.Id);
        }
    }
}
