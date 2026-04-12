namespace Platform.Api.Infrastructure.Notifications;

public class NotificationOptions
{
    public const string SectionName = "Notifications";
    public string PortalBaseUrl { get; set; } = "https://platform.softwareone.com";
    public ChannelsConfig Channels { get; set; } = new();
}

public class ChannelsConfig
{
    public EmailChannelConfig Email { get; set; } = new();
    public WebhookChannelConfig Webhook { get; set; } = new();
}

public class EmailChannelConfig
{
    public bool Enabled { get; set; }
    public string SmtpHost { get; set; } = "localhost";
    public int SmtpPort { get; set; } = 25;
    public string From { get; set; } = "noreply@platform.local";
    public bool UseSsl { get; set; }
}

public class WebhookChannelConfig
{
    public bool Enabled { get; set; }
    public string Url { get; set; } = "";
    public Dictionary<string, string> Headers { get; set; } = [];
}
