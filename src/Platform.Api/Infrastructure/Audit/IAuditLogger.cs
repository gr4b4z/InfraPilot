namespace Platform.Api.Infrastructure.Audit;

public interface IAuditLogger
{
    Task Log(
        string module,
        string action,
        string actorId,
        string actorName,
        string actorType,
        string entityType,
        Guid? entityId,
        object? beforeState = null,
        object? afterState = null,
        object? metadata = null);
}
