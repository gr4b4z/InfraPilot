using Platform.Api.Features.Requests.Models;

namespace Platform.Api.Features.Executors;

public class AzureDevOpsRepoExecutor : IExecutor
{
    public string Type => "azure-devops-repo";
    private readonly ILogger<AzureDevOpsRepoExecutor> _logger;

    public AzureDevOpsRepoExecutor(ILogger<AzureDevOpsRepoExecutor> logger)
    {
        _logger = logger;
    }

    public Task<bool> AlreadyExecuted(ServiceRequest request, CancellationToken ct)
    {
        // TODO: Check Azure DevOps API if repo already exists
        return Task.FromResult(false);
    }

    public Task<ExecutionResult> Execute(ServiceRequest request, CancellationToken ct)
    {
        _logger.LogInformation("Executing Azure DevOps repo creation for request {RequestId}", request.Id);
        // TODO: Implement Azure DevOps API calls
        return Task.FromResult(new ExecutionResult
        {
            Id = Guid.NewGuid(),
            ServiceRequestId = request.Id,
            Status = "Completed",
            OutputJson = "{\"message\": \"Repository created (stub)\"}",
            CompletedAt = DateTimeOffset.UtcNow
        });
    }
}
