namespace Platform.Api.Features.Deployments.Models;

public record Reference(
    string Type,
    string? Url = null,
    string? Provider = null,
    string? Key = null,
    string? Revision = null,
    string? Title = null,
    IReadOnlyList<Participant>? Participants = null,
    string? Content = null,
    // When the referenced thing happened in its source system. Meaning follows Type:
    // `pull-request` → merge/completion time, `commit` → committer date, `work-item` →
    // created in the tracker, `pipeline` → build finish. Feeds lead-time analytics as the
    // clock start (pull-request first, commit as fallback) — never read the timestamp of an
    // arbitrary reference type for that.
    DateTimeOffset? OccurredAt = null);
