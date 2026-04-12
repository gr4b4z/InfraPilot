namespace Platform.Api.Infrastructure.Identity;

public class StubIdentityService : IIdentityService
{
    public Task<IReadOnlyList<UserInfo>> GetGroupMembers(string groupId, CancellationToken ct = default)
    {
        IReadOnlyList<UserInfo> users = new List<UserInfo>
        {
            new("user-1", "Dev User", "dev@softwareone.com"),
        };
        return Task.FromResult(users);
    }

    public Task<UserInfo?> GetUser(string userId, CancellationToken ct = default)
    {
        return Task.FromResult<UserInfo?>(new UserInfo(userId, "Dev User", "dev@softwareone.com"));
    }

    public Task<IReadOnlyList<UserInfo>> SearchUsers(string query, CancellationToken ct = default)
    {
        IReadOnlyList<UserInfo> users = new List<UserInfo>
        {
            new("user-1", "Dev User", "dev@softwareone.com"),
        };
        return Task.FromResult(users);
    }
}
