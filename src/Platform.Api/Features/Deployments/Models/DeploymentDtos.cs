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
    // Secondary display line under Title. Set by producers on `work-item` references when the two
    // naming systems disagree: Title carries the commit subject (what actually changed), SubTitle
    // carries the tracker's own summary (e.g. the Jira ticket title). Null when the producer has
    // only one name for the thing — the UI renders a single line then.
    string? SubTitle = null,
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
    string? Content = null,
    // The referenced thing's state in its source system, as the producer found it. Set on
    // `work-item` references: a tracker that already reports the item finished means nobody needs to
    // sign it off again here, so the promotion records the sign-off itself and names whoever closed
    // it upstream. Null (the common case) means "no idea" — the item is signed off in InfraPortal as
    // usual. Meaningless on other reference types; ignored there.
    ReferenceResolutionDto? Resolution = null,
    // When the referenced thing happened in its source system. Meaning follows Type:
    // `pull-request` → merge/completion time, `commit` → committer date, `work-item` →
    // created in the tracker, `pipeline` → build finish. Producers should send the committer
    // date (not the author date — it survives rebase/squash and would overstate lead time).
    // Feeds the lead-time clock start (pull-request first, commit as fallback); ignored on
    // other reference types by the analytics read path.
    DateTimeOffset? OccurredAt = null);

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

/// <summary>
/// What the source system says about a reference's own lifecycle — today only whether it is finished.
/// A work item already closed in its tracker has been reviewed there; asking for a second sign-off in
/// InfraPortal adds a click, not assurance, so the promotion records the sign-off on the producer's
/// word and keeps <see cref="By"/> and <see cref="At"/> as the trail for who actually closed it.
/// </summary>
/// <param name="Resolved">
/// True when the tracker reports the item finished. Only true does anything: false and null are both
/// "still open", because a producer that cannot read a status should not be able to un-approve.
/// </param>
/// <param name="Status">The tracker's own word for the state ("Done", "Closed") — display only.</param>
/// <param name="At">When it was closed there, if the tracker exposes it.</param>
/// <param name="By">Who closed it there, if the tracker exposes it.</param>
public record ReferenceResolutionDto(
    bool Resolved,
    string? Status = null,
    DateTimeOffset? At = null,
    ResolutionActorDto? By = null);

/// <summary>Whoever closed a work item in its source system. Not a platform user — either half may be absent.</summary>
public record ResolutionActorDto(string? DisplayName = null, string? Email = null);

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
    // Secondary display line — the tracker's own summary when Title carries the commit subject.
    string? SubTitle,
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

// --- Service search & detail ---

/// <summary>
/// One hit from the cross-product service search. Identity is the (product, service) pair — the
/// same service name under two products is two hits — so the product always rides along: the whole
/// point of the search is finding a service without knowing which product it lives in.
/// </summary>
public record ServiceSearchResultDto(
    string Product,
    string Service,
    List<ServiceSearchEnvironmentDto> Environments,
    DateTimeOffset LastDeployedAt);

public record ServiceSearchEnvironmentDto(
    string Environment,
    DateTimeOffset LastDeployedAt);

/// <summary>
/// Everything the service detail page shows, in one round trip: where the service currently runs
/// (latest event per environment), the most recent distinct versions and which environments each
/// reached, and the promotions moving it between environments.
/// </summary>
public record ServiceDetailDto(
    string Product,
    string Service,
    List<DeploymentStateDto> Environments,
    List<ServiceVersionDto> RecentVersions,
    List<ServicePromotionDto> Promotions);

/// <summary>
/// One distinct version of a service, most-recent-first, with the environments it was deployed to.
/// An environment entry is that version's latest deploy there, so a redeploy doesn't duplicate it.
/// </summary>
public record ServiceVersionDto(
    string Version,
    DateTimeOffset LastDeployedAt,
    List<ServiceVersionEnvironmentDto> Environments);

public record ServiceVersionEnvironmentDto(
    Guid EventId,
    string Environment,
    string Status,
    bool IsRollback,
    DateTimeOffset DeployedAt);

/// <summary>A promotion of this service, regardless of version — the service page's promotion feed.</summary>
public record ServicePromotionDto(
    Guid Id,
    string SourceEnv,
    string TargetEnv,
    string Version,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ApprovedAt,
    DateTimeOffset? DeployedAt);

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

// --- Service product overrides (admin) ---

/// <summary>One configured service→product mapping as the settings list shows it.</summary>
/// <param name="FromProduct">
/// The sending product this row applies to; null means it applies whatever product was sent.
/// </param>
/// <param name="StoredEntities">
/// How many deploy events and builds are already filed under the target product for this service —
/// the cheap "is this row doing anything?" signal. A row that stays at zero usually spells the
/// service differently than the sender does.
/// </param>
/// <param name="StrandedEntities">
/// How many are still filed under some other product, i.e. what a remap would move. Zero means
/// history and new traffic agree.
/// </param>
public record ServiceProductOverrideDto(
    Guid Id,
    string Service,
    string? FromProduct,
    string Product,
    string? Reason,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt,
    string UpdatedByName,
    int StoredEntities,
    int StrandedEntities);

public record SaveServiceProductOverrideRequest(
    string Service,
    string Product,
    string? FromProduct = null,
    string? Reason = null);

/// <summary>
/// What remapping one override's history involves — returned unchanged by the preview and the apply,
/// so the admin compares like with like.
/// </summary>
/// <param name="FromProducts">Every product the service's rows are being pulled out of.</param>
public record ServiceProductRemapDto(
    Guid OverrideId,
    string Service,
    string Product,
    List<string> FromProducts,
    bool Applied,
    int Deployments,
    int DeployWorkItems,
    int Builds,
    int BuildConflicts,
    int Promotions,
    int OpenPromotions,
    int PromotionWorkItems,
    int Retirements,
    int RetirementMerges,
    int StrandedTicketApprovals);
