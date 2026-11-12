namespace Platform.Api.Features.Releases.Models;

public record ReleaseStepDto(
    Guid Id,
    int Order,
    string Key,
    string Title,
    string? Description,
    string? Owner,
    string? Check,
    string Status,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    string? CompletedBy,
    string? CompletedByName,
    string? Note,
    List<ReleaseLink> Links,
    bool Custom,
    // A standard step only some releases need (the maintenance page). While still to do it doesn't
    // count towards the release's progress.
    bool Optional);

/// <summary>A release as the list shows it: identity, dates and how far its checklist has got.</summary>
public record ReleaseSummaryDto(
    Guid Id,
    string Version,
    string Title,
    List<string> Products,
    string Status,
    DateOnly? TargetDate,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    DateTimeOffset? CancelledAt,
    // Steps done or skipped, out of all steps — leaving out optional steps nobody has started.
    int StepsClosed,
    int StepsTotal,
    // The first of those still open, in order — what the release is on now. Null when all are closed.
    string? CurrentStep,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

// CanCreate: whether the caller may open a release (an administrator or a release manager).
public record ReleaseListDto(List<ReleaseSummaryDto> Items, int Total, bool CanCreate);

/// <summary>
/// One gate requirement that makes its approvers release managers: on the promotion policy of
/// <see cref="Product"/> (and <see cref="Service"/>, for a service-specific policy) from
/// <see cref="SourceEnv"/> into the production environment <see cref="TargetEnv"/>.
/// </summary>
public record ReleaseManagerGateDto(
    Guid PolicyId,
    string Product,
    string? Service,
    string SourceEnv,
    string TargetEnv,
    string Step,
    string Requirement,
    List<Platform.Api.Features.Promotions.Models.GroupRef> Groups,
    List<string> Users);

/// <summary>
/// Who the release managers are: the approvers of the <see cref="Requirement"/> requirement on the
/// production promotion gates (<see cref="Gates"/>). <see cref="CanEdit"/> says whether the caller may
/// change which requirement that is (administrators); <see cref="IsReleaseManager"/> whether the caller
/// is one, for the products asked about (any product when none was).
/// <see cref="KnownRequirements"/> lists every requirement name the gates use.
/// </summary>
public record ReleaseManagersDto(
    string Requirement,
    // The approval step that is the QA gate, for the Test on staging check, and every step name the gates use.
    string QaStep,
    List<string> KnownSteps,
    List<ReleaseManagerGateDto> Gates,
    List<string> KnownRequirements,
    bool CanEdit,
    bool IsReleaseManager);

public record UpdateReleaseManagersRequest(string? Requirement = null, string? QaStep = null);

public record ReleaseDto(
    Guid Id,
    string Version,
    string Title,
    List<string> Products,
    string StagingEnvironment,
    string ProductionEnvironment,
    DateOnly? TargetDate,
    string Status,
    string? Notes,
    List<ReleaseLink> Links,
    // Services of the products left out of the release, as product/service.
    List<string> ExcludedServices,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    DateTimeOffset? CancelledAt,
    string? CancelReason,
    string CreatedBy,
    string CreatedByName,
    DateTimeOffset CreatedAt,
    string? UpdatedBy,
    DateTimeOffset UpdatedAt,
    List<ReleaseStepDto> Steps,
    // Whether the caller may change this release: an administrator or a release manager of its products.
    bool CanManage);

/// <summary>
/// Create a release. The checklist comes from the default template; <see cref="StartedAt"/> and
/// <see cref="CompletedAt"/> may be in the past, which is how a release that already happened is
/// recorded after the fact (each step then takes its own dates through the step update).
/// </summary>
public record CreateReleaseRequest(
    string Version,
    List<string> Products,
    string? Title = null,
    string? StagingEnvironment = null,
    string? ProductionEnvironment = null,
    DateOnly? TargetDate = null,
    string? Notes = null,
    DateTimeOffset? StartedAt = null,
    List<ReleaseLink>? Links = null);

/// <summary>Replace a release's details. Steps and status are changed through their own routes.</summary>
public record UpdateReleaseRequest(
    string Version,
    List<string> Products,
    string? Title = null,
    string? StagingEnvironment = null,
    string? ProductionEnvironment = null,
    DateOnly? TargetDate = null,
    string? Notes = null,
    DateTimeOffset? StartedAt = null,
    List<ReleaseLink>? Links = null);

/// <summary>
/// Change one step. Every field is optional: null leaves it as it is, an empty string clears a text
/// field and an empty <see cref="Links"/> list removes the links. Moving to <c>in_progress</c> or <c>done</c> stamps the missing dates with now; passing them
/// backdates the step. Moving back to <c>pending</c> clears them.
/// </summary>
public record UpdateReleaseStepRequest(
    string? Status = null,
    string? Note = null,
    List<ReleaseLink>? Links = null,
    DateTimeOffset? StartedAt = null,
    DateTimeOffset? CompletedAt = null,
    string? Title = null,
    string? Description = null,
    string? Owner = null);

/// <summary>Add a step of the release manager's own, after <see cref="AfterStepId"/> (at the end when null).</summary>
public record AddReleaseStepRequest(
    string Title,
    string? Description = null,
    string? Owner = null,
    Guid? AfterStepId = null);

/// <summary>The services of the products left out of the release, as <c>product/service</c>; replaces the list.</summary>
public record SetReleaseServicesRequest(List<string>? Excluded);

public record CompleteReleaseRequest(DateTimeOffset? CompletedAt = null);

public record CancelReleaseRequest(string Reason);

// ── Progress: what the portal's own records say about the release ─────────────────────────────────

/// <summary>
/// The pipeline run behind a deploy or a migration: "marketplace-sdp-prod 8971132", "Reconcile Prod #75".
/// <see cref="Url"/> is the job inside the run when the producer sent one, else the run.
/// </summary>
public record ReleaseRunRefDto(string? Provider, string Label, string Url);

/// <summary>A deploy of one service in one environment, as the release reads it.</summary>
public record ReleaseDeployDto(Guid EventId, string Version, string Status, DateTimeOffset DeployedAt, ReleaseRunRefDto? Run);

public record ReleasePromotionDto(
    Guid Id,
    string SourceEnv,
    string Version,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ApprovedAt,
    DateTimeOffset? DeployedAt,
    // The run the approval dispatched, when the deploying pipeline reported it back.
    string? ExternalRunUrl,
    // The approval steps a pending promotion is still short of ("QA Review", "Release Approval").
    List<string> OutstandingSteps,
    // A pending promotion held by its work-item gate.
    bool WorkItemsOutstanding,
    // Its work items and how far their sign-off has got; null when it carries none.
    ReleaseWorkItemsDto? WorkItems,
    // The QA gate: "approved", "pending", "rejected", or "not-required" when the gate has no such step.
    string QaReview,
    DateTimeOffset? QaApprovedAt,
    string? QaApprovedBy);

/// <summary>A promotion's work items: <see cref="Approved"/> signed off, <see cref="Issues"/> cleared with a flag, <see cref="Blocked"/> held.</summary>
public record ReleaseWorkItemsDto(int Total, int Approved, int Issues, int Blocked);

/// <summary>The newest registered build of the release for a service.</summary>
public record ReleaseBuildDto(Guid Id, string Version, string Branch, string? BuildId, string? BuildUrl, DateTimeOffset CreatedAt);

public record ReleaseMigrationDto(
    Guid Id,
    string Kind,
    string Version,
    string Status,
    DateTimeOffset QueuedAt,
    DateTimeOffset? CompletedAt,
    int AppliedCount,
    int FailedCount,
    string? FailureReason,
    ReleaseRunRefDto? Run);

/// <summary>One service of the release, staging to production.</summary>
public record ReleaseServiceDto(
    string Product,
    string Service,
    // Left out of the release by the release manager: listed, not counted by the checks.
    bool Excluded,
    // Has a build or promotion of the version line. False for a service on a version scheme of its
    // own, whose records are then the ones inside the release window.
    bool OnLine,
    // The newest registered build of the release (build registry).
    ReleaseBuildDto? Build,
    // The newest deploy of the release on staging, whatever became of it.
    ReleaseDeployDto? Staging,
    // The promotion that carries the service to production (the newest one still in play).
    ReleasePromotionDto? Promotion,
    // What production runs of the release: the newest successful deploy, else the newest attempt.
    ReleaseDeployDto? Production,
    // Production deploy attempts of the release that failed.
    int ProductionFailures,
    // Migrations of the version production runs.
    List<ReleaseMigrationDto> Migrations);

public record ReleaseCheckIssueDto(string Product, string Service, string Problem);

/// <summary>
/// One pipeline run behind a check — an SDP run, an mpt-release workflow run — with how many of the
/// release's services it handled and how they came out.
/// </summary>
public record ReleaseRunDto(
    string? Provider,
    string Label,
    string Url,
    int Services,
    int Succeeded,
    int Failed,
    DateTimeOffset? FirstAt,
    DateTimeOffset? LastAt);

/// <summary>
/// One measurement. <see cref="State"/>: <c>waiting</c> (nothing to measure yet), <c>in_progress</c>,
/// <c>complete</c>, or <c>attention</c> (something failed or was rejected and needs a person).
/// </summary>
public record ReleaseCheckDto(
    string Key,
    string State,
    int Done,
    int Total,
    string Summary,
    DateTimeOffset? FirstAt,
    DateTimeOffset? LastAt,
    List<ReleaseCheckIssueDto> Issues,
    // The pipeline runs the measurement read, oldest first.
    List<ReleaseRunDto> Runs)
{
    /// <summary>
    /// What the people running the release should know that the numbers don't say, one sentence each
    /// — e.g. that a failed deploy held back a run's data migrations until the next run.
    /// </summary>
    public List<string> Warnings { get; init; } = [];
}

public record ReleaseProgressDto(
    Guid ReleaseId,
    string Version,
    // The approval step that is the QA gate ("QA Review"), as Settings → Releases names it.
    string QaStep,
    DateTimeOffset ComputedAt,
    // The evidence window: version cut (or creation) to completion (or now).
    DateTimeOffset From,
    DateTimeOffset To,
    List<ReleaseServiceDto> Services,
    List<ReleaseCheckDto> Checks);
