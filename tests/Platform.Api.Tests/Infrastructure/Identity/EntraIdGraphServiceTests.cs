using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Internal;
using Microsoft.Extensions.Logging;
using Microsoft.Graph;
using Microsoft.Kiota.Abstractions.Authentication;
using NSubstitute;
using Platform.Api.Features.Promotions;
using Platform.Api.Features.Promotions.Models;
using Platform.Api.Infrastructure.Auth;
using Platform.Api.Infrastructure.Identity;

namespace Platform.Api.Tests.Infrastructure.Identity;

/// <summary>
/// Group-membership reads in <see cref="EntraIdGraphService"/>: the process-wide cache, failure
/// caching, object-id-only lookups, transitive user membership, and paging. A real
/// <see cref="GraphServiceClient"/> runs over a fake HTTP handler that serves canned Graph responses
/// and counts requests — nothing leaves the process.
/// </summary>
public class EntraIdGraphServiceTests
{
    private const string GroupId = "5a7c3b0e-8f1d-4c2a-9b6e-1d2f3a4b5c6d";

    private readonly FakeGraphHandler _graph = new();
    private readonly FakeClock _clock = new();
    private readonly ListLogger<EntraIdGraphService> _log = new();
    private readonly EntraIdGraphService _sut;

    public EntraIdGraphServiceTests()
    {
        var client = new GraphServiceClient(new HttpClient(_graph), new AnonymousAuthenticationProvider());
        var cache = new MemoryCache(new MemoryCacheOptions { Clock = _clock });
        _sut = new EntraIdGraphService(client, cache, _log);
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

    private sealed class FakeClock : ISystemClock
    {
        public DateTimeOffset UtcNow { get; private set; } = new(2026, 10, 7, 9, 0, 0, TimeSpan.Zero);
        public void Advance(TimeSpan by) => UtcNow += by;
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
