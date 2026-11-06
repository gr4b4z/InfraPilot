namespace Platform.Api.Features.Requests.Models;

/// <summary>
/// The approval settings that applied to a request when it was submitted: the catalog item's
/// <c>approval</c> block, or the first of its <c>rules</c> that matched the inputs. Stored on the
/// request (<see cref="ServiceRequest.ApprovalOutcome"/>) so a later catalog edit can't change who
/// may decide on a request already waiting.
/// </summary>
public class ApprovalOutcome
{
    public bool Required { get; set; }
    public string Strategy { get; set; } = "any";
    public int? QuorumCount { get; set; }
    public string? ApproverGroup { get; set; }
    public int? TimeoutHours { get; set; }
    public string? EscalationGroup { get; set; }

    /// <summary>Zero-based index into <c>approval.rules</c>; null when no rule matched.</summary>
    public int? RuleIndex { get; set; }

    /// <summary>The matched rule's <c>name</c>, when it has one.</summary>
    public string? RuleName { get; set; }
}
