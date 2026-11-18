using Platform.Api.Features.Deployments.Models;

namespace Platform.Api.Features.Integrations.Flux;

/// <summary>
/// Turns a Flux event into a deploy event. Pure: no lookups, so every rule here is unit-tested on its
/// own. References are added later by <see cref="FluxIngestService"/> from the promotion candidate.
/// </summary>
public static class FluxEventMapper
{
    /// <summary>Recorded as <c>source</c>: a Flux install is a real Helm deployment for analytics.</summary>
    public const string Source = "helm-deploy";
    public const string RunProvider = "flux";
    public const string LogName = "flux event";

    // Only the outcomes of an install. Everything else Flux reports (progress, dependency waits,
    // drift detection, test results) is not a deployment of a version.
    private static readonly Dictionary<string, string> StatusByReason = new(StringComparer.Ordinal)
    {
        ["InstallSucceeded"] = "succeeded",
        ["UpgradeSucceeded"] = "succeeded",
        ["InstallFailed"] = "failed",
        ["UpgradeFailed"] = "failed",
        ["RollbackFailed"] = "failed",
        ["UninstallFailed"] = "failed",
        ["UninstallSucceeded"] = DeployEventStatuses.Removed,
    };

    public abstract record Result;

    /// <summary>A deploy event to ingest.</summary>
    public sealed record Mapped(CreateDeployEventDto Event) : Result;

    /// <summary>Not an install outcome; accepted and dropped.</summary>
    public sealed record Ignored(string Reason) : Result;

    /// <summary>An install outcome the portal cannot place.</summary>
    public sealed record Invalid(List<string> Errors) : Result;

    public static Result Map(FluxEventDto evt, string client)
    {
        if (!string.Equals(evt.InvolvedObject?.Kind, "HelmRelease", StringComparison.Ordinal))
            return new Ignored($"only HelmRelease events are recorded, not '{evt.InvolvedObject?.Kind}'");
        if (evt.Reason is null || !StatusByReason.TryGetValue(evt.Reason, out var status))
            return new Ignored($"reason '{evt.Reason}' is not an install outcome");

        var metadata = evt.Metadata ?? new Dictionary<string, string>();
        var product = Get(metadata, "product");
        var service = Get(metadata, "service") ?? evt.InvolvedObject?.Name;
        var environment = Get(metadata, "environment");
        var version = Get(metadata, "app-version") ?? Get(metadata, "revision");
        var cluster = Get(metadata, "cluster") ?? client;

        var errors = new List<string>();
        if (product is null)
            errors.Add("'metadata.product' is required (annotation event.toolkit.fluxcd.io/product on the HelmRelease)");
        if (service is null) errors.Add("'metadata.service' or 'involvedObject.name' is required");
        if (environment is null)
            errors.Add("'metadata.environment' is required (eventMetadata on the Alert)");
        if (version is null) errors.Add("'metadata.app-version' or 'metadata.revision' is required");
        if (evt.Timestamp is null || evt.Timestamp == default) errors.Add("'timestamp' is required");
        if (errors.Count > 0) return new Invalid(errors);

        var failed = status == "failed";
        var run = new DeployRun(
            Provider: RunProvider,
            WorkflowName: $"Flux {cluster}",
            JobName: $"{evt.InvolvedObject!.Kind}/{evt.InvolvedObject.Namespace}/{evt.InvolvedObject.Name}",
            CompletedAt: evt.Timestamp,
            FailureReason: failed ? evt.Message : null);

        var logs = string.IsNullOrWhiteSpace(evt.Message)
            ? null
            : new List<CreateDeployLogDto> { new(LogName, evt.ReportingController, evt.Message) };

        var flux = new Dictionary<string, object>
        {
            ["kind"] = evt.InvolvedObject.Kind!,
            ["namespace"] = evt.InvolvedObject.Namespace ?? "",
            ["name"] = evt.InvolvedObject.Name ?? "",
            ["reason"] = evt.Reason,
            ["cluster"] = cluster,
            ["client"] = client,
        };
        if (Get(metadata, "revision") is { } revision) flux["revision"] = revision;

        return new Mapped(new CreateDeployEventDto(
            Product: product!,
            Service: service!,
            Environment: environment!,
            Version: version!,
            Source: Source,
            DeployedAt: evt.Timestamp!.Value,
            References: null,
            Participants: null,
            Metadata: new Dictionary<string, object> { ["flux"] = flux },
            Status: status,
            Run: run,
            Logs: logs));
    }

    private static string? Get(Dictionary<string, string> metadata, string key)
        => metadata.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;
}
