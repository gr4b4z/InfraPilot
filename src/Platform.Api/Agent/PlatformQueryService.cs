using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Platform.Api.Features.Deployments;
using Platform.Api.Features.Requests.Models;
using Platform.Api.Infrastructure.Persistence;

namespace Platform.Api.Agent;

/// <summary>
/// Read-only service that queries across ServiceRequests, ExecutionResults,
/// ApprovalRequests, ApprovalDecisions, and AuditLog for agent tool calls.
/// </summary>
public class PlatformQueryService
{
    private readonly PlatformDbContext _db;
    private readonly DeploymentService _deployments;

    public PlatformQueryService(PlatformDbContext db, DeploymentService deployments)
    {
        _db = db;
        _deployments = deployments;
    }

    public async Task<List<RequestCardItem>> QueryRequests(
        string? status = null,
        string? requester = null,
        string? catalogSlug = null,
        DateTimeOffset? from = null,
        DateTimeOffset? to = null,
        string? search = null,
        int limit = 20)
    {
        var query = _db.ServiceRequests
            .Include(r => r.CatalogItem)
            .AsNoTracking()
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(status))
        {
            if (Enum.TryParse<RequestStatus>(status, ignoreCase: true, out var parsed))
                query = query.Where(r => r.Status == parsed);
        }

        if (!string.IsNullOrWhiteSpace(requester))
            query = query.Where(r => r.RequesterName.Contains(requester));

        if (!string.IsNullOrWhiteSpace(catalogSlug))
            query = query.Where(r => r.CatalogItem != null && r.CatalogItem.Slug == catalogSlug);

        if (from.HasValue)
            query = query.Where(r => r.CreatedAt >= from.Value);

        if (to.HasValue)
            query = query.Where(r => r.CreatedAt <= to.Value);

        if (!string.IsNullOrWhiteSpace(search))
            query = query.Where(r =>
                r.RequesterName.Contains(search) ||
                (r.CatalogItem != null && r.CatalogItem.Name.Contains(search)));

        var results = await query
            .OrderByDescending(r => r.CreatedAt)
            .Take(limit)
            .Select(r => new RequestCardItem
            {
                Id = r.Id,
                ServiceName = r.CatalogItem != null ? r.CatalogItem.Name : "Unknown",
                Status = r.Status.ToString(),
                RequesterName = r.RequesterName,
                CreatedAt = r.CreatedAt,
                UpdatedAt = r.UpdatedAt,
            })
            .ToListAsync();

        return results;
    }

    public async Task<RequestDetailCardData?> GetRequestDetail(Guid requestId)
    {
        var r = await _db.ServiceRequests
            .Include(r => r.CatalogItem)
            .Include(r => r.ExecutionResults)
            .Include(r => r.ApprovalRequest)
                .ThenInclude(a => a!.Decisions)
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == requestId);

        if (r is null) return null;

        Dictionary<string, object?>? inputs = null;
        if (!string.IsNullOrWhiteSpace(r.InputsJson) && r.InputsJson != "{}")
        {
            try { inputs = JsonSerializer.Deserialize<Dictionary<string, object?>>(r.InputsJson); }
            catch { /* ignore parse errors */ }
        }

        var latestExec = r.ExecutionResults.OrderByDescending(e => e.StartedAt).FirstOrDefault();

        return new RequestDetailCardData
        {
            Id = r.Id,
            ServiceName = r.CatalogItem?.Name ?? "Unknown",
            Status = r.Status.ToString(),
            RequesterName = r.RequesterName,
            CreatedAt = r.CreatedAt,
            Inputs = inputs,
            ExecutionStatus = latestExec?.Status,
            ExecutionOutput = latestExec?.OutputJson,
            ApprovalStatus = r.ApprovalRequest?.Status,
            Decisions = r.ApprovalRequest?.Decisions
                .OrderBy(d => d.DecidedAt)
                .Select(d => new ApprovalDecisionDto
                {
                    ApproverName = d.ApproverName,
                    Decision = d.Decision,
                    Comment = d.Comment,
                    DecidedAt = d.DecidedAt,
                })
                .ToList(),
        };
    }

    public async Task<List<TimelineEventDto>> GetRequestTimeline(Guid requestId)
    {
        var entries = await _db.AuditLog
            .AsNoTracking()
            .Where(a => a.EntityType == "ServiceRequest" && a.EntityId == requestId)
            .OrderBy(a => a.Timestamp)
            .Take(50)
            .Select(a => new TimelineEventDto
            {
                Action = a.Action,
                ActorName = a.ActorName,
                ActorType = a.ActorType,
                Module = a.Module,
                Timestamp = a.Timestamp,
            })
            .ToListAsync();

        return entries;
    }

    public async Task<SummaryCardData> GetSummary(DateTimeOffset from, DateTimeOffset to)
    {
        var requests = await _db.ServiceRequests
            .Include(r => r.CatalogItem)
            .AsNoTracking()
            .Where(r => r.CreatedAt >= from && r.CreatedAt <= to)
            .OrderByDescending(r => r.CreatedAt)
            .Take(50)
            .ToListAsync();

        var items = requests.Select(r => new RequestCardItem
        {
            Id = r.Id,
            ServiceName = r.CatalogItem?.Name ?? "Unknown",
            Status = r.Status.ToString(),
            RequesterName = r.RequesterName,
            CreatedAt = r.CreatedAt,
            UpdatedAt = r.UpdatedAt,
        }).ToList();

        return new SummaryCardData
        {
            Total = requests.Count,
            Completed = requests.Count(r => r.Status == RequestStatus.Completed),
            Failed = requests.Count(r => r.Status == RequestStatus.Failed),
            AwaitingApproval = requests.Count(r => r.Status == RequestStatus.AwaitingApproval),
            Executing = requests.Count(r => r.Status == RequestStatus.Executing),
            Other = requests.Count(r => r.Status is not (RequestStatus.Completed or RequestStatus.Failed or RequestStatus.AwaitingApproval or RequestStatus.Executing)),
            From = from,
            To = to,
            Items = items,
        };
    }

    // ─── Deployment queries ───

    public async Task<DeploymentStateCardData> GetDeploymentState(string? product, string? service = null, CancellationToken ct = default)
    {
        var state = await _deployments.GetState(product, environment: null, serviceName: service, ct);

        var services = state.Select(s => s.Service).Distinct().OrderBy(s => s).ToList();
        var environments = state.Select(s => s.Environment).Distinct().OrderBy(e => e).ToList();

        var cells = state.Select(s => new DeploymentCellDto
        {
            Service = s.Service,
            Environment = s.Environment,
            Version = s.Version,
            PreviousVersion = s.PreviousVersion,
            DeployedAt = s.DeployedAt,
        }).ToList();

        return new DeploymentStateCardData
        {
            Product = string.IsNullOrWhiteSpace(product) ? null : product,
            Services = services,
            Environments = environments,
            Cells = cells,
        };
    }

    public async Task<DeploymentActivityCardData> GetRecentDeployments(
        string? product, string? environment, DateTimeOffset since,
        int limit = 50, string? service = null, CancellationToken ct = default)
    {
        var query = _db.DeployEvents
            .Where(e => e.DeployedAt >= since);

        if (!string.IsNullOrWhiteSpace(product))
            query = query.Where(e => e.Product == product);
        if (!string.IsNullOrWhiteSpace(environment))
            query = query.Where(e => e.Environment == environment);
        if (!string.IsNullOrWhiteSpace(service))
            query = query.Where(e => e.Service == service);

        var events = await query
            .OrderByDescending(e => e.DeployedAt)
            .Take(limit)
            .ToListAsync(ct);

        var items = events.Select(e =>
        {
            var refs = !string.IsNullOrEmpty(e.ReferencesJson)
                ? JsonSerializer.Deserialize<List<DeploymentRefDto>>(e.ReferencesJson, JsonOpts) ?? []
                : new List<DeploymentRefDto>();
            var participants = !string.IsNullOrEmpty(e.ParticipantsJson)
                ? JsonSerializer.Deserialize<List<DeploymentParticipantDto>>(e.ParticipantsJson, JsonOpts) ?? []
                : new List<DeploymentParticipantDto>();
            var enrichment = !string.IsNullOrEmpty(e.EnrichmentJson)
                ? JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(e.EnrichmentJson, JsonOpts)
                : null;

            string? workItemTitle = null;
            if (enrichment?.TryGetValue("labels", out var labels) == true)
            {
                if (labels.TryGetProperty("workItemTitle", out var title))
                    workItemTitle = title.GetString();
            }

            var workItemRef = refs.FirstOrDefault(r => r.Type == "work-item");
            var prRef = refs.FirstOrDefault(r => r.Type == "pull-request");

            return new DeploymentActivityItemDto
            {
                Service = e.Service,
                Environment = e.Environment,
                Version = e.Version,
                PreviousVersion = e.PreviousVersion,
                DeployedAt = e.DeployedAt,
                Source = e.Source,
                WorkItemKey = workItemRef?.Key,
                WorkItemTitle = workItemTitle,
                PrUrl = prRef?.Url,
                Participants = participants,
            };
        }).ToList();

        // Build URL params for navigation link
        var urlParams = new List<string> { "tab=activity" };
        if (!string.IsNullOrWhiteSpace(environment))
            urlParams.Add($"env={environment}");

        return new DeploymentActivityCardData
        {
            Product = product,
            Environment = environment,
            Since = since,
            Items = items,
            NavigationUrl = product != null
                ? $"/deployments/{product}?{string.Join("&", urlParams)}"
                : null,
        };
    }

    public async Task<List<string>> GetProducts(CancellationToken ct = default)
    {
        return await _db.DeployEvents
            .Select(e => e.Product)
            .Distinct()
            .OrderBy(p => p)
            .ToListAsync(ct);
    }

    public async Task<List<string>> GetServices(CancellationToken ct = default)
    {
        return await _db.DeployEvents
            .Select(e => e.Service)
            .Distinct()
            .OrderBy(s => s)
            .ToListAsync(ct);
    }

    public async Task<bool> ProductContainsService(string product, string service, CancellationToken ct = default)
    {
        return await _db.DeployEvents
            .AnyAsync(e => e.Product == product && e.Service == service, ct);
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };
}
