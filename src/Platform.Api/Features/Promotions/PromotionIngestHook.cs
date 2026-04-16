using Microsoft.EntityFrameworkCore;
using Platform.Api.Features.Deployments.Models;
using Platform.Api.Features.Promotions.Models;
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
/// Wires the promotion machinery into deploy-event ingestion. Two responsibilities:
///
/// <list type="number">
///   <item><b>Candidate generation (P3.B):</b> for each target environment reachable from the
///   ingested event's source environment, create a <see cref="PromotionCandidate"/>.</item>
///
///   <item><b>Completion matching (P3.C):</b> when a deploy event lands on a target environment
///   and matches an in-flight candidate by <c>(product, service, target_env, version)</c>, mark
///   that candidate <see cref="PromotionStatus.Deployed"/>.</item>
/// </list>
///
/// <para>All work is gated behind the <c>features.promotions</c> flag — the hook early-exits
/// when the feature is disabled so ingestion stays lean for deployments that don't need it.</para>
/// </summary>
public class PromotionIngestHook : IPromotionIngestHook
{
    private readonly IFeatureFlags _flags;
    private readonly PromotionTopologyService _topology;
    private readonly PromotionService _promotions;
    private readonly PlatformDbContext _db;
    private readonly ILogger<PromotionIngestHook> _logger;

    public PromotionIngestHook(
        IFeatureFlags flags,
        PromotionTopologyService topology,
        PromotionService promotions,
        PlatformDbContext db,
        ILogger<PromotionIngestHook> logger)
    {
        _flags = flags;
        _topology = topology;
        _promotions = promotions;
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
            if (!await _flags.IsEnabled(FeatureFlagKeys.Promotions, ct)) return;

            // 1) Match any in-flight candidate that this event completes.
            await MatchCompletionAsync(deployEvent, ct);

            // 2) Generate new candidates for downstream environments.
            await GenerateCandidatesAsync(deployEvent, ct);
        }
        catch (Exception ex)
        {
            // Swallow and log: ingestion must never 500 because of promotion bookkeeping.
            _logger.LogError(ex,
                "Promotion ingest hook failed for deploy event {EventId}", deployEvent.Id);
        }
    }

    private async Task GenerateCandidatesAsync(DeployEvent source, CancellationToken ct)
    {
        var nexts = await _topology.GetNextEnvironmentsAsync(source.Environment, ct);
        if (nexts.Count == 0) return;

        foreach (var target in nexts)
        {
            await _promotions.CreateCandidateAsync(source, target, ct);
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
