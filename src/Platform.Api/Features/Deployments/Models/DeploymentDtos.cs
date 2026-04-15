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
    bool IsRollback = false);

public record ReferenceDto(
    string Type,
    string? Url = null,
    string? Provider = null,
    string? Key = null,
    string? Revision = null);

public record ParticipantDto(
    string Role,
    string? DisplayName = null,
    string? Email = null);

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
