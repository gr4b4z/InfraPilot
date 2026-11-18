using Microsoft.EntityFrameworkCore;
using Platform.Api.Features.Deployments;
using Platform.Api.Features.Deployments.Models;
using Platform.Api.Features.Promotions.Models;
using Platform.Api.Infrastructure.Persistence;

namespace Platform.Api.Features.Integrations.Flux;

/// <summary>
/// Ingests a mapped Flux event. Flux knows nothing about the change it installed, so the event takes
/// the change set of the promotion candidate it delivers: the one the promotion hook is about to
/// close (same product, service, version and target environment, not yet <c>Deployed</c> or
/// <c>Superseded</c>). Its references are the net change set people reviewed and signed off.
/// Storing goes through <see cref="DeploymentService.IngestEventWithResult"/>, so webhooks, promotion
/// and rollback completion, analytics and release notes treat it like any other deploy event.
/// </summary>
public class FluxIngestService
{
    private readonly DeploymentService _deployments;
    private readonly PlatformDbContext _db;
    private readonly ILogger<FluxIngestService> _logger;

    public FluxIngestService(DeploymentService deployments, PlatformDbContext db, ILogger<FluxIngestService> logger)
    {
        _deployments = deployments;
        _db = db;
        _logger = logger;
    }

    public record Result(DeploymentService.IngestResult Ingest, Guid? PromotionId);

    public async Task<Result> IngestAsync(CreateDeployEventDto mapped, CancellationToken ct = default)
    {
        var candidate = await FindCandidateAsync(mapped, ct);

        var dto = candidate is null
            ? mapped
            : mapped with
            {
                References = candidate.References,
                Participants = candidate.Participants
                    .Select(p => new ParticipantDto(p.Role, p.DisplayName, p.Email))
                    .ToList(),
                Metadata = new Dictionary<string, object>(mapped.Metadata ?? new())
                {
                    ["promotionId"] = candidate.Id,
                },
            };

        if (candidate is null)
            _logger.LogInformation(
                "Flux event for {Product}/{Service} {Version} → {Environment} matches no open promotion; ingesting without references",
                mapped.Product, mapped.Service, mapped.Version, mapped.Environment);

        var ingest = await _deployments.IngestEventWithResult(dto, ct);
        return new Result(ingest, candidate?.Id);
    }

    /// <summary>
    /// The candidate this deploy delivers. Several can match when the version is promoted into the
    /// same environment from different sources; the one furthest along wins (it is the one that was
    /// dispatched), then the newest.
    /// </summary>
    private async Task<PromotionCandidate?> FindCandidateAsync(CreateDeployEventDto mapped, CancellationToken ct)
    {
        var (product, environment) = await _deployments.ResolveKeyAsync(
            mapped.Product, mapped.Service, mapped.Environment, ct);

        var matches = await _db.PromotionCandidates.AsNoTracking()
            .Where(c => c.Product == product
                     && c.Service == mapped.Service
                     && c.TargetEnv == environment
                     && c.Version == mapped.Version
                     && c.Status != PromotionStatus.Deployed
                     && c.Status != PromotionStatus.Superseded)
            .ToListAsync(ct);

        return matches
            .OrderBy(c => Rank(c.Status))
            .ThenByDescending(c => c.CreatedAt)
            .FirstOrDefault();
    }

    private static int Rank(PromotionStatus status) => status switch
    {
        PromotionStatus.Deploying => 0,
        PromotionStatus.Approved => 1,
        PromotionStatus.Pending => 2,
        PromotionStatus.Rejected => 3,
        _ => 4,
    };
}
