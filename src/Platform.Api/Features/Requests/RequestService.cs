using Microsoft.EntityFrameworkCore;
using Platform.Api.Features.Approvals;
using Platform.Api.Features.Approvals.Models;
using Platform.Api.Features.Catalog;
using Platform.Api.Features.Requests.Models;
using Platform.Api.Infrastructure.Auth;
using Platform.Api.Infrastructure.Notifications;
using Platform.Api.Infrastructure.Persistence;

namespace Platform.Api.Features.Requests;

public class RequestService
{
    private readonly PlatformDbContext _db;
    private readonly RequestStateMachine _stateMachine;
    private readonly ICurrentUser _currentUser;
    private readonly CatalogYamlLoader _yamlLoader;
    private readonly ApprovalService _approvalService;
    private readonly ApproverResolver _approverResolver;
    private readonly INotificationService _notificationService;

    public RequestService(
        PlatformDbContext db,
        RequestStateMachine stateMachine,
        ICurrentUser currentUser,
        CatalogYamlLoader yamlLoader,
        ApprovalService approvalService,
        ApproverResolver approverResolver,
        INotificationService notificationService)
    {
        _db = db;
        _stateMachine = stateMachine;
        _currentUser = currentUser;
        _yamlLoader = yamlLoader;
        _approvalService = approvalService;
        _approverResolver = approverResolver;
        _notificationService = notificationService;
    }

    public async Task<ServiceRequest> Create(Guid catalogItemId, Dictionary<string, object> inputs)
    {
        var request = new ServiceRequest
        {
            Id = Guid.NewGuid(),
            CorrelationId = Guid.NewGuid(),
            CatalogItemId = catalogItemId,
            RequesterId = _currentUser.Id,
            RequesterName = _currentUser.Name,
            RequesterEmail = _currentUser.Email,
            Status = RequestStatus.Draft,
            InputsJson = System.Text.Json.JsonSerializer.Serialize(inputs),
        };

        _db.ServiceRequests.Add(request);
        await _db.SaveChangesAsync();

        return request;
    }

    public async Task<List<ServiceRequest>> GetByRequester(string requesterId, string? status = null)
    {
        var query = _db.ServiceRequests
            .Include(r => r.CatalogItem)
            .AsQueryable();

        // "anonymous" means no auth — return all in dev
        if (requesterId != "anonymous")
            query = query.Where(r => r.RequesterId == requesterId);

        if (!string.IsNullOrWhiteSpace(status) && Enum.TryParse<RequestStatus>(status, out var s))
            query = query.Where(r => r.Status == s);

        return await query.OrderByDescending(r => r.UpdatedAt).ToListAsync();
    }

    public async Task<List<ServiceRequest>> GetAll(string? status = null)
    {
        var query = _db.ServiceRequests
            .Include(r => r.CatalogItem)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(status) && Enum.TryParse<RequestStatus>(status, out var s))
            query = query.Where(r => r.Status == s);

        return await query.OrderByDescending(r => r.UpdatedAt).ToListAsync();
    }

    public async Task<ServiceRequest?> GetById(Guid id)
    {
        return await _db.ServiceRequests
            .Include(r => r.CatalogItem)
            .Include(r => r.ExecutionResults)
            .Include(r => r.ApprovalRequest)
                .ThenInclude(a => a!.Decisions)
            .FirstOrDefaultAsync(r => r.Id == id);
    }

    public async Task Submit(Guid requestId)
    {
        var request = await _db.ServiceRequests
            .Include(r => r.CatalogItem)
            .FirstOrDefaultAsync(r => r.Id == requestId)
            ?? throw new KeyNotFoundException($"Request {requestId} not found");

        await _stateMachine.TransitionTo(request, RequestStatus.Validating,
            _currentUser.Id, _currentUser.Name, "user");

        // Auto-transition: check if approval is required
        var definition = request.CatalogItem != null
            ? _yamlLoader.LoadAll().FirstOrDefault(d => d.Id == request.CatalogItem.Slug)
            : null;

        var needsApproval = definition?.Approval?.Required == true;

        if (needsApproval)
        {
            await _stateMachine.TransitionTo(request, RequestStatus.AwaitingApproval,
                "system", "System", "system");

            var approval = definition!.Approval!;
            var strategy = Enum.TryParse<ApprovalStrategy>(approval.Strategy, ignoreCase: true, out var s)
                ? s : ApprovalStrategy.Any;

            var approvalRequest = await _approvalService.CreateApproval(
                request, strategy, approval.QuorumCount,
                approval.ApproverGroup, approval.TimeoutHours, approval.EscalationGroup);

            if (!string.IsNullOrEmpty(approval.ApproverGroup))
            {
                var approvers = await _approverResolver.ResolveApprovers(approval.ApproverGroup);
                var emails = approvers.Select(a => a.Email).Where(e => !string.IsNullOrEmpty(e)).ToList();

                if (emails.Count > 0)
                {
                    await _notificationService.SendApprovalNotification(
                        approvalRequest.Id,
                        definition.Name,
                        _currentUser.Name,
                        $"Request for {definition.Name}",
                        emails);
                }
            }
        }
        else
        {
            await _stateMachine.TransitionTo(request, RequestStatus.Executing,
                "system", "System", "system");
        }

        await _db.SaveChangesAsync();
    }

    public async Task Cancel(Guid requestId)
    {
        var request = await _db.ServiceRequests.FindAsync(requestId)
            ?? throw new KeyNotFoundException($"Request {requestId} not found");

        await _stateMachine.TransitionTo(request, RequestStatus.Cancelled,
            _currentUser.Id, _currentUser.Name, "user");
        await _db.SaveChangesAsync();
    }
}
