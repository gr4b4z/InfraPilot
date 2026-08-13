using System.Text.Json;
using Platform.Api.Features.Webhooks;
using Platform.Api.Features.Webhooks.Models;

namespace Platform.Api.Tests.Features.Webhooks;

/// <summary>
/// How a queued delivery is framed for each target. The generic case is a regression guard rather
/// than a feature test: subscriptions created before target types existed still run through this
/// builder, and any drift in body or headers silently breaks every receiver already in production.
/// The digests are hard-coded on purpose — recomputing them in the test would assert nothing.
/// </summary>
public class WebhookRequestBuilderTests
{
    private const string Payload =
        """{"id":"11111111-1111-1111-1111-111111111111","eventType":"deployment.created","timestamp":"2026-01-01T00:00:00+00:00","data":{"product":"acme"}}""";

    private const string Secret = "whsec_test_secret";

    private const string ExpectedSha256 = "c230bbbbaf341bb8b9ff62cfcd8a03ed223177af6a1d6dacc7fbd55838509457";
    private const string ExpectedSha1 = "d9bea68f29f4102b68e0ecb6319fab40956e28d6";

    /// <summary>Matches the envelope's own id, as the dispatcher always writes them together.</summary>
    private static readonly Guid DeliveryId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static WebhookDelivery Delivery(string eventType = "deployment.created") => new()
    {
        Id = DeliveryId,
        SubscriptionId = Guid.NewGuid(),
        EventType = eventType,
        PayloadJson = Payload,
        Status = "pending",
    };

    private static WebhookSubscription Subscription(string targetType) => new()
    {
        Id = Guid.NewGuid(),
        Name = "hook",
        Url = "https://example.test/hook",
        TargetType = targetType,
    };

    private static string? Header(WebhookRequestBuilder.WebhookHttpRequest request, string name)
        => request.Headers.FirstOrDefault(h =>
            string.Equals(h.Name, name, StringComparison.OrdinalIgnoreCase)).Value;

    // ── generic: the shape that must never change ───────────────────────────

    [Fact]
    public void Generic_SendsThePayloadVerbatim_SignedWithHmacSha256()
    {
        var request = WebhookRequestBuilder.Build(
            Subscription(WebhookTargetTypes.Generic), Delivery(), Secret);

        Assert.Equal(Payload, request.Body);
        Assert.Equal($"sha256={ExpectedSha256}", Header(request, "X-Hub-Signature-256"));
        Assert.Equal("deployment.created", Header(request, "X-Webhook-Event"));
        Assert.Equal(DeliveryId.ToString(), Header(request, "X-Webhook-Delivery"));
        Assert.Equal("application/json", Header(request, "Accept"));
        Assert.Equal(4, request.Headers.Count); // nothing new leaks into the original contract
    }

    [Fact]
    public void Generic_IsTheFallbackForAnUnrecognisedTargetType()
    {
        // Defensive: a row written by a newer build, or hand-edited, still delivers the old way
        // rather than throwing and stranding the delivery in the queue.
        var sub = Subscription("something_else");

        var request = WebhookRequestBuilder.Build(sub, Delivery(), Secret);

        Assert.Equal(Payload, request.Body);
        Assert.Equal($"sha256={ExpectedSha256}", Header(request, "X-Hub-Signature-256"));
    }

    // ── azure devops ────────────────────────────────────────────────────────

    [Fact]
    public void AzureDevOps_SignsTheUnchangedBodyWithHmacSha1_InTheDefaultHeader()
    {
        var request = WebhookRequestBuilder.Build(
            Subscription(WebhookTargetTypes.AzureDevOps), Delivery(), Secret);

        // The checksum must cover the exact bytes sent — Azure Pipelines recomputes it over the
        // raw body, so the payload cannot be reformatted on the way out.
        Assert.Equal(Payload, request.Body);
        Assert.Equal($"sha1={ExpectedSha1}", Header(request, "X-Hub-Signature"));
        Assert.Null(Header(request, "X-Hub-Signature-256"));
    }

    [Fact]
    public void AzureDevOps_HonoursTheConfiguredHeaderName()
    {
        var sub = Subscription(WebhookTargetTypes.AzureDevOps);
        sub.SignatureHeader = "X-WH-Checksum";

        var request = WebhookRequestBuilder.Build(sub, Delivery(), Secret);

        Assert.Equal($"sha1={ExpectedSha1}", Header(request, "X-WH-Checksum"));
        Assert.Null(Header(request, "X-Hub-Signature"));
    }

    [Fact]
    public void AzureDevOps_StillCarriesTheTracingHeaders()
    {
        var request = WebhookRequestBuilder.Build(
            Subscription(WebhookTargetTypes.AzureDevOps), Delivery(), Secret);

        Assert.Equal("deployment.created", Header(request, "X-Webhook-Event"));
        Assert.Equal(DeliveryId.ToString(), Header(request, "X-Webhook-Delivery"));
    }

    // ── github ──────────────────────────────────────────────────────────────

    [Fact]
    public void GitHub_WrapsTheEnvelopeInARepositoryDispatchBody()
    {
        var request = WebhookRequestBuilder.Build(
            Subscription(WebhookTargetTypes.GitHub), Delivery(), "ghp_token");

        using var body = JsonDocument.Parse(request.Body);
        Assert.Equal("deployment.created", body.RootElement.GetProperty("event_type").GetString());

        var clientPayload = body.RootElement.GetProperty("client_payload");
        Assert.Equal("deployment.created", clientPayload.GetProperty("eventType").GetString());
        Assert.Equal(DeliveryId.ToString(), clientPayload.GetProperty("id").GetString());
        Assert.Equal("acme", clientPayload.GetProperty("data").GetProperty("product").GetString());
    }

    [Fact]
    public void GitHub_EventTypeOverrideReplacesTheInfraPilotEventName()
    {
        var sub = Subscription(WebhookTargetTypes.GitHub);
        sub.GitHubEventType = "infrapilot";

        var request = WebhookRequestBuilder.Build(sub, Delivery(), "ghp_token");

        using var body = JsonDocument.Parse(request.Body);
        Assert.Equal("infrapilot", body.RootElement.GetProperty("event_type").GetString());
        // The real event type is still recoverable inside the payload.
        Assert.Equal("deployment.created",
            body.RootElement.GetProperty("client_payload").GetProperty("eventType").GetString());
    }

    [Fact]
    public void GitHub_AuthenticatesWithABearerTokenAndNeverSignsTheBody()
    {
        var request = WebhookRequestBuilder.Build(
            Subscription(WebhookTargetTypes.GitHub), Delivery(), "ghp_token");

        Assert.Equal("Bearer ghp_token", Header(request, "Authorization"));
        Assert.Equal("application/vnd.github+json", Header(request, "Accept"));
        Assert.Equal("2022-11-28", Header(request, "X-GitHub-Api-Version"));
        // GitHub answers 403 to a request with no User-Agent, so this one is load-bearing.
        Assert.False(string.IsNullOrWhiteSpace(Header(request, "User-Agent")));

        Assert.Null(Header(request, "X-Hub-Signature-256"));
        Assert.Null(Header(request, "X-Hub-Signature"));
    }

    // ── messaging targets ───────────────────────────────────────────────────
    // These frame a rendered message rather than the envelope, and each platform wants its own
    // wrapper. Nothing here signs or authenticates: the URL is the credential.

    private static readonly MessageTemplateRenderer.RenderedMessage Message =
        new("Deployment", "**api** `4.12.0` reached production");

    private static WebhookSubscription MessagingSubscription(string targetType, string url)
        => new() { Id = Guid.NewGuid(), Name = "chat", Url = url, TargetType = targetType };

    private const string TeamsWorkflowUrl =
        "https://prod-12.westeurope.logic.azure.com:443/workflows/abc/triggers/manual/paths/invoke";
    private const string TeamsConnectorUrl =
        "https://acme.webhook.office.com/webhookb2/guid@guid/IncomingWebhook/hash/guid";

    [Fact]
    public void Teams_WorkflowUrl_SendsAnAdaptiveCardInAMessageEnvelope()
    {
        var request = WebhookRequestBuilder.Build(
            MessagingSubscription(WebhookTargetTypes.MicrosoftTeams, TeamsWorkflowUrl),
            Delivery(), secret: "", Message);

        using var body = JsonDocument.Parse(request.Body);
        Assert.Equal("message", body.RootElement.GetProperty("type").GetString());

        var attachment = body.RootElement.GetProperty("attachments")[0];
        Assert.Equal("application/vnd.microsoft.card.adaptive",
            attachment.GetProperty("contentType").GetString());

        var content = attachment.GetProperty("content");
        Assert.Equal("AdaptiveCard", content.GetProperty("type").GetString());

        var blocks = content.GetProperty("body");
        Assert.Equal(2, blocks.GetArrayLength());
        Assert.Equal("Deployment", blocks[0].GetProperty("text").GetString());
        Assert.Equal("Bolder", blocks[0].GetProperty("weight").GetString());
        Assert.Equal("**api** `4.12.0` reached production", blocks[1].GetProperty("text").GetString());
        // Without wrap a long notification renders as a single clipped line.
        Assert.True(blocks[1].GetProperty("wrap").GetBoolean());
    }

    [Fact]
    public void Teams_WithoutAHeading_SendsOnlyTheBodyBlock()
    {
        var request = WebhookRequestBuilder.Build(
            MessagingSubscription(WebhookTargetTypes.MicrosoftTeams, TeamsWorkflowUrl),
            Delivery(), secret: "", new MessageTemplateRenderer.RenderedMessage("", "just the text"));

        using var body = JsonDocument.Parse(request.Body);
        var blocks = body.RootElement
            .GetProperty("attachments")[0].GetProperty("content").GetProperty("body");
        Assert.Equal(1, blocks.GetArrayLength());
        Assert.Equal("just the text", blocks[0].GetProperty("text").GetString());
    }

    /// <summary>
    /// A connector URL rejects the Adaptive Card envelope, so the shape has to be chosen from the
    /// host. Operators who set one up before the Workflows migration keep working without touching it.
    /// </summary>
    [Fact]
    public void Teams_LegacyConnectorUrl_SendsAMessageCard()
    {
        var request = WebhookRequestBuilder.Build(
            MessagingSubscription(WebhookTargetTypes.MicrosoftTeams, TeamsConnectorUrl),
            Delivery(), secret: "", Message);

        using var body = JsonDocument.Parse(request.Body);
        Assert.Equal("MessageCard", body.RootElement.GetProperty("@type").GetString());
        Assert.Equal("Deployment", body.RootElement.GetProperty("title").GetString());
        Assert.Equal("**api** `4.12.0` reached production",
            body.RootElement.GetProperty("text").GetString());
        // A MessageCard without a summary is rejected outright.
        Assert.False(string.IsNullOrWhiteSpace(body.RootElement.GetProperty("summary").GetString()));
        Assert.False(body.RootElement.TryGetProperty("attachments", out _));
    }

    [Fact]
    public void Discord_WithAHeading_SendsAnEmbed()
    {
        var request = WebhookRequestBuilder.Build(
            MessagingSubscription(WebhookTargetTypes.Discord, "https://discord.com/api/webhooks/1/t"),
            Delivery(), secret: "", Message);

        using var body = JsonDocument.Parse(request.Body);
        var embed = body.RootElement.GetProperty("embeds")[0];
        Assert.Equal("Deployment", embed.GetProperty("title").GetString());
        Assert.Equal("**api** `4.12.0` reached production", embed.GetProperty("description").GetString());
        Assert.False(body.RootElement.TryGetProperty("content", out _));
    }

    [Fact]
    public void Discord_WithoutAHeading_SendsPlainContent()
    {
        var request = WebhookRequestBuilder.Build(
            MessagingSubscription(WebhookTargetTypes.Discord, "https://discord.com/api/webhooks/1/t"),
            Delivery(), secret: "", new MessageTemplateRenderer.RenderedMessage("", "one-liner"));

        using var body = JsonDocument.Parse(request.Body);
        Assert.Equal("one-liner", body.RootElement.GetProperty("content").GetString());
        Assert.False(body.RootElement.TryGetProperty("embeds", out _));
    }

    /// <summary>
    /// Discord rejects an over-long body outright, so trimming is what makes the difference between a
    /// delivered notification and a failed one — a release note is easily longer than the cap.
    /// </summary>
    [Fact]
    public void Discord_TrimsAnOverlongMessageToTheLimit()
    {
        var request = WebhookRequestBuilder.Build(
            MessagingSubscription(WebhookTargetTypes.Discord, "https://discord.com/api/webhooks/1/t"),
            Delivery(), secret: "",
            new MessageTemplateRenderer.RenderedMessage("", new string('x', 5000)));

        using var body = JsonDocument.Parse(request.Body);
        var content = body.RootElement.GetProperty("content").GetString()!;
        Assert.Equal(2000, content.Length);
        Assert.EndsWith("…", content);
    }

    [Fact]
    public void Messaging_NeverSignsOrAuthenticates()
    {
        foreach (var targetType in WebhookTargetTypes.Messaging)
        {
            var url = targetType == WebhookTargetTypes.Discord
                ? "https://discord.com/api/webhooks/1/t"
                : TeamsWorkflowUrl;
            var request = WebhookRequestBuilder.Build(
                MessagingSubscription(targetType, url), Delivery(), secret: "", Message);

            Assert.Null(Header(request, "Authorization"));
            Assert.Null(Header(request, "X-Hub-Signature"));
            Assert.Null(Header(request, "X-Hub-Signature-256"));
        }
    }

    /// <summary>
    /// A messaging delivery framed without a message is a wiring bug, not a payload the receiver
    /// could make sense of — better to fail loudly than post an empty card.
    /// </summary>
    [Fact]
    public void Messaging_WithoutAMessage_Throws()
        => Assert.Throws<ArgumentException>(() => WebhookRequestBuilder.Build(
            MessagingSubscription(WebhookTargetTypes.Discord, "https://discord.com/api/webhooks/1/t"),
            Delivery(), secret: ""));
}
