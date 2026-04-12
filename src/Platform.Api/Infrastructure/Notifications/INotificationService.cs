namespace Platform.Api.Infrastructure.Notifications;

public interface INotificationService
{
    Task SendApprovalNotification(Guid approvalRequestId, string serviceName, string requesterName,
        string summary, IReadOnlyList<string> approverEmails);
    Task SendStatusNotification(Guid requestId, string serviceName, string requesterEmail,
        string status, string? message = null);
}
