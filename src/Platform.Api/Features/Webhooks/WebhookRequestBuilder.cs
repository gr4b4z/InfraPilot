using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Platform.Api.Features.ReleaseNotes;
using Platform.Api.Features.Webhooks.Models;

namespace Platform.Api.Features.Webhooks;

/// <summary>
/// Frames a queued delivery for the wire. Pure — no DB, no HTTP, no Data Protection, no template
/// compilation: it takes the already-decrypted secret and the already-rendered message and returns
/// the exact body and headers the worker should send, so the per-target request shapes are
/// unit-testable without any plumbing.
/// </summary>
public static class WebhookRequestBuilder
{
    /// <summary>Default Azure DevOps checksum header. The ADO docs cite GitHub's header as the example.</summary>
    public const string DefaultAzureDevOpsSignatureHeader = "X-Hub-Signature";

    /// <summary>GitHub rejects requests without a User-Agent, so this is not decoration.</summary>
    public const string GitHubUserAgent = "InfraPilot";

    private const string GitHubApiVersion = "2022-11-28";

    /// <summary>
    /// Adaptive Card schema version Teams renders reliably. Newer versions degrade silently on older
    /// clients, and nothing here needs what they add.
    /// </summary>
    private const string AdaptiveCardVersion = "1.4";

    /// <summary>
    /// A Teams card tops out around 28 KB in total, and a rendered release note has no natural
    /// ceiling — a note wide enough to blow the limit would otherwise fail delivery outright rather
    /// than arriving trimmed.
    /// </summary>
    private const int TeamsTextLimit = 20000;

    /// <summary>Discord's documented caps: 2000 for plain content, 4096 for an embed description, 256 for its title.</summary>
    private const int DiscordContentLimit = 2000;
    private const int DiscordDescriptionLimit = 4096;
    private const int DiscordTitleLimit = 256;

    /// <summary>
    /// Applied to Discord embeds and Teams MessageCards so notifications read as one source in a busy
    /// channel. Discord wants the integer, MessageCard wants the hex string.
    /// </summary>
    private const int AccentColor = 0x3B82F6;

    /// <summary>
    /// Hosts belonging to the retired Office 365 connector, which speaks MessageCard rather than the
    /// <c>type: message</c> Adaptive Card envelope a Power Automate Workflows URL expects. Existing
    /// connector URLs still work until Microsoft finishes switching them off, so the shape is chosen
    /// from the URL instead of asking the operator which vintage of Teams webhook they pasted.
    /// </summary>
    private static readonly string[] LegacyTeamsConnectorHosts =
        ["webhook.office.com", "outlook.office.com", "outlook.office365.com"];

    /// <param name="ContentType">
    /// Almost every target speaks JSON, so that is the default — spelled with the charset the worker
    /// used to append itself, so introducing this field changed nothing on the wire for the targets
    /// that predate it. The Teams HTML target is the exception: its body is an HTML fragment rather
    /// than a document describing one.
    /// </param>
    public sealed record WebhookHttpRequest(
        string Body,
        IReadOnlyList<(string Name, string Value)> Headers,
        string ContentType = "application/json; charset=utf-8");

    /// <param name="message">
    /// The rendered notification text. Required for messaging targets, which have no envelope to send
    /// — their body <em>is</em> the message — and ignored by every other target.
    /// </param>
    public static WebhookHttpRequest Build(
        WebhookSubscription sub,
        WebhookDelivery delivery,
        string secret,
        MessageTemplateRenderer.RenderedMessage? message = null)
        => sub.TargetType switch
        {
            WebhookTargetTypes.AzureDevOps => BuildAzureDevOps(sub, delivery, secret),
            WebhookTargetTypes.GitHub => BuildGitHub(sub, delivery, secret),
            WebhookTargetTypes.MicrosoftTeams => BuildMicrosoftTeams(sub, delivery, RequireMessage(sub, message)),
            WebhookTargetTypes.MicrosoftTeamsHtml => BuildMicrosoftTeamsHtml(delivery, RequireMessage(sub, message)),
            WebhookTargetTypes.Discord => BuildDiscord(delivery, RequireMessage(sub, message)),
            _ => BuildGeneric(delivery, secret),
        };

    private static MessageTemplateRenderer.RenderedMessage RequireMessage(
        WebhookSubscription sub, MessageTemplateRenderer.RenderedMessage? message)
        => message ?? throw new ArgumentException(
            $"Target '{sub.TargetType}' posts a rendered message and cannot be framed without one",
            nameof(message));

    /// <summary>
    /// The original shape, kept byte-for-byte: the stored envelope, signed with HMAC-SHA256.
    /// Any change here breaks every subscription created before target types existed.
    /// </summary>
    private static WebhookHttpRequest BuildGeneric(WebhookDelivery delivery, string secret)
    {
        var signature = ComputeHmacHex(HmacAlgorithm.Sha256, delivery.PayloadJson, secret);
        return new WebhookHttpRequest(delivery.PayloadJson,
        [
            ("X-Hub-Signature-256", $"sha256={signature}"),
            ("X-Webhook-Event", delivery.EventType),
            ("X-Webhook-Delivery", delivery.Id.ToString()),
            ("Accept", "application/json"),
        ]);
    }

    /// <summary>
    /// Azure DevOps Incoming WebHook service connection. Azure Pipelines recomputes an HMAC-SHA1 of
    /// the request body using the connection's secret and compares it to the configured header, so
    /// the digest must cover the exact bytes sent — the envelope is already minified, and whitespace
    /// or a trailing newline would fail validation.
    /// </summary>
    private static WebhookHttpRequest BuildAzureDevOps(
        WebhookSubscription sub, WebhookDelivery delivery, string secret)
    {
        var header = string.IsNullOrWhiteSpace(sub.SignatureHeader)
            ? DefaultAzureDevOpsSignatureHeader
            : sub.SignatureHeader;
        var signature = ComputeHmacHex(HmacAlgorithm.Sha1, delivery.PayloadJson, secret);

        return new WebhookHttpRequest(delivery.PayloadJson,
        [
            (header, $"sha1={signature}"),
            // Not read by Azure Pipelines, but they make the receiver's own logs traceable.
            ("X-Webhook-Event", delivery.EventType),
            ("X-Webhook-Delivery", delivery.Id.ToString()),
            ("Accept", "application/json"),
        ]);
    }

    /// <summary>
    /// GitHub has no inbound webhook receiver — the way in is the repository_dispatch REST call,
    /// which authenticates with a token rather than a signature and demands its own body shape.
    /// The whole envelope rides along as client_payload so workflows keep the delivery id.
    /// </summary>
    private static WebhookHttpRequest BuildGitHub(
        WebhookSubscription sub, WebhookDelivery delivery, string secret)
    {
        var eventType = string.IsNullOrWhiteSpace(sub.GitHubEventType)
            ? delivery.EventType
            : sub.GitHubEventType;

        var body = new JsonObject
        {
            ["event_type"] = eventType,
            // Parsed rather than re-serialized so the payload reaches the workflow unchanged.
            ["client_payload"] = JsonNode.Parse(delivery.PayloadJson),
        };

        return new WebhookHttpRequest(body.ToJsonString(),
        [
            ("Authorization", $"Bearer {secret}"),
            ("Accept", "application/vnd.github+json"),
            ("X-GitHub-Api-Version", GitHubApiVersion),
            ("User-Agent", GitHubUserAgent),
        ]);
    }

    // ── Chat notifications ──────────────────────────────────────────────────
    // These targets are the reason a message template exists: the receiver is a channel full of
    // people, not a system, so what goes on the wire is prose rather than the event envelope. No
    // signature and no token — the URL is the capability, which is why creating one demands https.

    /// <summary>
    /// Headers common to every chat target. Neither platform reads the two X- headers, but the relay
    /// in front of them does: a Power Automate flow can dedupe on the delivery id, and a run history
    /// that shows which delivery each run came from is the difference between diagnosing a duplicate
    /// post and guessing at it. Chat platforms have no idempotency of their own, so if a flow retries
    /// or is wired up twice, this id is the only thing tying the copies back to one send.
    /// </summary>
    private static (string Name, string Value)[] MessagingHeaders(WebhookDelivery delivery) =>
    [
        ("Accept", "application/json"),
        ("X-Webhook-Event", delivery.EventType),
        ("X-Webhook-Delivery", delivery.Id.ToString()),
    ];

    /// <summary>
    /// Microsoft Teams. A Power Automate Workflows URL expects a <c>type: message</c> envelope
    /// carrying an Adaptive Card; a legacy Office 365 connector URL expects a MessageCard. Both are
    /// emitted from the same rendered message so the operator's template does not depend on which
    /// kind of Teams webhook they were given.
    /// </summary>
    private static WebhookHttpRequest BuildMicrosoftTeams(
        WebhookSubscription sub, WebhookDelivery delivery, MessageTemplateRenderer.RenderedMessage message)
    {
        var title = Truncate(message.Title, TeamsTextLimit);
        var text = Truncate(message.Text, TeamsTextLimit);

        var body = IsLegacyTeamsConnector(sub.Url)
            ? LegacyTeamsMessageCard(title, text)
            : TeamsAdaptiveCard(title, text);

        return new WebhookHttpRequest(body.ToJsonString(), MessagingHeaders(delivery));
    }

    private static JsonObject TeamsAdaptiveCard(string title, string text)
    {
        // Adaptive Card text blocks render a markdown subset — bold, italics, links and bullet lists
        // survive; tables and headings do not. Nothing is stripped here: dropping syntax Teams
        // ignores would also drop it from the words a reader sees.
        var blocks = new JsonArray();
        if (title.Length > 0)
        {
            blocks.Add(new JsonObject
            {
                ["type"] = "TextBlock",
                ["text"] = title,
                ["weight"] = "Bolder",
                ["size"] = "Medium",
                ["wrap"] = true,
            });
        }
        blocks.Add(new JsonObject
        {
            ["type"] = "TextBlock",
            ["text"] = text,
            ["wrap"] = true,
        });

        return new JsonObject
        {
            ["type"] = "message",
            ["attachments"] = new JsonArray
            {
                new JsonObject
                {
                    ["contentType"] = "application/vnd.microsoft.card.adaptive",
                    ["contentUrl"] = null,
                    ["content"] = new JsonObject
                    {
                        ["$schema"] = "http://adaptivecards.io/schemas/adaptive-card.json",
                        ["type"] = "AdaptiveCard",
                        ["version"] = AdaptiveCardVersion,
                        ["body"] = blocks,
                    },
                },
            },
        };
    }

    private static JsonObject LegacyTeamsMessageCard(string title, string text)
    {
        // `summary` is not decoration: a MessageCard without one is rejected outright, and it is what
        // shows in the activity feed and mobile notification.
        var card = new JsonObject
        {
            ["@type"] = "MessageCard",
            ["@context"] = "https://schema.org/extensions",
            ["summary"] = title.Length > 0 ? title : "InfraPilot notification",
            ["themeColor"] = AccentColor.ToString("X6"),
            ["text"] = text,
        };
        if (title.Length > 0) card["title"] = title;
        return card;
    }

    /// <summary>
    /// Microsoft Teams through a Power Automate flow whose action is "Post message in a chat or
    /// channel" rather than "Post card". That action takes HTML, so the body here is the rendered
    /// message converted to an HTML fragment and POSTed raw — no JSON wrapper, matching the contract
    /// the marketplace pipeline's send-teams-notification.ps1 has been using against the same kind of
    /// flow.
    /// <para>
    /// Worth knowing why anyone would pick this over the Adaptive Card: the card renders inside a
    /// bordered box attributed to the Workflows app and supports only a markdown subset, dropping
    /// tables and headings. HTML posts as an ordinary message and keeps both — which for a release
    /// note is the difference between a table and a paragraph of run-together cells.
    /// </para>
    /// </summary>
    private static WebhookHttpRequest BuildMicrosoftTeamsHtml(
        WebhookDelivery delivery, MessageTemplateRenderer.RenderedMessage message)
    {
        // Trimmed as markdown, before conversion: cutting the HTML instead would sooner or later
        // sever a tag and hand Teams a fragment it renders as literal text.
        var text = Truncate(message.Text, TeamsTextLimit);

        // Markdig passes raw HTML through untouched, so a template that already emits HTML — the way
        // the pipeline script builds its report — reaches the channel exactly as written.
        var html = MarkdownRenderer.Shared.ToHtml(text);

        // The heading is prose, not markdown, so it is encoded rather than rendered: a service name
        // containing an ampersand should read as one, and <h2> matches the shape the existing
        // pipeline report opens with.
        if (message.Title.Length > 0)
        {
            var heading = WebUtility.HtmlEncode(Truncate(message.Title, TeamsTextLimit));
            html = $"<h2>{heading}</h2>\n{html}";
        }

        return new WebhookHttpRequest(html, MessagingHeaders(delivery), "text/html; charset=utf-8");
    }

    private static bool IsLegacyTeamsConnector(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri)
           && LegacyTeamsConnectorHosts.Any(host =>
               uri.Host.Equals(host, StringComparison.OrdinalIgnoreCase)
               || uri.Host.EndsWith($".{host}", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Discord. Without a heading the message is posted as plain content, which is what a one-line
    /// notification should look like in a channel; with one it becomes an embed, whose description
    /// also happens to allow twice the text.
    /// </summary>
    private static WebhookHttpRequest BuildDiscord(
        WebhookDelivery delivery, MessageTemplateRenderer.RenderedMessage message)
    {
        JsonObject body;
        if (message.Title.Length == 0)
        {
            body = new JsonObject { ["content"] = Truncate(message.Text, DiscordContentLimit) };
        }
        else
        {
            body = new JsonObject
            {
                ["embeds"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["title"] = Truncate(message.Title, DiscordTitleLimit),
                        ["description"] = Truncate(message.Text, DiscordDescriptionLimit),
                        ["color"] = AccentColor,
                    },
                },
            };
        }

        return new WebhookHttpRequest(body.ToJsonString(), MessagingHeaders(delivery));
    }

    /// <summary>
    /// Trims to a platform limit, marking the cut so a reader can tell a truncated message from one
    /// that simply ended. Both platforms reject an over-long body outright, so this is the difference
    /// between a trimmed notification and none.
    /// </summary>
    private static string Truncate(string value, int limit)
        => value.Length <= limit ? value : value[..(limit - 1)] + "…";

    private enum HmacAlgorithm { Sha1, Sha256 }

    private static string ComputeHmacHex(HmacAlgorithm algorithm, string payload, string secret)
    {
        var key = Encoding.UTF8.GetBytes(secret);
        var bytes = Encoding.UTF8.GetBytes(payload);
        using HMAC hmac = algorithm == HmacAlgorithm.Sha1 ? new HMACSHA1(key) : new HMACSHA256(key);
        return Convert.ToHexStringLower(hmac.ComputeHash(bytes));
    }
}
