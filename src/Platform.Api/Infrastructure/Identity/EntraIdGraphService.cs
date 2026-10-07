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
            r.QueryParameters.Filter = UserStartsWith(query);
            r.QueryParameters.Top = SearchLimit;
        }, cancellationToken: ct);

        return ToUserInfos(users?.Value);
    }

    /// <summary>
    /// One <c>transitiveMembers/microsoft.graph.user</c> query per group, so people who are in the
    /// group through a nested group count, unioned and deduped on object id. Filtering on a
    /// group's members is an advanced directory query, hence the <c>ConsistencyLevel</c> header and
    /// <c>$count</c>.
    /// </summary>
    public async Task<IReadOnlyList<UserInfo>> SearchUsersInGroups(
        string query, IReadOnlyList<GroupInfo> groups, CancellationToken ct = default)
    {
        var found = new List<UserInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in groups)
        {
            var groupId = await ResolveGroupIdAsync(group, ct);
            if (groupId is null) continue;

            var page = await _graphClient.Groups[groupId].TransitiveMembers.GraphUser.GetAsync(r =>
            {
                r.QueryParameters.Filter = UserStartsWith(query);
                r.QueryParameters.Count = true;
                r.QueryParameters.Top = SearchLimit;
                r.Headers.Add("ConsistencyLevel", "eventual");
            }, cancellationToken: ct);

            foreach (var user in ToUserInfos(page?.Value))
            {
                if (seen.Add(user.Id)) found.Add(user);
            }
        }

        return found
            .OrderBy(u => u.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Take(SearchLimit)
            .ToList()
            .AsReadOnly();
    }

    /// <summary>
    /// The directory object id to query. The group picker stores the id it was given by Graph, but
    /// also lets an admin type a group name it couldn't find — that one is looked up by exact
    /// display name, and skipped when the directory doesn't know it.
    /// </summary>
    private async Task<string?> ResolveGroupIdAsync(GroupInfo group, CancellationToken ct)
    {
        if (Guid.TryParse(group.Id, out _)) return group.Id;

        var name = string.IsNullOrWhiteSpace(group.DisplayName) ? group.Id : group.DisplayName;
        var matches = await _graphClient.Groups.GetAsync(r =>
        {
            r.QueryParameters.Filter = $"displayName eq '{ODataString(name)}'";
            r.QueryParameters.Top = 1;
        }, cancellationToken: ct);
        return matches?.Value?.FirstOrDefault()?.Id;
    }

    private const int SearchLimit = 10;

    private static string UserStartsWith(string query)
    {
        var q = ODataString(query);
        return $"startsWith(displayName, '{q}') or startsWith(mail, '{q}')";
    }

    // A quote in a name ("O'Brien") would otherwise end the OData string literal and fail the query.
    private static string ODataString(string value) => value.Replace("'", "''");

    private static IReadOnlyList<UserInfo> ToUserInfos(IEnumerable<User>? users)
        => (users ?? [])
            .Where(u => u.Id is not null)
            .Select(u => new UserInfo(u.Id!, u.DisplayName ?? "", u.Mail ?? u.UserPrincipalName ?? ""))
            .ToList()
            .AsReadOnly();

    public async Task<IReadOnlyList<GroupInfo>> SearchGroups(string query, CancellationToken ct = default)
    {
        var groups = await _graphClient.Groups.GetAsync(r =>
        {
            r.QueryParameters.Filter = $"startsWith(displayName, '{ODataString(query)}')";
            r.QueryParameters.Top = SearchLimit;
        }, cancellationToken: ct);

        return (groups?.Value ?? [])
            .Where(g => g.Id is not null)
            .Select(g => new GroupInfo(g.Id!, g.DisplayName ?? ""))
            .ToList()
            .AsReadOnly();
    }
}
