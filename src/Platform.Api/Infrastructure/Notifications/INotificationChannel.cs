namespace Platform.Api.Infrastructure.Notifications;

public interface INotificationChannel
{
    bool IsEnabled { get; }
    Task SendAsync(NotificationMessage message, CancellationToken ct = default);
}

public class NotificationMessage
{
    public required string Subject { get; init; }
    public required string BodyHtml { get; init; }
    public string? BodyText { get; init; }
    public IReadOnlyList<string> Recipients { get; init; } = [];
    public object? WebhookPayload { get; init; }
    public string? ActionUrl { get; init; }
}
