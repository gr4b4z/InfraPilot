using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Platform.Api.Infrastructure.Jira;

public class JiraClient
{
    private readonly HttpClient _http;
    private readonly JiraOptions _options;
    private readonly ILogger<JiraClient> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public JiraClient(HttpClient http, IOptions<JiraOptions> options, ILogger<JiraClient> logger)
    {
        _http = http;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<JiraIssueResponse> CreateIssue(
        string? connectionName,
        Dictionary<string, object> fields,
        CancellationToken ct = default)
    {
        var conn = ResolveConnection(connectionName);
        var url = $"{conn.BaseUrl.TrimEnd('/')}/rest/api/3/issue";

        var payload = new { fields };
        var json = JsonSerializer.Serialize(payload, JsonOptions);

        _logger.LogInformation("Creating Jira issue via {Url}", url);
        _logger.LogDebug("Jira payload: {Payload}", json);

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        SetAuthHeader(request, conn);

        using var response = await _http.SendAsync(request, ct);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(ct);
            _logger.LogError("Jira API error (HTTP {StatusCode}): {Body}", (int)response.StatusCode, errorBody);
            throw new JiraApiException(
                $"Failed to create Jira issue (HTTP {(int)response.StatusCode}): {errorBody}",
                (int)response.StatusCode);
        }

        var responseBody = await response.Content.ReadAsStringAsync(ct);
        var issue = JsonSerializer.Deserialize<JiraIssueResponse>(responseBody, JsonOptions)
            ?? throw new JiraApiException("Empty response from Jira API");

        _logger.LogInformation("Created Jira issue {Key} (id={Id})", issue.Key, issue.Id);
        return issue;
    }

    /// <summary>
    /// Look up a Jira user's accountId by email address.
    /// Returns null if not found or on error.
    /// </summary>
    public async Task<string?> FindAccountIdByEmail(string? connectionName, string email, CancellationToken ct = default)
    {
        var conn = ResolveConnection(connectionName);
        var url = $"{conn.BaseUrl.TrimEnd('/')}/rest/api/3/user/search?query={Uri.EscapeDataString(email)}";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        SetAuthHeader(request, conn);

        try
        {
            using var response = await _http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Jira user search returned {StatusCode} for {Email}", (int)response.StatusCode, email);
                return null;
            }

            var body = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(body);
            var users = doc.RootElement;

            if (users.GetArrayLength() == 0)
            {
                _logger.LogDebug("No Jira user found for email {Email}", email);
                return null;
            }

            var accountId = users[0].GetProperty("accountId").GetString();
            _logger.LogDebug("Resolved Jira user {Email} → {AccountId}", email, accountId);
            return accountId;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to look up Jira user by email {Email}", email);
            return null;
        }
    }

    /// <summary>
    /// Maps flat parameters_map keys to Jira's nested field structure.
    /// </summary>
    public static Dictionary<string, object> BuildFields(Dictionary<string, string> resolvedParams)
    {
        var fields = new Dictionary<string, object>();

        foreach (var (key, value) in resolvedParams)
        {
            if (string.IsNullOrEmpty(value)) continue;

            switch (key)
            {
                case "project_id":
                    fields["project"] = new { id = value };
                    break;
                case "project_key":
                    fields["project"] = new { key = value };
                    break;
                case "issue_type_id":
                    fields["issuetype"] = new { id = value };
                    break;
                case "issue_type_name":
                    fields["issuetype"] = new { name = value };
                    break;
                case "summary":
                    fields["summary"] = value;
                    break;
                case "description":
                    fields["description"] = BuildAdfDocument(value);
                    break;
                case "priority_id":
                    fields["priority"] = new { id = value };
                    break;
                case "priority_name":
                    fields["priority"] = new { name = value };
                    break;
                case "component_ids":
                    fields["components"] = ParseIdList(value);
                    break;
                case "fix_version_ids":
                    fields["fixVersions"] = ParseIdList(value);
                    break;
                case "priority":
                    // Map friendly names (low/medium/high) to Jira priority names
                    var priorityName = value.ToLowerInvariant() switch
                    {
                        "low" => "Low",
                        "medium" => "Medium",
                        "high" => "High",
                        "highest" => "Highest",
                        "lowest" => "Lowest",
                        "minor" => "Minor",
                        "major" => "Major",
                        "critical" => "Critical",
                        "blocker" => "Blocker",
                        _ => value // Pass through if already a valid Jira name
                    };
                    fields["priority"] = new { name = priorityName };
                    break;
                case "reporter_id":
                    fields["reporter"] = new { id = value };
                    break;
                case "labels":
                    fields["labels"] = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    break;
                default:
                    // Pass through as-is (for custom fields like customfield_10361)
                    fields[key] = value;
                    break;
            }
        }

        return fields;
    }

    public JiraConnection ResolveConnection(string? connectionName)
    {
        if (_options.Connections.Count == 0)
            throw new JiraApiException("No Jira connections configured. Add at least one connection under Jira:Connections in appsettings.json");

        var name = connectionName ?? _options.Connections.Keys.First();

        if (_options.Connections.TryGetValue(name, out var conn))
        {
            if (string.IsNullOrEmpty(conn.BaseUrl) || conn.ApiToken.StartsWith('<'))
                throw new JiraApiException(
                    $"Jira connection '{name}' is not configured. Set BaseUrl, Email, and ApiToken in appsettings.json under Jira:Connections:{name}");
            return conn;
        }

        throw new JiraApiException(
            $"Jira connection '{name}' not found in configuration. Available connections: [{string.Join(", ", _options.Connections.Keys)}]");
    }

    private static void SetAuthHeader(HttpRequestMessage request, JiraConnection conn)
    {
        var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{conn.Email}:{conn.ApiToken}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);
    }

    /// <summary>
    /// Build an Atlassian Document Format (ADF) document from plain text.
    /// Splits on newlines to create separate paragraphs.
    /// </summary>
    private static object BuildAdfDocument(string text)
    {
        var paragraphs = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        var content = paragraphs.Length > 0
            ? paragraphs.Select(p => new
            {
                type = "paragraph",
                content = new object[]
                {
                    new { type = "text", text = p }
                }
            }).ToArray<object>()
            : new object[]
            {
                new
                {
                    type = "paragraph",
                    content = new object[]
                    {
                        new { type = "text", text = text.Length > 0 ? text : "No description provided" }
                    }
                }
            };

        return new
        {
            version = 1,
            type = "doc",
            content
        };
    }

    /// <summary>
    /// Parse comma-separated IDs into Jira's array-of-objects format.
    /// "10195,10196" → [{ id: "10195" }, { id: "10196" }]
    /// </summary>
    private static object[] ParseIdList(string commaSeparated)
    {
        return commaSeparated
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(id => (object)new { id })
            .ToArray();
    }
}
