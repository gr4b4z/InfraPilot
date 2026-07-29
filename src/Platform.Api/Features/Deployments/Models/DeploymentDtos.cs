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
    string? PreviousVersion = null);

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
    Dictionary<string, object>? Metadata);

public record EnrichmentDto(
    Dictionary<string, string> Labels,
    List<ParticipantDto> Participants,
    DateTimeOffset EnrichedAt);

public record DeploymentStateDto(
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
    EnrichmentDto? Enrichment);

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
