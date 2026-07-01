using Microsoft.EntityFrameworkCore;
using Platform.Api.Features.Promotions.Models;
using Platform.Api.Infrastructure.Persistence;

namespace Platform.Api.Features.Promotions;

/// <summary>
/// Resolves which <see cref="PromotionPolicy"/> applies to a given
/// (product, service, source_env, target_env) tuple. Lookup order: service-specific row →
/// product-default (<c>Service IS NULL</c>) → no row. Policies are edge-scoped: a policy only
/// resolves for the exact source→target edge it was configured for.
/// </summary>
public class PromotionPolicyResolver
{
    private readonly PlatformDbContext _db;

    public PromotionPolicyResolver(PlatformDbContext db)
    {
        _db = db;
    }

    /// <summary>
    /// Returns the most specific policy that applies, or <c>null</c> if no policy exists for this edge.
    /// A <c>null</c> return means the product is <b>not enrolled</b> in promotions for this target —
    /// callers should skip candidate creation entirely rather than auto-approving.
    /// </summary>
    public async Task<PromotionPolicy?> ResolveAsync(
        string product, string service, string sourceEnv, string targetEnv, CancellationToken ct = default)
    {
        // Service-specific wins if present.
        var specific = await _db.PromotionPolicies.AsNoTracking()
            .FirstOrDefaultAsync(p =>
                p.Product == product && p.Service == service
                && p.SourceEnv == sourceEnv && p.TargetEnv == targetEnv, ct);
        if (specific is not null) return specific;

        // Fall back to product-default (Service column is NULL).
        return await _db.PromotionPolicies.AsNoTracking()
            .FirstOrDefaultAsync(p =>
                p.Product == product && p.Service == null
                && p.SourceEnv == sourceEnv && p.TargetEnv == targetEnv, ct);
    }

    /// <summary>
    /// Wraps <see cref="ResolveAsync"/> and projects the result into the snapshot record stored on the
    /// candidate. Callers should first check <see cref="ResolveAsync"/> to confirm a policy exists
    /// (no policy = not enrolled). When no policy row matches, this still returns a fallback
    /// auto-approve snapshot for backwards compatibility.
    /// </summary>
    public async Task<ResolvedPolicySnapshot> SnapshotAsync(
        string product, string service, string sourceEnv, string targetEnv, CancellationToken ct = default)
        => Project(await ResolveAsync(product, service, sourceEnv, targetEnv, ct));

    /// <summary>
    /// Target-only resolution (ignores source env): the first policy configured for this target,
    /// service-specific then product-default. Used by rollbacks — an in-place revert within a single
    /// environment has no source→target edge, so it just needs whatever gate guards that env.
    /// </summary>
    public async Task<PromotionPolicy?> ResolveForTargetAsync(
        string product, string service, string targetEnv, CancellationToken ct = default)
    {
        var specific = await _db.PromotionPolicies.AsNoTracking()
            .FirstOrDefaultAsync(p =>
                p.Product == product && p.Service == service && p.TargetEnv == targetEnv, ct);
        if (specific is not null) return specific;

        return await _db.PromotionPolicies.AsNoTracking()
            .FirstOrDefaultAsync(p =>
                p.Product == product && p.Service == null && p.TargetEnv == targetEnv, ct);
    }

    /// <summary>Snapshot variant of <see cref="ResolveForTargetAsync"/> for the rollback gate.</summary>
    public async Task<ResolvedPolicySnapshot> SnapshotForTargetAsync(
        string product, string service, string targetEnv, CancellationToken ct = default)
        => Project(await ResolveForTargetAsync(product, service, targetEnv, ct));

    /// <summary>
    /// Projects a resolved policy into the snapshot stored on the candidate. A <c>null</c> policy
    /// yields an auto-approve snapshot (empty ApprovalSteps ⇒ IsAutoApprove) — "no gate configured".
    /// Old candidates whose snapshot JSON predates a field deserialise to its default.
    /// </summary>
    private static ResolvedPolicySnapshot Project(PromotionPolicy? policy)
    {
        if (policy is null)
            return new ResolvedPolicySnapshot(PolicyId: null, TimeoutHours: 0, EscalationGroup: null);

        return new ResolvedPolicySnapshot(
            PolicyId: policy.Id,
            TimeoutHours: policy.TimeoutHours,
            EscalationGroup: policy.EscalationGroup)
        {
            ApprovalSteps = policy.ApprovalSteps,
            RequireAllWorkItemsApproved = policy.RequireAllWorkItemsApproved,
            AutoApproveOnAllWorkItemsApproved = policy.AutoApproveOnAllWorkItemsApproved,
            AutoApproveWhenNoWorkItems = policy.AutoApproveWhenNoWorkItems,
        };
    }
}
