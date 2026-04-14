using Microsoft.EntityFrameworkCore;
using Platform.Api.Features.Requests;
using Platform.Api.Features.Requests.Models;
using Platform.Api.Infrastructure.Notifications;
using Platform.Api.Infrastructure.Persistence;

namespace Platform.Api.BackgroundServices;

public class EscalationTimerService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan EscalationThreshold = TimeSpan.FromHours(4);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<EscalationTimerService> _logger;

    public EscalationTimerService(
        IServiceScopeFactory scopeFactory,
        ILogger<EscalationTimerService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("EscalationTimerService started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessPendingApprovals(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing pending approvals in escalation timer");
            }

            await Task.Delay(Interval, stoppingToken);
        }

        _logger.LogInformation("EscalationTimerService stopped");
    }

    private async Task ProcessPendingApprovals(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var stateMachine = scope.ServiceProvider.GetRequiredService<RequestStateMachine>();
        var notificationService = scope.ServiceProvider.GetRequiredService<INotificationService>();

        var pendingApprovals = await db.ApprovalRequests
            .Include(a => a.ServiceRequest)
                .ThenInclude(sr => sr!.CatalogItem)
            .Where(a => a.Status == "Pending" && a.TimeoutAt != null)
            .ToListAsync(ct);

        var now = DateTimeOffset.UtcNow;

        foreach (var approval in pendingApprovals)
        {
            try
            {
                if (approval.TimeoutAt!.Value <= now)
                {
                    await HandleTimeout(db, stateMachine, notificationService, approval, ct);
                }
                else if (approval.TimeoutAt.Value - now < EscalationThreshold && !approval.Escalated)
                {
                    await HandleEscalation(db, notificationService, approval, ct);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Error processing approval {ApprovalId} for request {RequestId}",
                    approval.Id, approval.ServiceRequestId);
            }
        }
    }

    private static readonly RequestStatus[] TerminalStatuses =
    [
        RequestStatus.Completed, RequestStatus.Failed, RequestStatus.Rejected,
        RequestStatus.TimedOut, RequestStatus.ManuallyResolved, RequestStatus.Cancelled,
    ];

    private async Task HandleTimeout(
        PlatformDbContext db,
        RequestStateMachine stateMachine,
        INotificationService notificationService,
        Features.Approvals.Models.ApprovalRequest approval,
        CancellationToken ct)
    {
        var request = approval.ServiceRequest!;

        // The request may have been cancelled/completed while the approval was still
        // pending. Mark the approval as timed-out but skip the state machine transition.
        if (TerminalStatuses.Contains(request.Status))
        {
            _logger.LogInformation(
                "Skipping timeout for approval {ApprovalId} — request {RequestId} is already {Status}",
                approval.Id, request.Id, request.Status);
            approval.Status = "TimedOut";
            await db.SaveChangesAsync(ct);
            return;
        }

        _logger.LogWarning(
            "Approval {ApprovalId} timed out for request {RequestId}",
            approval.Id, request.Id);

        await stateMachine.TransitionTo(
            request, RequestStatus.TimedOut,
            actorId: "system",
            actorName: "EscalationTimerService",
            actorType: "system");

        approval.Status = "TimedOut";

        await db.SaveChangesAsync(ct);

        var serviceName = request.CatalogItem?.Name ?? "Unknown Service";

        await notificationService.SendStatusNotification(
            request.Id,
            serviceName,
            request.RequesterId,
            "TimedOut",
            "The approval request timed out without receiving the required approvals.");
    }

    private async Task HandleEscalation(
        PlatformDbContext db,
        INotificationService notificationService,
        Features.Approvals.Models.ApprovalRequest approval,
        CancellationToken ct)
    {
        var request = approval.ServiceRequest!;

        _logger.LogWarning(
            "Escalating approval {ApprovalId} for request {RequestId} to group {EscalationGroup}",
            approval.Id, request.Id, approval.EscalationGroup);

        approval.Escalated = true;

        await db.SaveChangesAsync(ct);

        if (!string.IsNullOrEmpty(approval.EscalationGroup))
        {
            var serviceName = request.CatalogItem?.Name ?? "Unknown Service";

            await notificationService.SendStatusNotification(
                request.Id,
                serviceName,
                approval.EscalationGroup,
                "EscalationWarning",
                $"Approval for '{serviceName}' (requested by {request.RequesterName}) is approaching its timeout and has been escalated.");
        }
    }
}
