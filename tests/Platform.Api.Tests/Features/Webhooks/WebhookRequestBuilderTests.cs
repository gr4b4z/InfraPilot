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
}
