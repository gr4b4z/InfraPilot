namespace Platform.Api.Features.Incidents.Models;

public record IncidentDto(
    Guid Id,
    string Product,
    string Service,
    string Environment,
    DateTimeOffset StartedAt,
    DateTimeOffset? ResolvedAt,
    // ResolvedAt − StartedAt, hours; null while open.
    double? DurationHours,
    string? Version,
    Guid? CausedByDeployEventId,
    string Source,
    string? ExternalId,
    string? Url,
    string CreatedBy,
    string CreatedByName,
    DateTimeOffset CreatedAt,
    string? UpdatedBy,
    DateTimeOffset UpdatedAt);

/// <summary>Create or replace an incident. Only the pointer fields — see <see cref="Incident"/>.</summary>
public record UpsertIncidentRequest(
    string Product,
    string Service,
    string Environment,
    DateTimeOffset StartedAt,
    DateTimeOffset? ResolvedAt = null,
    string? Version = null,
    Guid? CausedByDeployEventId = null,
    string? Source = null,
    string? ExternalId = null,
    string? Url = null);

public record IncidentListDto(List<IncidentDto> Items, int Total);
