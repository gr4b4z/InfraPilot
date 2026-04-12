namespace Platform.Api.Features.Requests.Models;

public class ExecutionResult
{
    public Guid Id { get; set; }
    public Guid ServiceRequestId { get; set; }
    public int Attempt { get; set; } = 1;
    public string Status { get; set; } = "";
    public string? OutputJson { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }

    public ServiceRequest? ServiceRequest { get; set; }
}
