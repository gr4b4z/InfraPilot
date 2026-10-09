using System.Collections.Concurrent;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Graph;
using Microsoft.Graph.Models;

namespace Platform.Api.Infrastructure.Identity;

/// <summary>
/// Entra ID directory reads through Microsoft Graph. Registered as a singleton: group member lists
/// are cached process-wide (see <see cref="GetGroupMembers"/>), so one Graph read answers every
/// user and request that checks the same group.
/// </summary>
public class EntraIdGraphService : IIdentityService
{
    // How long a group's member list is trusted. The token `groups` claims the authorizer checks
    // first are already as old as the token (up to an hour or so), so this adds little staleness.
    public static readonly TimeSpan MembershipTtl = TimeSpan.FromMinutes(5);

    // A failed read is remembered briefly, so a Graph outage or a dead group reference costs one
    // round trip per group per minute instead of one per approval check.
    public static readonly TimeSpan FailureTtl = TimeSpan.FromMinutes(1);

    // Only what UserInfo carries — Graph returns ~30 properties per user otherwise. 999 is the
    // largest page /transitiveMembers serves, so even big groups take a handful of round trips.
    private static readonly string[] MemberFields = ["id", "displayName", "mail", "userPrincipalName"];
    private const int MemberPageSize = 999;

    private readonly GraphServiceClient _graphClient;
    private readonly IMemoryCache _cache;
    private readonly ILogger<EntraIdGraphService> _logger;

    // Concurrent misses for one group share a single Graph read.
    private readonly ConcurrentDictionary<string, Lazy<Task<IReadOnlyList<UserInfo>>>> _inFlight = new();

    public EntraIdGraphService(
        GraphServiceClient graphClient, IMemoryCache cache, ILogger<EntraIdGraphService> logger)
    {
        _graphClient = graphClient;
        _cache = cache;
        _logger = logger;
    }

    /// <summary>
    /// The users that are members of the group with object id <paramref name="groupId"/>, directly
    /// or through a nested group (the same transitive membership the token's <c>groups</c> claim
    /// carries), across every result page. Cached for <see cref="MembershipTtl"/> per group and
    /// shared by all callers.
    ///
    /// <para>Only directory object ids (GUIDs) are sent to Graph. A policy can also name a group by
    /// display name or app-role name — legacy bare strings, or text typed into the group picker —
    /// and <c>groups/{name}</c> can only fail with "Invalid object identifier", so such a reference
    /// answers with no members without a round trip. It still matches through role claims. Names are
    /// deliberately not resolved to ids here: display names are neither unique nor fixed, which is
    /// not something an approval decision should key off.</para>
    ///
    /// <para>A failed read is logged (Warning, at most once per group per
    /// <see cref="MembershipTtl"/>), remembered for <see cref="FailureTtl"/>, and answered with no
    /// members — the same "not a member" the approval check reached before by catching the
    /// error.</para>
    /// </summary>
    public async Task<IReadOnlyList<UserInfo>> GetGroupMembers(string groupId, CancellationToken ct = default)
    {
        if (!Guid.TryParse(groupId, out var objectId))
        {
            if (FirstInWindow($"graph-members-skipped:{groupId.ToLowerInvariant()}"))
                _logger.LogInformation(
                    "Group {Group} is not a directory object id; skipping the Graph membership lookup " +
                    "(it can still match through role claims)", groupId);
            return [];
        }

        var key = $"graph-members:{objectId:D}";
        if (_cache.TryGetValue(key, out IReadOnlyList<UserInfo>? cached) && cached is not null)
            return cached;

        var read = _inFlight.GetOrAdd(key,
            _ => new Lazy<Task<IReadOnlyList<UserInfo>>>(() => ReadAndCacheMembersAsync(objectId, key)));
        // The shared read runs uncancelled so one caller giving up doesn't fail the others waiting
        // on it; each caller only stops waiting on its own token.
        return await read.Value.WaitAsync(ct);
    }

    private async Task<IReadOnlyList<UserInfo>> ReadAndCacheMembersAsync(Guid objectId, string key)
    {
        try
        {
            var members = await ReadAllMembersAsync(objectId.ToString("D"));
            _cache.Set(key, members, MembershipTtl);
            return members;
        }
        catch (Exception ex)
        {
            if (FirstInWindow($"graph-members-failed:{objectId:D}"))
                _logger.LogWarning(ex,
                    "Group membership lookup failed for {Group}; treating it as having no members for {RetryAfter}",
                    objectId, FailureTtl);
            else
                _logger.LogDebug(ex, "Group membership lookup failed again for {Group}", objectId);

            IReadOnlyList<UserInfo> none = [];
            _cache.Set(key, none, FailureTtl);
            return none;
        }
        finally
        {
            _inFlight.TryRemove(key, out _);
        }
    }

    // Transitive, so someone in a group nested inside this one counts — as they already do through
    // the token's `groups` claim, which Entra fills with transitive memberships. The
    // microsoft.graph.user cast leaves out the nested groups themselves, devices and service
    // principals: only users can approve. Graph documents the cast as an advanced directory query,
    // hence ConsistencyLevel + $count; that index can trail a membership change briefly, which the
    // 5-minute cache already tolerates.
    private async Task<IReadOnlyList<UserInfo>> ReadAllMembersAsync(string groupId)
    {
        var users = new List<UserInfo>();
        var firstPage = await _graphClient.Groups[groupId].TransitiveMembers.GraphUser.GetAsync(r =>
        {
            r.QueryParameters.Select = MemberFields;
            r.QueryParameters.Top = MemberPageSize;
            r.QueryParameters.Count = true;
            r.Headers.Add("ConsistencyLevel", "eventual");
        });
        if (firstPage is null) return users.AsReadOnly();

        var pages = PageIterator<User, UserCollectionResponse>.CreatePageIterator(
            _graphClient, firstPage, user =>
            {
                // The cast already filters server-side; this keeps a stray non-user from ever counting.
                if (user is { Id: { } id, OdataType: null or "#microsoft.graph.user" })
                    users.Add(new UserInfo(id, user.DisplayName ?? "", user.Mail ?? user.UserPrincipalName ?? ""));
                return true;
            },
            // Next-page requests are built from @odata.nextLink alone and don't carry the header.
            next =>
            {
                next.Headers.Add("ConsistencyLevel", "eventual");
                return next;
            });
        await pages.IterateAsync();
        return users.AsReadOnly();
    }

    // True the first time per MembershipTtl for this key — keeps a recurring condition to one log line.
    private bool FirstInWindow(string key)
    {
        if (_cache.TryGetValue(key, out _)) return false;
        _cache.Set(key, true, MembershipTtl);
        return true;
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
