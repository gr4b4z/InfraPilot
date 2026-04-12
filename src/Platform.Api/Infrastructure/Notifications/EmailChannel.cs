using System.Net.Mail;
using Microsoft.Extensions.Options;

namespace Platform.Api.Infrastructure.Notifications;

public class EmailChannel : INotificationChannel
{
    private readonly EmailChannelConfig _config;
    private readonly ILogger<EmailChannel> _logger;

    public EmailChannel(IOptions<NotificationOptions> options, ILogger<EmailChannel> logger)
    {
        _config = options.Value.Channels.Email;
        _logger = logger;
    }

    public bool IsEnabled => _config.Enabled;

    public async Task SendAsync(NotificationMessage message, CancellationToken ct = default)
    {
        if (!IsEnabled || message.Recipients.Count == 0) return;

        foreach (var recipient in message.Recipients)
        {
            try
            {
                using var client = new SmtpClient(_config.SmtpHost, _config.SmtpPort)
                {
                    EnableSsl = _config.UseSsl,
                };

                using var mail = new MailMessage(_config.From, recipient, message.Subject, message.BodyHtml)
                {
                    IsBodyHtml = true,
                };

                await client.SendMailAsync(mail, ct);
                _logger.LogInformation("Email sent to {Recipient}: {Subject}", recipient, message.Subject);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send email to {Recipient}: {Subject}", recipient, message.Subject);
            }
        }
    }
}
