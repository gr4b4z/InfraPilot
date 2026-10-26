namespace Platform.Api.Features.Analytics.Models;

// Delivery metrics -------------------------------------------------------------------------
//
// One envelope for every metric the Delivery metrics report shows, DORA's four included. Every
// metric carries the same fields (value, sample size, delta, trend, coverage, definition) so the
// page renders them generically and a new metric is a backend-only change. Sections group them
// for the reader; the DORA section is the one most people come for.

/// <summary>
/// One metric. <paramref name="Available"/> is false when the installation cannot measure it at
/// all (no incident data, say) — then <paramref name="Reason"/> says what would make it
/// measurable and <paramref name="Value"/> is null. An available metric with nothing in the
/// window has <paramref name="N"/> 0 and a null value: no data, not "not a feature".
/// </summary>
/// <param name="Unit"><c>count</c> | <c>hours</c> | <c>ratio</c>.</param>
/// <param name="GoodWhen">Which movement reads as improvement: <c>up</c> | <c>down</c> | <c>none</c>.</param>
/// <param name="Definition">How the number was counted, spelled out — the ⓘ text, and what a
/// report must quote beside the figure.</param>
public record DeliveryMetricDto(
    string Key,
    string Title,
    bool Available,
    string? Reason,
    string Unit,
    double? Value,
    int N,
    DeliveryPercentilesDto? Percentiles,
    DeliveryDeltaDto? Delta,
    List<DeliveryTrendPointDto> Trend,
    DeliveryCoverageDto? Coverage,
    string Definition,
    string GoodWhen);

public record DeliveryPercentilesDto(double? P50, double? P75, double? P90);

/// <summary>Same metric over the equal-length window immediately before. Change = Value − Previous.</summary>
public record DeliveryDeltaDto(double? Previous, double? Change, int PreviousN);

public record DeliveryTrendPointDto(string Start, double? Value, int N);

/// <summary>How much of the population the metric could see — the qualifier every ticket-derived number needs.</summary>
public record DeliveryCoverageDto(string Label, int Numerator, int Denominator, double Ratio);

public record DeliverySectionDto(string Key, string Title, List<DeliveryMetricDto> Metrics);

/// <summary>The production set the report measured against, and which rule chose it.</summary>
/// <param name="Source"><c>marked</c> | <c>default-name</c> | <c>convention</c>.</param>
/// <param name="IgnoredProductionLike">Environments whose name reads as production but that the
/// resolution left out — unconfigured <c>prod</c> next to a marked <c>production</c>, say. The trap
/// this list exists to show: deploys there count nowhere on the page until an admin marks or
/// aliases the key.</param>
public record DeliveryProductionDto(List<string> Environments, string Source, string Explanation, List<string> IgnoredProductionLike);

public record DeliveryDefinitionDto(
    string Bucket,
    string Tz,
    // What "a deploy" is for the counts.
    string Deploy,
    // What "production" is and how it was chosen.
    string Production,
    // What "a story shipped" is.
    string StoryShipped,
    // Lead time clock.
    string LeadTime);

public record DeliveryProductRowDto(
    string Product,
    int Services,
    int Deploys,
    int PreviousDeploys,
    int StoriesShipped,
    double? LeadTimeP50Hours,
    double? LeadTimeCoverage,
    double? DeployFailureRate,
    double? RollbackRate,
    double? StoryCoverage,
    int StaleServices,
    int Incidents,
    double? ChangeFailureRate,
    double? TimeToRestoreP50Hours);

public record DeliveryServiceRowDto(
    string Product,
    string Service,
    int Deploys,
    int PreviousDeploys,
    double PerWeek,
    DateTimeOffset? LastDeployedAt,
    double? MedianIntervalHours,
    int Failed,
    int Rollbacks,
    double? BatchSizeP50,
    double? LeadTimeP50Hours,
    int Incidents);

/// <summary>
/// One incident as the report saw it — the admin-only drill-down behind change failure rate and time
/// to restore. <paramref name="Attribution"/>: <c>explicit</c> (deploy named), <c>version</c>,
/// <c>inferred</c> (live at start), <c>outside-window</c> (a deploy was found but older than the
/// attribution window), <c>none</c>.
/// </summary>
public record DeliveryIncidentDto(
    Guid Id,
    string Product,
    string Service,
    string Environment,
    DateTimeOffset StartedAt,
    DateTimeOffset? ResolvedAt,
    double? DurationHours,
    string Attribution,
    Guid? DeployEventId,
    DateTimeOffset? DeployedAt,
    string? Version,
    string? Url);

public record DeliveryLeadTimeEnvDto(string Environment, int N, double? P50Hours, double? P75Hours, double? P90Hours);

public record DeliveryMetricsResponseDto(
    // When the numbers were computed. They are live — a later backfill moves them — so two
    // screenshots that disagree can be explained by this line.
    DateTimeOffset AsOf,
    AnalyticsRangeDto Range,
    AnalyticsRangeDto PreviousRange,
    // The products the numbers cover: the selection, or every product seen in either window.
    List<string> Products,
    DeliveryProductionDto Production,
    DeliveryDefinitionDto Definition,
    List<DeliverySectionDto> Sections,
    // Lead time per environment (cumulative from commit), settings-ordered — where the time goes.
    List<DeliveryLeadTimeEnvDto> LeadTimeByEnvironment,
    List<DeliveryProductRowDto> ByProduct,
    List<DeliveryServiceRowDto> ByService,
    // The incidents behind the DORA figures. Null for non-admins: the aggregates are for everyone,
    // the records are not.
    List<DeliveryIncidentDto>? Incidents);
