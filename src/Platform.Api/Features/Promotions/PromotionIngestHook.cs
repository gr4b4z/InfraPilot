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
///   <item><b>Completion matching (D18):</b> when a succeeded deploy event lands on a target
///   environment and matches a candidate by <c>(product, service, target_env, version)</c>, mark
///   that candidate <see cref="PromotionStatus.Deployed"/> — whatever state it was in, including
///   Pending and Rejected, since the version is live either way. Each transition leaves a comment
///   on the candidate saying which deploy closed it. Ingestion stops <i>creating</i> promotions but
///   still <i>closes</i> them.</item>
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

                // 3) Supersede the ones it overtook: if this landing is newer than a promotion still
                //    waiting to deploy, that promotion is never going out.
                await SupersedeOvertakenAsync(deployEvent, ct);
            }

            // 4) Match any in-flight rollback this event completes — even when the operator forgot
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
        // regardless of whether it was deployed via our executor or out-of-band — and regardless of
        // what state the candidate was in. Approved/Deploying is the ordinary path (we accept
        // Approved to be resilient: the external CI may have skipped past "Deploying" by never
        // calling back). Pending and Rejected are the out-of-band ones: nobody here dispatched the
        // deploy, but the version is live in the target env, so the candidate describing it has to
        // say Deployed. Superseded is excluded — a newer candidate owns that edge and closes instead.
        //
        // Only a succeeded deploy counts. A failed attempt did not put the version live, so nothing
        // completes and every matching candidate keeps waiting.
        if (!string.Equals(landing.Status, "succeeded", StringComparison.OrdinalIgnoreCase)) return;

        var matches = await _db.PromotionCandidates
            .Where(c => c.Product == landing.Product
                     && c.Service == landing.Service
                     && c.TargetEnv == landing.Environment
                     && c.Version == landing.Version
                     && c.Status != PromotionStatus.Deployed
                     && c.Status != PromotionStatus.Superseded)
            .ToListAsync(ct);

        if (matches.Count == 0) return;

        foreach (var candidate in matches)
        {
            try
            {
                // The deploy's own timestamp, not now: this event is the authority on when the version
                // went live, and it may be describing something that happened well before ingest.
                await _promotions.MarkDeployedAsync(
                    candidate.Id, CompletionNote(candidate, landing), landing.DeployedAt, ct);
            }
            catch (InvalidOperationException ex)
            {
                // Race: candidate moved on between the read and the transition. Fine — log and
                // continue so a stuck sibling doesn't block others.
                _logger.LogWarning(ex,
                    "Could not close candidate {CandidateId} from deploy event {EventId}",
                    candidate.Id, landing.Id);
            }
        }
    }

    /// <summary>
    /// Supersedes promotions this landing overtook — the target environment has moved to a newer
    /// version, so the one they are still waiting to deploy is never going out. See
    /// <see cref="PromotionService.SupersedeOvertakenByDeployAsync"/> for the conservatism this relies
    /// on; an unorderable version pair leaves the promotion alone.
    /// </summary>
    private async Task SupersedeOvertakenAsync(DeployEvent landing, CancellationToken ct)
    {
        // Only a succeeded landing moves an environment on. A failed attempt overtook nothing.
        if (!string.Equals(landing.Status, "succeeded", StringComparison.OrdinalIgnoreCase)) return;

        var count = await _promotions.SupersedeOvertakenByDeployAsync(
            landing.Product, landing.Service, landing.Environment, landing.Version, landing.DeployedAt, ct);

        if (count > 0)
            _logger.LogInformation(
                "Deploy event {EventId} superseded {Count} overtaken promotion(s)", landing.Id, count);
    }

    /// <summary>
    /// What to write on the candidate's comment thread when this deploy closes it. A candidate we
    /// dispatched gets a plain statement; one that was still open — or that somebody had rejected —
    /// gets the fuller story, because "why is a rejected promotion marked Deployed?" is the first
    /// question whoever opens it will ask.
    /// </summary>
    private static string CompletionNote(PromotionCandidate candidate, DeployEvent landing)
    {
        var landed =
            $"{candidate.Service} {candidate.Version} was deployed to {candidate.TargetEnv} "
            + $"on {landing.DeployedAt:u}";

        return candidate.Status switch
        {
            PromotionStatus.Rejected =>
                $"Deployed anyway — {landed} outside this promotion, after it had been rejected. "
                + "The rejection stands in the approval trail; this promotion is closed because the "
                + "version is now live.",

            PromotionStatus.Pending =>
                $"Closed automatically — {landed} outside this promotion, so there is nothing left "
                + "to approve.",

            _ => $"Deployed to {candidate.TargetEnv} — {landed}.",
        };
    }
}
