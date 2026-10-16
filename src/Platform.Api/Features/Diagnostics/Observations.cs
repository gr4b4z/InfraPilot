namespace Platform.Api.Features.Diagnostics;

/// <summary>
/// The complete vocabulary of observation flags <see cref="DiagnosticsService"/> can emit.
/// </summary>
/// <remarks>
/// Playbook causes select themselves by naming these. A cause naming a flag that is never emitted
/// is dead — it would simply never be offered, with no error anywhere — so the vocabulary is
/// declared here once and asserted against the authored playbooks by DiagnosticsCorpusTests.
/// Adding a flag means adding it here, emitting it, and (usually) writing the cause that uses it.
/// </remarks>
public static class Observations
{
    // ── Promotion: timing and ordering ──
    public const string WithinGracePeriod = "within_grace_period";
    public const string NewerCandidateExists = "newer_candidate_exists";

    // ── Promotion: the announcement ──
    public const string NoActiveSubscription = "no_active_subscription";
    public const string NoDeliveryRecorded = "no_delivery_recorded";
    public const string DeliveryPending = "delivery_pending";
    public const string DeliveryFailed = "delivery_failed";
    public const string DeliverySucceeded = "delivery_succeeded";

    // ── Promotion: what happened downstream ──
    public const string NoDeployEventSinceApproval = "no_deploy_event_since_approval";
    public const string DeployEventOtherVersion = "deploy_event_other_version";
    public const string DeployEventFailed = "deploy_event_failed";
    public const string DeployEventSucceeded = "deploy_event_succeeded";

    // ── Promotion: gates ──
    public const string NoApprovalsRecorded = "no_approvals_recorded";
    public const string WorkItemsOutstanding = "work_items_outstanding";

    // ── Deployment ──
    public const string DeploySucceeded = "deploy_succeeded";
    public const string DeployFailed = "deploy_failed";
    public const string WasRollback = "was_rollback";
    public const string SyntheticSource = "synthetic_source";
    public const string NoLogsCaptured = "no_logs_captured";
    public const string LogsTruncated = "logs_truncated";

    // ── Deployment: signals read out of the captured logs ──
    public const string LogImagePullFailure = "log_image_pull_failure";
    public const string LogCrashLoop = "log_crash_loop";
    public const string LogProbeFailure = "log_probe_failure";
    public const string LogTimeout = "log_timeout";
    public const string LogHelmUpgradeFailed = "log_helm_upgrade_failed";
    public const string LogHelmLock = "log_helm_lock";
    public const string LogHelmNoDeployedRelease = "log_helm_no_deployed_release";
    public const string LogInsufficientResources = "log_insufficient_resources";
    public const string LogPermissionDenied = "log_permission_denied";
    public const string LogConnectionRefused = "log_connection_refused";

    /// <summary>Every flag above, for validating authored playbooks against reality.</summary>
    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        WithinGracePeriod, NewerCandidateExists,
        NoActiveSubscription, NoDeliveryRecorded, DeliveryPending, DeliveryFailed, DeliverySucceeded,
        NoDeployEventSinceApproval, DeployEventOtherVersion, DeployEventFailed, DeployEventSucceeded,
        NoApprovalsRecorded, WorkItemsOutstanding,
        DeploySucceeded, DeployFailed, WasRollback, SyntheticSource, NoLogsCaptured, LogsTruncated,
        LogImagePullFailure, LogCrashLoop, LogProbeFailure, LogTimeout, LogHelmUpgradeFailed,
        LogHelmLock, LogHelmNoDeployedRelease, LogInsufficientResources, LogPermissionDenied,
        LogConnectionRefused,
    };
}
