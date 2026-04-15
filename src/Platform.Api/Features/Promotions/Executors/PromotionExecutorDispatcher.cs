using Platform.Api.Features.Promotions.Models;

namespace Platform.Api.Features.Promotions.Executors;

/// <summary>
/// Small facade over keyed-DI lookup of <see cref="IPromotionExecutor"/>s. Handles the two
/// failure modes (unknown kind, executor threw) in one place so <see cref="PromotionService"/>
/// can treat dispatch as a single method call.
/// </summary>
public class PromotionExecutorDispatcher
{
    private readonly IServiceProvider _sp;
    private readonly ILogger<PromotionExecutorDispatcher> _logger;

    public PromotionExecutorDispatcher(IServiceProvider sp, ILogger<PromotionExecutorDispatcher> logger)
    {
        _sp = sp;
        _logger = logger;
    }

    /// <summary>
    /// Looks up the executor by kind and delegates dispatch. Returns <c>null</c> when no
    /// executor is configured on the snapshot; otherwise returns the executor's result.
    /// Never throws — translates unexpected exceptions into a <see cref="PromotionDispatchResult.Fail"/>.
    /// </summary>
    public async Task<PromotionDispatchResult?> DispatchAsync(
        PromotionCandidate candidate, ResolvedPolicySnapshot snapshot, CancellationToken ct)
    {
        if (!snapshot.HasExecutor) return null;

        var executor = _sp.GetKeyedService<IPromotionExecutor>(snapshot.ExecutorKind!);
        if (executor is null)
        {
            _logger.LogWarning(
                "No promotion executor registered for kind '{Kind}' (candidate {Id})",
                snapshot.ExecutorKind, candidate.Id);
            return PromotionDispatchResult.Fail(
                $"No promotion executor registered for kind '{snapshot.ExecutorKind}'");
        }

        try
        {
            return await executor.DispatchAsync(candidate, snapshot.ExecutorConfigJson, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Promotion executor '{Kind}' threw for candidate {Id}",
                snapshot.ExecutorKind, candidate.Id);
            return PromotionDispatchResult.Fail($"Executor '{snapshot.ExecutorKind}' threw: {ex.Message}");
        }
    }
}
