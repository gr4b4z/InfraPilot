namespace Platform.Api.Features.Analytics.Models;

// Shared -----------------------------------------------------------------------------------

/// <summary>The reporting window a response was computed over. Half-open: [From, To).</summary>
public record AnalyticsRangeDto(DateTimeOffset From, DateTimeOffset To);

/// <summary>Percentile stats over a duration set. Hours are fractional; null when N is 0.</summary>
public record LatencyStatsDto(int N, double? P50Hours = null, double? P90Hours = null);

// Deployment frequency ---------------------------------------------------------------------

/// <summary>
/// Echo of what was counted. Returned with every response so a chart pasted into a report
/// stays self-describing — two frequency numbers are only comparable when their definitions
/// match, and this block is how a reader checks that.
/// </summary>
public record FrequencyDefinitionDto(
    string Bucket,
    string GroupBy,
    string Tz,
    bool IncludeRollbacks,
    bool IncludeRedeploys,
    // How Summary.ChangeFailureRate is computed, spelled out.
    string ChangeFailureRate);

public record FrequencySeriesKeyDto(string? Product, string? ServiceName, string? Environment);

/// <summary>One time bucket. Count is per the definition; Failed/Rollbacks always reported.</summary>
public record FrequencyBucketDto(string Start, int Count, int Failed, int Rollbacks);

public record FrequencySummaryDto(
    int Total,
    double PerWeek,
    double? MedianIntervalHours,
    double? LongestGapHours,
    DateTimeOffset? LastDeployedAt,
    double? ChangeFailureRate,
    int PreviousPeriodTotal,
    // Work items per counted deployment (p50), zeros included — a deploy with no tracked
    // ticket counts as 0, so this doubles as a coverage smell when it sits below 1.
    double? BatchSizeP50);

public record FrequencySeriesDto(
    FrequencySeriesKeyDto Key,
    List<FrequencyBucketDto> Buckets,
    FrequencySummaryDto Summary);

public record FrequencyResponseDto(
    FrequencyDefinitionDto Definition,
    AnalyticsRangeDto Range,
    List<FrequencySeriesDto> Series);

// Work-item × environment matrix -----------------------------------------------------------

/// <summary>
/// How trustworthy the matrix is: of the product's deployments in the window, how many carried
/// no work-item reference at all. First-class on purpose — with real-world coverage around
/// two-thirds, a story count shown without this number actively misleads.
/// </summary>
public record MatrixCoverageDto(int Deployments, int WithoutWorkItem, double Ratio);

/// <summary>
/// One cell. State: "deployed" | "approved-awaiting-deploy" | "awaiting-approval" | "absent".
/// Deployed cells link the deploy event; pending states link the promotion candidate.
/// </summary>
public record MatrixCellDto(
    string State,
    string? Version = null,
    DateTimeOffset? At = null,
    Guid? DeployEventId = null,
    Guid? CandidateId = null);

public record MatrixItemDto(
    string Key,
    string? Title,
    string? Url,
    // The furthest environment (settings order) this story has a successful deploy in.
    string? FurthestEnv,
    Dictionary<string, MatrixCellDto> Envs,
    DateTimeOffset LastActivityAt);

public record MatrixResponseDto(
    // Settings-ordered; environments the product has actually deployed to (unknown keys appended).
    List<string> Environments,
    MatrixCoverageDto Coverage,
    // Per environment: how many of the selected stories are deployed there.
    Dictionary<string, int> Totals,
    int TotalItems,
    List<MatrixItemDto> Items,
    AnalyticsRangeDto Range);

// Promotion queue --------------------------------------------------------------------------

public record QueueEdgeDto(
    string Product,
    string TargetEnv,
    int Pending,
    int AwaitingDeploy,
    double? OldestPendingHours,
    double? OldestAwaitingDeployHours);

public record QueueResponseDto(
    List<QueueEdgeDto> Edges,
    // CreatedAt → ApprovedAt for candidates approved inside the window.
    LatencyStatsDto ApprovalLatency,
    // ApprovedAt → DeployedAt for candidates deployed inside the window.
    LatencyStatsDto DeployLatency,
    AnalyticsRangeDto Range);

// Lead time --------------------------------------------------------------------------------

public record LeadTimeDefinitionDto(
    string ClockStart,
    string ClockStartFallback,
    string ClockStop,
    string Grain);

public record LeadTimeCoverageDto(int WorkItems, int WithClockStart, double Ratio);

public record LeadTimeEnvStatsDto(string Environment, int N, double? P50Hours, double? P75Hours, double? P90Hours);

public record LeadTimeBucketDto(string Start, string Environment, int N, double? P50Hours);

public record LeadTimeSlowestDto(string WorkItemKey, string Environment, double Hours, Guid DeployEventId);

public record LeadTimeResponseDto(
    LeadTimeDefinitionDto Definition,
    LeadTimeCoverageDto Coverage,
    List<LeadTimeEnvStatsDto> ByEnvironment,
    List<LeadTimeBucketDto> Buckets,
    List<LeadTimeSlowestDto> Slowest,
    AnalyticsRangeDto Range);
