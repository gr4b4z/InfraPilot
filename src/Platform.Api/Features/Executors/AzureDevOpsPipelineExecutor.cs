using System.Text.Json;
using System.Text.RegularExpressions;
using Platform.Api.Features.Requests.Models;
using Platform.Api.Infrastructure.AzureDevOps;

namespace Platform.Api.Features.Executors;

public class AzureDevOpsPipelineExecutor : IExecutor
{
    public string Type => "azure-devops-pipeline";

    private static readonly TimeSpan StaleTimeout = TimeSpan.FromHours(4);

    private readonly AzureDevOpsClient _adoClient;
    private readonly ILogger<AzureDevOpsPipelineExecutor> _logger;

    public AzureDevOpsPipelineExecutor(AzureDevOpsClient adoClient, ILogger<AzureDevOpsPipelineExecutor> logger)
    {
        _adoClient = adoClient;
        _logger = logger;
    }

    public Task<bool> AlreadyExecuted(ServiceRequest request, CancellationToken ct)
    {
        // Check if request already has an InProgress or Completed execution result
        var hasResult = request.ExecutionResults?.Any(er =>
            er.Status is "InProgress" or "Completed") == true;
        return Task.FromResult(hasResult);
    }

    public async Task<ExecutionResult> Execute(ServiceRequest request, CancellationToken ct)
    {
        var executor = request.CatalogItem?.Executor;
        if (executor is null)
            return FailedResult(request.Id, "No executor configuration found on catalog item");

        // Resolve template variables from request inputs
        var inputs = DeserializeInputs(request.InputsJson);
        var resolvedParams = ResolveParameters(executor.ParametersMap, inputs);

        // Determine pipeline ID: explicit config first, then from resolved parameters
        var pipelineId = executor.PipelineId;
        if (!pipelineId.HasValue)
        {
            if (resolvedParams.TryGetValue("pipeline_id", out var pidStr) && int.TryParse(pidStr, out var pid))
                pipelineId = pid;
            else if (resolvedParams.TryGetValue("pipeline", out var pStr) && int.TryParse(pStr, out var p))
                pipelineId = p;
        }

        if (!pipelineId.HasValue)
            return FailedResult(request.Id, "No pipeline ID configured or provided in request inputs");

        var branch = resolvedParams.GetValueOrDefault("branch");
        var connection = executor.Connection;
        var project = executor.Project;

        // Build pipeline parameters (exclude meta keys)
        var pipelineParams = resolvedParams
            .Where(kv => kv.Key is not ("pipeline_id" or "pipeline" or "branch"))
            .ToDictionary(kv => kv.Key, kv => kv.Value);

        try
        {
            var run = await _adoClient.QueuePipelineRun(
                connection,
                project,
                pipelineId.Value,
                string.IsNullOrEmpty(branch) ? null : $"refs/heads/{branch.TrimStart('/')}",
                pipelineParams.Count > 0 ? pipelineParams : null,
                ct);

            var webUrl = run.Links?.Web?.Href ?? run.Url;

            var output = new
            {
                buildId = run.Id,
                buildNumber = run.Name,
                buildUrl = webUrl,
                connection,
                project,
                pipelineId = pipelineId.Value,
                pipelineName = run.Pipeline?.Name,
            };

            _logger.LogInformation(
                "Pipeline triggered: runId={RunId}, pipelineId={PipelineId}, request={RequestId}",
                run.Id, pipelineId.Value, request.Id);

            return new ExecutionResult
            {
                Id = Guid.NewGuid(),
                ServiceRequestId = request.Id,
                Status = "InProgress",
                OutputJson = JsonSerializer.Serialize(output),
                StartedAt = DateTimeOffset.UtcNow,
                CompletedAt = null,
            };
        }
        catch (AzureDevOpsApiException ex)
        {
            _logger.LogError(ex, "Failed to queue pipeline for request {RequestId}", request.Id);
            return FailedResult(request.Id, $"Azure DevOps API error: {ex.Message}");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "HTTP error queuing pipeline for request {RequestId}", request.Id);
            return FailedResult(request.Id, $"Connection error: {ex.Message}");
        }
    }

    public async Task<ExecutionResult?> CheckProgress(ExecutionResult current, CancellationToken ct)
    {
        // Parse tracking data from OutputJson
        using var doc = JsonDocument.Parse(current.OutputJson ?? "{}");
        var root = doc.RootElement;

        if (!root.TryGetProperty("buildId", out var buildIdEl))
        {
            _logger.LogWarning("No buildId in OutputJson for ExecutionResult {Id}", current.Id);
            current.Status = "Failed";
            current.ErrorMessage = "Missing buildId in execution tracking data";
            current.CompletedAt = DateTimeOffset.UtcNow;
            return current;
        }

        var buildId = buildIdEl.GetInt32();
        var connection = root.TryGetProperty("connection", out var connEl) ? connEl.GetString() : null;
        var project = root.TryGetProperty("project", out var projEl) ? projEl.GetString() : null;

        // Staleness check
        if (DateTimeOffset.UtcNow - current.StartedAt > StaleTimeout)
        {
            _logger.LogWarning(
                "Build {BuildId} for ExecutionResult {Id} has been in progress for over {Hours}h, marking as failed",
                buildId, current.Id, StaleTimeout.TotalHours);
            current.Status = "Failed";
            current.ErrorMessage = $"Build timed out after {StaleTimeout.TotalHours} hours. Check Azure DevOps for build {buildId}.";
            current.CompletedAt = DateTimeOffset.UtcNow;
            return current;
        }

        try
        {
            var build = await _adoClient.GetBuild(connection, project, buildId, ct);

            if (build.IsInProgress)
            {
                _logger.LogDebug("Build {BuildId} still in progress (status={Status})", buildId, build.Status);
                return null; // No update yet
            }

            // Build completed — update the execution result
            var webUrl = build.Links?.Web?.Href;

            // Enrich OutputJson with final data
            var enrichedOutput = new
            {
                buildId = build.Id,
                buildNumber = build.BuildNumber,
                buildUrl = webUrl,
                connection,
                project,
                pipelineId = root.TryGetProperty("pipelineId", out var pidEl) ? pidEl.GetInt32() : 0,
                pipelineName = build.Definition?.Name,
                result = build.Result,
                status = build.Status,
                sourceBranch = build.SourceBranch,
                startTime = build.StartTime,
                finishTime = build.FinishTime,
            };

            current.OutputJson = JsonSerializer.Serialize(enrichedOutput);
            current.CompletedAt = build.FinishTime ?? DateTimeOffset.UtcNow;

            if (build.IsSucceeded)
            {
                current.Status = "Completed";
                _logger.LogInformation("Build {BuildId} succeeded for ExecutionResult {Id}", buildId, current.Id);
            }
            else
            {
                current.Status = "Failed";
                current.ErrorMessage = $"Build {build.BuildNumber} {build.Result ?? "failed"} — see Azure DevOps for details";
                _logger.LogWarning(
                    "Build {BuildId} failed (result={Result}) for ExecutionResult {Id}",
                    buildId, build.Result, current.Id);
            }

            return current;
        }
        catch (AzureDevOpsApiException ex)
        {
            _logger.LogError(ex, "Error checking build {BuildId} progress", buildId);
            // Don't fail the execution on a transient API error — just skip this poll cycle
            return null;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "HTTP error checking build {BuildId} progress", buildId);
            return null;
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

    private static ExecutionResult FailedResult(Guid requestId, string error) => new()
    {
        Id = Guid.NewGuid(),
        ServiceRequestId = requestId,
        Status = "Failed",
        ErrorMessage = error,
        CompletedAt = DateTimeOffset.UtcNow,
    };
}
