using Microsoft.EntityFrameworkCore;
using Platform.Api.Features.Builds.Models;
using Platform.Api.Features.Promotions;
using Platform.Api.Infrastructure;
using Platform.Api.Infrastructure.Features;
using Platform.Api.Infrastructure.Persistence;

namespace Platform.Api.Features.Builds;

/// <summary>
/// Contract the build service uses to notify the promotion subsystem of registered builds. An
/// interface for the same reason as <see cref="IPromotionIngestHook"/>: tests substitute a no-op,
/// and <c>BuildService</c> stays unaware of promotion internals.
/// </summary>
public interface IBuildIngestHook
{
    Task OnRegisteredAsync(Build build, CancellationToken ct = default);
}

/// <summary>
/// Wires the promotion machinery into build registration — the twin of
/// <see cref="PromotionIngestHook"/> on the build side, and the whole of D5 (feature-branch-builds):
/// main → dev stops being a hardwired pipeline trigger and becomes a policy decision.
///
/// <para>On every registration, resolve the policies for this product/service whose source is the
/// synthetic <see cref="BuildPromotions.SourceEnv"/>; for each edge whose
/// <c>AutoCreateFromBranches</c> matches the build's branch, create a candidate carrying the
/// build's version, commit and manifest-copied references. A branch that matches nothing creates
/// nothing — feature builds sit in the registry until someone asks for them.</para>
///
/// <para>Runs on replays too: registration replays re-run this hook the same way deploy-ingest
/// replays re-run <see cref="PromotionIngestHook"/> — candidate creation is idempotent on its
/// natural key, and a hook failure the first time round is repaired by the pipeline's retry.</para>
/// </summary>
public class BuildIngestHook : IBuildIngestHook
{
    private readonly IFeatureFlags _flags;
    private readonly PromotionService _promotions;
    private readonly PlatformDbContext _db;
    private readonly ILogger<BuildIngestHook> _logger;

    public BuildIngestHook(
        IFeatureFlags flags,
        PromotionService promotions,
        PlatformDbContext db,
        ILogger<BuildIngestHook> logger)
    {
        _flags = flags;
        _promotions = promotions;
        _db = db;
        _logger = logger;
    }

    /// <summary>
    /// Called after a build has been persisted. Best-effort: failures log and swallow so a
    /// promotion misconfiguration can never fail the registration POST — the pipeline treats a
    /// non-2xx as a stage failure (D11), and the build itself was recorded fine.
    /// </summary>
    public async Task OnRegisteredAsync(Build build, CancellationToken ct = default)
    {
        try
        {
            if (!await _flags.IsEnabled(FeatureFlagKeys.Promotions, ct)) return;

            foreach (var policy in await ResolveBuildEdgePoliciesAsync(build.Product, build.Service, ct))
            {
                var patterns = policy.AutoCreateFromBranches;
                if (patterns.Count == 0 || !BuildPromotions.BranchMatches(build.Branch, patterns))
                    continue;

                await CreateCandidateAsync(build, policy.TargetEnv, ct);
            }
        }
        catch (Exception ex)
        {
            // Swallow and log: registration must never 500 because of promotion bookkeeping.
            _logger.LogError(ex, "Build ingest hook failed for build {BuildId}", build.Id);
        }
    }

    /// <summary>
    /// The effective <c>build → *</c> policy per target env for this service: service-specific row
    /// wins over the product default, mirroring <see cref="PromotionPolicyResolver"/> — but across
    /// every target at once, which the one-edge resolver can't answer without knowing the target
    /// up front.
    /// </summary>
    private async Task<List<Promotions.Models.PromotionPolicy>> ResolveBuildEdgePoliciesAsync(
        string product, string service, CancellationToken ct)
    {
        var rows = await _db.PromotionPolicies.AsNoTracking()
            .Where(p => p.Product == product
                     && p.SourceEnv == BuildPromotions.SourceEnv
                     && (p.Service == service || p.Service == null))
            .ToListAsync(ct);

        return rows
            .GroupBy(p => p.TargetEnv)
            .Select(g => g.OrderBy(p => p.Service == null ? 1 : 0).First())
            .ToList();
    }

    private async Task CreateCandidateAsync(Build build, string targetEnv, CancellationToken ct)
    {
        var dto = new CreatePromotionDto(
            Product: build.Product,
            Service: build.Service,
            SourceEnv: BuildPromotions.SourceEnv,
            TargetEnv: targetEnv,
            Version: build.Version,
            FromRevision: null,
            ToRevision: build.CommitSha,
            References: BuildPromotions.BuildReferences(build),
            Participants: null);

        try
        {
            var candidate = await _promotions.CreateExternalCandidateAsync(dto, ct);
            if (candidate is not null)
            {
                _logger.LogInformation(
                    "Build {BuildId} ({Branch}) auto-created promotion candidate {CandidateId} on build → {TargetEnv} ({Status})",
                    build.Id, LogSanitizer.Clean(build.Branch), candidate.Id,
                    LogSanitizer.Clean(targetEnv), candidate.Status);
            }
        }
        catch (TargetAlreadyAtVersionException)
        {
            // The target already runs this version — a registration replay after the deploy went
            // out, most likely. Nothing to promote, nothing to log above debug.
            _logger.LogDebug(
                "Build {BuildId}: {TargetEnv} already at {Version}; no candidate created",
                build.Id, LogSanitizer.Clean(targetEnv), LogSanitizer.Clean(build.Version));
        }
        catch (SourceDeploymentNotFoundException)
        {
            // The policy still requires a source deploy — but nothing is ever deployed to "build".
            // That is a misconfiguration of the edge, and silently never-deploying is the failure
            // mode this plan exists to kill, so say it loudly.
            _logger.LogWarning(
                "Policy for {Product} build → {TargetEnv} has SourceRequiresDeploy=true; the build "
                + "source env never receives deploys, so auto-created promotions will always fail. "
                + "Set sourceRequiresDeploy=false on that policy.",
                LogSanitizer.Clean(build.Product), LogSanitizer.Clean(targetEnv));
        }
    }
}
