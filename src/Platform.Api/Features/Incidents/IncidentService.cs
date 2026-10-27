using Microsoft.EntityFrameworkCore;
using Platform.Api.Features.Incidents.Models;
using Platform.Api.Features.Settings;
using Platform.Api.Infrastructure.Audit;
using Platform.Api.Infrastructure.Auth;
using Platform.Api.Infrastructure.Persistence;

namespace Platform.Api.Features.Incidents;

public sealed class IncidentValidationException(string message) : Exception(message);

public sealed class IncidentConflictException(Guid existingId) : Exception("An incident with this source and external id already exists.")
{
    public Guid ExistingId { get; } = existingId;
}

/// <summary>
/// Incident records for delivery metrics. Writes go through here so every path validates the same
/// way, resolves the environment through its aliases like deploy ingest does, and leaves an audit
/// entry. Reads are plain projections; the correlation with deploys lives in analytics, at query
/// time, so a later deploy backfill never invalidates a stored link.
/// </summary>
public class IncidentService
{
    private const string AuditModule = "incidents";

    private readonly PlatformDbContext _db;
    private readonly EnvironmentAliasResolver _environments;
    private readonly ICurrentUser _user;
    private readonly IAuditLogger _audit;

    public IncidentService(PlatformDbContext db, EnvironmentAliasResolver environments, ICurrentUser user, IAuditLogger audit)
    {
        _db = db;
        _environments = environments;
        _user = user;
        _audit = audit;
    }

    public async Task<IncidentListDto> ListAsync(
        string? product, string? environment, bool? open,
        DateTimeOffset? from, DateTimeOffset? to, int limit, int offset, CancellationToken ct)
    {
        var query = _db.Incidents.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(product)) query = query.Where(i => i.Product == product);
        var env = await _environments.ResolveFilterAsync(environment, ct);
        if (!string.IsNullOrWhiteSpace(env)) query = query.Where(i => i.Environment == env);
        if (open == true) query = query.Where(i => i.ResolvedAt == null);
        if (open == false) query = query.Where(i => i.ResolvedAt != null);
        if (from is not null) query = query.Where(i => i.StartedAt >= from);
        if (to is not null) query = query.Where(i => i.StartedAt < to);

        var total = await query.CountAsync(ct);
        var items = await query
            .OrderByDescending(i => i.StartedAt)
            .Skip(offset).Take(limit)
            .ToListAsync(ct);
        return new IncidentListDto(items.Select(ToDto).ToList(), total);
    }

    public async Task<IncidentDto?> GetAsync(Guid id, CancellationToken ct)
    {
        var row = await _db.Incidents.AsNoTracking().FirstOrDefaultAsync(i => i.Id == id, ct);
        return row is null ? null : ToDto(row);
    }

    public async Task<IncidentDto> CreateAsync(UpsertIncidentRequest request, CancellationToken ct)
    {
        var clean = await CleanAsync(request, ct);

        // (source, externalId) is the identity of an incident that lives elsewhere; a second record
        // for the same one would count the same outage twice.
        if (clean.ExternalId is not null)
        {
            var existing = await _db.Incidents.AsNoTracking()
                .FirstOrDefaultAsync(i => i.Source == clean.Source && i.ExternalId == clean.ExternalId, ct);
            if (existing is not null) throw new IncidentConflictException(existing.Id);
        }

        var now = DateTimeOffset.UtcNow;
        var row = new Incident
        {
            Id = Guid.NewGuid(),
            CreatedBy = Actor, CreatedByName = _user.Name, CreatedAt = now, UpdatedAt = now,
        };
        Apply(row, clean);
        _db.Incidents.Add(row);
        await _db.SaveChangesAsync(ct);

        await _audit.Log(AuditModule, "incident.recorded", _user.Id, _user.Name, "user",
            nameof(Incident), row.Id, null, Snapshot(row));
        return ToDto(row);
    }

    public async Task<IncidentDto?> UpdateAsync(Guid id, UpsertIncidentRequest request, CancellationToken ct)
    {
        var row = await _db.Incidents.FirstOrDefaultAsync(i => i.Id == id, ct);
        if (row is null) return null;
        var clean = await CleanAsync(request, ct);

        if (clean.ExternalId is not null)
        {
            var other = await _db.Incidents.AsNoTracking()
                .FirstOrDefaultAsync(i => i.Id != id && i.Source == clean.Source && i.ExternalId == clean.ExternalId, ct);
            if (other is not null) throw new IncidentConflictException(other.Id);
        }

        var before = Snapshot(row);
        Apply(row, clean);
        row.UpdatedBy = Actor;
        row.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);

        await _audit.Log(AuditModule, "incident.updated", _user.Id, _user.Name, "user",
            nameof(Incident), row.Id, before, Snapshot(row));
        return ToDto(row);
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct)
    {
        var row = await _db.Incidents.FirstOrDefaultAsync(i => i.Id == id, ct);
        if (row is null) return false;
        _db.Incidents.Remove(row);
        await _db.SaveChangesAsync(ct);
        await _audit.Log(AuditModule, "incident.deleted", _user.Id, _user.Name, "user",
            nameof(Incident), id, Snapshot(row), null);
        return true;
    }

    // ── Validation ───────────────────────────────────────────────────────────

    private sealed record Clean(
        string Product, string Service, string Environment, DateTimeOffset StartedAt, DateTimeOffset? ResolvedAt,
        string? Version, Guid? CausedByDeployEventId, string Source, string? ExternalId, string? Url);

    private async Task<Clean> CleanAsync(UpsertIncidentRequest r, CancellationToken ct)
    {
        var product = Required(r.Product, "product", 200);
        var service = Required(r.Service, "service", 200);
        var environment = await _environments.ResolveAsync(Required(r.Environment, "environment", 100), ct);
        if (r.StartedAt == default) throw new IncidentValidationException("'startedAt' is required.");
        if (r.ResolvedAt is not null && r.ResolvedAt < r.StartedAt)
            throw new IncidentValidationException("'resolvedAt' must not be before 'startedAt'.");

        var version = Optional(r.Version, "version", 200);
        var source = Optional(r.Source, "source", 50)?.ToLowerInvariant() ?? IncidentSources.Manual;
        var externalId = Optional(r.ExternalId, "externalId", 200);
        var url = Optional(r.Url, "url", 2000);
        if (url is not null && !(Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == "https" || uri.Scheme == "http")))
            throw new IncidentValidationException("'url' must be an absolute http(s) link.");

        // A named deploy must exist and be the same service in the same environment; it also fills
        // in the version when the reporter left it blank.
        if (r.CausedByDeployEventId is { } eventId)
        {
            var evt = await _db.DeployEvents.AsNoTracking()
                .Where(e => e.Id == eventId)
                .Select(e => new { e.Product, e.Service, e.Environment, e.Version })
                .FirstOrDefaultAsync(ct);
            if (evt is null) throw new IncidentValidationException("'causedByDeployEventId' does not name a known deploy event.");
            if (!string.Equals(evt.Product, product, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(evt.Service, service, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(evt.Environment, environment, StringComparison.OrdinalIgnoreCase))
                throw new IncidentValidationException("'causedByDeployEventId' belongs to a different product, service or environment.");
            version ??= evt.Version;
        }

        return new Clean(product, service, environment, r.StartedAt, r.ResolvedAt, version, r.CausedByDeployEventId, source, externalId, url);
    }

    private static string Required(string? value, string field, int max)
    {
        var v = (value ?? "").Trim();
        if (v.Length == 0) throw new IncidentValidationException($"'{field}' is required.");
        if (v.Length > max) throw new IncidentValidationException($"'{field}' is longer than {max} characters.");
        return v;
    }

    private static string? Optional(string? value, string field, int max)
    {
        var v = (value ?? "").Trim();
        if (v.Length == 0) return null;
        if (v.Length > max) throw new IncidentValidationException($"'{field}' is longer than {max} characters.");
        return v;
    }

    private static void Apply(Incident row, Clean c)
    {
        row.Product = c.Product;
        row.Service = c.Service;
        row.Environment = c.Environment;
        row.StartedAt = c.StartedAt;
        row.ResolvedAt = c.ResolvedAt;
        row.Version = c.Version;
        row.CausedByDeployEventId = c.CausedByDeployEventId;
        row.Source = c.Source;
        row.ExternalId = c.ExternalId;
        row.Url = c.Url;
    }

    private string Actor => !string.IsNullOrEmpty(_user.Email) ? _user.Email : _user.Id;

    private static object Snapshot(Incident i) => new
    {
        i.Product, i.Service, i.Environment, i.StartedAt, i.ResolvedAt, i.Version, i.CausedByDeployEventId, i.Source, i.ExternalId, i.Url,
    };

    public static IncidentDto ToDto(Incident i) => new(
        i.Id, i.Product, i.Service, i.Environment, i.StartedAt, i.ResolvedAt,
        i.ResolvedAt is null ? null : Math.Round((i.ResolvedAt.Value - i.StartedAt).TotalHours, 2),
        i.Version, i.CausedByDeployEventId, i.Source, i.ExternalId, i.Url,
        i.CreatedBy, i.CreatedByName, i.CreatedAt, i.UpdatedBy, i.UpdatedAt);
}
