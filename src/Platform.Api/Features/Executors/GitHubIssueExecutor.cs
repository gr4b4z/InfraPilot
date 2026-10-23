using System.Text.Json;
using System.Text.RegularExpressions;
using Platform.Api.Features.Catalog.Models;
using Platform.Api.Features.Requests.Models;
using Platform.Api.Infrastructure.GitHub;

namespace Platform.Api.Features.Executors;

/// <summary>
/// Files a request as a GitHub issue shaped like an issue-form submission, so existing
/// issue-driven automation (a workflow on <c>issues: opened</c> filtered by label) picks it up
/// without knowing the portal exists. The request then tracks the issue: closed as completed
/// finishes it, closed as not planned or a failure comment from the automation fails it.
/// </summary>
public class GitHubIssueExecutor : IExecutor
{
    public string Type => "github-issue";

    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromHours(168);

    private const int ErrorExcerptLength = 500;

    private static readonly Regex Placeholder = new(@"\{\{(\w+)\}\}", RegexOptions.Compiled);

    private readonly GitHubClient _github;
    private readonly ILogger<GitHubIssueExecutor> _logger;

    public GitHubIssueExecutor(GitHubClient github, ILogger<GitHubIssueExecutor> logger)
    {
        _github = github;
        _logger = logger;
    }

    public Task<bool> AlreadyExecuted(ServiceRequest request, CancellationToken ct)
        => Task.FromResult(!string.IsNullOrEmpty(request.ExternalTicketKey));

    public async Task<ExecutionResult> Execute(ServiceRequest request, CancellationToken ct)
    {
        var executor = request.CatalogItem?.Executor;
        if (executor is null)
            return FailedResult(request.Id, "No executor configuration found on catalog item");

        if (!GitHubClient.IsValidRepository(executor.Repository))
            return FailedResult(request.Id, "No target repository configured on catalog item (expected executor.repository: owner/repo)");

        if (executor.Fields.Count == 0 && string.IsNullOrWhiteSpace(executor.Body))
            return FailedResult(request.Id, "No issue content configured on catalog item (set executor.fields or executor.body)");

        var repository = executor.Repository!;
        var inputs = DeserializeInputs(request.InputsJson);
        var catalogInputs = request.CatalogItem?.Inputs ?? [];

        var title = ResolveTemplate(executor.Title ?? request.CatalogItem?.Name ?? "Request", inputs, catalogInputs, request).Trim();
        if (title.Length == 0)
            return FailedResult(request.Id, "Issue title resolved to an empty string");

        var body = executor.Fields.Count > 0
            ? IssueFormBodyRenderer.Render(executor.Fields.Select(f =>
                (f.Label, (string?)ResolveTemplate(f.Value, inputs, catalogInputs, request))))
            : ResolveTemplate(executor.Body!, inputs, catalogInputs, request);

        try
        {
            var issue = await _github.CreateIssue(executor.Connection, repository, title, body, executor.Labels, ct);

            // external_ticket_key is 100 chars wide; owner/repo can in theory exceed that, so fall back to the bare number
            var key = $"{repository}#{issue.Number}";
            request.ExternalTicketKey = key.Length <= 100 ? key : $"#{issue.Number}";
            request.ExternalTicketUrl = issue.HtmlUrl;

            _logger.LogInformation(
                "GitHub issue created: {Repository}#{Number} for request {RequestId}",
                repository, issue.Number, request.Id);

            if (!string.IsNullOrWhiteSpace(executor.Comment))
                await TryComment(executor.Connection, repository, issue.Number,
                    ResolveTemplate(executor.Comment, inputs, catalogInputs, request), request.Id, ct);

            var output = new IssueTracking
            {
                IssueNumber = issue.Number,
                IssueUrl = issue.HtmlUrl,
                Repository = repository,
                Connection = executor.Connection,
                Labels = executor.Labels,
            };

            return new ExecutionResult
            {
                Id = Guid.NewGuid(),
                ServiceRequestId = request.Id,
                Status = "InProgress",
                OutputJson = JsonSerializer.Serialize(output, JsonOpts),
                StartedAt = DateTimeOffset.UtcNow,
                CompletedAt = null,
            };
        }
        catch (GitHubApiException ex)
        {
            _logger.LogError(ex, "Failed to create GitHub issue for request {RequestId}", request.Id);
            return FailedResult(request.Id, $"GitHub API error: {ex.Message}");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "HTTP error creating GitHub issue for request {RequestId}", request.Id);
            return FailedResult(request.Id, $"Connection error: {ex.Message}");
        }
    }

    public async Task<ExecutionResult?> CheckProgress(ExecutionResult current, CancellationToken ct)
    {
        IssueTracking? tracking = null;
        try
        {
            tracking = JsonSerializer.Deserialize<IssueTracking>(current.OutputJson ?? "{}", JsonOpts);
        }
        catch (JsonException)
        {
            // handled below as missing tracking data
        }

        if (tracking is null || tracking.IssueNumber <= 0 || !GitHubClient.IsValidRepository(tracking.Repository))
        {
            _logger.LogWarning("No issue tracking data in OutputJson for ExecutionResult {Id}", current.Id);
            return Complete(current, "Failed", "Missing issue tracking data", tracking);
        }

        var completion = current.ServiceRequest?.CatalogItem?.Executor?.Completion ?? new IssueCompletionConfig();
        var timeout = completion.TimeoutHours is > 0 ? TimeSpan.FromHours(completion.TimeoutHours.Value) : DefaultTimeout;

        if (DateTimeOffset.UtcNow - current.StartedAt > timeout)
        {
            _logger.LogWarning(
                "Issue {Repository}#{Number} for ExecutionResult {Id} still open after {Hours}h, marking as failed",
                tracking.Repository, tracking.IssueNumber, current.Id, timeout.TotalHours);
            return Complete(current, "Failed",
                $"Issue {tracking.Repository}#{tracking.IssueNumber} was not closed within {timeout.TotalHours:0} hours. See {tracking.IssueUrl}.",
                tracking);
        }

        try
        {
            var issue = await _github.GetIssue(tracking.Connection, tracking.Repository!, tracking.IssueNumber, ct);

            if (issue.IsClosed)
            {
                tracking.State = issue.State;
                tracking.StateReason = issue.StateReason;
                tracking.ClosedAt = issue.ClosedAt;

                var notPlanned = issue.StateReason is "not_planned" or "duplicate";
                var status = NormalizeStatus(notPlanned ? completion.ClosedNotPlanned : completion.ClosedCompleted);
                var error = status == "Failed"
                    ? $"Issue {tracking.Repository}#{tracking.IssueNumber} was closed as {issue.StateReason ?? "closed"}. See {tracking.IssueUrl}."
                    : null;

                _logger.LogInformation(
                    "Issue {Repository}#{Number} closed ({Reason}) for ExecutionResult {Id} → {Status}",
                    tracking.Repository, tracking.IssueNumber, issue.StateReason ?? "no reason", current.Id, status);

                return Complete(current, status, error, tracking, issue.ClosedAt);
            }

            if (!string.IsNullOrWhiteSpace(completion.FailureCommentMarker))
            {
                var comments = await _github.ListIssueComments(
                    tracking.Connection, tracking.Repository!, tracking.IssueNumber, current.StartedAt, ct);

                var failure = comments.FirstOrDefault(c =>
                    c.Body.Contains(completion.FailureCommentMarker, StringComparison.OrdinalIgnoreCase));

                if (failure is not null)
                {
                    tracking.FailureCommentUrl = failure.HtmlUrl;

                    _logger.LogWarning(
                        "Failure comment on issue {Repository}#{Number} for ExecutionResult {Id}",
                        tracking.Repository, tracking.IssueNumber, current.Id);

                    return Complete(current, "Failed", Excerpt(failure.Body) + $"\n\nSee {failure.HtmlUrl}", tracking, failure.CreatedAt);
                }
            }

            _logger.LogDebug("Issue {Repository}#{Number} still open", tracking.Repository, tracking.IssueNumber);
            return null;
        }
        catch (GitHubApiException ex)
        {
            // Transient API trouble is not a failed request — skip this poll cycle
            _logger.LogError(ex, "Error checking issue {Repository}#{Number}", tracking.Repository, tracking.IssueNumber);
            return null;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "HTTP error checking issue {Repository}#{Number}", tracking.Repository, tracking.IssueNumber);
            return null;
        }
    }

    private async Task TryComment(string? connection, string repository, int number, string body, Guid requestId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(body))
            return;

        try
        {
            await _github.AddIssueComment(connection, repository, number, body, ct);
        }
        catch (Exception ex) when (ex is GitHubApiException or HttpRequestException)
        {
            // The issue exists and the workflow is already running; a missing note is not worth failing the request.
            _logger.LogWarning(ex, "Failed to comment on {Repository}#{Number} for request {RequestId}", repository, number, requestId);
        }
    }

    /// <summary>
    /// Fills {{placeholders}} from request inputs, rendering each as an issue form would show it
    /// (option labels, not ids). Inputs that are absent (hidden by visible_when, never filled)
    /// resolve to an empty string so the section renders as <c>_No response_</c>.
    /// </summary>
    internal static string ResolveTemplate(
        string template,
        Dictionary<string, string> inputs,
        List<CatalogInput> catalogInputs,
        ServiceRequest request)
    {
        return Placeholder.Replace(template, match =>
        {
            var name = match.Groups[1].Value;
            switch (name)
            {
                case "requester_name": return request.RequesterName;
                case "requester_email": return request.RequesterEmail;
                case "request_id": return request.Id.ToString();
            }

            var input = catalogInputs.FirstOrDefault(i => i.Id == name);
            return IssueFormBodyRenderer.DisplayValue(input, inputs.GetValueOrDefault(name));
        });
    }

    private static string NormalizeStatus(string? configured)
        => string.Equals(configured, "Failed", StringComparison.OrdinalIgnoreCase) ? "Failed" : "Completed";

    private static ExecutionResult Complete(
        ExecutionResult current, string status, string? error, IssueTracking? tracking, DateTimeOffset? completedAt = null)
    {
        current.Status = status;
        current.ErrorMessage = error;
        current.CompletedAt = completedAt ?? DateTimeOffset.UtcNow;
        if (tracking is not null)
            current.OutputJson = JsonSerializer.Serialize(tracking, JsonOpts);
        return current;
    }

    private static string Excerpt(string text)
    {
        var trimmed = text.Trim();
        return trimmed.Length <= ErrorExcerptLength ? trimmed : trimmed[..(ErrorExcerptLength - 3)] + "...";
    }

    private static Dictionary<string, string> DeserializeInputs(string? inputsJson)
    {
        if (string.IsNullOrEmpty(inputsJson))
            return [];

        try
        {
            using var doc = JsonDocument.Parse(inputsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return [];

            return doc.RootElement.EnumerateObject()
                .ToDictionary(
                    p => p.Name,
                    p => p.Value.ValueKind switch
                    {
                        JsonValueKind.String => p.Value.GetString() ?? "",
                        JsonValueKind.True => "true",
                        JsonValueKind.False => "false",
                        JsonValueKind.Null => "",
                        _ => p.Value.GetRawText(),
                    });
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static ExecutionResult FailedResult(Guid requestId, string error) => new()
    {
        Id = Guid.NewGuid(),
        ServiceRequestId = requestId,
        Status = "Failed",
        ErrorMessage = error,
        CompletedAt = DateTimeOffset.UtcNow,
    };

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>What CheckProgress needs, persisted in OutputJson so a poll is self-contained.</summary>
    public sealed class IssueTracking
    {
        public int IssueNumber { get; set; }
        public string? IssueUrl { get; set; }
        public string? Repository { get; set; }
        public string? Connection { get; set; }
        public List<string>? Labels { get; set; }
        public string? State { get; set; }
        public string? StateReason { get; set; }
        public DateTimeOffset? ClosedAt { get; set; }
        public string? FailureCommentUrl { get; set; }
    }
}
