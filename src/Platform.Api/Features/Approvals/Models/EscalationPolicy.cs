namespace Platform.Api.Features.Approvals.Models;

public class EscalationPolicy
{
    public int? TimeoutHours { get; set; }
    public string? EscalationGroup { get; set; }
}
