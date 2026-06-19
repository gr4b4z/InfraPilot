namespace Platform.Api.Features.Settings.Models;

/// <summary>
/// Admin-curated, server-authoritative UI configuration shared across all users:
/// environment key→label mappings (and ordering), participant-role labels, and the
/// activity-card template. Previously this lived only in browser localStorage, so it
/// silently reverted to defaults whenever storage was evicted.
/// </summary>
public record AppSettingsDto(
    List<EnvironmentConfigDto> Environments,
    List<RoleConfigDto> Roles,
    List<ActivityTemplateLineDto> ActivityTemplate);

public record EnvironmentConfigDto(string Key, string DisplayName);

public record RoleConfigDto(string Key, string DisplayName);

public record ActivityTemplateLineDto(string Template, string Style);
