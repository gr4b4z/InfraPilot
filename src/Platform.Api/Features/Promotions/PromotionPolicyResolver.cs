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
    /// Returns the most specific policy that applies, or <c>null</c> if no policy exists for this edge.
    /// A <c>null</c> return means the product is <b>not enrolled</b> in promotions for this target —
    /// callers should skip candidate creation entirely rather than auto-approving.
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
    /// candidate. Callers should first check <see cref="ResolveAsync"/> to confirm a policy exists
    /// (no policy = not enrolled). When no policy row matches, this still returns a fallback
    /// auto-approve snapshot for backwards compatibility.
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
                ExcludeRole: null,
                TimeoutHours: 0,
                EscalationGroup: null);
        }

        // Carry the gate forward so PR3's evaluator sees the configured ticket-level mode for
        // newly created candidates. Old candidates whose snapshot JSON predates this field
        // deserialise to the default PromotionOnly — preserving today's flow.
        return new ResolvedPolicySnapshot(
            PolicyId: policy.Id,
            ApproverGroup: policy.ApproverGroup,
            Strategy: policy.Strategy,
            MinApprovers: policy.MinApprovers,
            ExcludeRole: policy.ExcludeRole,
            TimeoutHours: policy.TimeoutHours,
            EscalationGroup: policy.EscalationGroup)
        {
            Gate = policy.Gate,
            RequireAllTicketsApproved = policy.RequireAllTicketsApproved,
            AutoApproveOnAllTicketsApproved = policy.AutoApproveOnAllTicketsApproved,
            AutoApproveWhenNoTickets = policy.AutoApproveWhenNoTickets,
        };
    }
}
