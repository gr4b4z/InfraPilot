using Platform.Api.Features.Promotions.Models;

namespace Platform.Api.Features.Promotions.Executors;

/// <summary>
/// Dispatches a promotion to whatever out-of-band system actually performs the target-env deploy
/// (Azure DevOps pipeline, GitHub Actions workflow, generic webhook, etc). Implementations are
/// registered with keyed DI keyed on <see cref="PromotionPolicy.ExecutorKind"/>.
///
/// <para>Dispatch is fire-and-forget from the platform's perspective: the executor returns a
/// best-effort external run URL so operators can jump to the external system to watch progress.
/// The candidate is then moved to <see cref="PromotionStatus.Deploying"/>; it will reach
/// <see cref="PromotionStatus.Deployed"/> only once a matching deploy event is ingested.</para>
/// </summary>
public interface IPromotionExecutor
{
    /// <summary>
    /// Short identifier for the executor kind (e.g. "webhook", "azure-devops-pipeline",
    /// "github-actions"). Mirrors the keyed DI registration key.
    /// </summary>
    string Kind { get; }

    /// <summary>
    /// Invoke the external system. <paramref name="configJson"/> is opaque — each executor
    /// parses it into its own shape. Return a result describing the outcome; throwing is
    /// acceptable but the dispatcher logs and swallows to keep approval flows resilient.
    /// </summary>
    Task<PromotionDispatchResult> DispatchAsync(
        PromotionCandidate candidate, string? configJson, CancellationToken ct);
}

/// <summary>
/// Outcome of a <see cref="IPromotionExecutor.DispatchAsync"/> call. Successful dispatch returns
/// an optional external run URL. Failures carry an error message but do not transition the
/// candidate back — the approval decision itself is already persisted.
/// </summary>
public record PromotionDispatchResult(bool Success, string? ExternalRunUrl, string? Error)
{
    public static PromotionDispatchResult Ok(string? url) => new(true, url, null);
    public static PromotionDispatchResult Fail(string error) => new(false, null, error);
}
