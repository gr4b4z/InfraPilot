using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Platform.Api.Features.Deployments.Models;
using Platform.Api.Features.Promotions;
using Platform.Api.Features.Promotions.Models;
using Platform.Api.Features.Rollbacks.Models;
using Platform.Api.Features.Webhooks;
using Platform.Api.Infrastructure;
using Platform.Api.Infrastructure.Audit;
using Platform.Api.Infrastructure.Auth;
using Platform.Api.Infrastructure.Features;
using Platform.Api.Infrastructure.Persistence;

namespace Platform.Api.Features.Rollbacks;

/// <summary>
/// Domain service for rollbacks — reverting one or more services in an environment to an earlier,
/// previously-deployed version. Rollback is the inverse of promotion and deliberately reuses the
/// promotion approval machinery (policy → approver group/strategy → gate), so "rollbacks follow
/// promotion rules". The differences are: an extra safety rule (target version must have run in
/// the env before), in-place (no topology), and an optional faster/auto-approve policy.
///
/// <para>Completion is detected from the deploy event the operator/executor emits when the target
/// version lands — there is no trusted callback (see <see cref="MatchCompletionAsync"/>).</para>
/// </summary>
public class RollbackService
{
    public const string EnabledProductsKey = "rollback.enabledProducts";

    private readonly PlatformDbContext _db;
    private readonly PromotionPolicyResolver _resolver;
    private readonly PromotionApprovalAuthorizer _auth;
    private readonly ICurrentUser _user;
    private readonly IAuditLogger _audit;
    private readonly IWebhookDispatcher _webhooks;
    private readonly IFeatureFlags _flags;
    private readonly ILogger<RollbackService> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public RollbackService(
        PlatformDbContext db,
        PromotionPolicyResolver resolver,
        PromotionApprovalAuthorizer auth,
        ICurrentUser user,
        IAuditLogger audit,
        IWebhookDispatcher webhooks,
        IFeatureFlags flags,
        ILogger<RollbackService> logger)
    {
        _db = db;
        _resolver = resolver;
        _auth = auth;
        _user = user;
        _audit = audit;
        _webhooks = webhooks;
        _flags = flags;
        _logger = logger;
    }

    // ---------------------------------------------------------------------
    // Per-product enrollment (on top of the global features.rollbacks flag)
    // ---------------------------------------------------------------------

    public async Task<List<string>> GetEnabledProductsAsync(CancellationToken ct = default)
    {
        var row = await _db.PlatformSettings.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Key == EnabledProductsKey, ct);
        if (row is null || string.IsNullOrWhiteSpace(row.Value)) return new();
        try { return JsonSerializer.Deserialize<List<string>>(row.Value, JsonOptions) ?? new(); }
        catch (JsonException) { return new(); }
    }

    public async Task<bool> IsProductEnabledAsync(string product, CancellationToken ct = default)
    {
        if (!await _flags.IsEnabled(FeatureFlagKeys.Rollbacks, ct)) return false;
        var enabled = await GetEnabledProductsAsync(ct);
        return enabled.Contains(product, StringComparer.OrdinalIgnoreCase);
    }

    public async Task SetEnabledProductsAsync(IEnumerable<string> products, CancellationToken ct = default)
    {
        var cleaned = products.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var json = JsonSerializer.Serialize(cleaned, JsonOptions);
        var row = await _db.PlatformSettings.FirstOrDefaultAsync(s => s.Key == EnabledProductsKey, ct);
        if (row is null)
            _db.PlatformSettings.Add(new PlatformSetting
            {
                Key = EnabledProductsKey, Value = json, UpdatedAt = DateTimeOffset.UtcNow,
                UpdatedBy = string.IsNullOrEmpty(_user.Email) ? _user.Name : _user.Email,
            });
        else { row.Value = json; row.UpdatedAt = DateTimeOffset.UtcNow; row.UpdatedBy = _user.Email ?? _user.Name; }
        await _db.SaveChangesAsync(ct);
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

    public async Task<RollbackPreview> PreviewAsync(CreateRollbackRequestDto dto, CancellationToken ct = default)
    {
        var mode = ParseMode(dto.Mode);
        var items = await ResolveItemsAsync(dto, mode, ct);
        return new RollbackPreview(dto.Product, dto.TargetEnv, mode.ToString(), dto.ReferenceEnv, items);
    }

    // ---------------------------------------------------------------------
    // Create
    // ---------------------------------------------------------------------

    public async Task<RollbackRequest> CreateAsync(CreateRollbackRequestDto dto, CancellationToken ct = default)
    {
        if (!await _flags.IsEnabled(FeatureFlagKeys.Rollbacks, ct))
            throw new InvalidOperationException("Rollbacks are not enabled on this platform");
        if (!await IsProductEnabledAsync(dto.Product, ct))
            throw new InvalidOperationException($"Rollbacks are not enabled for product '{dto.Product}'");

        var mode = ParseMode(dto.Mode);
        var resolved = await ResolveItemsAsync(dto, mode, ct);
        var eligible = resolved.Where(r => r.Eligible).ToList();
        if (eligible.Count == 0)
            throw new InvalidOperationException("No eligible services to roll back");

        // Phase 1: one request-level gate resolved from the (product, target env) promotion policy,
        // using the first eligible service as representative (service-specific → product-default →
        // auto-approve). Per-service policy divergence within one request is a future refinement.
        // Rollback is in-place within one env, so it has no source→target edge — resolve by target
        // only (any configured source policy for the env) rather than the edge-scoped ResolveAsync.
        var snapshot = await _resolver.SnapshotForTargetAsync(dto.Product, eligible[0].Service, dto.TargetEnv, ct);
        var autoApprove = snapshot.IsAutoApprove;

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
            new { request.Product, request.TargetEnv, mode = mode.ToString(), itemCount = request.Items.Count, autoApprove });

        _logger.LogInformation("Created rollback request {Id} for {Product}/{Env} ({Count} items, {Status})",
            request.Id, LogSanitizer.Clean(request.Product), LogSanitizer.Clean(request.TargetEnv), request.Items.Count, request.Status);

        if (request.Status == RollbackStatus.Approved)
            await DispatchWebhookAsync(request, "rollback.approved", ct);

        return request;
    }

    // ---------------------------------------------------------------------
    // Approval / rejection / cancel (reuse promotion approver-group + strategy rules)
    // ---------------------------------------------------------------------

    public async Task<RollbackRequest> ApproveAsync(Guid id, string? comment, CancellationToken ct = default)
    {
        var request = await LoadPendingAsync(id, ct);
        var snapshot = ReadSnapshot(request);
        if (snapshot.IsAutoApprove)
            throw new InvalidOperationException("This rollback does not require approval");
        if (!await _auth.IsAuthorizedForAnyRequirementAsync(snapshot, _user.Email, ct))
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

        var approverEmails = await _db.RollbackApprovals.AsNoTracking()
            .Where(a => a.RequestId == id && a.Decision == PromotionDecision.Approved)
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

    public async Task<RollbackRequest> RejectAsync(Guid id, string? comment, CancellationToken ct = default)
    {
        var request = await LoadPendingAsync(id, ct);
        var snapshot = ReadSnapshot(request);
        if (!snapshot.IsAutoApprove && !await _auth.IsAuthorizedForAnyRequirementAsync(snapshot, _user.Email, ct))
            throw new UnauthorizedAccessException("You are not authorized to approve this rollback");

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
        if (snapshot.IsAutoApprove) return false;
        if (await _db.RollbackApprovals.AsNoTracking().AnyAsync(a => a.RequestId == request.Id && a.ApproverEmail == _user.Email, ct))
            return false;
        return await _auth.IsAuthorizedForAnyRequirementAsync(snapshot, _user.Email, ct);
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
