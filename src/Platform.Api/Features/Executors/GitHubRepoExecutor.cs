using Platform.Api.Features.Requests.Models;

namespace Platform.Api.Features.Executors;

public class GitHubRepoExecutor : IExecutor
{
    public string Type => "github-repo";
    private readonly ILogger<GitHubRepoExecutor> _logger;

    public GitHubRepoExecutor(ILogger<GitHubRepoExecutor> logger)
    {
        _logger = logger;
    }

    public Task<bool> AlreadyExecuted(ServiceRequest request, CancellationToken ct) => Task.FromResult(false);

    public Task<ExecutionResult> Execute(ServiceRequest request, CancellationToken ct)
    {
        _logger.LogInformation("Executing GitHub repo creation for request {RequestId}", request.Id);
        return Task.FromResult(new ExecutionResult
        {
            Id = Guid.NewGuid(),
            ServiceRequestId = request.Id,
            Status = "Completed",
            OutputJson = "{\"message\": \"GitHub repo created (stub)\"}",
            CompletedAt = DateTimeOffset.UtcNow
        });
    }
}
