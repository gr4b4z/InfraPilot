namespace Platform.Api.Infrastructure.Identity;

public record UserInfo(string Id, string DisplayName, string Email);

public record GroupInfo(string Id, string DisplayName);

/// <summary>What the directory says about an account right now (see <see cref="IIdentityService.GetAccountStatus"/>).</summary>
public enum AccountStatus
{
    /// <summary>The directory could not say: unreachable, throttled, or not the authority for this id.</summary>
    Unknown,
    Active,
    /// <summary>The account exists but may not sign in (Entra <c>accountEnabled</c> false, or an inactive local user).</summary>
    Disabled,
    /// <summary>The directory positively reports that no such account exists (deleted).</summary>
    Missing,
}

public interface IIdentityService
{
    /// <summary>
    /// The users in group <paramref name="groupId"/> — directly or through a nested group, the same
    /// membership an Entra <c>groups</c> token claim reflects. Who may approve a promotion (the
    /// fallback after role and group claims) and who is notified about a catalog request approval.
    /// </summary>
    Task<IReadOnlyList<UserInfo>> GetGroupMembers(string groupId, CancellationToken ct = default);
    Task<UserInfo?> GetUser(string userId, CancellationToken ct = default);

    /// <summary>
    /// Whether account <paramref name="userId"/> may still sign in. A personal access token outlives
    /// its owner's sign-ins, so this is how it finds out its owner was disabled or deleted (see
    /// <see cref="Platform.Api.Infrastructure.Auth.PersonalTokenOwnerCheck"/>). Never throws for a
    /// lookup that fails: it answers <see cref="AccountStatus.Unknown"/>, because only a positive
    /// answer may stop a credential.
    /// </summary>
    Task<AccountStatus> GetAccountStatus(string userId, CancellationToken ct = default);
    Task<IReadOnlyList<UserInfo>> SearchUsers(string query, CancellationToken ct = default);
    Task<IReadOnlyList<GroupInfo>> SearchGroups(string query, CancellationToken ct = default);

    /// <summary>
    /// <see cref="SearchUsers"/> narrowed to people in any of <paramref name="groups"/> — directly or
    /// through a nested group. What the assignment picker uses for a participant role an admin
    /// restricted to certain groups (Settings → Participant Roles). A group is identified by its
    /// <see cref="GroupInfo.Id"/>; when that isn't a directory object id (a name typed into the
    /// group picker by hand) implementations may fall back to matching on the name.
    /// </summary>
    Task<IReadOnlyList<UserInfo>> SearchUsersInGroups(
        string query, IReadOnlyList<GroupInfo> groups, CancellationToken ct = default);
}
