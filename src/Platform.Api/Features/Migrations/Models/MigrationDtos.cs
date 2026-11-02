using Platform.Api.Features.Deployments.Models;

namespace Platform.Api.Features.Migrations.Models;

// --- Input DTOs ---

/// <summary>
/// What a pipeline reports about one migration — <c>POST /api/migrations/runs</c>. Posted as the
/// migration moves: a data migration is reported Pending when the deploy queues it, Running when the
/// job starts and Succeeded / Failed when it ends, each time to the same
/// <c>(kind, product, service, environment, version, runKey)</c>, which the server upserts.
/// </summary>
/// <param name="Kind"><c>schema</c> or <c>data</c>.</param>
/// <param name="RunKey">
/// Optional discriminator between executions of the same version. Omit it for data migrations
/// (one per version); a schema migration re-run by a redeploy may send the deploying run's id.
/// </param>
/// <param name="DeployEventId">
/// The deploy event this migration belongs to, when the producer has its id. Otherwise the server
/// links it to the deployment of the same version in the same environment.
/// </param>
/// <param name="Migrations">
/// The individual migrations and how each went. When omitted, the server reads them out of
/// <paramref name="Logs"/>, which is what the migration library prints anyway.
/// </param>
public record ReportMigrationRunDto(
    string Kind,
    string Product,
    string Service,
    string Environment,
    string Version,
    string Status,
    string Source,
    string? RunKey = null,
    Guid? DeployEventId = null,
    DateTimeOffset? QueuedAt = null,
    DateTimeOffset? StartedAt = null,
    DateTimeOffset? CompletedAt = null,
    string? SkipReason = null,
    DeployRun? Run = null,
    List<MigrationItem>? Migrations = null,
    List<CreateDeployLogDto>? Logs = null);

// --- Output DTOs ---

/// <summary>A migration as the list and the deployment page show it — everything but items and logs.</summary>
public record MigrationRunSummaryDto(
    Guid Id,
    string Kind,
    string Product,
    string Service,
    string Environment,
    string Version,
    string Status,
    string Source,
    Guid? DeployEventId,
    DateTimeOffset QueuedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    int Attempts,
    int AppliedCount,
    int FailedCount,
    int TotalCount,
    string? FailureReason,
    string? SkipReason,
    Guid? SupersededById,
    string? RunUrl,
    DateTimeOffset UpdatedAt);

public record MigrationRunListDto(List<MigrationRunSummaryDto> Items, int Total);

/// <summary>
/// One migration in full: the run, what ran inside it, its captured output (summaries — text is a
/// separate call), the deployment it belongs to, and — for data migrations — the newer one that
/// replaced it or the older ones it replaced.
/// </summary>
public record MigrationRunDetailDto(
    MigrationRunSummaryDto Run,
    DeployRun? CiRun,
    List<MigrationItem> Migrations,
    List<DeployLogSummaryDto> Logs,
    MigrationDeploymentDto? Deployment,
    MigrationRunSummaryDto? SupersededBy,
    List<MigrationRunSummaryDto> Supersedes,
    List<MigrationRunSummaryDto> History);

/// <summary>The deploy event a migration belongs to — enough for a link and a status chip.</summary>
public record MigrationDeploymentDto(
    Guid Id,
    string Version,
    string Status,
    string Source,
    DateTimeOffset DeployedAt);

/// <summary>
/// A migration that needs attention — Pending, Running or Failed and not superseded. What the
/// deployment views overlay as a flare. Data migrations are matched to a matrix cell by
/// (service, environment) — only one can be open per cell — and schema migrations by deploy event.
/// </summary>
public record MigrationFlagDto(
    Guid Id,
    string Kind,
    string Product,
    string Service,
    string Environment,
    string Version,
    string Status,
    Guid? DeployEventId,
    DateTimeOffset QueuedAt,
    int FailedCount,
    string? FailureReason);

/// <summary>
/// Answer to "has this migration run there?" — <c>GET /api/migrations/search</c>. One entry per
/// migration name that matched the query, best match first, each with where it has been seen.
/// </summary>
/// <param name="Truncated">More names matched than were returned; the query should be narrowed.</param>
public record MigrationSearchResultDto(string Query, List<MigrationSearchMatchDto> Matches, bool Truncated);

/// <param name="Score">How literal the match was, 0–100 (100 = the exact name).</param>
public record MigrationSearchMatchDto(string Name, int Score, List<MigrationSearchHitDto> Hits);

/// <summary>
/// One migration in one environment of one service. <c>Status</c> is <c>applied</c> when any run
/// recorded it applied — the library never runs an applied migration again, so later runs don't
/// mention it — otherwise the latest run's word for it (<c>failed</c>, <c>rolled-back</c>,
/// <c>pending</c>…). <c>RunId</c>/<c>Version</c> point at the run that applied it, or the latest one
/// that mentioned it.
/// </summary>
public record MigrationSearchHitDto(
    string Product,
    string Service,
    string Environment,
    string Kind,
    string Status,
    DateTimeOffset? AppliedAt,
    string Version,
    Guid RunId,
    Guid? DeployEventId,
    DateTimeOffset SeenAt,
    int Runs,
    string? Error);

/// <summary>Result of re-reading stored deploy logs for schema migrations (admin backfill).</summary>
public record MigrationDeriveResultDto(int Examined, int Created, int Updated, int Unchanged);
