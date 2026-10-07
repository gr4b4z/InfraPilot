namespace Platform.Api.Infrastructure.Identity;

public record UserInfo(string Id, string DisplayName, string Email);

public record GroupInfo(string Id, string DisplayName);

public interface IIdentityService
{
    Task<IReadOnlyList<UserInfo>> GetGroupMembers(string groupId, CancellationToken ct = default);
    Task<UserInfo?> GetUser(string userId, CancellationToken ct = default);
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
