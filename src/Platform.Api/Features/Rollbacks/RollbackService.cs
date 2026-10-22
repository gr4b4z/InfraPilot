using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Platform.Api.Features.Deployments.Models;
using Platform.Api.Features.Promotions;
using Platform.Api.Features.Promotions.Models;
using Platform.Api.Features.Rollbacks.Models;
using Platform.Api.Features.Settings;
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
    private readonly PromotionService _promotions;
    private readonly ICurrentUser _user;
    private readonly IAuditLogger _audit;
    private readonly IWebhookDispatcher _webhooks;
    private readonly IFeatureFlags _flags;
    private readonly EnvironmentAliasResolver _environments;
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
        PromotionService promotions,
        ICurrentUser user,
        IAuditLogger audit,
        IWebhookDispatcher webhooks,
        IFeatureFlags flags,
        EnvironmentAliasResolver environments,
        UserPreferencesService userPrefs,
        ILogger<RollbackService> logger)
    {
        _db = db;
        _policies = policies;
        _auth = auth;
        _promotions = promotions;
        _user = user;
        _audit = audit;
        _webhooks = webhooks;
        _flags = flags;
        _environments = environments;
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

        // The policy is keyed on the canonical environment, so an alias has to resolve before the
        // lookup â€” otherwise "prod" reads as unconfigured while a policy for "production" governs it.
        targetEnv = await _environments.ResolveAsync(targetEnv, ct);
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
                var running = await LatestSucceededAsync(dto.Product, input.Service, dto.TargetEnv, ct);
                var current = running?.Version;
                var history = await VersionHistoryAsync(dto.Product, input.Service, dto.TargetEnv, ct);
                // A manual rollback is most often raised because the newest deploy failed. The failed
                // version is what the release pipeline now declares (and what the cluster may half be
                // running), so the item is written against it, and the last version that succeeded is
                // a real target: putting it back means deploying it again. Align keeps reading
                // succeeded deploys only; a bulk align over a failed release is the 2026-09-21 case.
                var failed = await FailedReplacementAsync(dto.Product, input.Service, dto.TargetEnv, running, ct);
                var (eligible, reason) = EvaluateTarget(input.ToVersion, current, failed?.Version, history);
                var note = failed is null
                    ? await FailedAttemptNoteAsync(dto.Product, input.Service, dto.TargetEnv, running, ct)
                    : RestoreNote(failed, running);
                result.Add(new ResolvedRollbackItem(
                    input.Service, failed?.Version ?? current ?? "", input.ToVersion, eligible, reason, note));
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
            var running = await LatestSucceededAsync(dto.Product, service, dto.TargetEnv, ct);
            var current = running?.Version;
            var refVersion = await CurrentVersionAsync(dto.Product, service, dto.ReferenceEnv, ct);
            var note = await FailedAttemptNoteAsync(dto.Product, service, dto.TargetEnv, running, ct);

            if (exclude.Contains(service))
            {
                result.Add(new ResolvedRollbackItem(service, current ?? "", refVersion ?? "", false, "excluded", note));
                continue;
            }
            if (refVersion is null)
            {
                result.Add(new ResolvedRollbackItem(service, current ?? "", "", false, $"not present in {dto.ReferenceEnv}", note));
                continue;
            }
            // Same version in both envs — not part of the diff, so not a candidate at all. Emitting it
            // as a skipped item padded the preview with rows the operator can do nothing about (and
            // inflated the "N of M" denominator); align is defined by the diff, so it's omitted.
            // The exception is a service whose newest deploy failed: deploy history shows that failed
            // version, so the row is kept to say why the service is not in the diff.
            if (string.Equals(refVersion, current, StringComparison.OrdinalIgnoreCase))
            {
                if (note is not null)
                    result.Add(new ResolvedRollbackItem(service, current ?? "", refVersion, false, "already running this version", note));
                continue;
            }
            var history = await VersionHistoryAsync(dto.Product, service, dto.TargetEnv, ct);
            var (eligible, reason) = EvaluateTarget(refVersion, current, failedVersion: null, history);
            result.Add(new ResolvedRollbackItem(service, current ?? "", refVersion, eligible, reason, note));
        }

        return result;
    }

    // The safety rule: target must be a version that previously ran successfully in this env, and not
    // equal to what's already running (that would be a no-op). Both inputs come from succeeded deploys
    // only — see LatestSucceededAsync. The exception is a manual rollback over a failed deploy
    // (failedVersion set): the running version is then exactly what to restore, and the failed
    // version is the one target that would change nothing.
    private static (bool eligible, string? reason) EvaluateTarget(
        string toVersion, string? current, string? failedVersion, HashSet<string> history)
    {
        if (string.IsNullOrWhiteSpace(toVersion)) return (false, "no target version");
        if (SameVersion(toVersion, failedVersion)) return (false, "this is the version whose deploy failed");
        if (failedVersion is null && SameVersion(toVersion, current)) return (false, "already running this version");
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
        dto = await ResolveEnvironmentsAsync(dto, ct);
        var (allowed, reason) = await CanCreateAsync(dto.Product, dto.TargetEnv, ct);
        if (!allowed) throw new UnauthorizedAccessException(reason!);

        var mode = ParseMode(dto.Mode);
        var items = await ResolveItemsAsync(dto, mode, ct);
        return new RollbackPreview(dto.Product, dto.TargetEnv, mode.ToString(), dto.ReferenceEnv, items);
    }

    // ---------------------------------------------------------------------
    // Create
    // ---------------------------------------------------------------------

    /// <summary>
    /// Canonicalises the environments on an incoming request once, at the top, so the permission
    /// check, the version lookups, the policy snapshot and the stored row all name the same
    /// environment. Resolving per-use is how a rollback ends up gated by one environment's policy
    /// and recorded against another's.
    /// </summary>
    private async Task<CreateRollbackRequestDto> ResolveEnvironmentsAsync(
        CreateRollbackRequestDto dto, CancellationToken ct)
        => dto with
        {
            TargetEnv = await _environments.ResolveAsync(dto.TargetEnv, ct),
            ReferenceEnv = await _environments.ResolveFilterAsync(dto.ReferenceEnv, ct),
        };

    public async Task<RollbackRequest> CreateAsync(CreateRollbackRequestDto dto, CancellationToken ct = default)
    {
        dto = await ResolveEnvironmentsAsync(dto, ct);
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

        // Auto-approval needs no SettleOnApprovalAsync pass: the items were resolved against the running
        // versions a moment ago, and EvaluateTarget already dropped any that were at their target.
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
        {
            await ReopenRolledBackPromotionsAsync(request, "approved automatically by its rollback policy", ct);
            await DispatchWebhookAsync(request, "rollback.approved", ct);
        }

        return request;
    }

    /// <summary>
    /// Raises the rollback the failed-deployment page offers: back to the last version that deployed
    /// successfully (see <see cref="GetLastKnownGoodAsync"/>). The target is resolved here rather than
    /// taken from the caller, so the button can never put a version the server did not offer in the
    /// request. Refused with <see cref="RollbackAlreadyOpenException"/> while another open request
    /// already carries the service, which is also what makes a double click harmless.
    /// </summary>
    public async Task<RollbackRequest> CreateLastKnownGoodAsync(Guid eventId, string? reason, CancellationToken ct = default)
    {
        var offer = await GetLastKnownGoodAsync(eventId, ct);
        if (!offer.Available) throw new InvalidOperationException(offer.Reason);
        if (offer.OpenRollback is { } open) throw new RollbackAlreadyOpenException(open.Id, open.Status);

        var trimmed = string.IsNullOrWhiteSpace(reason) ? null : RequireReason(reason, "roll back");
        trimmed ??= $"Back to the last known good version {offer.LastGoodVersion}: the deploy of " +
                    $"{offer.FailedVersion} on {offer.FailedAt:u} failed.";

        return await CreateAsync(new CreateRollbackRequestDto(
            offer.Product, offer.TargetEnv, nameof(RollbackMode.Manual),
            Items: new() { new RollbackItemInput(offer.Service, offer.LastGoodVersion!) },
            Reason: trimmed), ct);
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

        var now = DateTimeOffset.UtcNow;
        request.Status = RollbackStatus.Approved;
        request.ApprovedAt = now;
        var settled = await SettleOnApprovalAsync(request, now, ct);
        await _db.SaveChangesAsync(ct);

        await _audit.Log("rollbacks", "rollback.approved",
            "system", "System (gate satisfied)", "system", "RollbackRequest", id, null,
            new { approvedCount = distinct.Count, requirements = requirements.Count });
        _logger.LogInformation("Rollback request {Id} → {Status}", id, request.Status);

        await ReopenRolledBackPromotionsAsync(request, $"approved by {ActorName}", ct);
        await DispatchWebhookAsync(request, "rollback.approved", ct);
        if (settled) await DispatchWebhookAsync(request, "rollback.deployed", ct);
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
        var settled = await SettleOnApprovalAsync(request, now, ct);
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

        await ReopenRolledBackPromotionsAsync(request, $"approved by {ActorName} with an admin override", ct);
        await DispatchWebhookAsync(request, "rollback.approved", ct);
        if (settled) await DispatchWebhookAsync(request, "rollback.deployed", ct);
        return request;
    }

    /// <summary>
    /// Cancels a request. A Pending one may be cancelled by its creator or an admin, with an optional
    /// reason.
    ///
    /// <para>An Approved or RollingBack one may be cancelled by an admin only, and only with a reason.
    /// The <c>rollback.approved</c> webhook has already fired, so cancelling cannot recall a deploy that
    /// is already under way. What it does stop is the release pipeline: that reads the open items of
    /// Approved and RollingBack requests on every run, so a stuck request would otherwise keep
    /// reapplying its versions. Before this action existed, a stuck request could only be fixed in the
    /// database.</para>
    ///
    /// <para>Items still open are closed as Skipped, so no open item is left under a closed request.</para>
    /// </summary>
    public async Task<RollbackRequest> CancelAsync(Guid id, string? reason = null, CancellationToken ct = default)
    {
        var request = await _db.RollbackRequests.FirstOrDefaultAsync(r => r.Id == id, ct)
            ?? throw new KeyNotFoundException($"Rollback request {id} not found");
        var trimmed = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        var inFlight = request.Status is RollbackStatus.Approved or RollbackStatus.RollingBack;

        if (inFlight)
        {
            if (!_user.IsAdmin)
                throw new UnauthorizedAccessException(
                    $"Rollback request is {request.Status}; only an admin can cancel an approved rollback");
            trimmed = RequireReason(trimmed, "cancel an approved rollback");
        }
        else if (request.Status != RollbackStatus.Pending)
        {
            throw new InvalidOperationException(
                $"Rollback request is {request.Status}; only Pending, Approved or RollingBack requests can be cancelled");
        }
        else if (!string.Equals(request.CreatedBy, _user.Email, StringComparison.OrdinalIgnoreCase) && !_user.IsAdmin)
        {
            throw new UnauthorizedAccessException("Only the creator or an admin can cancel this rollback");
        }
        else if (trimmed is not null)
        {
            trimmed = RequireReason(trimmed, "cancel this rollback");
        }

        var previousStatus = request.Status;
        var now = DateTimeOffset.UtcNow;
        // All of them, not just the open ones: the response renders the request's full item list.
        // The note stays short because who cancelled and why are on the request itself.
        var items = await _db.RollbackItems.Where(i => i.RequestId == id).ToListAsync(ct);
        var skipped = items.Where(i => !IsTerminal(i.Status)).ToList();
        foreach (var item in skipped)
            Close(item, RollbackItemStatus.Skipped, null, now, "The request was cancelled.");

        request.Status = RollbackStatus.Cancelled;
        request.CancelledBy = _user.Email;
        request.CancelledByName = _user.Name;
        request.CancelledAt = now;
        request.CancelReason = trimmed;
        await _db.SaveChangesAsync(ct);

        await _audit.Log("rollbacks", "rollback.cancelled",
            _user.Id, _user.Name, "user", "RollbackRequest", id, null,
            new
            {
                reason = trimmed,
                previousStatus = previousStatus.ToString(),
                skippedItems = skipped.Select(i => i.Service).ToList(),
            });
        if (inFlight)
            _logger.LogWarning("Rollback request {Id} cancelled while {Status} by {Actor} ({Count} open items skipped)",
                id, previousStatus, LogSanitizer.Clean(_user.Email), skipped.Count);

        await DispatchWebhookAsync(request, "rollback.cancelled", ct);
        return request;
    }

    /// <summary>
    /// Admin escape hatch for one item of an Approved or RollingBack request: closes it as Skipped with
    /// a mandatory reason, so the release pipeline stops carrying it. This is for an item that will never
    /// land on its own, such as a revert nobody wants any more, when the rest of the request should
    /// still go out. Skipping the last open item settles the request, as a landing would.
    /// </summary>
    public async Task<RollbackRequest> SkipItemAsync(Guid id, Guid itemId, string? reason, CancellationToken ct = default)
    {
        if (!_user.IsAdmin)
            throw new UnauthorizedAccessException("Only an admin can skip a rollback item");
        var trimmed = RequireReason(reason, "skip a rollback item");

        var request = await _db.RollbackRequests.FirstOrDefaultAsync(r => r.Id == id, ct)
            ?? throw new KeyNotFoundException($"Rollback request {id} not found");
        if (request.Status is not (RollbackStatus.Approved or RollbackStatus.RollingBack))
            throw new InvalidOperationException(
                $"Rollback request is {request.Status}; items can only be skipped on an Approved or RollingBack request " +
                "(reject or cancel a Pending one instead)");

        var item = await _db.RollbackItems.FirstOrDefaultAsync(i => i.Id == itemId && i.RequestId == id, ct)
            ?? throw new KeyNotFoundException($"Item {itemId} is not part of rollback request {id}");
        if (IsTerminal(item.Status))
            throw new InvalidOperationException($"{item.Service} is already {item.Status} and cannot be skipped");

        Close(item, RollbackItemStatus.Skipped, null, DateTimeOffset.UtcNow, $"Skipped by {_user.Name}: {trimmed}");
        await _db.SaveChangesAsync(ct);

        await _audit.Log("rollbacks", "rollback.item.skipped",
            _user.Id, _user.Name, "user", "RollbackRequest", id, null,
            new { itemId, item.Service, item.FromVersion, item.ToVersion, reason = trimmed });
        _logger.LogWarning("Rollback item {Service} on request {Id} skipped by {Actor}",
            LogSanitizer.Clean(item.Service), id, LogSanitizer.Clean(_user.Email));

        await SettleRequestsAsync(new[] { request }, $"{item.Service} skipped", ct);
        return request;
    }

    // Reasons end up in item notes and on the request card, so they are kept short. The ticket or
    // incident they point at is where the detail belongs.
    private const int MaxReasonLength = 500;

    private static string RequireReason(string? reason, string action)
    {
        if (string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException($"A reason is required to {action}", nameof(reason));
        var trimmed = reason.Trim();
        if (trimmed.Length > MaxReasonLength)
            throw new ArgumentException($"The reason must be at most {MaxReasonLength} characters", nameof(reason));
        return trimmed;
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
    ///
    /// <para>Only a succeeded deploy counts. A failed attempt at the target version did not put it live,
    /// so the item stays open and the release keeps carrying it.</para>
    /// </summary>
    public async Task<bool> MatchCompletionAsync(DeployEvent landing, CancellationToken ct = default)
    {
        if (!IsSucceeded(landing)) return false;

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
        foreach (var item in items)
            Close(item, RollbackItemStatus.RolledBack, landing.Id, now, null);
        await _db.SaveChangesAsync(ct);

        var touched = items.Select(i => i.RequestId).ToHashSet();
        await SettleRequestsAsync(openRequests.Where(r => touched.Contains(r.Id)),
            $"item for {landing.Service} landed", ct);
        return true;
    }

    /// <summary>
    /// Called from the deployment ingest hook after <see cref="MatchCompletionAsync"/>. A succeeded deploy
    /// of another version, landing after a request was approved, means the environment has moved on
    /// from the state the rollback was written against. Reverting now would undo a change nobody
    /// decided to undo: the release pipeline rebuilds its manifest from the open items on every run, so
    /// a stale item stays hidden behind a newer approved promotion and reverts it the moment that
    /// promotion deploys. Every such open item is closed as Superseded. Unlike promotions this needs no
    /// version ordering: the rollback no longer describes the environment whichever way the version
    /// moved.
    ///
    /// <para>The exception is a deploy of the item's <c>FromVersion</c>. That is the version the
    /// rollback exists to remove landing again, from a retry or an overlapping pipeline run finishing
    /// late. The rollback still describes the environment, so it stays open.</para>
    ///
    /// <para>Returns the number of items superseded.</para>
    /// </summary>
    public async Task<int> SupersedeOvertakenAsync(DeployEvent landing, CancellationToken ct = default)
    {
        // A failed attempt moved the environment nowhere.
        if (!IsSucceeded(landing)) return 0;

        var openRequests = await _db.RollbackRequests
            .Where(r => r.Product == landing.Product
                     && r.TargetEnv == landing.Environment
                     && (r.Status == RollbackStatus.Approved || r.Status == RollbackStatus.RollingBack))
            .ToListAsync(ct);
        // Only what this deploy overtook: a request approved after the deploy landed was decided with
        // the deploy already in view.
        openRequests = openRequests.Where(r => r.ApprovedAt is { } a && landing.DeployedAt >= a).ToList();
        if (openRequests.Count == 0) return 0;

        var requestIds = openRequests.Select(r => r.Id).ToList();
        var items = (await _db.RollbackItems
                .Where(i => requestIds.Contains(i.RequestId)
                         && i.Service == landing.Service
                         && (i.Status == RollbackItemStatus.Pending || i.Status == RollbackItemStatus.RollingBack))
                .ToListAsync(ct))
            .Where(i => !SameVersion(i.ToVersion, landing.Version) && !SameVersion(i.FromVersion, landing.Version))
            .ToList();
        if (items.Count == 0) return 0;

        var now = DateTimeOffset.UtcNow;
        foreach (var item in items)
            Close(item, RollbackItemStatus.Superseded, landing.Id, now,
                $"{landing.Service} {landing.Version} was deployed to {landing.Environment} " +
                $"on {landing.DeployedAt:u}, after this rollback was approved, so the rollback to " +
                $"{item.ToVersion} is no longer going out.");
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Deploy event {EventId} superseded {Count} rollback item(s) for {Service}: {Environment} moved on to {Version}",
            landing.Id, items.Count, LogSanitizer.Clean(landing.Service),
            LogSanitizer.Clean(landing.Environment), LogSanitizer.Clean(landing.Version));

        var touched = items.Select(i => i.RequestId).ToHashSet();
        await SettleRequestsAsync(openRequests.Where(r => touched.Contains(r.Id)),
            $"item for {landing.Service} superseded", ct);
        return items.Count;
    }

    /// <summary>
    /// Run when a request clears its gate (approval or override), before the approval is dispatched.
    /// Closes the open items that no longer describe the environment, so the release pipeline never
    /// sees them:
    /// <list type="bullet">
    ///   <item>already running the target version: RolledBack, pointing at the deploy that put it there
    ///     (a diff deploy skips it, so no deploy event of its own would ever arrive);</item>
    ///   <item>moved to some third version since the request was raised: Superseded, the approval-time
    ///     version of <see cref="SupersedeOvertakenAsync"/>, for a request that waited Pending while
    ///     the environment moved on.</item>
    /// </list>
    /// Returns <c>true</c> when that left nothing open and settled the request. The caller saves.
    /// </summary>
    private async Task<bool> SettleOnApprovalAsync(RollbackRequest request, DateTimeOffset now, CancellationToken ct)
    {
        var items = await _db.RollbackItems.Where(i => i.RequestId == request.Id).ToListAsync(ct);
        foreach (var item in items.Where(i => !IsTerminal(i.Status)))
        {
            var running = await LatestSucceededAsync(request.Product, item.Service, request.TargetEnv, ct);
            if (running is null) continue;

            if (SameVersion(running.Version, item.ToVersion))
            {
                // Putting the running version back after a failed deploy of another one: the release
                // pipeline still declares the failed version, so the redeploy is the whole point. The
                // item closes when the target lands, like any other.
                if (await FailedReplacementAsync(request.Product, item.Service, request.TargetEnv, running, ct) is not null)
                    continue;
                Close(item, RollbackItemStatus.RolledBack, running.Id, now,
                    $"Already running {item.ToVersion} when the rollback was approved " +
                    $"(deployed {running.DeployedAt:u}), so there was nothing to deploy.");
            }
            // DeployedAt is checked as well as the version: an item from before failed deploys stopped
            // counting may carry a failed version as FromVersion, and that environment never moved.
            else if (!SameVersion(running.Version, item.FromVersion) && running.DeployedAt > request.CreatedAt)
            {
                Close(item, RollbackItemStatus.Superseded, running.Id, now,
                    $"{request.TargetEnv} moved from {item.FromVersion} to {running.Version} " +
                    $"on {running.DeployedAt:u}, after this rollback was raised. Raise a new rollback if " +
                    "it is still needed.");
            }
        }
        return Reaggregate(request, items, now);
    }

    /// <summary>
    /// Re-derives each request's status from its items after some of them closed, saves, and fires
    /// <c>rollback.deployed</c> for each request that this settled.
    /// </summary>
    private async Task SettleRequestsAsync(IEnumerable<RollbackRequest> requests, string trigger, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var request in requests)
        {
            var items = await _db.RollbackItems.Where(i => i.RequestId == request.Id).ToListAsync(ct);
            var settled = Reaggregate(request, items, now);
            await _db.SaveChangesAsync(ct);

            _logger.LogInformation("Rollback request {Id} → {Status} ({Trigger})",
                request.Id, request.Status, LogSanitizer.Clean(trigger));
            if (settled)
                await DispatchWebhookAsync(request, "rollback.deployed", ct);
        }
    }

    /// <summary>
    /// A request's status follows its items: RolledBack once every item is terminal, however each one
    /// closed, and RollingBack once any is. Returns <c>true</c> when this call moved it to RolledBack.
    /// </summary>
    private static bool Reaggregate(RollbackRequest request, IReadOnlyCollection<RollbackItem> items, DateTimeOffset now)
    {
        if (items.Count > 0 && items.All(i => IsTerminal(i.Status)))
        {
            request.Status = RollbackStatus.RolledBack;
            request.CompletedAt = now;
            return true;
        }
        if (request.Status == RollbackStatus.Approved && items.Any(i => IsTerminal(i.Status)))
            request.Status = RollbackStatus.RollingBack;
        return false;
    }

    private static bool IsTerminal(RollbackItemStatus status) => status is RollbackItemStatus.RolledBack
        or RollbackItemStatus.Failed or RollbackItemStatus.Skipped or RollbackItemStatus.Superseded;

    private static void Close(RollbackItem item, RollbackItemStatus status, Guid? deployEventId, DateTimeOffset at, string? note)
    {
        item.Status = status;
        item.CompletedDeployEventId = deployEventId;
        item.CompletedAt = at;
        // Notes embed service names and versions, so a long pair could overrun the column.
        item.Note = note is { Length: > 1000 } ? note[..1000] : note;
    }

    private static bool IsSucceeded(DeployEvent e)
        => string.Equals(e.Status, "succeeded", StringComparison.OrdinalIgnoreCase);

    private static bool SameVersion(string? a, string? b)
        => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

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

    /// <summary>
    /// What the failed-deployment page offers: rolling the service back to the last version that
    /// deployed successfully in the environment. Available while the failure is still the
    /// environment's latest word on the service, that is, while no succeeded deploy has landed after
    /// it. When not available, <see cref="LastKnownGoodOffer.Reason"/> says why in a sentence the
    /// page shows as it is.
    ///
    /// <para>Everything the confirmation needs comes back with it, so the page explains the
    /// consequences before anyone clicks: whether the caller may raise it, who has to approve it,
    /// whether an open rollback already carries the service, which promotions its approval would
    /// reopen, and whether anything subscribes to <c>rollback.approved</c> to carry it out. That last
    /// one is not a formality: an approved rollback nobody executes changes nothing.</para>
    /// </summary>
    public async Task<LastKnownGoodOffer> GetLastKnownGoodAsync(Guid eventId, CancellationToken ct = default)
    {
        if (!await _flags.IsEnabled(FeatureFlagKeys.Rollbacks, ct))
            throw new InvalidOperationException("Rollbacks are not enabled on this platform");

        var evt = await _db.DeployEvents.AsNoTracking()
            .Where(e => e.Id == eventId)
            .Select(e => new { e.Id, e.Product, e.Service, e.Environment, e.Version, e.Status, e.DeployedAt })
            .FirstOrDefaultAsync(ct)
            ?? throw new KeyNotFoundException($"Deploy event {eventId} not found");

        LastKnownGoodOffer Unavailable(string reason) => new(
            evt.Id, evt.Product, evt.Service, evt.Environment, false, reason,
            null, null, null, null, null, null, false, null, null, Array.Empty<string>(), null, false,
            Array.Empty<PromotionToReopen>());

        if (!string.Equals(evt.Status, "failed", StringComparison.OrdinalIgnoreCase))
            return Unavailable("This deployment did not fail.");

        var running = await LatestSucceededAsync(evt.Product, evt.Service, evt.Environment, ct);
        if (running is null)
            return Unavailable($"No version of {evt.Service} has deployed successfully in {evt.Environment}, " +
                               "so there is no known good version to go back to.");
        if (running.DeployedAt > evt.DeployedAt)
            return Unavailable($"{evt.Service} {running.Version} deployed successfully in {evt.Environment} " +
                               $"after this failure ({running.DeployedAt:u}), so this failure is no longer what needs undoing.");

        var failed = await FailedReplacementAsync(evt.Product, evt.Service, evt.Environment, running, ct);
        if (failed is null)
        {
            if (SameVersion(evt.Version, running.Version))
                return Unavailable($"{running.Version} is also the last version that deployed successfully in {evt.Environment}, " +
                                   "so there is no earlier good version to go back to. Redeploy it instead.");

            // A later deploy put the last good version back (a rollback, or a redeploy) and that failed
            // too: the version that worked no longer does, which points at something around the service
            // rather than at what this deploy changed. Saying "not finished" here sent people waiting.
            var newest = await NewestDeployAsync(evt.Product, evt.Service, evt.Environment, ct);
            return Unavailable(newest is not null && string.Equals(newest.Status, "failed", StringComparison.OrdinalIgnoreCase)
                ? $"{newest.Version}, the last version that deployed successfully in {evt.Environment}, failed too " +
                  $"when it was deployed again on {newest.DeployedAt:u}, so there is no known good version left to roll back to. " +
                  "When the version that worked fails as well, look at what changed around the service: configuration, " +
                  "secrets, credentials or a dependency."
                : $"A newer deploy of {evt.Service} in {evt.Environment} has not finished yet.");
        }

        var (canCreate, createReason) = await CanCreateAsync(evt.Product, evt.Environment, ct);

        var snapshot = await _policies.SnapshotAsync(evt.Product, evt.Environment, ct);
        var approval = snapshot.PolicyId is null ? "unconfigured" : snapshot.IsAutoApprove ? "auto" : "required";
        var approvers = snapshot.ApprovalSteps
            .SelectMany(s => s.Requirements.Count == 0
                ? Array.Empty<string>()
                : new[] { string.IsNullOrWhiteSpace(s.Name) ? string.Join(", ", s.Requirements.Select(r => r.Name)) : s.Name })
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct()
            .ToList();

        var open = await _db.RollbackRequests.AsNoTracking()
            .Where(r => r.Product == evt.Product && r.TargetEnv == evt.Environment
                     && (r.Status == RollbackStatus.Pending || r.Status == RollbackStatus.Approved
                         || r.Status == RollbackStatus.RollingBack)
                     && r.Items.Any(i => i.Service == evt.Service
                                      && (i.Status == RollbackItemStatus.Pending || i.Status == RollbackItemStatus.RollingBack)))
            .OrderByDescending(r => r.CreatedAt)
            .Select(r => new { r.Id, r.Status, r.CreatedAt })
            .FirstOrDefaultAsync(ct);

        var reopen = (await PromotionsToReopenQuery(evt.Product, evt.Service, evt.Environment, failed.Version)
                .OrderByDescending(c => c.CreatedAt)
                .Select(c => new { c.Id, c.SourceEnv, c.Version, c.Status })
                .ToListAsync(ct))
            .Select(c => new PromotionToReopen(c.Id, c.SourceEnv, c.Version, c.Status.ToString()))
            .ToList();

        return new LastKnownGoodOffer(
            evt.Id, evt.Product, evt.Service, evt.Environment, true, null,
            failed.Version, failed.Id, failed.DeployedAt,
            running.Version, running.Id, running.DeployedAt,
            canCreate, createReason, approval, approvers,
            open is null ? null : new OpenRollbackRef(open.Id, open.Status.ToString(), open.CreatedAt),
            await HasRollbackExecutorAsync(evt.Product, evt.Environment, ct),
            reopen);
    }

    // The promotions a rollback away from fromVersion takes back: they put that version in the
    // environment (or are about to) and nothing else will ever close them.
    private IQueryable<PromotionCandidate> PromotionsToReopenQuery(
        string product, string service, string env, string fromVersion)
        => _db.PromotionCandidates.AsNoTracking()
            .Where(c => c.Product == product && c.Service == service && c.TargetEnv == env
                     && c.Version == fromVersion
                     && (c.Status == PromotionStatus.Approved || c.Status == PromotionStatus.Deploying));

    /// <summary>
    /// Run once a request has cleared its gate and been saved, before <c>rollback.approved</c> goes
    /// out: every open item takes its <c>FromVersion</c> back out of the environment, so the
    /// promotions still Approved or Deploying for that version are reopened
    /// (<see cref="PromotionService.ReopenForRollbackAsync"/>). The usual case is a deploy that
    /// failed: its promotion never reaches Deployed, and a rollback to an older version supersedes
    /// nothing, so without this it would stay Approved, cleared to go out again, indefinitely.
    /// Reopening first means a pipeline that rebuilds its manifest from the approved set on
    /// <c>rollback.approved</c> already sees the promotion gone.
    ///
    /// <para>Best effort, like webhook dispatch: the approval is already saved, and a promotion that
    /// could not be reopened is logged rather than turned into a failed approval.</para>
    /// </summary>
    private async Task ReopenRolledBackPromotionsAsync(RollbackRequest request, string how, CancellationToken ct)
    {
        try
        {
            var items = await _db.RollbackItems.AsNoTracking()
                .Where(i => i.RequestId == request.Id
                         && (i.Status == RollbackItemStatus.Pending || i.Status == RollbackItemStatus.RollingBack))
                .ToListAsync(ct);

            var outcomes = new List<object>();
            foreach (var item in items.Where(i => !string.IsNullOrEmpty(i.FromVersion)))
            {
                var ids = await PromotionsToReopenQuery(request.Product, item.Service, request.TargetEnv, item.FromVersion)
                    .Select(c => c.Id)
                    .ToListAsync(ct);
                if (ids.Count == 0) continue;

                var summary = $"Rollback {request.Id.ToString()[..8]} of {item.Service} in {request.TargetEnv} " +
                              $"({item.FromVersion} → {item.ToVersion}) was {how}.";
                foreach (var id in ids)
                {
                    var outcome = await _promotions.ReopenForRollbackAsync(id, request.Id, summary, ct);
                    outcomes.Add(new { candidateId = id, item.Service, item.FromVersion, outcome.Reopened, outcome.Reason });
                    _logger.LogInformation("Rollback {Id} {Action} promotion {CandidateId} for {Service} {Version}{Why}",
                        request.Id, outcome.Reopened ? "reopened" : "left", id, LogSanitizer.Clean(item.Service),
                        LogSanitizer.Clean(item.FromVersion), outcome.Reason is null ? "" : $": {outcome.Reason}");
                }
            }

            if (outcomes.Count > 0)
                await _audit.Log("rollbacks", "rollback.promotions.reopened",
                    _user.Id, _user.Name, "user", "RollbackRequest", request.Id, null, new { promotions = outcomes });
        }
        catch (Exception ex)
        {
            // The context is shared with the webhook dispatch that follows; a half-staged reopen must
            // not ride along on its save. Everything this request needed is already persisted.
            _db.ChangeTracker.Clear();
            _logger.LogWarning(ex, "Reopening the promotions rolled back by request {Id} failed", request.Id);
        }
    }

    /// <summary>
    /// The newest failed deploy of each open item's target version since its request was approved,
    /// keyed by item id. A failed attempt never completes an item, so without this an item the
    /// executor already tried would look as if nothing had happened.
    /// </summary>
    public async Task<Dictionary<Guid, FailedDeployAttempt>> GetFailedAttemptsAsync(
        IReadOnlyCollection<RollbackRequest> requests, CancellationToken ct = default)
    {
        var open = requests
            .Where(r => r.ApprovedAt is not null
                     && r.Status is RollbackStatus.Approved or RollbackStatus.RollingBack)
            .SelectMany(r => r.Items.Where(i => !IsTerminal(i.Status)).Select(i => (Request: r, Item: i)))
            .ToList();
        if (open.Count == 0) return new();

        var products = open.Select(o => o.Request.Product).Distinct().ToList();
        var services = open.Select(o => o.Item.Service).Distinct().ToList();
        var versions = open.Select(o => o.Item.ToVersion).Distinct().ToList();
        var failed = await _db.DeployEvents.AsNoTracking()
            .Where(e => e.Status == "failed"
                     && products.Contains(e.Product)
                     && services.Contains(e.Service)
                     && versions.Contains(e.Version))
            .Select(e => new { e.Id, e.Product, e.Service, e.Environment, e.Version, e.DeployedAt })
            .ToListAsync(ct);

        var result = new Dictionary<Guid, FailedDeployAttempt>();
        foreach (var (request, item) in open)
        {
            var latest = failed
                .Where(e => e.Product == request.Product
                         && e.Environment == request.TargetEnv
                         && e.Service == item.Service
                         && SameVersion(e.Version, item.ToVersion)
                         && e.DeployedAt >= request.ApprovedAt)
                .MaxBy(e => e.DeployedAt);
            if (latest is not null)
                result[item.Id] = new FailedDeployAttempt(latest.Id, latest.DeployedAt);
        }
        return result;
    }

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

    /// <summary>The deploy that put the running version in place.</summary>
    private sealed record RunningDeploy(Guid Id, string Version, DateTimeOffset DeployedAt);

    // The version queries below read succeeded deploys only. A failed deploy never replaced what was
    // running: a rollback that took it as the current version would "revert" services already at
    // their target, and those items could never close. Failed deploys stay in deploy history and are
    // reported on previews (FailedAttemptNoteAsync); they just never count as what is running.

    private async Task<RunningDeploy?> LatestSucceededAsync(string product, string service, string env, CancellationToken ct)
        => await _db.DeployEvents.AsNoTracking()
            .Where(e => e.Product == product && e.Service == service && e.Environment == env
                     && e.Status == "succeeded")
            .OrderByDescending(e => e.DeployedAt)
            .Select(e => new RunningDeploy(e.Id, e.Version, e.DeployedAt))
            .FirstOrDefaultAsync(ct);

    private async Task<string?> CurrentVersionAsync(string product, string service, string env, CancellationToken ct)
        => (await LatestSucceededAsync(product, service, env, ct))?.Version;

    /// <summary>A failed deploy that tried to replace the running version.</summary>
    private sealed record FailedDeploy(Guid Id, string Version, DateTimeOffset DeployedAt);

    /// <summary>
    /// The service's newest deploy in the environment, when it failed while trying to put a different
    /// version in place of the running one. A failed deploy does not change what InfraPortal counts as
    /// running, but it does change what the release pipeline declares, and the workload may be half
    /// rolled out on it. So a rollback raised after one starts from the failed version, and the running
    /// version is something to put back rather than something already there.
    /// </summary>
    private async Task<FailedDeploy?> FailedReplacementAsync(
        string product, string service, string env, RunningDeploy? running, CancellationToken ct)
    {
        var newest = await NewestDeployAsync(product, service, env, ct);
        if (newest is null || !string.Equals(newest.Status, "failed", StringComparison.OrdinalIgnoreCase))
            return null;
        if (running is not null && SameVersion(newest.Version, running.Version))
            return null;
        return new FailedDeploy(newest.Id, newest.Version, newest.DeployedAt);
    }

    /// <summary>The service's newest deploy in the environment, whatever its status.</summary>
    private sealed record NewestDeploy(Guid Id, string Version, string Status, DateTimeOffset DeployedAt);

    private async Task<NewestDeploy?> NewestDeployAsync(string product, string service, string env, CancellationToken ct)
        => await _db.DeployEvents.AsNoTracking()
            .Where(e => e.Product == product && e.Service == service && e.Environment == env)
            .OrderByDescending(e => e.DeployedAt)
            .Select(e => new NewestDeploy(e.Id, e.Version, e.Status, e.DeployedAt))
            .FirstOrDefaultAsync(ct);

    private static string RestoreNote(FailedDeploy failed, RunningDeploy? running) => running is null
        ? $"The deploy of {failed.Version} on {failed.DeployedAt:u} failed, and no deploy has succeeded here yet."
        : $"The deploy of {failed.Version} on {failed.DeployedAt:u} failed, so the rollback starts from it. " +
          $"{running.Version} is the last version that deployed successfully here.";

    /// <summary>
    /// Whether any active webhook subscription delivers <c>rollback.approved</c> for this scope. The
    /// portal has no executor of its own: a release pipeline that subscribes is what turns an approved
    /// rollback into a deploy, so a scope with none would approve a rollback that changes nothing.
    /// </summary>
    private async Task<bool> HasRollbackExecutorAsync(string product, string env, CancellationToken ct)
    {
        var subscriptions = await _db.WebhookSubscriptions.AsNoTracking().Where(s => s.Active).ToListAsync(ct);
        var filters = new WebhookEventFilters(Product: product, Environment: env);
        return subscriptions.Any(s =>
            (JsonSerializer.Deserialize<List<string>>(s.EventsJson) ?? new()).Contains("rollback.approved")
            && WebhookSubscriptionFilters.Matches(s, filters));
    }

    /// <summary>How the current user is named in text other people read (promotion comments).</summary>
    private string ActorName => string.IsNullOrWhiteSpace(_user.Name) ? _user.Email : _user.Name;

    private async Task<HashSet<string>> VersionHistoryAsync(string product, string service, string env, CancellationToken ct)
    {
        var versions = await _db.DeployEvents.AsNoTracking()
            .Where(e => e.Product == product && e.Service == service && e.Environment == env
                     && e.Status == "succeeded")
            .Select(e => e.Version)
            .Distinct()
            .ToListAsync(ct);
        return new HashSet<string>(versions, StringComparer.OrdinalIgnoreCase);
    }

    private async Task<List<string>> ServicesInEnvAsync(string product, string env, CancellationToken ct)
        => await _db.DeployEvents.AsNoTracking()
            .Where(e => e.Product == product && e.Environment == env && e.Status == "succeeded")
            .Select(e => e.Service)
            .Distinct()
            .OrderBy(s => s)
            .ToListAsync(ct);

    /// <summary>
    /// When the newest deploy of a service failed after the running version landed, says so. That
    /// failed version is the first thing deploy history shows, and the preview has to explain why it
    /// reports something else as current.
    /// </summary>
    private async Task<string?> FailedAttemptNoteAsync(
        string product, string service, string env, RunningDeploy? running, CancellationToken ct)
    {
        var newest = await _db.DeployEvents.AsNoTracking()
            .Where(e => e.Product == product && e.Service == service && e.Environment == env)
            .OrderByDescending(e => e.DeployedAt)
            .Select(e => new { e.Version, e.Status, e.DeployedAt })
            .FirstOrDefaultAsync(ct);
        if (newest is null || !string.Equals(newest.Status, "failed", StringComparison.OrdinalIgnoreCase))
            return null;

        return running is null
            ? $"The deploy of {newest.Version} on {newest.DeployedAt:u} failed, and no deploy has succeeded here yet."
            : $"The deploy of {newest.Version} on {newest.DeployedAt:u} failed, so {running.Version} is still running.";
    }

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
