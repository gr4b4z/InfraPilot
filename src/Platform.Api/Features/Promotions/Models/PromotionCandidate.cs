using System.Text.Json;
using Platform.Api.Features.Deployments.Models;

namespace Platform.Api.Features.Promotions.Models;

/// <summary>
/// A promotion candidate: "service X version V in source env should move forward to target env."
/// Lifecycle is Pending → Approved → Deploying → Deployed, with Superseded / Rejected as
/// terminal off-ramps. <see cref="PromotionStatus.Deployed"/> is the one state reality can force:
/// once the version lands in the target environment the change IS live, so ingestion closes the
/// candidate there from any open state — including Rejected.
///
/// <para>Approved → Pending is the one backwards edge, and only while the candidate is still
/// undispatched: <see cref="PromotionService.CancelApprovalAsync"/> undoes an approval given by
/// mistake. From Deploying onwards the pipeline owns the change and the answer is a rollback.</para>
///
/// <para>Candidates are created externally via <see cref="PromotionService.CreateExternalCandidateAsync"/>
/// (an external system POSTs the authoritative net change set) and closed by either approval +
/// executor dispatch or a newer version replacing them. The candidate is <b>self-contained</b>: it
/// carries its own <see cref="References"/> (the net change set), so supersede is a pure state flip
/// — no inheritance or event-id copying.</para>
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

    // Display/traceability only (not used for gating): the target env's current SHA and the SHA
    // being promoted. Supplied by the external creator; the tool records but never validates them.
    public string? FromRevision { get; set; }
    public string? ToRevision { get; set; }

    /// <summary>
    /// The version the target environment was running when this candidate was created — the
    /// baseline its <see cref="References"/> were computed against, and the left-hand side of the
    /// "v1 → v2" the promotion describes. Captured server-side (latest succeeded deploy in
    /// <see cref="TargetEnv"/>), refreshed alongside <see cref="FromRevision"/> when the source
    /// system re-pushes the same candidate, and frozen from then on.
    ///
    /// <para>Stored rather than re-read at display time because the target env moves on: once a
    /// promotion lands, the target's <i>current</i> version is the promoted one, so a historical
    /// candidate rendered off live state forgets where it came from and reads as "v2 → v2".
    /// Null on a first deploy into the target, and on candidates created before this was
    /// recorded — read paths fall back to live state for those.</para>
    /// </summary>
    public string? FromVersion { get; set; }

    public PromotionStatus Status { get; set; } = PromotionStatus.Pending;

    // Resolved policy snapshot: the rules this candidate is actually gated on. Taken at creation
    // time, and re-taken for still-Pending candidates when the policy is edited (see
    // PromotionService.RefreshPolicySnapshotsAsync) so a settings change applies to in-flight
    // promotions. Frozen from Approved onwards — the decision that fired keeps the rules it was
    // judged under.
    public Guid? PolicyId { get; set; }
    public string? ResolvedPolicyJson { get; set; }

    // CI run URL captured after executor dispatch.
    public string? ExternalRunUrl { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ApprovedAt { get; set; }
    public DateTimeOffset? DeployedAt { get; set; }

    // Set when a newer version creates a candidate on the same edge and supersedes this one.
    public Guid? SupersededById { get; set; }

    // The authoritative net change set this candidate ships, supplied by the external creator.
    // Shape: [{ type, provider, key, url, title, revision }] — same as DeployEvent.References so
    // the UI and downstream integrations can treat both sources uniformly. Self-contained: this is
    // the single source of truth for "what ships", so supersede never copies/inherits anything.
    public string ReferencesJson { get; set; } = "[]";

    public List<ReferenceDto> References
    {
        get => string.IsNullOrEmpty(ReferencesJson)
            ? new()
            : JsonSerializer.Deserialize<List<ReferenceDto>>(ReferencesJson, JsonOpts) ?? new();
        set => ReferencesJson = JsonSerializer.Serialize(value, JsonOpts);
    }

    // Free-form participants attached at the promotion level (not from any deploy event).
    // Shape: [{ role, displayName, email }] — same as DeployEvent.Participants so UI and downstream
    // integrations (Jira, Slack) can treat both sources uniformly. Roles are user-defined strings;
    // the platform doesn't enforce a fixed taxonomy.
    public string ParticipantsJson { get; set; } = "[]";

    public List<PromotionParticipant> Participants
    {
        get => string.IsNullOrEmpty(ParticipantsJson)
            ? new()
            : JsonSerializer.Deserialize<List<PromotionParticipant>>(ParticipantsJson, JsonOpts) ?? new();
        set => ParticipantsJson = JsonSerializer.Serialize(value, JsonOpts);
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };
}

/// <summary>
/// Promotion-level participant. <c>Role</c> is the canonical lower-kebab-case key used for
/// dedupe and downstream mapping; display is controlled by the admin-managed role dictionary.
/// </summary>
public record PromotionParticipant(string Role, string? DisplayName, string? Email);

public enum PromotionStatus
{
    Pending,
    Approved,
    Deploying,
    Deployed,
    Superseded,
    Rejected,
}
