namespace Platform.Api.Features.Promotions.Models;

/// <summary>
/// An auto-generated promotion candidate: "service X version V that landed in source env
/// should move forward to target env." Lifecycle is Pending → Approved → Deploying → Deployed,
/// with Superseded / Rejected as terminal off-ramps.
///
/// Candidates are produced by <see cref="PromotionService.TryCreateCandidate"/> on deploy-event
/// ingest and closed by either approval + executor dispatch or a newer version replacing them.
/// </summary>
public class PromotionCandidate
{
    public Guid Id { get; set; }

    // Natural key — identifies which edge this candidate belongs to.
    public string Product { get; set; } = "";
    public string Service { get; set; } = "";
    public string SourceEnv { get; set; } = "";
    public string TargetEnv { get; set; } = "";
    public string Version { get; set; } = "";

    // Back-reference to the deploy event that spawned this candidate, plus who deployed it
    // (for separation-of-duties checks on approval).
    public Guid SourceDeployEventId { get; set; }
    public string? SourceDeployerName { get; set; }
    public string? SourceDeployerEmail { get; set; }

    public PromotionStatus Status { get; set; } = PromotionStatus.Pending;

    // Policy snapshot at creation time — preserves audit integrity even if the policy row
    // is edited or deleted between candidate creation and approval.
    public Guid? PolicyId { get; set; }
    public string? ResolvedPolicyJson { get; set; }

    // CI run URL captured after executor dispatch.
    public string? ExternalRunUrl { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ApprovedAt { get; set; }
    public DateTimeOffset? DeployedAt { get; set; }

    // Set when a newer version creates a candidate on the same edge and supersedes this one.
    public Guid? SupersededById { get; set; }
}

public enum PromotionStatus
{
    Pending,
    Approved,
    Deploying,
    Deployed,
    Superseded,
    Rejected,
}
