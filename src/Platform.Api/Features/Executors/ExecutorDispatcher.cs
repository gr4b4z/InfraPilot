using Platform.Api.Features.Requests.Models;

namespace Platform.Api.Features.Executors;

public class ExecutorDispatcher
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<ExecutorDispatcher> _logger;

    public ExecutorDispatcher(IServiceProvider serviceProvider, ILogger<ExecutorDispatcher> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    public async Task<ExecutionResult> Dispatch(ServiceRequest request, string executorType, CancellationToken ct)
    {
        var executor = _serviceProvider.GetKeyedService<IExecutor>(executorType);
        if (executor is null)
        {
            _logger.LogError("No executor registered for type: {ExecutorType}", executorType);
            return new ExecutionResult
            {
                Id = Guid.NewGuid(),
                ServiceRequestId = request.Id,
                Status = "Failed",
                ErrorMessage = $"No executor registered for type: {executorType}",
                CompletedAt = DateTimeOffset.UtcNow
            };
        }

        if (await executor.AlreadyExecuted(request, ct))
        {
            _logger.LogInformation("Request {RequestId} already executed by {ExecutorType}, skipping", request.Id, executorType);
            return new ExecutionResult
            {
                Id = Guid.NewGuid(),
                ServiceRequestId = request.Id,
                Status = "Completed",
                OutputJson = "{\"skipped\": true, \"reason\": \"already_executed\"}",
                CompletedAt = DateTimeOffset.UtcNow
            };
        }

        return await executor.Execute(request, ct);
    }
}
