using Microsoft.EntityFrameworkCore;
using Platform.Api.Infrastructure.Persistence;

namespace Platform.Api.Infrastructure.Audit;

public static class AuditEndpoints
{
    public static RouteGroupBuilder MapAuditEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/", async (
            PlatformDbContext db,
            Guid? correlationId,
            string? entityType,
            Guid? entityId,
            string? actorId,
            string? module,
            string? action,
            DateTimeOffset? from,
            DateTimeOffset? to,
            int page = 1,
            int pageSize = 50) =>
        {
            var query = db.AuditLog.AsQueryable();

            if (correlationId.HasValue)
                query = query.Where(a => a.CorrelationId == correlationId.Value);
            if (!string.IsNullOrWhiteSpace(entityType))
                query = query.Where(a => a.EntityType == entityType);
            if (entityId.HasValue)
                query = query.Where(a => a.EntityId == entityId.Value);
            if (!string.IsNullOrWhiteSpace(actorId))
                query = query.Where(a => a.ActorId == actorId);
            if (!string.IsNullOrWhiteSpace(module))
                query = query.Where(a => a.Module == module);
            if (!string.IsNullOrWhiteSpace(action))
                query = query.Where(a => a.Action == action);
            if (from.HasValue)
                query = query.Where(a => a.Timestamp >= from.Value);
            if (to.HasValue)
                query = query.Where(a => a.Timestamp <= to.Value);

            var total = await query.CountAsync();
            var items = await query
                .OrderByDescending(a => a.Timestamp)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync();

            return Results.Ok(new { items, total, page, pageSize });
        });

        return group;
    }
}
