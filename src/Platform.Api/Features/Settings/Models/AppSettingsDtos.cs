using Platform.Api.Features.Promotions.Models;

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
// IsProduction marks the environment as a production stage — the environment(s) executive
// analytics report on ("deploys to production", "shipped this period"). More than one may be
// marked (multi-region production). When none is marked, consumers fall back to the historical
// convention: the LAST environment in this list is the end of the pipeline.
//
// Aliases are the other names producers call this same environment ("prod" for "production",
// "develop"/"development" for "dev"). Every write path that takes an environment from a caller
// resolves through them (EnvironmentAliasResolver), so one physical environment stops arriving
// as three. Null/empty means "no aliases" — the field is optional so settings rows written
// before aliases existed deserialize unchanged.
public record EnvironmentConfigDto(
    string Key,
    string DisplayName,
    string? Color = null,
    bool IsProduction = false,
    List<string>? Aliases = null);

// Aliases are the other names producers use for this same role — Jira's "QA" field arriving as
// `qa` on a ticket whose policy asks for a `qa-owner`. Every place that compares roles resolves
// through them (RoleAliasMap), so a participant sent under an alias fills the canonical role,
// labels as it, and is replaced when somebody is assigned to it. Stored canonicalised.
//
// AssigneeGroups narrow who the assignment picker offers for this role to the members of these
// directory groups (any of them). Empty means anybody in the directory. A picker filter, not a
// write gate: ingest still records whoever a producer named.
//
// Both optional so settings rows written before they existed deserialize unchanged.
public record RoleConfigDto(
    string Key,
    string DisplayName,
    List<string>? Aliases = null,
    List<GroupRef>? AssigneeGroups = null);

public record ActivityTemplateLineDto(string Template, string Style);

/// <summary>
/// Request to fold one or more environment names into another. <paramref name="RecordAliases"/>
/// defaults to true: a merge that does not also record the aliases is undone by the next pipeline
/// run, so opting out is the unusual choice and has to be made explicitly.
/// </summary>
public record MergeEnvironmentsRequest(string Into, List<string> From, bool RecordAliases = true);

/// <summary>
/// What a merge involves — identical shape from the preview and the apply, so the confirmation the
/// admin read is the same shape as the receipt they get back. <paramref name="Applied"/> is what
/// tells them apart.
/// </summary>
/// <param name="Moved">Rows that change environment. Pre-computed so the client isn't summing a
/// dozen fields to answer "did this do anything".</param>
/// <param name="LeftBehind">
/// Rows the merge cannot take with it — collisions with a row the target already has, and promotion
/// edges that would become self-referential. Non-zero means the merge is honest but incomplete, and
/// the counts say where to look.
/// </param>
public record EnvironmentMergePlanDto(
    string Into,
    List<string> Sources,
    bool AliasesRecorded,
    List<string> RemovedEnvironments,
    bool Applied,
    EnvironmentMergeService.MergeCounts Counts,
    int Moved,
    int LeftBehind);
