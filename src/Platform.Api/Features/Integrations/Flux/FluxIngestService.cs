using System.Text.Json;
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

    public record Result(DeploymentService.IngestResult Ingest, Guid? PromotionId, IReadOnlyList<string> RolledBackVersions);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public async Task<Result> IngestAsync(CreateDeployEventDto mapped, CancellationToken ct = default)
    {
        var (product, environment) = await _deployments.ResolveKeyAsync(
            mapped.Product, mapped.Service, mapped.Environment, ct);

        var candidate = await FindCandidateAsync(product, environment, mapped, ct);
        if (candidate is not null)
        {
            var dto = mapped with
            {
                References = candidate.References,
                Participants = candidate.Participants
                    .Select(p => new ParticipantDto(p.Role, p.DisplayName, p.Email))
                    .ToList(),
                Metadata = WithMetadata(mapped, "promotionId", candidate.Id),
            };
            return new Result(await _deployments.IngestEventWithResult(dto, ct), candidate.Id, []);
        }

        // A rollback has no candidate, but it goes back to a version this environment already ran, so
        // the change it undoes is in the deploy history: everything deployed here since that version.
        var rollback = await FindRolledBackAsync(product, environment, mapped, ct);
        if (rollback is not null)
        {
            var dto = mapped with
            {
                IsRollback = true,
                References = rollback.References,
                Metadata = WithMetadata(mapped, "rolledBackVersions", rollback.Versions),
            };
            return new Result(await _deployments.IngestEventWithResult(dto, ct), null, rollback.Versions);
        }

        _logger.LogInformation(
            "Flux event for {Product}/{Service} {Version} → {Environment} matches no open promotion and is not a rollback; ingesting without references",
            mapped.Product, mapped.Service, mapped.Version, mapped.Environment);
        return new Result(await _deployments.IngestEventWithResult(mapped, ct), null, []);
    }

    private static Dictionary<string, object> WithMetadata(CreateDeployEventDto mapped, string key, object value)
        => new(mapped.Metadata ?? new()) { [key] = value };

    private sealed record RolledBack(List<ReferenceDto> References, List<string> Versions);

    /// <summary>
    /// When <paramref name="mapped"/> lands a version this environment ran before and has since moved
    /// past, the versions deployed after it are the ones being undone, and their references are the
    /// change the rollback removes. Null when the version is new here or still the current one
    /// (a redeploy). Only succeeded, forward deploys count: a failed attempt changed nothing, and an
    /// earlier rollback's references already describe a removal rather than a change.
    /// </summary>
    private async Task<RolledBack?> FindRolledBackAsync(
        string product, string environment, CreateDeployEventDto mapped, CancellationToken ct)
    {
        var history = (await _db.DeployEvents.AsNoTracking()
                .Where(e => e.Product == product
                         && e.Service == mapped.Service
                         && e.Environment == environment
                         && e.Status == "succeeded")
                .Select(e => new { e.Version, e.DeployedAt, e.IsRollback, e.ReferencesJson })
                .ToListAsync(ct))
            .Where(e => e.DeployedAt < mapped.DeployedAt)
            .OrderBy(e => e.DeployedAt)
            .ToList();

        var lastTarget = history.FindLastIndex(e => e.Version == mapped.Version);
        if (lastTarget < 0) return null;

        var undone = history.Skip(lastTarget + 1).Where(e => e.Version != mapped.Version).ToList();
        if (undone.Count == 0) return null;

        var references = undone
            .Where(e => !e.IsRollback)
            .SelectMany(e => JsonSerializer.Deserialize<List<ReferenceDto>>(e.ReferencesJson, JsonOptions) ?? [])
            .DistinctBy(r => (r.Type.ToLowerInvariant(), (r.Key ?? r.Url ?? r.Title ?? "").ToLowerInvariant()))
            .ToList();
        var versions = undone.Select(e => e.Version).Distinct().ToList();

        return new RolledBack(references, versions);
    }

    /// <summary>
    /// The candidate this deploy delivers. Several can match when the version is promoted into the
    /// same environment from different sources; the one furthest along wins (it is the one that was
    /// dispatched), then the newest.
    /// </summary>
    private async Task<PromotionCandidate?> FindCandidateAsync(
        string product, string environment, CreateDeployEventDto mapped, CancellationToken ct)
    {
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
