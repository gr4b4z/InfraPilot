using System.Text.Json.Serialization;

namespace Platform.Api.Features.Diagnostics;

/// <summary>
/// Known symptoms the assistant can diagnose. The value is the key a playbook file declares.
/// </summary>
public static class Symptoms
{
    /// <summary>A promotion has been sitting at Approved without a deployment behind it.</summary>
    public const string PromotionStuckApproved = "promotion-stuck-approved";

    /// <summary>A promotion is still Pending — nobody has cleared its gates.</summary>
    public const string PromotionStuckPending = "promotion-stuck-pending";

    /// <summary>A deploy event came back failed.</summary>
    public const string DeploymentFailed = "deployment-failed";
}

/// <summary>
/// An authored catalogue of what usually goes wrong for one symptom, and how to tell which case
/// applies.
/// </summary>
/// <remarks>
/// The point of this file existing at all is that the assistant must not invent causes. The
/// diagnostic tools compute a set of observation flags from live state, and a cause is only offered
/// when its flags actually hold. An operator reading the answer can therefore check the reasoning:
/// every claim traces to an observation the platform made, not to the model's prior.
/// </remarks>
public class Playbook
{
    public string Id { get; set; } = "";

    /// <summary>Which symptom this playbook covers — one of <see cref="Symptoms"/>.</summary>
    public string Symptom { get; set; } = "";

    public string Title { get; set; } = "";

    /// <summary>Plain explanation of what the symptom means, shown before the causes.</summary>
    public string Summary { get; set; } = "";

    public List<PlaybookCause> Causes { get; set; } = [];

    public string? Source { get; set; }
    public string? AsOf { get; set; }

    [JsonIgnore]
    public string? SourcePath { get; set; }
}

/// <summary>One candidate explanation for a symptom.</summary>
public class PlaybookCause
{
    public string Id { get; set; } = "";

    /// <summary>Short statement of the cause, e.g. "Nothing is subscribed to promotion.approved".</summary>
    public string Title { get; set; } = "";

    /// <summary>
    /// Observation flags that must ALL hold for this cause to be offered. An empty list makes the
    /// cause a general possibility, always listed but ranked below anything the evidence supports.
    /// </summary>
    public List<string> MatchesWhen { get; set; } = [];

    /// <summary>Observation flags that rule this cause out even when <see cref="MatchesWhen"/> holds.</summary>
    public List<string> Unless { get; set; } = [];

    /// <summary>Why this happens — the mechanism, not just the name.</summary>
    public string Explanation { get; set; } = "";

    /// <summary>How the user can confirm it, in terms of things they can actually look at.</summary>
    public string? Confirm { get; set; }

    /// <summary>What to do about it.</summary>
    public string? Fix { get; set; }

    /// <summary>Guide id that walks through the fix, when one exists.</summary>
    public string? FixGuide { get; set; }
}
