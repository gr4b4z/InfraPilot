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

/// <summary>
/// One configured environment. <paramref name="Color"/> is an admin-chosen accent used to
/// tell environments apart at a glance wherever an item targets one (promotions, rollbacks,
/// deploy activity). Stored normalised as <c>#rrggbb</c>; null means "no explicit choice",
/// in which case the client derives a stable colour from the key.
/// </summary>
public record EnvironmentConfigDto(string Key, string DisplayName, string? Color = null);

public record RoleConfigDto(string Key, string DisplayName);

public record ActivityTemplateLineDto(string Template, string Style);
