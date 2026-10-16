using Microsoft.EntityFrameworkCore;
using Platform.Api.Features.Promotions.Models;
using Platform.Api.Features.Rollbacks.Models;
using Platform.Api.Infrastructure.Persistence;

namespace Platform.Api.Features.Rollbacks;

/// <summary>
/// Resolves which <see cref="RollbackPolicy"/> governs a (product, target env) pair. Lookup order:
/// environment-specific row → product-default row (<c>TargetEnv IS NULL</c>) → none.
///
/// <para>A rollback is in-place within a single environment, so there is no source→target edge to
/// scope on — the target environment is the whole scope. (This replaces
/// <c>PromotionPolicyResolver.ResolveForTargetAsync</c>, which existed only to squeeze rollbacks into
/// the edge-scoped promotion table by ignoring the source env.)</para>
///
/// <para>Three outcomes, and the difference between the last two is the point of this class:</para>
/// <list type="bullet">
///   <item><b>No row</b> — the product is not configured for rollbacks in this environment. Only
///     admins may create, and the request can only ever be approved by an explicit admin override.
///     Signalled by <c>PolicyId == null</c> on the snapshot.</item>
///   <item><b>A row with an empty approval tree</b> — configured, and deliberately ungated:
///     auto-approve. Signalled by a non-null <c>PolicyId</c> with <c>IsAutoApprove</c>.</item>
///   <item><b>A row with requirements</b> — the normal gate.</item>
/// </list>
/// </summary>
public class RollbackPolicyResolver
{
    private readonly PlatformDbContext _db;

    public RollbackPolicyResolver(PlatformDbContext db)
    {
        _db = db;
    }

    /// <summary>
    /// The most specific policy governing this environment, or <c>null</c> when the product is not
    /// configured for rollbacks here.
    /// </summary>
    public async Task<RollbackPolicy?> ResolveAsync(
        string product, string targetEnv, CancellationToken ct = default)
    {
        var specific = await _db.RollbackPolicies.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Product == product && p.TargetEnv == targetEnv, ct);
        if (specific is not null) return specific;

        return await _db.RollbackPolicies.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Product == product && p.TargetEnv == null, ct);
    }

    /// <summary>
    /// Resolves and projects to the snapshot persisted on the request, so gate evaluation never
    /// depends on a live join and editing a policy cannot retroactively change an in-flight decision.
    /// </summary>
    public async Task<ResolvedPolicySnapshot> SnapshotAsync(
        string product, string targetEnv, CancellationToken ct = default)
        => Project(await ResolveAsync(product, targetEnv, ct));

    /// <summary>
    /// Projects a rollback policy onto <see cref="ResolvedPolicySnapshot"/> — the record already
    /// persisted in <c>RollbackRequest.ResolvedPolicyJson</c>, reused here so historical rollback
    /// requests keep deserialising unchanged. Only the fields rollbacks actually gate on are set; the
    /// promotion work-item flags stay at their defaults and are never read on this path.
    ///
    /// <para>A <c>null</c> policy yields <c>PolicyId == null</c> with no requirements. Callers must
    /// treat that as "unconfigured, needs an override" and <b>not</b> as auto-approve — the two are
    /// indistinguishable through <c>IsAutoApprove</c> alone, which is why
    /// <see cref="RollbackService"/> checks <c>PolicyId</c> first.</para>
    /// </summary>
    public static ResolvedPolicySnapshot Project(RollbackPolicy? policy)
    {
        if (policy is null)
            return new ResolvedPolicySnapshot(PolicyId: null, EscalationGroup: null);

        return new ResolvedPolicySnapshot(
            PolicyId: policy.Id,
            EscalationGroup: policy.EscalationGroup)
        {
            ApprovalSteps = policy.ApprovalSteps,
        };
    }
}
