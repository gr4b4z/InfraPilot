using Microsoft.EntityFrameworkCore;
using Platform.Api.Features.Deployments;
using Platform.Api.Features.Deployments.Models;
using Platform.Api.Features.Promotions.Models;
using Platform.Api.Features.Rollbacks;
using Platform.Api.Infrastructure.Features;
using Platform.Api.Infrastructure.Persistence;

namespace Platform.Api.Features.Promotions;

/// <summary>
/// Contract the deployment service uses to notify the promotion subsystem of new events.
/// Having this as an interface lets tests substitute a no-op and keeps <c>DeploymentService</c>
/// unaware of the promotion implementation details.
/// </summary>
public interface IPromotionIngestHook
{
    Task OnIngestedAsync(DeployEvent deployEvent, CancellationToken ct = default);
}

/// <summary>
/// Wires the promotion machinery into deploy-event ingestion.
///
/// <para>Promotion candidates are no longer auto-generated on ingest (D18/D19) — they are created
/// externally via the create-promotion API (an external system POSTs the authoritative net change
/// set). The hook keeps two ingest-driven concerns:</para>
///
/// <list type="number">
///   <item><b>Work-item sync:</b> projects the event's <c>work-item</c> references into
///   <see cref="DeployEventWorkItem"/> for deploy-history ("which builds carry ticket X"). This no
///   longer feeds the promotion gate (that reads <see cref="PromotionWorkItem"/>), but the table
///   has other readers (backfill, history).</item>
///
///   <item><b>Completion matching (D18):</b> when a deploy event lands on a target environment and
///   matches an in-flight candidate by <c>(product, service, target_env, version)</c>, mark that
///   candidate <see cref="PromotionStatus.Deployed"/>. Ingestion stops <i>creating</i> promotions
///   but still <i>closes</i> them.</item>
/// </list>
///
/// <para>All work is gated behind the <c>features.promotions</c> flag — the hook early-exits
/// when the feature is disabled so ingestion stays lean for deployments that don't need it.</para>
/// </summary>
public class PromotionIngestHook : IPromotionIngestHook
{
    private readonly IFeatureFlags _flags;
    private readonly PromotionService _promotions;
    private readonly RollbackService _rollbacks;
    private readonly WorkItemSyncService _workItemSync;
    private readonly PlatformDbContext _db;
    private readonly ILogger<PromotionIngestHook> _logger;

    public PromotionIngestHook(
        IFeatureFlags flags,
        PromotionService promotions,
        RollbackService rollbacks,
        WorkItemSyncService workItemSync,
        PlatformDbContext db,
        ILogger<PromotionIngestHook> logger)
    {
        _flags = flags;
        _promotions = promotions;
        _rollbacks = rollbacks;
        _workItemSync = workItemSync;
        _db = db;
        _logger = logger;
    }

    /// <summary>
    /// Called after a deploy event has been persisted. Best-effort: failures here log and
    /// swallow so ingestion never 500s because of promotion bookkeeping.
    /// </summary>
    public async Task OnIngestedAsync(DeployEvent deployEvent, CancellationToken ct = default)
    {
        try
        {
            var promotionsOn = await _flags.IsEnabled(FeatureFlagKeys.Promotions, ct);

            if (promotionsOn)
            {
                // 1) Project work-item references into the deploy-history index (DeployEventWorkItem).
                //    No longer feeds the promotion gate — kept for deploy-history readers.
                await _workItemSync.SyncAsync(deployEvent, ct);
                await _db.SaveChangesAsync(ct);

                // 2) Match any in-flight promotion candidate that this event completes (D18).
                await MatchCompletionAsync(deployEvent, ct);
            }

            // 3) Match any in-flight rollback this event completes — even when the operator forgot
            //    to set IsRollback on the deploy.
            if (await _flags.IsEnabled(FeatureFlagKeys.Rollbacks, ct))
                await _rollbacks.MatchCompletionAsync(deployEvent, ct);

            // Candidate generation removed (D19): promotions are created externally via the
            // create-promotion API, not derived from deploy events.
        }
        catch (Exception ex)
        {
            // Swallow and log: ingestion must never 500 because of promotion bookkeeping.
            _logger.LogError(ex,
                "Promotion ingest hook failed for deploy event {EventId}", deployEvent.Id);
        }
    }

    private async Task MatchCompletionAsync(DeployEvent landing, CancellationToken ct)
    {
        // A candidate "completes" when a matching version lands in its target environment,
        // regardless of whether it was deployed via our executor or out-of-band. We also accept
        // the Approved state to be resilient: if the external CI skipped past "Deploying" (e.g.
        // it never called back to us) we still want to close the loop on the deploy event.
        var matches = await _db.PromotionCandidates
            .Where(c => c.Product == landing.Product
                     && c.Service == landing.Service
                     && c.TargetEnv == landing.Environment
                     && c.Version == landing.Version
                     && (c.Status == PromotionStatus.Deploying || c.Status == PromotionStatus.Approved))
            .ToListAsync(ct);

        if (matches.Count == 0) return;

        foreach (var candidate in matches)
        {
            try
            {
                await _promotions.MarkDeployedAsync(candidate.Id, ct);
            }
            catch (InvalidOperationException ex)
            {
                // Race: candidate moved on between the read and the transition. Fine — log and
                // continue so a stuck sibling doesn't block others.
                _logger.LogWarning(ex,
                    "Could not mark candidate {CandidateId} as Deployed", candidate.Id);
            }
        }
    }
}
