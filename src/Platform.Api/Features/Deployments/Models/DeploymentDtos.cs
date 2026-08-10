namespace Platform.Api.Features.Deployments.Models;

// --- Input DTOs ---

public record CreateDeployEventDto(
    string Product,
    string Service,
    string Environment,
    string Version,
    string Source,
    DateTimeOffset DeployedAt,
    List<ReferenceDto>? References,
    List<ParticipantDto>? Participants,
    Dictionary<string, object>? Metadata,
    string? Status = null,
    bool IsRollback = false,
    string? PreviousVersion = null,
    // The CI run that performed this deployment, plus the error it identified. Optional: a producer
    // that only reports "version V is live" has nothing to say here.
    DeployRun? Run = null,
    // Output the deploying pipeline captured — the Helm printout and its failure diagnostics. Sent
    // with the event rather than through a follow-up call so a failed deploy is explainable the
    // moment it appears, with no second request to lose.
    List<CreateDeployLogDto>? Logs = null);

/// <summary>
/// One block of captured pipeline output. <c>Name</c> is the idempotency key within an event, so a
/// retrying sender replaces its earlier copy instead of appending a duplicate. Set
/// <c>Truncated</c> when the producer itself already dropped part of the log; the server sets it
/// too when the content exceeds its own cap.
/// </summary>
public record CreateDeployLogDto(
    string Name,
    string? Source = null,
    string? Content = null,
    bool Truncated = false);

/// <summary>
/// Human/agent-authored manual deployment. Creates a NEW <c>DeployEvent</c> based on the latest one
/// for <c>(Product, Service, Environment)</c>, changing only <c>Version</c> and <c>Status</c>. The
/// server stamps human/agent attribution (Source="manual" + triggered-by = caller) — the body cannot
/// spoof it — so it's always clear this was manual, not a CI report. <c>Note</c> is required.
/// </summary>
public record CreateManualDeployRequest(
    string Product,
    string Service,
    string Environment,
    string Version,
    string Note,
    string? Status = null);

/// <summary>Resolved identity of whoever authored a manual deployment (a signed-in user or an API key).</summary>
public record ManualDeployActor(string Id, string DisplayName, string? Email, string ActorType);

public record ReferenceDto(
    string Type,
    string? Url = null,
    string? Provider = null,
    string? Key = null,
    string? Revision = null,
    string? Title = null,
    // Optional reference-scoped participants. A PR has its author/reviewer; a ticket has
    // its QA/assignee. When present these are persisted nested under the reference in
    // ReferencesJson and are honoured by the excluded-role check (reference-level wins
    // over event-level when both carry a participant for a given role).
    IReadOnlyList<ParticipantDto>? Participants = null,
    // Commit hashes this reference was derived from. Set by the producer on `work-item`
    // references: the work item was discovered by parsing commit messages, and this records
    // which commits mentioned it. The read path uses it to link the ticket back to its
    // `commit` references (matched on Key) and to the `pull-request` references those commits
    // merged (matched on Revision), so the detail page can show the change that carried it.
    // Meaningless on other reference types; ignored there.
    IReadOnlyList<string>? Commits = null,
    // The reference's body, verbatim from the source system: a Jira ticket's description, a
    // PR's description, a commit message body. Where Title is the one-line summary, this is
    // the prose under it — what a reviewer reads to understand what they're signing off on.
    // Unbounded by design (a ticket description can run long); stored as-is and rendered as
    // plain text, never interpreted as markup.
    string? Content = null);

public record ParticipantDto(
    string Role,
    string? DisplayName = null,
    string? Email = null,
    // Server-owned read-path metadata. Both default to null/false on ingest payloads —
    // operators don't supply these. The deployments read paths (and the promotion read
    // paths that surface the source event) flip IsOverride=true and populate AssignedBy
    // when an operator override has displaced the original participant for a given role
    // on a given reference, so the UI can render an "(overridden by …)" hint.
    bool IsOverride = false,
    string? AssignedBy = null);

// --- Output DTOs ---

public record DeployEventResponseDto(
    Guid Id,
    string Product,
    string Service,
    string Environment,
    string Version,
    string? PreviousVersion,
    bool IsRollback,
    string Status,
    string Source,
    DateTimeOffset DeployedAt,
    List<ReferenceDto> References,
    List<ParticipantDto> Participants,
    EnrichmentDto? Enrichment,
    Dictionary<string, object>? Metadata,
    DeployRun? Run = null);

public record EnrichmentDto(
    Dictionary<string, string> Labels,
    List<ParticipantDto> Participants,
    DateTimeOffset EnrichedAt);

public record DeploymentStateDto(
    // Identifies the event behind this cell so the UI can link straight to its detail page.
    Guid Id,
    string Product,
    string Service,
    string Environment,
    string Version,
    string? PreviousVersion,
    bool IsRollback,
    string Status,
    string Source,
    DateTimeOffset DeployedAt,
    List<ReferenceDto> References,
    List<ParticipantDto> Participants,
    EnrichmentDto? Enrichment,
    DeployRun? Run = null);

// --- Detail view ---

/// <summary>
/// Everything the deployment detail page shows, in one round trip: the event itself, what its
/// pipeline printed (as summaries — content is fetched per block on demand), the neighbouring
/// deployments of the same service, and the promotions and work items that connect this deployment
/// to the rest of the release process.
/// </summary>
public record DeployEventDetailDto(
    DeployEventResponseDto Event,
    List<DeployLogSummaryDto> Logs,
    List<DeployEventHistoryEntryDto> History,
    List<RelatedPromotionDto> Promotions,
    List<RelatedWorkItemDto> WorkItems);

/// <summary>
/// A log block without its content. Sizes are reported so the UI can warn before pulling a
/// multi-megabyte block, and so a zero-length capture is visibly distinct from a missing one.
/// </summary>
public record DeployLogSummaryDto(
    Guid Id,
    string Name,
    string? Source,
    int Sequence,
    int ByteCount,
    int LineCount,
    bool Truncated,
    DateTimeOffset CreatedAt);

public record DeployLogContentDto(
    Guid Id,
    string Name,
    string? Source,
    string Content,
    bool Truncated,
    int OriginalByteCount);

/// <summary>Compact neighbour row: enough to render a timeline and link onward, nothing more.</summary>
public record DeployEventHistoryEntryDto(
    Guid Id,
    string Environment,
    string Version,
    string? PreviousVersion,
    bool IsRollback,
    string Status,
    string Source,
    DateTimeOffset DeployedAt,
    string? FailureReason);

/// <summary>
/// A promotion candidate carrying the same (product, service, version) as this deployment.
/// <c>Direction</c> says how the deployment relates to it: <c>outbound</c> when this environment is
/// the promotion's source (this deploy is what may move forward), <c>inbound</c> when it is the
/// target (this deploy is what the promotion delivered).
/// </summary>
public record RelatedPromotionDto(
    Guid Id,
    string SourceEnv,
    string TargetEnv,
    string Version,
    string Status,
    string Direction,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ApprovedAt,
    DateTimeOffset? DeployedAt);

/// <summary>
/// A work item this deployment carries. <c>SignOffTargetEnvs</c> lists the environments the ticket is
/// gated for, taken from the promotions on this version — the work-item detail page keys sign-off on
/// (key, product, targetEnv), so a link needs one of them. Empty when no promotion exists yet.
/// </summary>
public record RelatedWorkItemDto(
    string Key,
    string? Provider,
    string? Url,
    string? Title,
    List<string> SignOffTargetEnvs);

public record ProductSummaryDto(
    string Product,
    Dictionary<string, EnvironmentSummaryDto> Environments);

public record EnvironmentSummaryDto(
    int TotalServices,
    int DeployedServices,
    DateTimeOffset? LastDeployedAt);

/// <summary>
/// Compact shape for the version picker / rollback-target selector: each entry represents a
/// distinct deployed version, not a single deploy event (so the list doesn't balloon when a
/// version was re-deployed multiple times).
/// </summary>
public record DeploymentVersionDto(
    Guid Id,
    string Service,
    string Version,
    DateTimeOffset DeployedAt,
    string? DeployerEmail,
    bool IsRollback);

// --- Retired services (admin soft delete) ---

/// <summary>A retired service as the restore list shows it.</summary>
public record DeletedServiceDto(
    Guid Id,
    string Product,
    string Service,
    DateTimeOffset DeletedAt,
    string DeletedByName,
    string? Reason);

public record DeleteServiceRequest(string Product, string Service, string? Reason = null);

/// <summary>
/// What the retirement took out of view. Reported back so the admin sees the size of what they just
/// hid — particularly the open promotions, which are the part nobody thinks about when retiring a
/// service and the part somebody may still be waiting to approve.
/// </summary>
public record DeleteServiceResultDto(
    DeletedServiceDto Service,
    int HiddenDeployments,
    int HiddenOpenPromotions);
