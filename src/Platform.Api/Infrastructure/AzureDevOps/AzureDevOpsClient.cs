using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Platform.Api.Infrastructure.AzureDevOps;

public class AzureDevOpsClient
{
    private readonly HttpClient _http;
    private readonly AzureDevOpsOptions _options;
    private readonly ILogger<AzureDevOpsClient> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public AzureDevOpsClient(HttpClient http, IOptions<AzureDevOpsOptions> options, ILogger<AzureDevOpsClient> logger)
    {
        _http = http;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>
    /// Queue a new build (pipeline run).
    /// </summary>
    public async Task<BuildResponse> QueueBuild(
        string? connectionName,
        string? projectOverride,
        int pipelineId,
        string? sourceBranch = null,
        Dictionary<string, string>? parameters = null,
        CancellationToken ct = default)
    {
        var conn = ResolveConnection(connectionName);
        var project = projectOverride ?? conn.Project;
        var url = $"{conn.OrganizationUrl.TrimEnd('/')}/{project}/_apis/build/builds?api-version=7.1";

        var body = new QueueBuildRequest
        {
            Definition = new BuildDefinitionRef { Id = pipelineId },
            SourceBranch = sourceBranch,
            Parameters = parameters is { Count: > 0 }
                ? JsonSerializer.Serialize(parameters)
                : null,
        };

        _logger.LogInformation(
            "Queuing build: pipeline={PipelineId}, branch={Branch}, connection={Connection}",
            pipelineId, sourceBranch ?? "(default)", connectionName ?? "default");

        var request = CreateRequest(HttpMethod.Post, url, conn.Pat);
        request.Content = new StringContent(
            JsonSerializer.Serialize(body, JsonOptions),
            Encoding.UTF8,
            "application/json");

        var response = await _http.SendAsync(request, ct);
        var responseBody = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError(
                "Azure DevOps QueueBuild failed: {StatusCode} {Body}",
                response.StatusCode, responseBody);
            throw new AzureDevOpsApiException(
                $"Failed to queue build (HTTP {(int)response.StatusCode}): {responseBody}");
        }

        var build = JsonSerializer.Deserialize<BuildResponse>(responseBody, JsonOptions)
            ?? throw new AzureDevOpsApiException("Empty response from Azure DevOps QueueBuild API");

        _logger.LogInformation(
            "Build queued: buildId={BuildId}, buildNumber={BuildNumber}",
            build.Id, build.BuildNumber);

        return build;
    }

    /// <summary>
    /// Queue a pipeline run using the Pipelines API (supports YAML template parameters).
    /// Returns the run ID which is the same as the build ID for status checks via GetBuild.
    /// </summary>
    public async Task<PipelineRunResponse> QueuePipelineRun(
        string? connectionName,
        string? projectOverride,
        int pipelineId,
        string? sourceBranch = null,
        Dictionary<string, string>? templateParameters = null,
        CancellationToken ct = default)
    {
        var conn = ResolveConnection(connectionName);
        var project = projectOverride ?? conn.Project;
        var url = $"{conn.OrganizationUrl.TrimEnd('/')}/{project}/_apis/pipelines/{pipelineId}/runs?api-version=7.1";

        var body = new PipelineRunRequest
        {
            TemplateParameters = templateParameters is { Count: > 0 } ? templateParameters : null,
            Resources = sourceBranch != null
                ? new PipelineRunResources
                {
                    Repositories = new Dictionary<string, PipelineRepositoryRef>
                    {
                        ["self"] = new() { RefName = sourceBranch }
                    }
                }
                : null,
        };

        _logger.LogInformation(
            "Queuing pipeline run: pipeline={PipelineId}, branch={Branch}, connection={Connection}",
            pipelineId, sourceBranch ?? "(default)", connectionName ?? "default");

        var request = CreateRequest(HttpMethod.Post, url, conn.Pat);
        request.Content = new StringContent(
            JsonSerializer.Serialize(body, JsonOptions),
            Encoding.UTF8,
            "application/json");

        var response = await _http.SendAsync(request, ct);
        var responseBody = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError(
                "Azure DevOps QueuePipelineRun failed: {StatusCode} {Body}",
                response.StatusCode, responseBody);
            throw new AzureDevOpsApiException(
                $"Failed to queue pipeline run (HTTP {(int)response.StatusCode}): {responseBody}");
        }

        var run = JsonSerializer.Deserialize<PipelineRunResponse>(responseBody, JsonOptions)
            ?? throw new AzureDevOpsApiException("Empty response from Azure DevOps Pipelines Run API");

        _logger.LogInformation(
            "Pipeline run queued: runId={RunId}, pipelineId={PipelineId}",
            run.Id, pipelineId);

        return run;
    }

    /// <summary>
    /// Get the current status of a build.
    /// </summary>
    public async Task<BuildResponse> GetBuild(
        string? connectionName,
        string? projectOverride,
        int buildId,
        CancellationToken ct = default)
    {
        var conn = ResolveConnection(connectionName);
        var project = projectOverride ?? conn.Project;
        var url = $"{conn.OrganizationUrl.TrimEnd('/')}/{project}/_apis/build/builds/{buildId}?api-version=7.1";

        var request = CreateRequest(HttpMethod.Get, url, conn.Pat);
        var response = await _http.SendAsync(request, ct);
        var responseBody = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError(
                "Azure DevOps GetBuild failed: {StatusCode} {Body}",
                response.StatusCode, responseBody);
            throw new AzureDevOpsApiException(
                $"Failed to get build {buildId} (HTTP {(int)response.StatusCode}): {responseBody}");
        }

        return JsonSerializer.Deserialize<BuildResponse>(responseBody, JsonOptions)
            ?? throw new AzureDevOpsApiException("Empty response from Azure DevOps GetBuild API");
    }

    /// <summary>
    /// Get the build timeline (stages/jobs/tasks breakdown).
    /// </summary>
    public async Task<BuildTimelineResponse> GetBuildTimeline(
        string? connectionName,
        string? projectOverride,
        int buildId,
        CancellationToken ct = default)
    {
        var conn = ResolveConnection(connectionName);
        var project = projectOverride ?? conn.Project;
        var url = $"{conn.OrganizationUrl.TrimEnd('/')}/{project}/_apis/build/builds/{buildId}/timeline?api-version=7.1";

        var request = CreateRequest(HttpMethod.Get, url, conn.Pat);
        var response = await _http.SendAsync(request, ct);
        var responseBody = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning(
                "Azure DevOps GetBuildTimeline failed: {StatusCode}",
                response.StatusCode);
            return new BuildTimelineResponse();
        }

        return JsonSerializer.Deserialize<BuildTimelineResponse>(responseBody, JsonOptions)
            ?? new BuildTimelineResponse();
    }

    private AzureDevOpsConnection ResolveConnection(string? connectionName)
    {
        var name = connectionName ?? "default";

        if (_options.Connections.TryGetValue(name, out var conn))
        {
            if (string.IsNullOrEmpty(conn.OrganizationUrl) || conn.Pat.StartsWith('<'))
                throw new AzureDevOpsApiException(
                    $"Azure DevOps connection '{name}' is not configured. Set OrganizationUrl and Pat in appsettings.json under AzureDevOps:Connections:{name}");
            return conn;
        }

        throw new AzureDevOpsApiException(
            $"Azure DevOps connection '{name}' not found in configuration. Available connections: [{string.Join(", ", _options.Connections.Keys)}]");
    }

    private static HttpRequestMessage CreateRequest(HttpMethod method, string url, string pat)
    {
        var request = new HttpRequestMessage(method, url);
        var credentials = Convert.ToBase64String(Encoding.ASCII.GetBytes($":{pat}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }
}

public class AzureDevOpsApiException : Exception
{
    public AzureDevOpsApiException(string message) : base(message) { }
    public AzureDevOpsApiException(string message, Exception inner) : base(message, inner) { }
}
