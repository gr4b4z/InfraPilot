using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Platform.Api.Features.Webhooks.Models;

namespace Platform.Api.Features.Webhooks;

/// <summary>
/// Frames a queued delivery for the wire. Pure — no DB, no HTTP, no Data Protection: it takes the
/// already-decrypted secret and returns the exact body and headers the worker should send, so the
/// per-target request shapes are unit-testable without any plumbing.
/// </summary>
public static class WebhookRequestBuilder
{
    /// <summary>Default Azure DevOps checksum header. The ADO docs cite GitHub's header as the example.</summary>
    public const string DefaultAzureDevOpsSignatureHeader = "X-Hub-Signature";

    /// <summary>GitHub rejects requests without a User-Agent, so this is not decoration.</summary>
    public const string GitHubUserAgent = "InfraPilot";

    private const string GitHubApiVersion = "2022-11-28";

    public sealed record WebhookHttpRequest(string Body, IReadOnlyList<(string Name, string Value)> Headers);

    public static WebhookHttpRequest Build(WebhookSubscription sub, WebhookDelivery delivery, string secret)
        => sub.TargetType switch
        {
            WebhookTargetTypes.AzureDevOps => BuildAzureDevOps(sub, delivery, secret),
            WebhookTargetTypes.GitHub => BuildGitHub(sub, delivery, secret),
            _ => BuildGeneric(delivery, secret),
        };

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

    private enum HmacAlgorithm { Sha1, Sha256 }

    private static string ComputeHmacHex(HmacAlgorithm algorithm, string payload, string secret)
    {
        var key = Encoding.UTF8.GetBytes(secret);
        var bytes = Encoding.UTF8.GetBytes(payload);
        using HMAC hmac = algorithm == HmacAlgorithm.Sha1 ? new HMACSHA1(key) : new HMACSHA256(key);
        return Convert.ToHexStringLower(hmac.ComputeHash(bytes));
    }
}
