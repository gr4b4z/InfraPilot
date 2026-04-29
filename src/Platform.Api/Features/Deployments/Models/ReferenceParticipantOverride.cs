namespace Platform.Api.Features.Deployments.Models;

/// <summary>
/// Operator-supplied routing override for a reference-scoped participant on a deploy event.
/// Lives separately from <see cref="DeployEvent.ReferencesJson"/> so re-ingesting the same
/// upstream event does not clobber the manual override — the assignee is "just routing"
/// and shouldn't fight with the source-of-truth participant payload.
///
/// <para>Tombstone semantics: a row whose <see cref="AssigneeEmail"/> is <c>null</c>
/// represents the operator explicitly clearing a slot — read paths treat that as
/// "no participant for this role on this reference," suppressing any underlying
/// reference-level OR event-level value. Without tombstones, "remove the Jira-supplied QA"
/// can't be expressed.</para>
/// </summary>
public class ReferenceParticipantOverride
{
    public Guid Id { get; set; }

    /// <summary>FK to <see cref="DeployEvent.Id"/>; rows cascade-delete with the parent event.</summary>
    public Guid DeployEventId { get; set; }

    /// <summary>Matches <c>ReferenceDto.Key</c> on the deploy event (e.g. "FOO-123", a commit sha, a PR number).</summary>
    public string ReferenceKey { get; set; } = "";

    /// <summary>
    /// Matches <c>ParticipantDto.Role</c>. Canonicalised on write via
    /// <see cref="Platform.Api.Infrastructure.RoleNormalizer.Normalize"/> so casing/punctuation
    /// differences don't break the unique-key invariant.
    /// </summary>
    public string Role { get; set; } = "";

    /// <summary>
    /// Override assignee email. Null when this row is a tombstone (operator cleared the slot).
    /// </summary>
    public string? AssigneeEmail { get; set; }

    /// <summary>Display name for the assignee. Null on tombstone rows.</summary>
    public string? AssigneeDisplayName { get; set; }

    /// <summary>Stable id (oid/sub) of the user who made the change.</summary>
    public string AssignedById { get; set; } = "";

    /// <summary>Display name of the user who made the change.</summary>
    public string AssignedByName { get; set; } = "";

    public DateTimeOffset AssignedAt { get; set; } = DateTimeOffset.UtcNow;
}
