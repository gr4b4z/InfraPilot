namespace Platform.Api.Infrastructure.Audit;

public class AuditEntry
{
    public Guid Id { get; set; }
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;
    public Guid CorrelationId { get; set; }
    public string Module { get; set; } = "";
    public string Action { get; set; } = "";
    public string ActorId { get; set; } = "";
    public string ActorName { get; set; } = "";
    public string ActorType { get; set; } = "";
    public string EntityType { get; set; } = "";
    public Guid? EntityId { get; set; }
    public string? BeforeState { get; set; }
    public string? AfterState { get; set; }
    public string? Metadata { get; set; }
    public string? SourceIp { get; set; }
}
