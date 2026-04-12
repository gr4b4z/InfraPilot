using Platform.Api.Features.Requests.Models;

namespace Platform.Api.Features.Executors;

public class GitHubActionsExecutor : IExecutor
{
    public string Type => "github-actions";
    private readonly ILogger<GitHubActionsExecutor> _logger;

    public GitHubActionsExecutor(ILogger<GitHubActionsExecutor> logger)
    {
        _logger = logger;
    }

    public Task<bool> AlreadyExecuted(ServiceRequest request, CancellationToken ct) => Task.FromResult(false);

    public Task<ExecutionResult> Execute(ServiceRequest request, CancellationToken ct)
    {
        _logger.LogInformation("Executing GitHub Actions workflow for request {RequestId}", request.Id);
        return Task.FromResult(new ExecutionResult
        {
            Id = Guid.NewGuid(),
            ServiceRequestId = request.Id,
            Status = "Completed",
            OutputJson = "{\"message\": \"GitHub Actions triggered (stub)\"}",
            CompletedAt = DateTimeOffset.UtcNow
        });
    }
}
