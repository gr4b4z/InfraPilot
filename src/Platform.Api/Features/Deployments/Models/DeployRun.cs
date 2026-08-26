namespace Platform.Api.Features.Deployments.Models;

/// <summary>
/// The CI run that performed a deployment — the answer to "what created this?". Distinct from the
/// <c>pipeline</c> reference, which points at the run that <i>built</i> the artifact: a component is
/// built once in Azure DevOps and then deployed many times by the release repository's GitHub
/// Actions workflow, so the build link cannot explain a specific deployment's outcome.
///
/// <para>Stored as a JSON blob on <see cref="DeployEvent.RunJson"/> rather than as columns: it is a
/// display-only bundle read whole on the detail page, never filtered or joined on, and producers
/// keep adding fields to it. <c>FailureReason</c> is the one line the producer identified as the
/// cause — surfaced on its own so the UI does not have to guess which log line matters.</para>
/// </summary>
public record DeployRun(
    // "github-actions", "azure-devops", … — drives the provider label and icon.
    string? Provider = null,
    // Provider-native run identity. RunId is the API/URL id; RunNumber is the human counter
    // ("#294") a person reads in the pipeline UI.
    string? RunId = null,
    string? RunNumber = null,
    // Re-runs of the same run. Null/1 for a first attempt.
    int? Attempt = null,
    string? WorkflowName = null,
    // The matrix leg / job that ran this component, e.g. "Deploy Helm (mpt-extension-nav-billing)".
    string? JobName = null,
    // Link to the whole run.
    string? RunUrl = null,
    // Link to the specific job inside the run — the deep link a person actually wants when a single
    // component in a fan-out failed. Falls back to RunUrl when the producer could not resolve it.
    string? JobUrl = null,
    string? TriggeredBy = null,
    DateTimeOffset? StartedAt = null,
    DateTimeOffset? CompletedAt = null,
    // The specific error, as identified by the deploying pipeline (e.g. "pod x cannot start
    // (container waiting reason=ErrImagePull)"). Null on success.
    string? FailureReason = null);
