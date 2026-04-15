using Microsoft.EntityFrameworkCore;
using Platform.Api.Features.Promotions.Models;
using Platform.Api.Infrastructure.Persistence;

namespace Platform.Api.Features.Promotions;

/// <summary>
/// Resolves which <see cref="PromotionPolicy"/> applies to a given (product, service, target_env)
/// tuple. Lookup order: service-specific row → product-default (<c>Service IS NULL</c>) → no row.
/// </summary>
public class PromotionPolicyResolver
{
    private readonly PlatformDbContext _db;

    public PromotionPolicyResolver(PlatformDbContext db)
    {
        _db = db;
    }

    /// <summary>
    /// Returns the most specific policy that applies, or <c>null</c> if no policy exists for this edge
    /// (caller should treat <c>null</c> as implicit auto-approve).
    /// </summary>
    public async Task<PromotionPolicy?> ResolveAsync(
        string product, string service, string targetEnv, CancellationToken ct = default)
    {
        // Service-specific wins if present.
        var specific = await _db.PromotionPolicies.AsNoTracking()
            .FirstOrDefaultAsync(p =>
                p.Product == product && p.Service == service && p.TargetEnv == targetEnv, ct);
        if (specific is not null) return specific;

        // Fall back to product-default (Service column is NULL).
        return await _db.PromotionPolicies.AsNoTracking()
            .FirstOrDefaultAsync(p =>
                p.Product == product && p.Service == null && p.TargetEnv == targetEnv, ct);
    }

    /// <summary>
    /// Wraps <see cref="ResolveAsync"/> and projects the result into the snapshot record stored on the
    /// candidate. When no policy row matches, returns an auto-approve snapshot so the caller has a
    /// uniform shape to persist.
    /// </summary>
    public async Task<ResolvedPolicySnapshot> SnapshotAsync(
        string product, string service, string targetEnv, CancellationToken ct = default)
    {
        var policy = await ResolveAsync(product, service, targetEnv, ct);
        if (policy is null)
        {
            // Implicit auto-approve: no policy row means "no approval gate configured".
            return new ResolvedPolicySnapshot(
                PolicyId: null,
                ApproverGroup: null,
                Strategy: PromotionStrategy.Any,
                MinApprovers: 0,
                ExcludeDeployer: false,
                TimeoutHours: 0,
                EscalationGroup: null);
        }

        return new ResolvedPolicySnapshot(
            PolicyId: policy.Id,
            ApproverGroup: policy.ApproverGroup,
            Strategy: policy.Strategy,
            MinApprovers: policy.MinApprovers,
            ExcludeDeployer: policy.ExcludeDeployer,
            TimeoutHours: policy.TimeoutHours,
            EscalationGroup: policy.EscalationGroup);
    }
}
