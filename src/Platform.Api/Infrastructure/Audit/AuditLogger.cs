using System.Text.Json;
using Platform.Api.Infrastructure.Persistence;

namespace Platform.Api.Infrastructure.Audit;

public class AuditLogger : IAuditLogger
{
    private readonly PlatformDbContext _db;
    private readonly IHttpContextAccessor _httpContextAccessor;

    public AuditLogger(PlatformDbContext db, IHttpContextAccessor httpContextAccessor)
    {
        _db = db;
        _httpContextAccessor = httpContextAccessor;
    }

    public async Task Log(
        string module,
        string action,
        string actorId,
        string actorName,
        string actorType,
        string entityType,
        Guid? entityId,
        object? beforeState = null,
        object? afterState = null,
        object? metadata = null)
    {
        var context = _httpContextAccessor.HttpContext;
        var correlationId = Guid.TryParse(context?.Items["CorrelationId"]?.ToString(), out var cid)
            ? cid
            : Guid.NewGuid();

        var entry = new AuditEntry
        {
            Id = Guid.NewGuid(),
            Timestamp = DateTimeOffset.UtcNow,
            CorrelationId = correlationId,
            Module = module,
            Action = action,
            ActorId = actorId,
            ActorName = actorName,
            ActorType = actorType,
            EntityType = entityType,
            EntityId = entityId,
            BeforeState = beforeState is not null ? JsonSerializer.Serialize(beforeState) : null,
            AfterState = afterState is not null ? JsonSerializer.Serialize(afterState) : null,
            Metadata = metadata is not null ? JsonSerializer.Serialize(metadata) : null,
            SourceIp = context?.Connection.RemoteIpAddress?.ToString()
        };

        _db.AuditLog.Add(entry);
        await _db.SaveChangesAsync();
    }
}
