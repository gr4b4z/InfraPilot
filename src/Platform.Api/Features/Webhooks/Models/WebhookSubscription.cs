namespace Platform.Api.Features.Webhooks.Models;

/// <summary>
/// How a delivery is framed on the wire. The queue, filtering, retry and history are shared —
/// only the request body and headers differ per target.
/// </summary>
public static class WebhookTargetTypes
{
    /// <summary>Signed JSON POST of the InfraPilot envelope (HMAC-SHA256, X-Hub-Signature-256).</summary>
    public const string Generic = "generic";
    /// <summary>Azure DevOps Incoming WebHook service connection (HMAC-SHA1 in a configurable header).</summary>
    public const string AzureDevOps = "azure_devops";
    /// <summary>GitHub <c>repository_dispatch</c> REST call (bearer token, no signature).</summary>
    public const string GitHub = "github";
    /// <summary>Microsoft Teams channel post — an Adaptive Card (or legacy MessageCard) built from the template.</summary>
    public const string MicrosoftTeams = "msteams";
    /// <summary>Discord channel post — plain content or an embed built from the template.</summary>
    public const string Discord = "discord";

    public static readonly string[] All = [Generic, AzureDevOps, GitHub, MicrosoftTeams, Discord];

    /// <summary>
    /// Targets that post a human-readable message into a chat channel rather than handing a machine
    /// the event envelope. They share a distinct set of rules: the URL is itself the credential (so
    /// there is no separate secret), and the body is rendered from a message template rather than
    /// being the envelope. Everything else — event filtering, retries, delivery history — is shared.
    /// </summary>
    public static readonly string[] Messaging = [MicrosoftTeams, Discord];

    public static bool IsValid(string? value) => value is not null && All.Contains(value);

    public static bool IsMessaging(string? value) => value is not null && Messaging.Contains(value);
}

public class WebhookSubscription
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
    /// <summary>
    /// Secret encrypted via Data Protection API. Used as the HMAC key for the generic and Azure
    /// DevOps targets, and as the bearer token for the GitHub target. Empty for messaging targets,
    /// whose URL already carries the capability to post — there is nothing else to authenticate with.
    /// </summary>
    public string EncryptedSecret { get; set; } = "";
    /// <summary>Event types this subscription listens to (JSON array stored as text).</summary>
    public string EventsJson { get; set; } = "[]";
    /// <summary>Optional product filter for deployment events.</summary>
    public string? FilterProduct { get; set; }
    /// <summary>Optional environment filter for deployment events.</summary>
    public string? FilterEnvironment { get; set; }
    /// <summary>One of <see cref="WebhookTargetTypes"/>. Immutable after creation.</summary>
    public string TargetType { get; set; } = WebhookTargetTypes.Generic;
    /// <summary>
    /// Azure DevOps only: the header carrying the HMAC-SHA1 checksum, matching the "Http Header"
    /// field of the Incoming WebHook service connection. Null falls back to X-Hub-Signature.
    /// </summary>
    public string? SignatureHeader { get; set; }
    /// <summary>
    /// GitHub only: overrides the <c>event_type</c> sent to <c>repository_dispatch</c>. Null sends
    /// the InfraPilot event type verbatim, so a workflow can filter on e.g. deployment.created.
    /// </summary>
    public string? GitHubEventType { get; set; }
    /// <summary>
    /// Messaging targets only: Handlebars template rendered against the delivery envelope to produce
    /// the message body. Null or blank falls back to <see cref="NotificationTemplates"/>, which knows
    /// a sensible default per event — release notes already arrive rendered, so their default simply
    /// forwards <c>data.renderedContent</c>.
    /// </summary>
    public string? MessageTemplate { get; set; }
    /// <summary>
    /// Messaging targets only: optional Handlebars template for the card / embed heading. Null falls
    /// back to the per-event default title.
    /// </summary>
    public string? MessageTitle { get; set; }
    public bool Active { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public List<WebhookDelivery> Deliveries { get; set; } = [];
}
