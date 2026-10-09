using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Platform.Api.Features.Catalog.Models;
using Platform.Api.Features.Executors;
using Platform.Api.Features.Requests.Models;
using Platform.Api.Infrastructure.GitHub;

namespace Platform.Api.Tests.Features.Executors;

public class GitHubIssueExecutorTests
{
    private readonly FakeGitHubHandler _handler = new();
    private readonly GitHubIssueExecutor _sut;

    public GitHubIssueExecutorTests()
    {
        var options = Options.Create(new GitHubOptions
        {
            Connections = { ["default"] = new GitHubConnection { Token = "ghp_test" } },
        });
        var client = new GitHubClient(new HttpClient(_handler), options, Substitute.For<ILogger<GitHubClient>>());
        _sut = new GitHubIssueExecutor(client, Substitute.For<ILogger<GitHubIssueExecutor>>());
    }

    // ── Body rendering ─────────────────────────────────────────────

    [Fact]
    public void Render_matches_issue_form_submission_shape()
    {
        var body = IssueFormBodyRenderer.Render(
        [
            ("Repository name", "mpt-billing-service"),
            ("Product (if applicable)", ""),
            ("Business justification", "Line one\nLine two"),
        ]);

        Assert.Equal(
            "### Repository name\n\nmpt-billing-service\n\n" +
            "### Product (if applicable)\n\n_No response_\n\n" +
            "### Business justification\n\nLine one\nLine two",
            body);
    }

    [Fact]
    public void Render_treats_whitespace_only_values_as_no_response()
    {
        var body = IssueFormBodyRenderer.Render([("Jira key", "   ")]);
        Assert.Equal("### Jira key\n\n_No response_", body);
    }

    [Fact]
    public void DisplayValue_uses_option_label_for_select_inputs()
    {
        var input = new CatalogInput
        {
            Id = "infraportal",
            Component = "Select",
            Options =
            [
                new CatalogInputOption { Id = "yes", Label = "yes — publish registers the build in InfraPortal" },
                new CatalogInputOption { Id = "no", Label = "no — built, never deployed" },
            ],
        };

        Assert.Equal("yes — publish registers the build in InfraPortal", IssueFormBodyRenderer.DisplayValue(input, "yes"));
        Assert.Equal("unknown", IssueFormBodyRenderer.DisplayValue(input, "unknown"));
        Assert.Equal("", IssueFormBodyRenderer.DisplayValue(input, null));
    }

    [Fact]
    public void DisplayValue_joins_multi_select_labels()
    {
        var input = new CatalogInput
        {
            Id = "teams",
            Options =
            [
                new CatalogInputOption { Id = "a", Label = "Team A" },
                new CatalogInputOption { Id = "b", Label = "Team B" },
            ],
        };

        Assert.Equal("Team A, Team B", IssueFormBodyRenderer.DisplayValue(input, "[\"a\",\"b\"]"));
    }

    // ── Execute ────────────────────────────────────────────────────

    [Fact]
    public async Task Execute_creates_issue_with_form_body_label_and_tracking()
    {
        _handler.Respond = req => req.Method == HttpMethod.Post && req.RequestUri!.AbsolutePath.EndsWith("/issues")
            ? Json(HttpStatusCode.Created, """{"number": 42, "html_url": "https://github.com/acme/.github/issues/42", "state": "open"}""")
            : Json(HttpStatusCode.Created, """{"id": 1, "html_url": "https://github.com/acme/.github/issues/42#issuecomment-1", "body": "x", "created_at": "2026-10-09T10:00:00Z"}""");

        var request = NewRequest(inputs: new
        {
            name = "mpt-billing-service",
            visibility = "Internal",
            infraportal = "yes",
            justification = "Billing needs its own service.",
        });

        var result = await _sut.Execute(request, CancellationToken.None);

        Assert.Equal("InProgress", result.Status);
        Assert.Null(result.CompletedAt);
        Assert.Equal("acme/.github#42", request.ExternalTicketKey);
        Assert.Equal("https://github.com/acme/.github/issues/42", request.ExternalTicketUrl);

        var create = _handler.Requests.First(r => r.Path == "/repos/acme/.github/issues");
        Assert.Equal("Bearer ghp_test", create.Authorization);
        Assert.Equal("InfraPilot", create.UserAgent);

        using var doc = JsonDocument.Parse(create.Body!);
        Assert.Equal("[Repo Request] mpt-billing-service", doc.RootElement.GetProperty("title").GetString());
        Assert.Equal(["repository-request"], doc.RootElement.GetProperty("labels").EnumerateArray().Select(l => l.GetString()!).ToArray());
        Assert.Equal(
            "### Repository name\n\nmpt-billing-service\n\n" +
            "### Product (if applicable)\n\n_No response_\n\n" +
            "### Visibility\n\nInternal\n\n" +
            "### Register builds in InfraPortal?\n\nyes — publish registers the build in InfraPortal\n\n" +
            "### Business justification\n\nBilling needs its own service.",
            doc.RootElement.GetProperty("body").GetString());

        var comment = _handler.Requests.Single(r => r.Path == "/repos/acme/.github/issues/42/comments");
        using var commentDoc = JsonDocument.Parse(comment.Body!);
        Assert.Equal(
            $"Requested via InfraPortal by Ada Lovelace (ada@example.com). Portal request `{request.Id}`.",
            commentDoc.RootElement.GetProperty("body").GetString());

        var tracking = JsonSerializer.Deserialize<GitHubIssueExecutor.IssueTracking>(result.OutputJson!, CamelCase)!;
        Assert.Equal(42, tracking.IssueNumber);
        Assert.Equal("acme/.github", tracking.Repository);
        Assert.Equal("default", tracking.Connection);
    }

    [Fact]
    public async Task Execute_without_comment_template_posts_no_comment()
    {
        _handler.Respond = _ => Json(HttpStatusCode.Created, """{"number": 7, "html_url": "https://github.com/acme/.github/issues/7", "state": "open"}""");

        var request = NewRequest(inputs: new { name = "x" }, configure: e => e.Comment = null);
        var result = await _sut.Execute(request, CancellationToken.None);

        Assert.Equal("InProgress", result.Status);
        Assert.Single(_handler.Requests);
    }

    [Fact]
    public async Task Execute_fails_cleanly_on_github_error()
    {
        _handler.Respond = _ => Json(HttpStatusCode.UnprocessableEntity, """{"message": "Validation Failed"}""");

        var request = NewRequest(inputs: new { name = "x" });
        var result = await _sut.Execute(request, CancellationToken.None);

        Assert.Equal("Failed", result.Status);
        Assert.Contains("HTTP 422", result.ErrorMessage);
        Assert.Null(request.ExternalTicketKey);
    }

    [Fact]
    public async Task Execute_rejects_missing_repository_before_calling_github()
    {
        var request = NewRequest(inputs: new { name = "x" }, configure: e => e.Repository = "not a repo");
        var result = await _sut.Execute(request, CancellationToken.None);

        Assert.Equal("Failed", result.Status);
        Assert.Contains("repository", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public async Task Execute_uses_free_form_body_when_no_fields_configured()
    {
        _handler.Respond = _ => Json(HttpStatusCode.Created, """{"number": 1, "html_url": "u", "state": "open"}""");

        var request = NewRequest(inputs: new { name = "x" }, configure: e =>
        {
            e.Fields = [];
            e.Body = "Please create {{name}} for {{requester_name}}";
            e.Comment = null;
        });
        await _sut.Execute(request, CancellationToken.None);

        using var doc = JsonDocument.Parse(_handler.Requests.Single().Body!);
        Assert.Equal("Please create x for Ada Lovelace", doc.RootElement.GetProperty("body").GetString());
    }

    [Fact]
    public async Task AlreadyExecuted_when_request_has_external_ticket()
    {
        var request = NewRequest(inputs: new { });
        Assert.False(await _sut.AlreadyExecuted(request, CancellationToken.None));

        request.ExternalTicketKey = "acme/.github#1";
        Assert.True(await _sut.AlreadyExecuted(request, CancellationToken.None));
    }

    // ── CheckProgress ──────────────────────────────────────────────

    [Fact]
    public async Task CheckProgress_returns_null_while_issue_open_and_no_failure_comment()
    {
        _handler.Respond = req => req.RequestUri!.AbsolutePath.EndsWith("/comments")
            ? Json(HttpStatusCode.OK, """[{"id": 1, "html_url": "c1", "body": "Thanks, PR opened: #99", "created_at": "2026-10-09T10:00:00Z"}]""")
            : Json(HttpStatusCode.OK, """{"number": 42, "html_url": "u", "state": "open"}""");

        var result = await _sut.CheckProgress(InProgress(), CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task CheckProgress_completes_when_issue_closed_as_completed()
    {
        _handler.Respond = _ => Json(HttpStatusCode.OK,
            """{"number": 42, "html_url": "u", "state": "closed", "state_reason": "completed", "closed_at": "2026-10-10T08:00:00Z"}""");

        var current = InProgress();
        var result = await _sut.CheckProgress(current, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("Completed", result.Status);
        Assert.Null(result.ErrorMessage);
        Assert.Equal(DateTimeOffset.Parse("2026-10-10T08:00:00Z"), result.CompletedAt);

        var tracking = JsonSerializer.Deserialize<GitHubIssueExecutor.IssueTracking>(result.OutputJson!, CamelCase)!;
        Assert.Equal("completed", tracking.StateReason);
    }

    [Fact]
    public async Task CheckProgress_fails_when_issue_closed_as_not_planned()
    {
        _handler.Respond = _ => Json(HttpStatusCode.OK,
            """{"number": 42, "html_url": "https://github.com/acme/.github/issues/42", "state": "closed", "state_reason": "not_planned"}""");

        var result = await _sut.CheckProgress(InProgress(), CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("Failed", result.Status);
        Assert.Contains("not_planned", result.ErrorMessage);
    }

    [Fact]
    public async Task CheckProgress_fails_on_failure_comment_with_comment_as_error()
    {
        _handler.Respond = req => req.RequestUri!.AbsolutePath.EndsWith("/comments")
            ? Json(HttpStatusCode.OK, """
                [
                  {"id": 1, "html_url": "c1", "body": "Looking into it", "created_at": "2026-10-09T10:00:00Z"},
                  {"id": 2, "html_url": "https://github.com/acme/.github/issues/42#issuecomment-2",
                   "body": "❌ Validation FAILED: owning team `mpt-billing-owners` does not exist.", "created_at": "2026-10-09T10:05:00Z"}
                ]
                """)
            : Json(HttpStatusCode.OK, """{"number": 42, "html_url": "u", "state": "open"}""");

        var result = await _sut.CheckProgress(InProgress(), CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("Failed", result.Status);
        Assert.StartsWith("❌ Validation FAILED: owning team", result.ErrorMessage);
        Assert.Contains("issuecomment-2", result.ErrorMessage);

        var comments = _handler.Requests.Single(r => r.Path.EndsWith("/comments"));
        Assert.Contains("since=", comments.Query);
    }

    [Fact]
    public async Task CheckProgress_does_not_fetch_comments_without_marker()
    {
        _handler.Respond = _ => Json(HttpStatusCode.OK, """{"number": 42, "html_url": "u", "state": "open"}""");

        var current = InProgress(configure: e => e.Completion = new IssueCompletionConfig());
        var result = await _sut.CheckProgress(current, CancellationToken.None);

        Assert.Null(result);
        Assert.Single(_handler.Requests);
    }

    [Fact]
    public async Task CheckProgress_times_out_using_configured_hours()
    {
        var current = InProgress(configure: e => e.Completion = new IssueCompletionConfig { TimeoutHours = 24 });
        current.StartedAt = DateTimeOffset.UtcNow.AddHours(-25);

        var result = await _sut.CheckProgress(current, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("Failed", result.Status);
        Assert.Contains("24 hours", result.ErrorMessage);
        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public async Task CheckProgress_skips_cycle_on_transient_api_error()
    {
        _handler.Respond = _ => Json(HttpStatusCode.BadGateway, "upstream");

        var result = await _sut.CheckProgress(InProgress(), CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task CheckProgress_fails_when_tracking_data_missing()
    {
        var current = InProgress();
        current.OutputJson = "{}";

        var result = await _sut.CheckProgress(current, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("Failed", result.Status);
        Assert.Empty(_handler.Requests);
    }

    // ── Helpers ────────────────────────────────────────────────────

    private static readonly JsonSerializerOptions CamelCase = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private static CatalogItem NewCatalogItem(Action<ExecutorConfig>? configure = null)
    {
        var executor = new ExecutorConfig
        {
            Type = "github-issue",
            Connection = "default",
            Repository = "acme/.github",
            Title = "[Repo Request] {{name}}",
            Labels = ["repository-request"],
            Fields =
            [
                new IssueFormField { Label = "Repository name", Value = "{{name}}" },
                new IssueFormField { Label = "Product (if applicable)", Value = "{{product}}" },
                new IssueFormField { Label = "Visibility", Value = "{{visibility}}" },
                new IssueFormField { Label = "Register builds in InfraPortal?", Value = "{{infraportal}}" },
                new IssueFormField { Label = "Business justification", Value = "{{justification}}" },
            ],
            Comment = "Requested via InfraPortal by {{requester_name}} ({{requester_email}}). Portal request `{{request_id}}`.",
            Completion = new IssueCompletionConfig { FailureCommentMarker = "validation failed" },
        };
        configure?.Invoke(executor);

        return new CatalogItem
        {
            Id = Guid.NewGuid(),
            Slug = "request-repository",
            Name = "Request Repository",
            Inputs =
            [
                new CatalogInput { Id = "name", Component = "TextInput" },
                new CatalogInput { Id = "product", Component = "ResourcePicker" },
                new CatalogInput
                {
                    Id = "visibility", Component = "Select",
                    Options = [new CatalogInputOption { Id = "Internal", Label = "Internal" }, new CatalogInputOption { Id = "Private", Label = "Private" }],
                },
                new CatalogInput
                {
                    Id = "infraportal", Component = "Select",
                    Options =
                    [
                        new CatalogInputOption { Id = "yes", Label = "yes — publish registers the build in InfraPortal" },
                        new CatalogInputOption { Id = "no", Label = "no — built, never deployed" },
                    ],
                },
                new CatalogInput { Id = "justification", Component = "TextArea" },
            ],
            Executor = executor,
        };
    }

    private static ServiceRequest NewRequest(object inputs, Action<ExecutorConfig>? configure = null)
    {
        var item = NewCatalogItem(configure);
        return new ServiceRequest
        {
            Id = Guid.NewGuid(),
            CatalogItemId = item.Id,
            CatalogItem = item,
            RequesterId = "u1",
            RequesterName = "Ada Lovelace",
            RequesterEmail = "ada@example.com",
            Status = RequestStatus.Executing,
            InputsJson = JsonSerializer.Serialize(inputs),
        };
    }

    private static ExecutionResult InProgress(Action<ExecutorConfig>? configure = null)
    {
        var request = NewRequest(new { name = "x" }, configure);
        return new ExecutionResult
        {
            Id = Guid.NewGuid(),
            ServiceRequestId = request.Id,
            ServiceRequest = request,
            Status = "InProgress",
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            OutputJson = JsonSerializer.Serialize(new GitHubIssueExecutor.IssueTracking
            {
                IssueNumber = 42,
                IssueUrl = "https://github.com/acme/.github/issues/42",
                Repository = "acme/.github",
                Connection = "default",
            }, CamelCase),
        };
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private sealed record RecordedRequest(HttpMethod Method, string Path, string Query, string? Body, string? Authorization, string? UserAgent);

    private sealed class FakeGitHubHandler : HttpMessageHandler
    {
        private readonly ConcurrentQueue<RecordedRequest> _requests = new();

        public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; }
            = _ => throw new InvalidOperationException("No GitHub response configured");

        public IReadOnlyCollection<RecordedRequest> Requests => _requests;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            _requests.Enqueue(new RecordedRequest(
                request.Method,
                request.RequestUri!.AbsolutePath,
                request.RequestUri.Query,
                body,
                request.Headers.Authorization?.ToString(),
                request.Headers.UserAgent.ToString()));
            return Respond(request);
        }
    }
}
