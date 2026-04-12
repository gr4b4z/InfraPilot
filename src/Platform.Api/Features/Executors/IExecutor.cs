using Platform.Api.Features.Requests.Models;

namespace Platform.Api.Features.Executors;

public interface IExecutor
{
    string Type { get; }
    Task<bool> AlreadyExecuted(ServiceRequest request, CancellationToken ct);
    Task<ExecutionResult> Execute(ServiceRequest request, CancellationToken ct);

    /// <summary>
    /// Check progress of an in-progress execution. Return updated result if completed/failed,
    /// or null if still running. Default implementation returns null (for synchronous executors).
    /// </summary>
    Task<ExecutionResult?> CheckProgress(ExecutionResult current, CancellationToken ct)
        => Task.FromResult<ExecutionResult?>(null);
}
