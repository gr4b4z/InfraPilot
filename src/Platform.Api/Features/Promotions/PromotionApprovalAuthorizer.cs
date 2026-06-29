using Platform.Api.Features.Promotions.Models;
using Platform.Api.Infrastructure.Auth;
using Platform.Api.Infrastructure.Identity;

namespace Platform.Api.Features.Promotions;

/// <summary>
/// Capability checks shared between promotion-level and ticket-level approval flows.
/// Pulled out of <see cref="PromotionService"/> so <see cref="WorkItemApprovalService"/>
/// can reuse the same Graph-fallback logic without duplicating it. Stateless and scoped.
///
/// <para>Responsibility: approver-group membership (role claim, group claim, live Graph) for the
/// current user.</para>
///
/// <para>The separation-of-duties (excluded-role) machinery was removed (D17) — it was the only
/// consumer of the dropped <c>SourceDeployEventId</c> / <c>SupersededSourceEventIds</c> fields.
/// Anyone authorized for a promotion may approve it, including whoever scheduled the deploy. When
/// SoD is re-introduced it will be payload-driven (read from <c>candidate.Participants</c>), not
/// deploy-event-linked.</para>
/// </summary>
public class PromotionApprovalAuthorizer
{
    private readonly ICurrentUser _currentUser;
    private readonly IIdentityService _identity;
    private readonly ILogger<PromotionApprovalAuthorizer> _logger;

    public PromotionApprovalAuthorizer(
        ICurrentUser currentUser,
        IIdentityService identity,
        ILogger<PromotionApprovalAuthorizer> logger)
    {
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

        // NOTE: the blanket IsQA shortcut was removed (D11). QA is no longer a global approver role;
        // it is now just another group on a requirement, configured explicitly per policy.

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
    /// Whether the current user is in <paramref name="group"/>, matching on <b>either</b> its object
    /// id or its display name. Role claims usually carry the name; group claims / Graph use the id —
    /// checking both maximises correct matches. When id == name (legacy bare-string data) only one
    /// lookup is performed.
    /// </summary>
    public async Task<bool> IsInApproverGroupAsync(GroupRef group, CancellationToken ct)
    {
        if (await IsInApproverGroupAsync(group.Id, ct)) return true;
        if (group.Name != group.Id && await IsInApproverGroupAsync(group.Name, ct)) return true;
        return false;
    }

    /// <summary>
    /// Whether <paramref name="email"/> can satisfy <paramref name="req"/>: a direct member of
    /// <see cref="ApproverRequirement.Users"/> (case-insensitive email match) OR a member of any
    /// group in <see cref="ApproverRequirement.Groups"/> (each checked via
    /// <see cref="IsInApproverGroupAsync"/>, which also honours the admin bootstrap shortcut).
    ///
    /// <para>The group checks resolve membership for the <b>current</b> user (that's the only
    /// identity <see cref="IIdentityService"/>/<see cref="ICurrentUser"/> can answer for); callers
    /// therefore pass the current user's email. A non-current email only matches via the explicit
    /// user list.</para>
    /// </summary>
    public async Task<bool> IsAuthorizedForRequirementAsync(
        ApproverRequirement req, string email, CancellationToken ct)
    {
        if (req.Users.Any(u => string.Equals(u, email, StringComparison.OrdinalIgnoreCase)))
            return true;

        foreach (var group in req.Groups)
        {
            if (await IsInApproverGroupAsync(group, ct)) return true;
        }
        return false;
    }



    /// <summary>
    /// Whether <paramref name="email"/> can satisfy <b>at least one</b> requirement in the snapshot's
    /// rule tree. Used by capability probes ("can this user approve at all?") and the manual-gate
    /// authorization guard.
    /// </summary>
    public async Task<bool> IsAuthorizedForAnyRequirementAsync(
        ResolvedPolicySnapshot snapshot, string email, CancellationToken ct)
    {
        foreach (var req in snapshot.AllRequirements)
        {
            if (await IsAuthorizedForRequirementAsync(req, email, ct)) return true;
        }
        return false;
    }
}
