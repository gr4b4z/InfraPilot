using Microsoft.EntityFrameworkCore;
using Platform.Api.Features.Approvals.Models;
using Platform.Api.Features.Promotions;
using Platform.Api.Features.Requests;
using Platform.Api.Features.Requests.Models;
using Platform.Api.Infrastructure.Auth;
using Platform.Api.Infrastructure.Audit;
using Platform.Api.Features.Webhooks;
using Platform.Api.Infrastructure.Notifications;
using Platform.Api.Infrastructure.Persistence;
using Platform.Api.Infrastructure.Realtime;

namespace Platform.Api.Features.Approvals;

public class ApprovalService
{
    private readonly PlatformDbContext _db;
    private readonly RequestStateMachine _stateMachine;
    private readonly IAuditLogger _auditLogger;
    private readonly ICurrentUser _currentUser;
    private readonly IPlatformEventPublisher _eventPublisher;
    private readonly INotificationService _notificationService;
    private readonly IWebhookDispatcher _webhookDispatcher;
    private readonly PromotionApprovalAuthorizer _authorizer;

    public ApprovalService(
        PlatformDbContext db,
        RequestStateMachine stateMachine,
        IAuditLogger auditLogger,
        ICurrentUser currentUser,
        IPlatformEventPublisher eventPublisher,
        INotificationService notificationService,
        IWebhookDispatcher webhookDispatcher,
        PromotionApprovalAuthorizer authorizer)
    {
        _db = db;
        _stateMachine = stateMachine;
        _auditLogger = auditLogger;
        _currentUser = currentUser;
        _eventPublisher = eventPublisher;
        _notificationService = notificationService;
        _webhookDispatcher = webhookDispatcher;
        _authorizer = authorizer;
    }

    /// <summary>
    /// Whether the current user may decide on <paramref name="approval"/>: not the person who raised
    /// the request, and, when the request has an approver group, a member of it (admins count as
    /// members of every group, as for promotions). The group comes from the approval outcome stored
    /// on submit; requests submitted before that existed fall back to the item's top-level group.
    /// Needs <c>ServiceRequest</c> loaded; loads its <c>CatalogItem</c> for those older requests.
    /// </summary>
    public async Task<DecisionCheck> CheckCanDecide(ApprovalRequest approval, CancellationToken ct = default)
    {
        if (approval.Status != "Pending")
            return new DecisionCheck(false, "This approval is no longer pending.");

        if (approval.Decisions.Any(d => d.ApproverId == _currentUser.Id))
            return new DecisionCheck(false, "You have already made a decision on this approval.");

        var request = approval.ServiceRequest;
        if (request is null)
            return new DecisionCheck(false, "The request for this approval can't be found.");

        if (IsRequester(request))
            return new DecisionCheck(false, "You raised this request, so someone else has to decide on it.");

        var outcome = request.ApprovalOutcome;
        if (outcome is null && request.CatalogItem is null)
            await _db.Entry(request).Reference(r => r.CatalogItem).LoadAsync(ct);
        var group = outcome is not null
            ? outcome.ApproverGroup
            : request.CatalogItem?.Approval?.ApproverGroup;
        if (!string.IsNullOrEmpty(group) && !await _authorizer.IsInApproverGroupAsync(group, ct))
            return new DecisionCheck(false, "Only members of this request's approver group can decide on it.");

        return new DecisionCheck(true, null);
    }

    private bool IsRequester(ServiceRequest request)
    {
        // "anonymous" is the no-auth placeholder id, not a person.
        if (!string.IsNullOrEmpty(request.RequesterId) && request.RequesterId != "anonymous"
            && string.Equals(request.RequesterId, _currentUser.Id, StringComparison.OrdinalIgnoreCase))
            return true;

        return !string.IsNullOrEmpty(request.RequesterEmail) && !string.IsNullOrEmpty(_currentUser.Email)
            && string.Equals(request.RequesterEmail, _currentUser.Email, StringComparison.OrdinalIgnoreCase);
    }

    public async Task<ApprovalRequest> CreateApproval(ServiceRequest request, ApprovalStrategy strategy, int? quorumCount, string? approverGroup, int? timeoutHours, string? escalationGroup)
    {
        var approval = new ApprovalRequest
        {
            Id = Guid.NewGuid(),
            ServiceRequestId = request.Id,
            Strategy = strategy,
            QuorumCount = quorumCount,
            Status = "Pending",
            TimeoutAt = timeoutHours.HasValue ? DateTimeOffset.UtcNow.AddHours(timeoutHours.Value) : null,
            EscalationGroup = escalationGroup,
        };

        _db.ApprovalRequests.Add(approval);
        await _db.SaveChangesAsync();

        await _auditLogger.Log("approvals", "approval.created", "system", "System", "system",
            "ApprovalRequest", approval.Id, null, new { strategy, quorumCount, approverGroup });

        await _webhookDispatcher.DispatchAsync("approval.created", new
        {
            approval.Id,
            approval.ServiceRequestId,
            Strategy = strategy.ToString(),
            approval.QuorumCount,
            ApproverGroup = approverGroup,
        });

        return approval;
    }

    public async Task<List<ApprovalRequest>> GetPending(string approverId)
    {
        return await _db.ApprovalRequests
            .Include(a => a.ServiceRequest)
                .ThenInclude(r => r!.CatalogItem)
            .Include(a => a.Decisions)
            .Where(a => a.Status == "Pending")
            .OrderBy(a => a.TimeoutAt)
            .ToListAsync();
    }

    public async Task<List<ApprovalRequest>> GetAll()
    {
        return await _db.ApprovalRequests
            .Include(a => a.ServiceRequest)
                .ThenInclude(r => r!.CatalogItem)
            .Include(a => a.Decisions)
            .OrderByDescending(a => a.CreatedAt)
            .ToListAsync();
    }

    public async Task<ApprovalRequest?> GetById(Guid id)
    {
        return await _db.ApprovalRequests
            .Include(a => a.ServiceRequest)
                .ThenInclude(r => r!.CatalogItem)
            .Include(a => a.Decisions)
            .FirstOrDefaultAsync(a => a.Id == id);
    }

    public async Task RecordDecision(Guid approvalId, string decision, string? comment)
    {
        var approval = await _db.ApprovalRequests
            .Include(a => a.Decisions)
            .Include(a => a.ServiceRequest)
            .FirstOrDefaultAsync(a => a.Id == approvalId)
            ?? throw new KeyNotFoundException($"Approval {approvalId} not found");

        if (approval.Status != "Pending")
            throw new InvalidOperationException("Approval is no longer pending");

        if (approval.Decisions.Any(d => d.ApproverId == _currentUser.Id))
            throw new InvalidOperationException("You have already made a decision on this approval");

        var check = await CheckCanDecide(approval);
        if (!check.Allowed)
            throw new UnauthorizedAccessException(check.Reason);

        var approvalDecision = new ApprovalDecision
        {
            Id = Guid.NewGuid(),
            ApprovalRequestId = approvalId,
            ApproverId = _currentUser.Id,
            ApproverName = _currentUser.Name,
            Decision = decision,
            Comment = comment,
        };

        _db.ApprovalDecisions.Add(approvalDecision);

        await _auditLogger.Log("approvals", $"approval.{decision.ToLowerInvariant()}", _currentUser.Id, _currentUser.Name, "user",
            "ApprovalRequest", approvalId, null, new { decision, comment });

        // Check if strategy condition is met
        await CheckStrategyCompletion(approval, decision);
        await _db.SaveChangesAsync();

        // Publish SSE event
        var serviceName = approval.ServiceRequest?.CatalogItem?.Name ?? "Request";
        await _eventPublisher.PublishApprovalDecision(
            approval.ServiceRequestId, serviceName, decision, _currentUser.Name, comment);

        await _webhookDispatcher.DispatchAsync($"approval.{decision.ToLowerInvariant()}", new
        {
            ApprovalId = approvalId,
            approval.ServiceRequestId,
            Decision = decision,
            DecidedBy = _currentUser.Name,
            Comment = comment,
            approval.Status,
        });

        // Notify requester when approval is resolved
        if (approval.Status != "Pending")
        {
            var requesterEmail = approval.ServiceRequest?.RequesterEmail;
            if (!string.IsNullOrEmpty(requesterEmail))
            {
                var commentSuffix = string.IsNullOrEmpty(comment) ? "" : $" Comment: {comment}";
                await _notificationService.SendStatusNotification(
                    approval.ServiceRequestId,
                    serviceName,
                    requesterEmail,
                    approval.Status,
                    $"Your request for {serviceName} has been {approval.Status.ToLowerInvariant()} by {_currentUser.Name}.{commentSuffix}");
            }
        }
    }

    private async Task CheckStrategyCompletion(ApprovalRequest approval, string latestDecision)
    {
        var decisions = approval.Decisions;
        var approveCount = decisions.Count(d => d.Decision == "Approved") + (latestDecision == "Approved" ? 1 : 0);
        var rejectCount = decisions.Count(d => d.Decision == "Rejected") + (latestDecision == "Rejected" ? 1 : 0);

        var request = approval.ServiceRequest!;

        switch (approval.Strategy)
        {
            case ApprovalStrategy.Any:
                if (latestDecision == "Approved")
                {
                    approval.Status = "Approved";
                    await _stateMachine.TransitionTo(request, RequestStatus.Executing, "system", "System", "system");
                }
                else if (latestDecision == "Rejected")
                {
                    approval.Status = "Rejected";
                    await _stateMachine.TransitionTo(request, RequestStatus.Rejected, "system", "System", "system");
                }
                else if (latestDecision == "ChangesRequested")
                {
                    approval.Status = "ChangesRequested";
                    await _stateMachine.TransitionTo(request, RequestStatus.ChangesRequested, "system", "System", "system");
                }
                break;

            case ApprovalStrategy.All:
                if (latestDecision == "Rejected")
                {
                    approval.Status = "Rejected";
                    await _stateMachine.TransitionTo(request, RequestStatus.Rejected, "system", "System", "system");
                }
                // For All, we'd need to know total approvers — simplified here
                break;

            case ApprovalStrategy.Quorum:
                if (approval.QuorumCount.HasValue && approveCount >= approval.QuorumCount.Value)
                {
                    approval.Status = "Approved";
                    await _stateMachine.TransitionTo(request, RequestStatus.Executing, "system", "System", "system");
                }
                break;
        }
    }
}

/// <summary>Whether the current user may decide on an approval, and if not, why (shown in the portal).</summary>
public record DecisionCheck(bool Allowed, string? Reason);
