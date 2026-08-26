using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Platform.Api.Features.Deployments.Models;
using Platform.Api.Infrastructure;
using Platform.Api.Infrastructure.Audit;
using Platform.Api.Infrastructure.Auth;
using Platform.Api.Infrastructure.Persistence;

namespace Platform.Api.Features.Deployments;

/// <summary>
/// Manages operator-supplied reference participant overrides — the routing overlay that lets
/// users assign / reassign / clear (tombstone) a participant on a specific reference of a
/// deploy event without mutating the source <see cref="DeployEvent.ReferencesJson"/>.
///
/// <para>Single public entry point: <see cref="AssignAsync"/> upserts (or tombstones) a row
/// keyed by (DeployEventId, ReferenceKey, Role). Re-ingesting the same event preserves the
/// override because the source JSON is never touched.</para>
/// </summary>
public class ReferenceParticipantOverrideService
{
    private readonly PlatformDbContext _db;
    private readonly ICurrentUser _currentUser;
    private readonly IAuditLogger _audit;
    private readonly ILogger<ReferenceParticipantOverrideService> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    // Loose email shape check — must contain an "@" with non-whitespace on both sides.
    // Deliberately permissive: single-label domains like "user@localhost" are valid in dev
    // setups, and RFC 5321 is broader than any pragmatic regex anyway. Just enough to catch
    // obvious garbage like "not-an-email" or empty strings.
    private static readonly Regex EmailShape = new(
        @"^[^\s@]+@[^\s@]+$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public ReferenceParticipantOverrideService(
        PlatformDbContext db,
        ICurrentUser currentUser,
        IAuditLogger audit,
        ILogger<ReferenceParticipantOverrideService> logger)
    {
        _db = db;
        _currentUser = currentUser;
        _audit = audit;
        _logger = logger;
    }

    /// <summary>
    /// Result of <see cref="AssignAsync"/>: the merged participant list for the target
    /// reference (event + reference + override layers reconciled). Returned to the caller so
    /// the UI can re-render without a follow-up GET.
    /// </summary>
    public record AssignResult(
        IReadOnlyList<ParticipantDto> Participants,
        bool Tombstone,
        ParticipantDto? Override);

    /// <summary>
    /// Upsert (or tombstone) the override for (eventId, refKey, role). Returns the merged
    /// participant list for the target reference.
    /// <list type="bullet">
    ///   <item><paramref name="assigneeEmail"/> + <paramref name="assigneeDisplayName"/>
    ///         non-null → upsert override row.</item>
    ///   <item>Both null → upsert tombstone row (the row's existence suppresses lower layers).</item>
    /// </list>
    /// Throws <see cref="ArgumentException"/> on invalid input (bad role, bad email shape).
    /// Throws <see cref="KeyNotFoundException"/> when the event doesn't exist or the
    /// referenceKey is not present on its <c>ReferencesJson</c>.
    /// </summary>
    public async Task<AssignResult> AssignAsync(
        Guid eventId,
        string refKey,
        string role,
        string? assigneeEmail,
        string? assigneeDisplayName,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(role))
            throw new ArgumentException("'role' is required", nameof(role));
        if (string.IsNullOrWhiteSpace(refKey))
            throw new ArgumentException("'referenceKey' is required", nameof(refKey));

        var canonicalRole = RoleNormalizer.Normalize(role);
        if (canonicalRole.Length == 0)
            throw new ArgumentException("'role' did not normalise to a non-empty value", nameof(role));

        // Tombstone vs assign: both fields null → tombstone. Otherwise email is required and
        // must look like an email (loose shape check).
        var isTombstone = assigneeEmail is null && assigneeDisplayName is null;
        if (!isTombstone)
        {
            if (string.IsNullOrWhiteSpace(assigneeEmail))
                throw new ArgumentException("'assignee.email' is required when assigning", nameof(assigneeEmail));
            if (!EmailShape.IsMatch(assigneeEmail!))
                throw new ArgumentException("'assignee.email' is not a valid email", nameof(assigneeEmail));
        }

        var ev = await _db.DeployEvents.AsNoTracking()
            .Where(e => e.Id == eventId)
            .Select(e => new { e.Id, e.ReferencesJson, e.ParticipantsJson })
            .FirstOrDefaultAsync(ct)
            ?? throw new KeyNotFoundException($"Deploy event {eventId} not found");

        var refs = DeserializeReferences(ev.ReferencesJson);
        var matchedRef = refs.FirstOrDefault(r =>
            !string.IsNullOrEmpty(r.Key)
            && string.Equals(r.Key, refKey, StringComparison.OrdinalIgnoreCase));
        if (matchedRef is null)
            throw new KeyNotFoundException(
                $"Reference '{refKey}' not found on event {eventId}. Note: events that carry only a flat participants[] (no references[]) cannot be overridden — operators need a reference to scope the assignment to.");

        // Upsert by unique (DeployEventId, ReferenceKey, Role) — canonicalise role so the
        // unique index doesn't fragment on casing.
        var existing = await _db.ReferenceParticipantOverrides
            .FirstOrDefaultAsync(o =>
                o.DeployEventId == eventId
                && o.ReferenceKey == refKey
                && o.Role == canonicalRole, ct);

        if (existing is null)
        {
            existing = new ReferenceParticipantOverride
            {
                Id = Guid.NewGuid(),
                DeployEventId = eventId,
                ReferenceKey = refKey,
                Role = canonicalRole,
                AssigneeEmail = isTombstone ? null : assigneeEmail,
                AssigneeDisplayName = isTombstone ? null : assigneeDisplayName,
                AssignedById = _currentUser.Id,
                AssignedByName = _currentUser.Name,
                AssignedAt = DateTimeOffset.UtcNow,
            };
            _db.ReferenceParticipantOverrides.Add(existing);
        }
        else
        {
            existing.AssigneeEmail = isTombstone ? null : assigneeEmail;
            existing.AssigneeDisplayName = isTombstone ? null : assigneeDisplayName;
            existing.AssignedById = _currentUser.Id;
            existing.AssignedByName = _currentUser.Name;
            existing.AssignedAt = DateTimeOffset.UtcNow;
        }

        await _db.SaveChangesAsync(ct);

        var action = isTombstone ? "deployment.participant.cleared" : "deployment.participant.assigned";
        await _audit.Log(
            "deployments", action,
            _currentUser.Id, _currentUser.Name, "user",
            "DeployEvent", eventId, null,
            new
            {
                deployEventId = eventId,
                referenceKey = refKey,
                role = canonicalRole,
                assignee = isTombstone
                    ? null
                    : new { email = assigneeEmail, displayName = assigneeDisplayName },
                actor = _currentUser.Email,
            });

        _logger.LogInformation(
            "Reference participant {Action} on event {EventId} ref {RefKey} role {Role} by {Actor}",
            action, eventId, refKey, canonicalRole, _currentUser.Email);

        // Build the merged participant view for this single reference so the UI can re-render
        // without a follow-up GET.
        var allOverrides = await _db.ReferenceParticipantOverrides.AsNoTracking()
            .Where(o => o.DeployEventId == eventId && o.ReferenceKey == refKey)
            .ToListAsync(ct);

        var merged = MergeForReference(matchedRef, allOverrides);

        ParticipantDto? overrideDto = null;
        if (!isTombstone)
        {
            overrideDto = new ParticipantDto(
                Role: canonicalRole,
                DisplayName: assigneeDisplayName,
                Email: assigneeEmail,
                IsOverride: true,
                AssignedBy: _currentUser.Name);
        }

        return new AssignResult(merged, Tombstone: isTombstone, Override: overrideDto);
    }

    /// <summary>
    /// Static merger: given a single reference plus the override rows scoped to that
    /// (DeployEventId, ReferenceKey), returns the effective participant list.
    /// <list type="bullet">
    ///   <item>Each (canonical-role) appears at most once.</item>
    ///   <item>Override-with-email beats reference-level entry for the same role.</item>
    ///   <item>Tombstone (override with null email) hides the reference-level entry.</item>
    /// </list>
    /// Used by the read path on <see cref="DeploymentService"/> too — kept here so the merge
    /// rule lives in one place.
    /// </summary>
    public static IReadOnlyList<ParticipantDto> MergeForReference(
        ReferenceDto referenceDto,
        IReadOnlyList<ReferenceParticipantOverride> overrides)
    {
        // Index overrides by canonical role.
        var overrideByRole = overrides
            .GroupBy(o => RoleNormalizer.Normalize(o.Role))
            .ToDictionary(g => g.Key, g => g.OrderByDescending(o => o.AssignedAt).First());

        var merged = new List<ParticipantDto>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        // Original reference-level participants, possibly displaced or tombstoned.
        if (referenceDto.Participants is { Count: > 0 } original)
        {
            foreach (var p in original)
            {
                var canonical = RoleNormalizer.Normalize(p.Role);
                if (overrideByRole.TryGetValue(canonical, out var ov))
                {
                    if (ov.AssigneeEmail is null) { seen.Add(canonical); continue; } // tombstone — hide
                    merged.Add(new ParticipantDto(
                        Role: p.Role,
                        DisplayName: ov.AssigneeDisplayName ?? p.DisplayName,
                        Email: ov.AssigneeEmail,
                        IsOverride: true,
                        AssignedBy: ov.AssignedByName));
                    seen.Add(canonical);
                }
                else
                {
                    merged.Add(p);
                    seen.Add(canonical);
                }
            }
        }

        // Override entries that introduced a brand-new role on this reference (no original
        // participant to displace). Tombstones with no original entry are inert — drop them.
        foreach (var (canonical, ov) in overrideByRole)
        {
            if (seen.Contains(canonical)) continue;
            if (ov.AssigneeEmail is null) continue;
            merged.Add(new ParticipantDto(
                Role: canonical,
                DisplayName: ov.AssigneeDisplayName,
                Email: ov.AssigneeEmail,
                IsOverride: true,
                AssignedBy: ov.AssignedByName));
        }

        return merged;
    }

    private static List<ReferenceDto> DeserializeReferences(string? referencesJson)
    {
        if (string.IsNullOrWhiteSpace(referencesJson)) return new();
        try
        {
            return JsonSerializer.Deserialize<List<ReferenceDto>>(referencesJson, JsonOptions) ?? new();
        }
        catch
        {
            return new();
        }
    }
}
