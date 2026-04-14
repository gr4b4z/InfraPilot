using Microsoft.EntityFrameworkCore;
using Platform.Api.Features.Executors;
using Platform.Api.Features.Requests;
using Platform.Api.Features.Requests.Models;
using Platform.Api.Infrastructure.Audit;
using Platform.Api.Infrastructure.Persistence;

namespace Platform.Api.BackgroundServices;

public class ExecutorWorkerService : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ExecutorWorkerService> _logger;

    public ExecutorWorkerService(
        IServiceScopeFactory scopeFactory,
        ILogger<ExecutorWorkerService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "ExecutorWorkerService started (polling mode — replace with Azure Service Bus consumer in production)");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessExecutingRequests(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing executing requests");
            }

            try
            {
                await PollInProgressResults(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error polling in-progress results");
            }

            await Task.Delay(PollInterval, stoppingToken);
        }

        _logger.LogInformation("ExecutorWorkerService stopped");
    }

    // ──────────────────────────────────────────────
    //  Phase 1: Dispatch new Executing requests
    // ──────────────────────────────────────────────

    private async Task ProcessExecutingRequests(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var executorDispatcher = scope.ServiceProvider.GetRequiredService<ExecutorDispatcher>();
        var stateMachine = scope.ServiceProvider.GetRequiredService<RequestStateMachine>();
        var auditLogger = scope.ServiceProvider.GetRequiredService<IAuditLogger>();

        // Only pick up Executing requests that don't already have an InProgress or Completed result
        var executingRequests = await db.ServiceRequests
            .Include(r => r.CatalogItem)
            .Include(r => r.ExecutionResults)
            .Where(r => r.Status == RequestStatus.Executing)
            .Where(r => !r.ExecutionResults.Any(er => er.Status == "InProgress" || er.Status == "Completed"))
            .ToListAsync(ct);

        if (executingRequests.Count == 0)
            return;

        _logger.LogInformation("Found {Count} new request(s) in Executing status", executingRequests.Count);

        foreach (var request in executingRequests)
        {
            try
            {
                await ProcessSingleRequest(db, executorDispatcher, stateMachine, auditLogger, request, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to process request {RequestId}", request.Id);

                try
                {
                    await stateMachine.TransitionTo(
                        request, RequestStatus.Failed,
                        actorId: "system",
                        actorName: "ExecutorWorkerService",
                        actorType: "system");

                    await db.SaveChangesAsync(ct);
                }
                catch (Exception innerEx)
                {
                    _logger.LogError(innerEx,
                        "Failed to transition request {RequestId} to Failed after execution error",
                        request.Id);
                }
            }
        }
    }

    private async Task ProcessSingleRequest(
        PlatformDbContext db,
        ExecutorDispatcher executorDispatcher,
        RequestStateMachine stateMachine,
        IAuditLogger auditLogger,
        ServiceRequest request,
        CancellationToken ct)
    {
        var executorType = request.CatalogItem?.Executor?.Type;

        if (string.IsNullOrEmpty(executorType))
        {
            _logger.LogWarning(
                "Request {RequestId} has no executor type configured, transitioning to Failed",
                request.Id);

            await stateMachine.TransitionTo(
                request, RequestStatus.Failed,
                actorId: "system",
                actorName: "ExecutorWorkerService",
                actorType: "system");

            await db.SaveChangesAsync(ct);
            return;
        }

        _logger.LogInformation(
            "Dispatching request {RequestId} to executor {ExecutorType}",
            request.Id, executorType);

        var result = await executorDispatcher.Dispatch(request, executorType, ct);

        db.ExecutionResults.Add(result);

        if (result.Status == "InProgress")
        {
            // Async executor — keep request in Executing, poll later
            _logger.LogInformation(
                "Request {RequestId} execution in progress (async executor), will poll for completion",
                request.Id);

            await db.SaveChangesAsync(ct);

            await auditLogger.Log(
                module: "executor",
                action: "execution.triggered",
                actorId: "system",
                actorName: "ExecutorWorkerService",
                actorType: "system",
                entityType: "ServiceRequest",
                entityId: request.Id,
                metadata: new
                {
                    ExecutorType = executorType,
                    ResultId = result.Id,
                    OutputJson = result.OutputJson,
                });
        }
        else
        {
            // Synchronous executor — transition immediately
            var newStatus = result.Status == "Completed"
                ? RequestStatus.Completed
                : RequestStatus.Failed;

            await stateMachine.TransitionTo(
                request, newStatus,
                actorId: "system",
                actorName: "ExecutorWorkerService",
                actorType: "system");

            await db.SaveChangesAsync(ct);

            await auditLogger.Log(
                module: "executor",
                action: $"execution.{newStatus.ToString().ToLowerInvariant()}",
                actorId: "system",
                actorName: "ExecutorWorkerService",
                actorType: "system",
                entityType: "ServiceRequest",
                entityId: request.Id,
                metadata: new
                {
                    ExecutorType = executorType,
                    ResultStatus = result.Status,
                    ResultId = result.Id,
                    ErrorMessage = result.ErrorMessage,
                });

            _logger.LogInformation(
                "Request {RequestId} execution completed with status {Status}",
                request.Id, newStatus);
        }
    }

    // ──────────────────────────────────────────────
    //  Phase 2: Poll in-progress execution results
    // ──────────────────────────────────────────────

    private async Task PollInProgressResults(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var stateMachine = scope.ServiceProvider.GetRequiredService<RequestStateMachine>();
        var auditLogger = scope.ServiceProvider.GetRequiredService<IAuditLogger>();

        var inProgressResults = await db.ExecutionResults
            .Include(er => er.ServiceRequest)
                .ThenInclude(sr => sr!.CatalogItem)
            .Where(er => er.Status == "InProgress")
            .ToListAsync(ct);

        if (inProgressResults.Count == 0)
            return;

        _logger.LogDebug("Polling {Count} in-progress execution result(s)", inProgressResults.Count);

        foreach (var result in inProgressResults)
        {
            try
            {
                await PollSingleResult(db, scope.ServiceProvider, stateMachine, auditLogger, result, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error polling ExecutionResult {Id}", result.Id);
            }
        }
    }

    private async Task PollSingleResult(
        PlatformDbContext db,
        IServiceProvider serviceProvider,
        RequestStateMachine stateMachine,
        IAuditLogger auditLogger,
        ExecutionResult result,
        CancellationToken ct)
    {
        var request = result.ServiceRequest;
        if (request is null)
        {
            _logger.LogWarning("ExecutionResult {Id} has no associated ServiceRequest", result.Id);
            return;
        }

        var executorType = request.CatalogItem?.Executor?.Type;
        if (string.IsNullOrEmpty(executorType))
        {
            _logger.LogWarning("Cannot poll ExecutionResult {Id}: no executor type", result.Id);
            result.Status = "Failed";
            result.ErrorMessage = "Executor type not found in catalog configuration";
            result.CompletedAt = DateTimeOffset.UtcNow;
            await TransitionOnCompletion(db, stateMachine, auditLogger, request, result, executorType, ct);
            return;
        }

        var executor = serviceProvider.GetKeyedService<IExecutor>(executorType);
        if (executor is null)
        {
            _logger.LogWarning("No executor registered for type {Type}", executorType);
            result.Status = "Failed";
            result.ErrorMessage = $"No executor registered for type: {executorType}";
            result.CompletedAt = DateTimeOffset.UtcNow;
            await TransitionOnCompletion(db, stateMachine, auditLogger, request, result, executorType, ct);
            return;
        }

        var updatedResult = await executor.CheckProgress(result, ct);

        if (updatedResult is null)
            return; // Still in progress, no update

        // Result has changed — persist and transition
        await TransitionOnCompletion(db, stateMachine, auditLogger, request, updatedResult, executorType, ct);
    }

    private async Task TransitionOnCompletion(
        PlatformDbContext db,
        RequestStateMachine stateMachine,
        IAuditLogger auditLogger,
        ServiceRequest request,
        ExecutionResult result,
        string? executorType,
        CancellationToken ct)
    {
        var newStatus = result.Status == "Completed"
            ? RequestStatus.Completed
            : RequestStatus.Failed;

        await stateMachine.TransitionTo(
            request, newStatus,
            actorId: "system",
            actorName: "ExecutorWorkerService",
            actorType: "system");

        await db.SaveChangesAsync(ct);

        await auditLogger.Log(
            module: "executor",
            action: $"execution.{newStatus.ToString().ToLowerInvariant()}",
            actorId: "system",
            actorName: "ExecutorWorkerService",
            actorType: "system",
            entityType: "ServiceRequest",
            entityId: request.Id,
            metadata: new
            {
                ExecutorType = executorType,
                ResultStatus = result.Status,
                ResultId = result.Id,
                ErrorMessage = result.ErrorMessage,
            });

        _logger.LogInformation(
            "Request {RequestId} execution completed via polling with status {Status}",
            request.Id, newStatus);
    }

}
