namespace Platform.Api.Features.Rollbacks.Models;

/// <summary>Request body for creating a rollback. Two selection modes share one shape.</summary>
public record CreateRollbackRequestDto(
    string Product,
    string TargetEnv,
    string Mode,                          // "manual" | "align"
    string? ReferenceEnv = null,          // align: env whose versions we match
    List<string>? Exclude = null,         // align: services to hold back ("all except")
    List<RollbackItemInput>? Items = null,// manual: explicit (service, toVersion) list
    string? Reason = null);

public record RollbackItemInput(string Service, string ToVersion);

/// <summary>One resolved item in a create/preview result, with why it was included or skipped.</summary>
public record ResolvedRollbackItem(
    string Service,
    string FromVersion,
    string ToVersion,
    bool Eligible,
    string? SkipReason);

/// <summary>Dry-run result so the UI can show "will roll back N, skip M" before submitting.</summary>
public record RollbackPreview(
    string Product,
    string TargetEnv,
    string Mode,
    string? ReferenceEnv,
    List<ResolvedRollbackItem> Items);

public record RollbackQuery(
    RollbackStatus? Status = null,
    string? Product = null,
    string? TargetEnv = null,
    int Limit = 200);
