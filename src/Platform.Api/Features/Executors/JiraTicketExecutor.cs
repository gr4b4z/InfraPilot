using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Platform.Api.Features.Requests.Models;
using Platform.Api.Infrastructure.Jira;

namespace Platform.Api.Features.Executors;

public class JiraTicketExecutor : IExecutor
{
    public string Type => "jira-ticket";

    private readonly JiraClient _jira;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<JiraTicketExecutor> _logger;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public JiraTicketExecutor(JiraClient jira, IHttpClientFactory httpClientFactory, IConfiguration configuration, ILogger<JiraTicketExecutor> logger)
    {
        _jira = jira;
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _logger = logger;
    }

    public Task<bool> AlreadyExecuted(ServiceRequest request, CancellationToken ct)
        => Task.FromResult(!string.IsNullOrEmpty(request.ExternalTicketKey));

    public async Task<ExecutionResult> Execute(ServiceRequest request, CancellationToken ct)
    {
        var executor = request.CatalogItem?.Executor;
        if (executor is null)
            return FailedResult(request.Id, "No executor configuration found on catalog item");

        // Resolve template variables from request inputs
        var inputs = DeserializeInputs(request.InputsJson);

        // Inject requester_name so it can be used in {{requester_name}} templates
        inputs["requester_name"] = request.RequesterName;
        inputs["requester_email"] = request.RequesterEmail;

        // Generate AI summary from description if no explicit summary provided
        if (!inputs.ContainsKey("summary") || string.IsNullOrWhiteSpace(inputs["summary"]))
        {
            var description = inputs.GetValueOrDefault("description", "");
            if (!string.IsNullOrWhiteSpace(description))
            {
                var aiSummary = await GenerateSummary(description, ct);
                inputs["summary"] = $"[Portal] {aiSummary} — {request.RequesterName}";
            }
            else
            {
                inputs["summary"] = $"[Portal] General Request — {request.RequesterName}";
            }
        }

        var resolvedParams = ResolveParameters(executor.ParametersMap, inputs);

        var connection = executor.Connection;

        // Resolve reporter from requester email
        if (!string.IsNullOrEmpty(request.RequesterEmail))
        {
            var accountId = await _jira.FindAccountIdByEmail(connection, request.RequesterEmail, ct);
            if (accountId is not null)
                resolvedParams["reporter_id"] = accountId;
        }

        try
        {
            var fields = JiraClient.BuildFields(resolvedParams);
            var issue = await _jira.CreateIssue(connection, fields, ct);

            var conn = _jira.ResolveConnection(connection);
            request.ExternalTicketKey = issue.Key;
            request.ExternalTicketUrl = $"{conn.BaseUrl.TrimEnd('/')}/browse/{issue.Key}";

            var output = new
            {
                ticketKey = issue.Key,
                ticketUrl = request.ExternalTicketUrl,
                ticketId = issue.Id,
                connection,
            };

            _logger.LogInformation(
                "Jira ticket created: {Key} for request {RequestId}",
                issue.Key, request.Id);

            return new ExecutionResult
            {
                Id = Guid.NewGuid(),
                ServiceRequestId = request.Id,
                Status = "Completed",
                OutputJson = JsonSerializer.Serialize(output),
                CompletedAt = DateTimeOffset.UtcNow,
            };
        }
        catch (JiraApiException ex)
        {
            _logger.LogError(ex, "Failed to create Jira ticket for request {RequestId}", request.Id);
            return FailedResult(request.Id, $"Jira API error: {ex.Message}");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "HTTP error creating Jira ticket for request {RequestId}", request.Id);
            return FailedResult(request.Id, $"Connection error: {ex.Message}");
        }
    }

    private static Dictionary<string, string> DeserializeInputs(string? inputsJson)
    {
        if (string.IsNullOrEmpty(inputsJson))
            return [];

        try
        {
            using var doc = JsonDocument.Parse(inputsJson);
            return doc.RootElement.EnumerateObject()
                .ToDictionary(
                    p => p.Name,
                    p => p.Value.ValueKind switch
                    {
                        JsonValueKind.String => p.Value.GetString() ?? "",
                        JsonValueKind.True => "true",
                        JsonValueKind.False => "false",
                        JsonValueKind.Number => p.Value.GetRawText(),
                        _ => p.Value.GetRawText(),
                    });
        }
        catch
        {
            return [];
        }
    }

    private static Dictionary<string, string> ResolveParameters(
        Dictionary<string, string> parametersMap,
        Dictionary<string, string> inputs)
    {
        var resolved = new Dictionary<string, string>();

        foreach (var (key, template) in parametersMap)
        {
            var value = Regex.Replace(template, @"\{\{(\w+)\}\}", match =>
            {
                var fieldName = match.Groups[1].Value;
                return inputs.GetValueOrDefault(fieldName, match.Value);
            });
            resolved[key] = value;
        }

        return resolved;
    }

    /// <summary>
    /// Use Azure OpenAI to generate a concise one-line summary from the user's description.
    /// Falls back to truncated description if AI is unavailable.
    /// </summary>
    private async Task<string> GenerateSummary(string description, CancellationToken ct)
    {
        var endpoint = _configuration["AzureOpenAI:Endpoint"];
        var apiKey = _configuration["AzureOpenAI:ApiKey"];
        var deploymentName = _configuration["AzureOpenAI:DeploymentName"];

        if (string.IsNullOrEmpty(endpoint) || string.IsNullOrEmpty(apiKey) || string.IsNullOrEmpty(deploymentName))
        {
            _logger.LogDebug("Azure OpenAI not configured, falling back to truncated description");
            return Truncate(description, 80);
        }

        try
        {
            var url = $"{endpoint.TrimEnd('/')}/openai/deployments/{deploymentName}/chat/completions?api-version=2024-10-21";

            var body = new
            {
                messages = new[]
                {
                    new { role = "system", content = "Generate a concise one-line summary (max 80 characters) for a Jira ticket title. Only output the summary, nothing else." },
                    new { role = "user", content = description },
                },
                temperature = 0.2,
                max_tokens = 60,
            };

            var json = JsonSerializer.Serialize(body, JsonOpts);
            using var httpClient = _httpClientFactory.CreateClient();
            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
            request.Headers.Add("api-key", apiKey);

            using var response = await httpClient.SendAsync(request, ct);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Azure OpenAI returned {StatusCode} for summary generation, using fallback", (int)response.StatusCode);
                return Truncate(description, 80);
            }

            var responseBody = await response.Content.ReadAsStringAsync(ct);
            var doc = JsonDocument.Parse(responseBody);
            var summary = doc.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString()?.Trim() ?? "";

            return string.IsNullOrEmpty(summary) ? Truncate(description, 80) : summary;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to generate AI summary, using fallback");
            return Truncate(description, 80);
        }
    }

    private static string Truncate(string text, int maxLength)
    {
        if (text.Length <= maxLength) return text;
        return text[..(maxLength - 3)] + "...";
    }

    private static ExecutionResult FailedResult(Guid requestId, string error) => new()
    {
        Id = Guid.NewGuid(),
        ServiceRequestId = requestId,
        Status = "Failed",
        ErrorMessage = error,
        CompletedAt = DateTimeOffset.UtcNow,
    };
}
