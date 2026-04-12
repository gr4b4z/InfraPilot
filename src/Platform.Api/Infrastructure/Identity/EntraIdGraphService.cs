using Microsoft.Graph;
using Microsoft.Graph.Models;

namespace Platform.Api.Infrastructure.Identity;

public class EntraIdGraphService : IIdentityService
{
    private readonly GraphServiceClient _graphClient;

    public EntraIdGraphService(GraphServiceClient graphClient)
    {
        _graphClient = graphClient;
    }

    public async Task<IReadOnlyList<UserInfo>> GetGroupMembers(string groupId, CancellationToken ct = default)
    {
        var members = await _graphClient.Groups[groupId].Members.GetAsync(cancellationToken: ct);
        var users = new List<UserInfo>();

        if (members?.Value is null) return users.AsReadOnly();

        foreach (var member in members.Value.OfType<User>())
        {
            if (member.Id is not null)
            {
                users.Add(new UserInfo(member.Id, member.DisplayName ?? "", member.Mail ?? member.UserPrincipalName ?? ""));
            }
        }

        return users.AsReadOnly();
    }

    public async Task<UserInfo?> GetUser(string userId, CancellationToken ct = default)
    {
        var user = await _graphClient.Users[userId].GetAsync(cancellationToken: ct);
        if (user?.Id is null) return null;
        return new UserInfo(user.Id, user.DisplayName ?? "", user.Mail ?? user.UserPrincipalName ?? "");
    }

    public async Task<IReadOnlyList<UserInfo>> SearchUsers(string query, CancellationToken ct = default)
    {
        var users = await _graphClient.Users.GetAsync(r =>
        {
            r.QueryParameters.Filter = $"startsWith(displayName, '{query}') or startsWith(mail, '{query}')";
            r.QueryParameters.Top = 10;
        }, cancellationToken: ct);

        return (users?.Value ?? [])
            .Where(u => u.Id is not null)
            .Select(u => new UserInfo(u.Id!, u.DisplayName ?? "", u.Mail ?? u.UserPrincipalName ?? ""))
            .ToList()
            .AsReadOnly();
    }
}
