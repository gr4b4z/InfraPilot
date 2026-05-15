namespace Platform.Api.Features.ReleaseNotes.Models;

public record RawPreviewDto(
    string Product,
    string Environment,
    DateTimeOffset From,
    DateTimeOffset To,
    DateTimeOffset GeneratedAt,
    List<ServiceReleaseDto> Services);

public record ServiceReleaseDto(
    string Service,
    string? PreviousVersion,
    string CurrentVersion,
    bool IsRollback,
    DateTimeOffset DeployedAt,
    List<WorkItemSummaryDto> WorkItems,
    List<PullRequestSummaryDto> PullRequests,
    List<PipelineSummaryDto> Pipelines,
    List<ParticipantSummaryDto> Participants,
    // Convenience accessors so templates don't need to filter participants
    // by role — surfaced as the first match for each well-known role.
    // Each is a {DisplayName, Email} pair so templates can build mailto: links.
    ParticipantSummaryDto? Author,
    ParticipantSummaryDto? Qa,
    ParticipantSummaryDto? TriggeredBy);

public record WorkItemSummaryDto(string Key, string? Title, string? Type, string? Url);

public record PullRequestSummaryDto(string? Key, string? Title, string? Url);

public record PipelineSummaryDto(string? Key, string? Title, string? Url);

public record ParticipantSummaryDto(string Role, string? DisplayName, string? Email);

public record GenerateReleaseNoteRequest(
    string Product,
    string Environment,
    DateTimeOffset? From,
    DateTimeOffset? To,
    // When supplied (typically from a preview-then-edit UI), this markdown is
    // persisted verbatim instead of running the template against the aggregated
    // services. Aggregation still runs so the structured `raw` payload reflects
    // the actual deploys in the window.
    string? RenderedContent = null);

public record SaveTemplateRequest(string? Product, string? Environment, string Template);

public record ReleaseNoteListItemDto(
    Guid Id,
    string Product,
    string Environment,
    DateTimeOffset From,
    DateTimeOffset To,
    DateTimeOffset GeneratedAt,
    int ServicesCount,
    string Status);

public record ReleaseNoteDetailDto(
    Guid Id,
    string Product,
    string Environment,
    DateTimeOffset From,
    DateTimeOffset To,
    DateTimeOffset GeneratedAt,
    string RenderedContent,
    string Status,
    RawPreviewDto Raw);
