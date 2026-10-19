using System.Collections.Concurrent;
using Azure.Identity;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Graph.Models.ODataErrors;

namespace Platform.Api.Infrastructure.Identity;

/// <summary>
/// Entra ID directory reads through Microsoft Graph. Registered as a singleton: group member lists
/// are cached process-wide (see <see cref="GetGroupMembers"/>), so one Graph read answers every
/// user and request that checks the same group.
///
/// <para>Every read goes through <see cref="ReadGraphAsync{T}"/>, which tells
/// <see cref="GraphCredentialHealth"/> whether the app's own sign-in is working — the one failure
/// every caller here swallows (empty searches, "not a member", "unknown") but an admin has to act
/// on.</para>
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
    private readonly GraphCredentialHealth _credentialHealth;
    private readonly ILogger<EntraIdGraphService> _logger;

    // Concurrent misses for one group share a single Graph read.
    private readonly ConcurrentDictionary<string, Lazy<Task<IReadOnlyList<UserInfo>>>> _inFlight = new();

    public EntraIdGraphService(
        GraphServiceClient graphClient, IMemoryCache cache, GraphCredentialHealth credentialHealth,
        ILogger<EntraIdGraphService> logger)
    {
        _graphClient = graphClient;
        _cache = cache;
        _credentialHealth = credentialHealth;
        _logger = logger;
    }

    // ── Credential health ────────────────────────────────────────────────────

    private const string CredentialFailedLogKey = "graph-credential-failed";

    /// <summary>
    /// Runs one Graph read and keeps <see cref="GraphCredentialHealth"/> current. A credential
    /// failure (Azure.Identity's <see cref="AuthenticationFailedException"/>: an expired or wrong
    /// client secret) is recorded and logged before it propagates; Graph answering — even with an
    /// error, like a 404 for a deleted group — proves the credential works and clears it.
    /// </summary>
    private async Task<T> ReadGraphAsync<T>(Func<Task<T>> read)
    {
        try
        {
            var result = await read();
            CredentialWorked();
            return result;
        }
        catch (Exception ex)
        {
            if (GraphCredentialHealth.CredentialFailureIn(ex) is { } credentialFailure)
                CredentialFailed(credentialFailure);
            // Any answer took a token to get, except a 401: that is Graph refusing the token.
            else if (ex is ODataError { ResponseStatusCode: not (int)System.Net.HttpStatusCode.Unauthorized })
                CredentialWorked();
            throw;
        }
    }

    // Error, once per MembershipTtl however many reads hit it: this one stops every directory read,
    // and nothing else surfaces it — each caller deliberately carries on with an empty answer.
    private void CredentialFailed(AuthenticationFailedException ex)
    {
        var failure = _credentialHealth.RecordFailure(ex);
        if (FirstInWindow(CredentialFailedLogKey))
            _logger.LogError(ex,
                "Microsoft Graph sign-in is failing ({Code}) since {Since:u}; directory search, group-membership " +
                "approval checks and account checks get no answers until the Graph app's client secret " +
                "(GRAPH_CLIENT_SECRET) is fixed",
                failure.Code ?? "no AADSTS code", failure.Since);
        else
            _logger.LogDebug(ex, "Microsoft Graph sign-in failed again ({Code})", failure.Code);
    }

    private void CredentialWorked()
    {
        if (_credentialHealth.RecordSuccess() is not { } cleared) return;
        // So that a later failure is a new outage and logs at Error straight away.
        _cache.Remove(CredentialFailedLogKey);
        _logger.LogInformation(
            "Microsoft Graph sign-in works again; it had been failing ({Code}) since {Since:u}",
            cleared.Code ?? "no AADSTS code", cleared.Since);
    }

    // ── Group membership ─────────────────────────────────────────────────────

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
    /// <see cref="MembershipTtl"/>; a credential failure is logged once for all groups instead, see
    /// <see cref="ReadGraphAsync{T}"/>), remembered for <see cref="FailureTtl"/>, and answered with
    /// no members — the same "not a member" the approval check reached before by catching the
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
            var members = await ReadGraphAsync(() => ReadAllMembersAsync(objectId.ToString("D")));
            _cache.Set(key, members, MembershipTtl);
            return members;
        }
        catch (Exception ex)
        {
            if (GraphCredentialHealth.CredentialFailureIn(ex) is not null)
                _logger.LogDebug(ex,
                    "Group membership lookup failed for {Group} because Graph sign-in is failing; " +
                    "treating it as having no members for {RetryAfter}", objectId, FailureTtl);
            else if (FirstInWindow($"graph-members-failed:{objectId:D}"))
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
        var user = await ReadGraphAsync(() => _graphClient.Users[userId].GetAsync(cancellationToken: ct));
        if (user?.Id is null) return null;
        return new UserInfo(user.Id, user.DisplayName ?? "", user.Mail ?? user.UserPrincipalName ?? "");
    }

    /// <summary>
    /// <c>users/{id}?$select=id,accountEnabled</c> — one small read, within the <c>User.Read.All</c>
    /// the Graph app already holds. A 404 means the user was deleted; <c>accountEnabled</c> false
    /// means disabled. Anything else that goes wrong (throttling, an outage, a missing permission
    /// that leaves <c>accountEnabled</c> out) is <see cref="AccountStatus.Unknown"/>, logged, never
    /// thrown. Not cached here: the caller decides how long an answer holds.
    /// </summary>
    public async Task<AccountStatus> GetAccountStatus(string userId, CancellationToken ct = default)
    {
        // An Entra object id is a GUID. Anything else (an API key's "apikey:name", a local id from
        // another mode) is not this directory's to judge, and users/{name} would only 404 on it.
        if (!Guid.TryParse(userId, out var objectId)) return AccountStatus.Unknown;

        try
        {
            var user = await ReadGraphAsync(() => _graphClient.Users[objectId.ToString("D")].GetAsync(r =>
                r.QueryParameters.Select = ["id", "accountEnabled"], cancellationToken: ct));
            if (user?.Id is null) return AccountStatus.Unknown;
            return user.AccountEnabled == false ? AccountStatus.Disabled : AccountStatus.Active;
        }
        catch (ODataError ex) when (ex.ResponseStatusCode == (int)System.Net.HttpStatusCode.NotFound)
        {
            return AccountStatus.Missing;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // A credential failure is already logged, once for every lookup (ReadGraphAsync).
            if (GraphCredentialHealth.CredentialFailureIn(ex) is null
                && FirstInWindow($"graph-account-failed:{objectId:D}"))
                _logger.LogWarning(ex, "Account status lookup failed for {User}; treating it as unknown", objectId);
            return AccountStatus.Unknown;
        }
    }

    public async Task<IReadOnlyList<UserInfo>> SearchUsers(string query, CancellationToken ct = default)
    {
        var users = await ReadGraphAsync(() => _graphClient.Users.GetAsync(r =>
        {
            r.QueryParameters.Filter = UserStartsWith(query);
            r.QueryParameters.Top = SearchLimit;
        }, cancellationToken: ct));

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

            var page = await ReadGraphAsync(() => _graphClient.Groups[groupId].TransitiveMembers.GraphUser.GetAsync(r =>
            {
                r.QueryParameters.Filter = UserStartsWith(query);
                r.QueryParameters.Count = true;
                r.QueryParameters.Top = SearchLimit;
                r.Headers.Add("ConsistencyLevel", "eventual");
            }, cancellationToken: ct));

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
        var matches = await ReadGraphAsync(() => _graphClient.Groups.GetAsync(r =>
        {
            r.QueryParameters.Filter = $"displayName eq '{ODataString(name)}'";
            r.QueryParameters.Top = 1;
        }, cancellationToken: ct));
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
        var groups = await ReadGraphAsync(() => _graphClient.Groups.GetAsync(r =>
        {
            r.QueryParameters.Filter = $"startsWith(displayName, '{ODataString(query)}')";
            r.QueryParameters.Top = SearchLimit;
        }, cancellationToken: ct));

        return (groups?.Value ?? [])
            .Where(g => g.Id is not null)
            .Select(g => new GroupInfo(g.Id!, g.DisplayName ?? ""))
            .ToList()
            .AsReadOnly();
    }
}
