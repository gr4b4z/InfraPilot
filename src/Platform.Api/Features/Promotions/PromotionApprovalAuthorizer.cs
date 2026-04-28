using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Platform.Api.Features.Deployments.Models;
using Platform.Api.Features.Promotions.Models;
using Platform.Api.Infrastructure;
using Platform.Api.Infrastructure.Auth;
using Platform.Api.Infrastructure.Identity;
using Platform.Api.Infrastructure.Persistence;

namespace Platform.Api.Features.Promotions;

/// <summary>
/// Capability checks shared between promotion-level and ticket-level approval flows.
/// Pulled out of <see cref="PromotionService"/> so <see cref="WorkItemApprovalService"/>
/// can reuse the same Graph-fallback logic without duplicating it. Stateless and scoped.
///
/// <para>Two responsibilities:</para>
/// <list type="bullet">
///   <item>Approver-group membership (role claim, group claim, live Graph) for the current user.</item>
///   <item>Excluded-role check against the source deploy event's participants for any email.</item>
/// </list>
/// </summary>
public class PromotionApprovalAuthorizer
{
    private readonly PlatformDbContext _db;
    private readonly ICurrentUser _currentUser;
    private readonly IIdentityService _identity;
    private readonly ILogger<PromotionApprovalAuthorizer> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public PromotionApprovalAuthorizer(
        PlatformDbContext db,
        ICurrentUser currentUser,
        IIdentityService identity,
        ILogger<PromotionApprovalAuthorizer> logger)
    {
        _db = db;
        _currentUser = currentUser;
        _identity = identity;
        _logger = logger;
    }

    /// <summary>
    /// Checks whether the current user is in <paramref name="approverGroup"/>. Matches against
    /// (a) their role claims (for policies using a role string like "InfraPortal.Approver"),
    /// (b) their group claims (for policies using an Entra group object ID), and
    /// (c) live Graph membership (fallback, via <see cref="IIdentityService"/>).
    /// </summary>
    public async Task<bool> IsInApproverGroupAsync(string approverGroup, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(approverGroup)) return false;

        // Admin always qualifies — avoids bootstrapping hell when groups aren't wired up yet.
        if (_currentUser.IsAdmin) return true;

        // QA role qualifies for all promotions — lightweight alternative to AD groups for small teams.
        if (_currentUser.IsQA) return true;

        if (_currentUser.Roles.Contains(approverGroup, StringComparer.OrdinalIgnoreCase)) return true;
        if (_currentUser.IsInGroup(approverGroup)) return true;

        // Fall back to Graph. A stub/local identity service returns an empty list, which is fine.
        try
        {
            var members = await _identity.GetGroupMembers(approverGroup, ct);
            return members.Any(m =>
                string.Equals(m.Email, _currentUser.Email, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(m.Id, _currentUser.Id, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Group membership lookup failed for {Group}", approverGroup);
            return false;
        }
    }

    /// <summary>
    /// True when the policy specifies an excluded role and <paramref name="email"/> appears on
    /// the source deploy event with that role (after normalisation). Returns false when:
    /// the snapshot has no exclusion configured, no source event id is supplied, the email is
    /// blank, or the email is not on the event with the excluded role.
    ///
    /// <para>Decoupled from <see cref="PromotionCandidate"/> so ticket-level flows that pick a
    /// candidate dynamically can pass <c>candidate.SourceDeployEventId</c> directly.</para>
    /// </summary>
    public async Task<bool> IsEmailExcludedByRoleAsync(
        ResolvedPolicySnapshot snapshot,
        Guid? sourceDeployEventId,
        string email,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(snapshot.ExcludeRole)) return false;
        if (string.IsNullOrEmpty(email)) return false;
        if (sourceDeployEventId is null) return false;

        var participantsJson = await _db.DeployEvents.AsNoTracking()
            .Where(e => e.Id == sourceDeployEventId.Value)
            .Select(e => e.ParticipantsJson)
            .FirstOrDefaultAsync(ct);

        return EmailMatchesExcludedRole(participantsJson, snapshot.ExcludeRole, email);
    }

    /// <summary>
    /// Pure helper: checks a participants-JSON blob for an entry matching the role and email.
    /// Used both here and from <see cref="PromotionService.CanUserApproveManyAsync"/> where the
    /// JSON has already been batch-loaded.
    /// </summary>
    public static bool EmailMatchesExcludedRole(string? participantsJson, string excludedRole, string email)
    {
        if (string.IsNullOrWhiteSpace(participantsJson)) return false;
        try
        {
            var canonical = RoleNormalizer.Normalize(excludedRole);
            if (canonical.Length == 0) return false;

            var parts = JsonSerializer.Deserialize<List<ParticipantDto>>(participantsJson, JsonOptions);
            if (parts is null) return false;

            return parts.Any(p =>
                RoleNormalizer.Normalize(p.Role) == canonical &&
                !string.IsNullOrEmpty(p.Email) &&
                string.Equals(p.Email, email, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }
}
