using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Internal;
using Microsoft.Extensions.Logging;
using Microsoft.Graph;
using NSubstitute;
using Platform.Api.Features.Promotions;
using Platform.Api.Features.Promotions.Models;
using Platform.Api.Infrastructure.Auth;
using Platform.Api.Infrastructure.Identity;

namespace Platform.Api.Tests.Infrastructure.Identity;

/// <summary>
/// Group-membership reads in <see cref="EntraIdGraphService"/>: the process-wide cache, failure
/// caching, object-id-only lookups, transitive user membership, and paging — and the app's own
/// sign-in failing (<see cref="GraphCredentialHealth"/>). A real <see cref="GraphServiceClient"/>
/// signs in through a fake credential and runs over a fake HTTP handler that serves canned Graph
/// responses and counts requests — nothing leaves the process.
/// </summary>
public class EntraIdGraphServiceTests
{
    private const string GroupId = "5a7c3b0e-8f1d-4c2a-9b6e-1d2f3a4b5c6d";

    private readonly FakeGraphHandler _graph = new();
    private readonly FakeCredential _credential = new();
    private readonly FakeClock _clock = new();
    private readonly ListLogger<EntraIdGraphService> _log = new();
    private readonly GraphCredentialHealth _health;
    private readonly EntraIdGraphService _sut;

    public EntraIdGraphServiceTests()
    {
        var client = new GraphServiceClient(new HttpClient(_graph), _credential);
        var cache = new MemoryCache(new MemoryCacheOptions { Clock = _clock });
        _health = new GraphCredentialHealth(_clock);
        _sut = new EntraIdGraphService(client, cache, _health, _log);
    }

    // ── Cache ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Members_are_read_once_and_served_from_cache_after()
    {
        _graph.Respond = _ => MembersPage(null, ("u1", "a@example.com"));

        var first = await _sut.GetGroupMembers(GroupId);
        var second = await _sut.GetGroupMembers(GroupId);

        Assert.Equal(1, _graph.RequestCount);
        Assert.Equal("a@example.com", Assert.Single(first).Email);
        Assert.Same(first, second);
    }

    [Fact]
    public async Task Cache_is_keyed_by_object_id_regardless_of_case()
    {
        _graph.Respond = _ => MembersPage(null, ("u1", "a@example.com"));

        await _sut.GetGroupMembers(GroupId);
        await _sut.GetGroupMembers(GroupId.ToUpperInvariant());

        Assert.Equal(1, _graph.RequestCount);
    }

    [Fact]
    public async Task Cached_members_expire_after_the_membership_ttl()
    {
        _graph.Respond = _ => MembersPage(null, ("u1", "a@example.com"));

        await _sut.GetGroupMembers(GroupId);
        _clock.Advance(EntraIdGraphService.MembershipTtl - TimeSpan.FromSeconds(1));
        await _sut.GetGroupMembers(GroupId);
        Assert.Equal(1, _graph.RequestCount);

        _clock.Advance(TimeSpan.FromSeconds(2));
        await _sut.GetGroupMembers(GroupId);
        Assert.Equal(2, _graph.RequestCount);
    }

    [Fact]
    public async Task Concurrent_misses_for_one_group_share_a_single_read()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _graph.RespondAsync = async _ =>
        {
            await release.Task;
            return MembersPage(null, ("u1", "a@example.com"));
        };

        var calls = Enumerable.Range(0, 5).Select(_ => _sut.GetGroupMembers(GroupId)).ToList();
        release.SetResult();
        var results = await Task.WhenAll(calls);

        Assert.Equal(1, _graph.RequestCount);
        Assert.All(results, r => Assert.Equal("u1", Assert.Single(r).Id));
    }

    // ── Object ids only ──────────────────────────────────────────────────────

    [Fact]
    public async Task A_name_is_never_sent_to_graph_and_is_logged_once_per_ttl()
    {
        // The 2026-10-07 incident: Graph was asked for groups/Org_CTO_SS_DEVOPS_SG on every check.
        for (var i = 0; i < 3; i++)
            Assert.Empty(await _sut.GetGroupMembers("Org_CTO_SS_DEVOPS_SG"));

        Assert.Equal(0, _graph.RequestCount);
        var entry = Assert.Single(_log.Entries);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Contains("Org_CTO_SS_DEVOPS_SG", entry.Message);

        _clock.Advance(EntraIdGraphService.MembershipTtl + TimeSpan.FromSeconds(1));
        await _sut.GetGroupMembers("Org_CTO_SS_DEVOPS_SG");
        Assert.Equal(2, _log.Entries.Count);
        Assert.Equal(0, _graph.RequestCount);
    }

    // ── Failures ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_failed_read_is_cached_briefly_and_warned_about_once_per_ttl()
    {
        _graph.Respond = _ => GraphError(HttpStatusCode.NotFound, "Request_ResourceNotFound");

        // Within the failure TTL: one round trip, one warning, no members — however many checks.
        for (var i = 0; i < 3; i++)
            Assert.Empty(await _sut.GetGroupMembers(GroupId));
        Assert.Equal(1, _graph.RequestCount);
        Assert.Equal(1, _log.Count(LogLevel.Warning));

        // After it, Graph is retried — but the repeat failure isn't warned about again yet.
        _clock.Advance(EntraIdGraphService.FailureTtl + TimeSpan.FromSeconds(1));
        Assert.Empty(await _sut.GetGroupMembers(GroupId));
        Assert.Equal(2, _graph.RequestCount);
        Assert.Equal(1, _log.Count(LogLevel.Warning));

        // Once the membership TTL has passed since the first warning, a failure warns again.
        _clock.Advance(EntraIdGraphService.MembershipTtl);
        await _sut.GetGroupMembers(GroupId);
        Assert.Equal(3, _graph.RequestCount);
        Assert.Equal(2, _log.Count(LogLevel.Warning));
    }

    [Fact]
    public async Task A_group_that_recovers_is_read_again_after_the_failure_ttl()
    {
        _graph.Respond = _ => GraphError(HttpStatusCode.ServiceUnavailable, "serviceNotAvailable");
        Assert.Empty(await _sut.GetGroupMembers(GroupId));

        _graph.Respond = _ => MembersPage(null, ("u1", "a@example.com"));
        _clock.Advance(EntraIdGraphService.FailureTtl + TimeSpan.FromSeconds(1));

        Assert.Single(await _sut.GetGroupMembers(GroupId));
        Assert.Single(await _sut.GetGroupMembers(GroupId));
        Assert.Equal(2, _graph.RequestCount);
    }

    // ── Paging and shape ─────────────────────────────────────────────────────

    [Fact]
    public async Task Every_page_is_read_transitively_with_a_trimmed_select()
    {
        const string next =
            $"https://graph.microsoft.com/v1.0/groups/{GroupId}/transitiveMembers/microsoft.graph.user?$skiptoken=page2";
        _graph.Respond = req => req.RequestUri!.Query.Contains("skiptoken")
            ? MembersPage(null, ("u3", "c@example.com"))
            : MembersPage(next, ("u1", "a@example.com"), ("u2", "b@example.com"));

        var members = await _sut.GetGroupMembers(GroupId);

        Assert.Equal(new[] { "u1", "u2", "u3" }, members.Select(m => m.Id));
        Assert.Equal(2, _graph.RequestCount);

        // Nested-group members included, users only: the transitive list with the user cast (which
        // the SDK spells graph.user, short for microsoft.graph.user).
        var first = _graph.Requests.First();
        Assert.Contains($"/groups/{GroupId}/transitiveMembers/graph.user?", first);
        Assert.Contains("$select=id,displayName,mail,userPrincipalName", first);
        Assert.Contains("$top=999", first);
        // The cast is an advanced query — and the follow-up page needs the header too.
        Assert.Contains("$count=true", first);
        Assert.All(_graph.ConsistencyLevels, h => Assert.Equal("eventual", h));
    }

    [Fact]
    public async Task Only_users_are_kept()
    {
        // The cast means Graph shouldn't send anything else, but nothing else may become an approver.
        _graph.Respond = _ => Json(new
        {
            value = new object[]
            {
                new Dictionary<string, object?> { ["@odata.type"] = "#microsoft.graph.user", ["id"] = "u1", ["mail"] = "a@example.com" },
                new Dictionary<string, object?> { ["@odata.type"] = "#microsoft.graph.group", ["id"] = "nested-group" },
                new Dictionary<string, object?> { ["@odata.type"] = "#microsoft.graph.servicePrincipal", ["id"] = "sp" },
                new Dictionary<string, object?> { ["@odata.type"] = "#microsoft.graph.device", ["id"] = "device" },
            },
        });

        var member = Assert.Single(await _sut.GetGroupMembers(GroupId));
        Assert.Equal("u1", member.Id);
    }

    [Fact]
    public async Task Mail_falls_back_to_user_principal_name()
    {
        _graph.Respond = _ => Json(new
        {
            value = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["@odata.type"] = "#microsoft.graph.user",
                    ["id"] = "u1",
                    ["mail"] = null,
                    ["userPrincipalName"] = "upn@example.com",
                },
            },
        });

        var member = Assert.Single(await _sut.GetGroupMembers(GroupId));
        Assert.Equal("upn@example.com", member.Email);
    }

    // ── User search ──────────────────────────────────────────────────────────

    [Fact]
    public async Task SearchUsers_escapes_single_quotes_in_the_filter()
    {
        _graph.Respond = _ => Json(new { value = Array.Empty<object>() });

        await _sut.SearchUsers("O'Brien");

        var request = Assert.Single(_graph.Requests);
        Assert.Contains("startsWith(displayName, 'O''Brien') or startsWith(mail, 'O''Brien')", request);
    }

    // ── Through the approval authorizer ──────────────────────────────────────

    [Fact]
    public async Task Approval_checks_by_different_users_share_one_graph_read()
    {
        _graph.Respond = _ => MembersPage(null, ("member-id", "member@example.com"));
        var group = new GroupRef(GroupId, "Release Approvers");

        Assert.False(await AuthorizerFor("someone-id", "someone@example.com").IsInApproverGroupAsync(group, default));
        Assert.True(await AuthorizerFor("member-id", "other@example.com").IsInApproverGroupAsync(group, default));
        Assert.True(await AuthorizerFor("x", "MEMBER@example.com").IsInApproverGroupAsync(group, default));

        // One read for the object id; the display name never reaches Graph.
        var request = Assert.Single(_graph.Requests);
        Assert.Contains($"/groups/{GroupId}/transitiveMembers/graph.user?", request);
    }

    [Fact]
    public async Task A_name_only_group_matches_through_role_claims_without_graph()
    {
        var group = new GroupRef("Org_CTO_SS_DEVOPS_SG", "Org_CTO_SS_DEVOPS_SG");

        Assert.False(await AuthorizerFor("a", "a@example.com").IsInApproverGroupAsync(group, default));
        Assert.True(await AuthorizerFor("b", "b@example.com", roles: ["Org_CTO_SS_DEVOPS_SG"])
            .IsInApproverGroupAsync(group, default));

        Assert.Equal(0, _graph.RequestCount);
        Assert.Equal(0, _log.Count(LogLevel.Warning));
    }

    private PromotionApprovalAuthorizer AuthorizerFor(string id, string email, string[]? roles = null)
    {
        var user = Substitute.For<ICurrentUser>();
        user.Id.Returns(id);
        user.Email.Returns(email);
        user.IsAdmin.Returns(false);
        user.Roles.Returns((roles ?? []).ToList().AsReadOnly());
        user.Groups.Returns(new List<string>().AsReadOnly());
        user.IsInGroup(Arg.Any<string>()).Returns(false);
        return new PromotionApprovalAuthorizer(user, _sut, new ListLogger<PromotionApprovalAuthorizer>());
    }

    // ── Account status (personal-token owner check) ─────────────────────────

    private const string UserId = "0f3b6a2e-1c4d-4e5f-8a9b-7c6d5e4f3a2b";

    [Theory]
    [InlineData(true, AccountStatus.Active)]
    [InlineData(false, AccountStatus.Disabled)]
    public async Task Account_status_reads_account_enabled_and_nothing_else(bool enabled, AccountStatus expected)
    {
        _graph.Respond = _ => Json(new { id = UserId, accountEnabled = enabled });

        Assert.Equal(expected, await _sut.GetAccountStatus(UserId));

        var request = Assert.Single(_graph.Requests);
        Assert.Contains($"/users/{UserId}", request);
        Assert.Contains("$select=id,accountEnabled", request);
    }

    [Fact]
    public async Task A_deleted_user_is_missing()
    {
        _graph.Respond = _ => GraphError(HttpStatusCode.NotFound, "Request_ResourceNotFound");

        Assert.Equal(AccountStatus.Missing, await _sut.GetAccountStatus(UserId));
    }

    [Fact]
    public async Task An_unanswered_lookup_is_unknown_and_warned_about_once_per_ttl()
    {
        _graph.Respond = _ => GraphError(HttpStatusCode.TooManyRequests, "TooManyRequests");

        Assert.Equal(AccountStatus.Unknown, await _sut.GetAccountStatus(UserId));
        Assert.Equal(AccountStatus.Unknown, await _sut.GetAccountStatus(UserId));
        Assert.Equal(1, _log.Count(LogLevel.Warning));
    }

    [Fact]
    public async Task A_missing_account_enabled_is_not_read_as_disabled()
    {
        // What a Graph app without User.Read.All would get back: the property left out.
        _graph.Respond = _ => Json(new { id = UserId });

        Assert.Equal(AccountStatus.Active, await _sut.GetAccountStatus(UserId));
    }

    [Fact]
    public async Task A_non_object_id_is_unknown_without_a_round_trip()
    {
        Assert.Equal(AccountStatus.Unknown, await _sut.GetAccountStatus("apikey:pipeline"));
        Assert.Equal(0, _graph.RequestCount);
    }

    // ── Credential health ────────────────────────────────────────────────────

    // What Azure.Identity's ClientSecretCredential threw in prod on 2026-10-19 (ids made up).
    private const string ExpiredSecret =
        "ClientSecretCredential authentication failed: AADSTS7000222: The provided client secret keys for app " +
        "'b8e60444-0000-4000-8000-000000000000' are expired. Visit the Azure portal to create new keys for your " +
        "app: https://aka.ms/NewClientSecret, or consider using certificate credentials for added security: " +
        "https://aka.ms/certCreds. Trace ID: 0d6a3c1e-2f4b-4e5a-9c7d-8e9f0a1b2c3d Correlation ID: " +
        "5f2b7c9d-1e3a-4b6c-8d0e-2f4a6b8c0d1e Timestamp: 2026-10-19 00:15:10Z\n" +
        "See the troubleshooting guide for more information. " +
        "https://aka.ms/azsdk/net/identity/clientsecretcredential/troubleshoot";

    [Fact]
    public async Task An_expired_secret_is_recorded_from_every_kind_of_read()
    {
        _credential.FailWith = ExpiredSecret;
        var since = _clock.UtcNow;

        // Searches let it through for the endpoint to turn into `directoryUnavailable`; the reads
        // that answer for an approval or a token check swallow it, as they always did.
        await Assert.ThrowsAsync<AuthenticationFailedException>(() => _sut.SearchUsers("mateusz"));
        await Assert.ThrowsAsync<AuthenticationFailedException>(() => _sut.SearchGroups("org_cto"));
        await Assert.ThrowsAsync<AuthenticationFailedException>(() =>
            _sut.SearchUsersInGroups("mateusz", [new GroupInfo(GroupId, "Release Approvers")]));
        _clock.Advance(TimeSpan.FromSeconds(30));
        Assert.Empty(await _sut.GetGroupMembers(GroupId));
        Assert.Equal(AccountStatus.Unknown, await _sut.GetAccountStatus(UserId));

        // Nothing reached Graph: the sign-in fails before a request goes out.
        Assert.Equal(0, _graph.RequestCount);
        var failure = Assert.IsType<GraphCredentialFailure>(_health.Failure);
        Assert.Equal("AADSTS7000222", failure.Code);
        Assert.Equal(
            "The provided client secret keys for app 'b8e60444-0000-4000-8000-000000000000' are expired.",
            failure.Message);
        Assert.Equal(since, failure.Since);
        Assert.Equal(_clock.UtcNow, failure.LastFailedAt);
    }

    [Fact]
    public async Task A_credential_failure_is_logged_as_an_error_once_per_window_and_never_as_a_warning()
    {
        _credential.FailWith = ExpiredSecret;

        for (var i = 0; i < 3; i++)
        {
            await Assert.ThrowsAsync<AuthenticationFailedException>(() => _sut.SearchUsers("mateusz"));
            Assert.Empty(await _sut.GetGroupMembers(GroupId));
            Assert.Equal(AccountStatus.Unknown, await _sut.GetAccountStatus(UserId));
        }
        var error = Assert.Single(_log.Entries, e => e.Level == LogLevel.Error);
        Assert.Contains("AADSTS7000222", error.Message);
        Assert.Contains("GRAPH_CLIENT_SECRET", error.Message);
        Assert.Equal(0, _log.Count(LogLevel.Warning));

        // Still failing a window later: one more Error, so an alert on Error keeps firing.
        _clock.Advance(EntraIdGraphService.MembershipTtl + TimeSpan.FromSeconds(1));
        await Assert.ThrowsAsync<AuthenticationFailedException>(() => _sut.SearchUsers("mateusz"));
        Assert.Equal(2, _log.Count(LogLevel.Error));
        Assert.Equal(0, _log.Count(LogLevel.Warning));
    }

    [Fact]
    public async Task The_next_read_that_gets_through_clears_the_failure()
    {
        _credential.FailWith = ExpiredSecret;
        await Assert.ThrowsAsync<AuthenticationFailedException>(() => _sut.SearchUsers("mateusz"));
        Assert.NotNull(_health.Failure);

        _credential.FailWith = null;
        _graph.Respond = _ => Json(new { value = Array.Empty<object>() });
        Assert.Empty(await _sut.SearchUsers("mateusz"));

        Assert.Null(_health.Failure);
        Assert.Contains(_log.Entries, e => e.Level == LogLevel.Information && e.Message.Contains("works again"));

        // A new failure right after is a new outage: logged at Error straight away, with its own start.
        _clock.Advance(TimeSpan.FromSeconds(10));
        _credential.FailWith = ExpiredSecret;
        await Assert.ThrowsAsync<AuthenticationFailedException>(() => _sut.SearchUsers("mateusz"));
        Assert.Equal(2, _log.Count(LogLevel.Error));
        Assert.Equal(_clock.UtcNow, _health.Failure!.Since);
    }

    [Fact]
    public async Task Graph_answering_with_an_error_still_proves_the_credential_works()
    {
        _credential.FailWith = ExpiredSecret;
        Assert.Equal(AccountStatus.Unknown, await _sut.GetAccountStatus(UserId));
        Assert.NotNull(_health.Failure);

        // A deleted user's 404 took a token to get.
        _credential.FailWith = null;
        _graph.Respond = _ => GraphError(HttpStatusCode.NotFound, "Request_ResourceNotFound");
        Assert.Equal(AccountStatus.Missing, await _sut.GetAccountStatus(UserId));

        Assert.Null(_health.Failure);
    }

    [Fact]
    public async Task A_graph_outage_is_not_a_credential_failure()
    {
        _graph.Respond = _ => GraphError(HttpStatusCode.ServiceUnavailable, "serviceNotAvailable");

        Assert.Empty(await _sut.GetGroupMembers(GroupId));

        Assert.Null(_health.Failure);
        Assert.Equal(1, _log.Count(LogLevel.Warning));
        Assert.Equal(0, _log.Count(LogLevel.Error));
    }

    [Fact]
    public async Task Approval_checks_answer_not_a_member_while_the_credential_fails()
    {
        _credential.FailWith = ExpiredSecret;

        var group = new GroupRef(GroupId, "Release Approvers");
        Assert.False(await AuthorizerFor("member-id", "member@example.com").IsInApproverGroupAsync(group, default));

        Assert.Equal("AADSTS7000222", _health.Failure?.Code);
    }

    // ── Fakes ────────────────────────────────────────────────────────────────

    // A page of transitiveMembers/microsoft.graph.user: users only, and — like Graph's cast
    // responses — without a per-item @odata.type.
    private static HttpResponseMessage MembersPage(string? nextLink, params (string Id, string Mail)[] users)
    {
        var value = users
            .Select(u => (object)new Dictionary<string, object?>
            {
                ["id"] = u.Id,
                ["displayName"] = u.Id,
                ["mail"] = u.Mail,
                ["userPrincipalName"] = u.Mail,
            })
            .ToArray();

        var body = new Dictionary<string, object?> { ["value"] = value };
        if (nextLink is not null) body["@odata.nextLink"] = nextLink;
        return Json(body);
    }

    private static HttpResponseMessage GraphError(HttpStatusCode status, string code)
        => Json(new { error = new { code, message = "Simulated Graph failure" } }, status);

    private static HttpResponseMessage Json(object body, HttpStatusCode status = HttpStatusCode.OK)
        => new(status)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };

    private sealed class FakeGraphHandler : HttpMessageHandler
    {
        private readonly ConcurrentQueue<string> _requests = new();
        private readonly ConcurrentQueue<string?> _consistencyLevels = new();

        public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; }
            = _ => throw new InvalidOperationException("No Graph response configured");

        public Func<HttpRequestMessage, Task<HttpResponseMessage>>? RespondAsync { get; set; }

        /// <summary>Decoded path and query of every request, in order.</summary>
        public IReadOnlyCollection<string> Requests => _requests;
        public int RequestCount => _requests.Count;

        /// <summary>The <c>ConsistencyLevel</c> header of every request, in order (null when absent).</summary>
        public IReadOnlyCollection<string?> ConsistencyLevels => _consistencyLevels;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _requests.Enqueue(Uri.UnescapeDataString(request.RequestUri!.PathAndQuery));
            _consistencyLevels.Enqueue(request.Headers.TryGetValues("ConsistencyLevel", out var values)
                ? string.Join(",", values)
                : null);
            return RespondAsync is not null ? await RespondAsync(request) : Respond(request);
        }
    }

    // The Graph app's client-secret credential: hands out a token, or fails the way Azure.Identity does.
    private sealed class FakeCredential : TokenCredential
    {
        public string? FailWith { get; set; }

        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
            => FailWith is null
                ? new AccessToken("test-token", DateTimeOffset.UtcNow.AddHours(1))
                : throw new AuthenticationFailedException(FailWith);

        public override ValueTask<AccessToken> GetTokenAsync(
            TokenRequestContext requestContext, CancellationToken cancellationToken)
            => new(GetToken(requestContext, cancellationToken));
    }

    // One clock for the memory cache (ISystemClock) and the credential health (TimeProvider).
    private sealed class FakeClock : TimeProvider, ISystemClock
    {
        public DateTimeOffset UtcNow { get; private set; } = new(2026, 10, 7, 9, 0, 0, TimeSpan.Zero);
        public void Advance(TimeSpan by) => UtcNow += by;
        public override DateTimeOffset GetUtcNow() => UtcNow;
    }

    private sealed class ListLogger<T> : ILogger<T>
    {
        private readonly ConcurrentQueue<(LogLevel Level, string Message)> _entries = new();
        public IReadOnlyCollection<(LogLevel Level, string Message)> Entries => _entries;
        public int Count(LogLevel level) => _entries.Count(e => e.Level == level);

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel)) _entries.Enqueue((logLevel, formatter(state, exception)));
        }
    }
}
