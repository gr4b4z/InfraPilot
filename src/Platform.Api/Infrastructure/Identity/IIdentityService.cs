namespace Platform.Api.Infrastructure.Identity;

public record UserInfo(string Id, string DisplayName, string Email);

public interface IIdentityService
{
    Task<IReadOnlyList<UserInfo>> GetGroupMembers(string groupId, CancellationToken ct = default);
    Task<UserInfo?> GetUser(string userId, CancellationToken ct = default);
    Task<IReadOnlyList<UserInfo>> SearchUsers(string query, CancellationToken ct = default);
}
