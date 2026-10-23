using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace Platform.Api.Infrastructure.GitHub;

/// <summary>
/// Thin REST client for the GitHub Issues API. Mirrors <see cref="Jira.JiraClient"/>: connections
/// come from configuration, every call resolves one by name, and HTTP failures surface as
/// <see cref="GitHubApiException"/> so executors can turn them into a failed execution result.
/// </summary>
public class GitHubClient
{
    /// <summary>GitHub rejects requests without a User-Agent, so this is not decoration.</summary>
    public const string UserAgent = "InfraPilot";

    private const string ApiVersion = "2022-11-28";

    private static readonly Regex RepositoryPattern = new(@"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$", RegexOptions.Compiled);

    private readonly HttpClient _http;
    private readonly GitHubOptions _options;
    private readonly ILogger<GitHubClient> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public GitHubClient(HttpClient http, IOptions<GitHubOptions> options, ILogger<GitHubClient> logger)
    {
        _http = http;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<GitHubIssue> CreateIssue(
        string? connectionName,
        string repository,
        string title,
        string body,
        IReadOnlyCollection<string>? labels,
        CancellationToken ct = default)
    {
        var conn = ResolveConnection(connectionName);
        var url = $"{IssuesUrl(conn, repository)}";

        var payload = new
        {
            title,
            body,
            labels = labels is { Count: > 0 } ? labels : null,
        };

        _logger.LogInformation("Creating GitHub issue in {Repository}", repository);

        using var request = NewRequest(HttpMethod.Post, url, conn);
        request.Content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json");

        using var response = await _http.SendAsync(request, ct);
        var responseBody = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
            throw ApiError("create issue", repository, response, responseBody);

        var issue = JsonSerializer.Deserialize<GitHubIssue>(responseBody, JsonOptions)
            ?? throw new GitHubApiException("Empty response from GitHub API");

        _logger.LogInformation("Created GitHub issue {Repository}#{Number}", repository, issue.Number);
        return issue;
    }

    public async Task<GitHubIssue> GetIssue(string? connectionName, string repository, int number, CancellationToken ct = default)
    {
        var conn = ResolveConnection(connectionName);
        var url = $"{IssuesUrl(conn, repository)}/{number}";

        using var request = NewRequest(HttpMethod.Get, url, conn);
        using var response = await _http.SendAsync(request, ct);
        var responseBody = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
            throw ApiError($"read issue #{number}", repository, response, responseBody);

        return JsonSerializer.Deserialize<GitHubIssue>(responseBody, JsonOptions)
            ?? throw new GitHubApiException("Empty response from GitHub API");
    }

    /// <summary>Comments on an issue, oldest first. <paramref name="since"/> trims to comments updated at or after it.</summary>
    public async Task<List<GitHubIssueComment>> ListIssueComments(
        string? connectionName, string repository, int number, DateTimeOffset? since, CancellationToken ct = default)
    {
        var conn = ResolveConnection(connectionName);
        var url = $"{IssuesUrl(conn, repository)}/{number}/comments?per_page=100";
        if (since.HasValue)
            url += $"&since={Uri.EscapeDataString(since.Value.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ"))}";

        using var request = NewRequest(HttpMethod.Get, url, conn);
        using var response = await _http.SendAsync(request, ct);
        var responseBody = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
            throw ApiError($"list comments on issue #{number}", repository, response, responseBody);

        return JsonSerializer.Deserialize<List<GitHubIssueComment>>(responseBody, JsonOptions) ?? [];
    }

    public async Task<GitHubIssueComment> AddIssueComment(
        string? connectionName, string repository, int number, string body, CancellationToken ct = default)
    {
        var conn = ResolveConnection(connectionName);
        var url = $"{IssuesUrl(conn, repository)}/{number}/comments";

        using var request = NewRequest(HttpMethod.Post, url, conn);
        request.Content = new StringContent(JsonSerializer.Serialize(new { body }, JsonOptions), Encoding.UTF8, "application/json");

        using var response = await _http.SendAsync(request, ct);
        var responseBody = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
            throw ApiError($"comment on issue #{number}", repository, response, responseBody);

        return JsonSerializer.Deserialize<GitHubIssueComment>(responseBody, JsonOptions)
            ?? throw new GitHubApiException("Empty response from GitHub API");
    }

    public GitHubConnection ResolveConnection(string? connectionName)
    {
        if (_options.Connections.Count == 0)
            throw new GitHubApiException("No GitHub connections configured. Add at least one connection under GitHub:Connections in appsettings.json");

        var name = connectionName ?? _options.Connections.Keys.First();

        if (_options.Connections.TryGetValue(name, out var conn))
        {
            if (string.IsNullOrWhiteSpace(conn.Token) || conn.Token.StartsWith('<'))
                throw new GitHubApiException(
                    $"GitHub connection '{name}' is not configured. Set Token in appsettings.json under GitHub:Connections:{name}");
            return conn;
        }

        throw new GitHubApiException(
            $"GitHub connection '{name}' not found in configuration. Available connections: [{string.Join(", ", _options.Connections.Keys)}]");
    }

    /// <summary>"owner/repo", nothing else: the value lands in a URL path.</summary>
    public static bool IsValidRepository(string? repository)
        => !string.IsNullOrWhiteSpace(repository) && RepositoryPattern.IsMatch(repository);

    private static string IssuesUrl(GitHubConnection conn, string repository)
    {
        if (!IsValidRepository(repository))
            throw new GitHubApiException($"Invalid repository '{repository}': expected the form owner/repo");

        return $"{conn.ApiUrl.TrimEnd('/')}/repos/{repository}/issues";
    }

    private static HttpRequestMessage NewRequest(HttpMethod method, string url, GitHubConnection conn)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", conn.Token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", ApiVersion);
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        return request;
    }

    private GitHubApiException ApiError(string action, string repository, HttpResponseMessage response, string body)
    {
        var status = (int)response.StatusCode;
        _logger.LogError("GitHub API error on {Action} in {Repository} (HTTP {StatusCode}): {Body}", action, repository, status, body);
        return new GitHubApiException($"Failed to {action} in {repository} (HTTP {status}): {Truncate(body, 500)}", status);
    }

    private static string Truncate(string text, int max)
        => text.Length <= max ? text : text[..(max - 3)] + "...";
}
