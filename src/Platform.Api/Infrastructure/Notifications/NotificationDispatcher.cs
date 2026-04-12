using System.Net;
using Microsoft.Extensions.Options;

namespace Platform.Api.Infrastructure.Notifications;

public class NotificationDispatcher : INotificationService
{
    private readonly IEnumerable<INotificationChannel> _channels;
    private readonly NotificationOptions _options;
    private readonly ILogger<NotificationDispatcher> _logger;

    public NotificationDispatcher(
        IEnumerable<INotificationChannel> channels,
        IOptions<NotificationOptions> options,
        ILogger<NotificationDispatcher> logger)
    {
        _channels = channels;
        _options = options.Value;
        _logger = logger;
    }

    public async Task SendApprovalNotification(
        Guid approvalRequestId,
        string serviceName,
        string requesterName,
        string summary,
        IReadOnlyList<string> approverEmails)
    {
        var actionUrl = $"{_options.PortalBaseUrl.TrimEnd('/')}/approvals/{approvalRequestId}";

        var message = new NotificationMessage
        {
            Subject = $"Approval Required: {serviceName} — requested by {requesterName}",
            BodyHtml = $"""
                <html>
                <body style="font-family: sans-serif; color: #333;">
                    <h2>Approval Request</h2>
                    <p><strong>Service:</strong> {WebUtility.HtmlEncode(serviceName)}</p>
                    <p><strong>Requested by:</strong> {WebUtility.HtmlEncode(requesterName)}</p>
                    <p><strong>Summary:</strong> {WebUtility.HtmlEncode(summary)}</p>
                    <hr />
                    <p>Please <a href="{actionUrl}">review and approve or reject</a> this request in the platform portal.</p>
                </body>
                </html>
                """,
            BodyText = $"Approval required for {serviceName}, requested by {requesterName}. {summary}",
            Recipients = approverEmails,
            ActionUrl = actionUrl,
            WebhookPayload = new
            {
                type = "approval_request",
                approvalRequestId,
                serviceName,
                requesterName,
                summary,
                approverEmails,
                actionUrl,
                timestamp = DateTimeOffset.UtcNow,
            },
        };

        await DispatchToChannels(message);
    }

    public async Task SendStatusNotification(
        Guid requestId,
        string serviceName,
        string requesterEmail,
        string status,
        string? detailMessage = null)
    {
        var actionUrl = $"{_options.PortalBaseUrl.TrimEnd('/')}/requests/{requestId}";
        var messageHtml = detailMessage is not null
            ? $"<p><strong>Details:</strong> {WebUtility.HtmlEncode(detailMessage)}</p>"
            : "";

        var message = new NotificationMessage
        {
            Subject = $"Request Update: {serviceName} — {status}",
            BodyHtml = $"""
                <html>
                <body style="font-family: sans-serif; color: #333;">
                    <h2>Request Status Update</h2>
                    <p><strong>Service:</strong> {WebUtility.HtmlEncode(serviceName)}</p>
                    <p><strong>Status:</strong> {WebUtility.HtmlEncode(status)}</p>
                    {messageHtml}
                    <hr />
                    <p>You can <a href="{actionUrl}">view the full details</a> in the platform portal.</p>
                </body>
                </html>
                """,
            BodyText = $"Request {serviceName} status: {status}. {detailMessage ?? ""}".Trim(),
            Recipients = [requesterEmail],
            ActionUrl = actionUrl,
            WebhookPayload = new
            {
                type = "status_update",
                requestId,
                serviceName,
                requesterEmail,
                status,
                message = detailMessage,
                actionUrl,
                timestamp = DateTimeOffset.UtcNow,
            },
        };

        await DispatchToChannels(message);
    }

    private async Task DispatchToChannels(NotificationMessage message)
    {
        foreach (var channel in _channels)
        {
            if (!channel.IsEnabled) continue;

            try
            {
                await channel.SendAsync(message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Notification channel {Channel} failed for: {Subject}",
                    channel.GetType().Name, message.Subject);
            }
        }
    }
}
