using System.Text.Json;
using Platform.Api.Features.Deployments.Models;

namespace Platform.Api.Features.Promotions.Models;

/// <summary>
/// A promotion candidate: "service X version V in source env should move forward to target env."
/// Lifecycle is Pending → Approved → Deploying → Deployed, with Superseded / Rejected as terminal
/// off-ramps.
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
