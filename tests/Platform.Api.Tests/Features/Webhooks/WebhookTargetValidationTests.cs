using Platform.Api.Features.Webhooks;
using Platform.Api.Features.Webhooks.Models;

namespace Platform.Api.Tests.Features.Webhooks;

/// <summary>
/// The create/update guard rails for the non-generic targets. Two of these are security rules
/// rather than usability ones: a signature header name becomes an actual HTTP header name, and a
/// GitHub token becomes an Authorization value — neither may carry anything that could split the
/// request into a second header.
/// </summary>
public class WebhookTargetValidationTests
{
    private const string GitHubUrl = "https://api.github.com/repos/acme/infra/dispatches";
    private const string AdoUrl =
        "https://dev.azure.com/acme/_apis/public/distributedtask/webhooks/deploy?api-version=6.0-preview";

    private const string DiscordUrl = "https://discord.com/api/webhooks/1234567890/aBcDeF-token";
    private const string TeamsUrl =
        "https://prod-12.westeurope.logic.azure.com:443/workflows/abc/triggers/manual/paths/invoke";

    private static string? Validate(
        string targetType,
        string url = "https://example.test/hook",
        string? secret = null,
        string? signatureHeader = null,
        string? gitHubEventType = null,
        bool requireSecret = true,
        string? messageTemplate = null,
        string? messageTitle = null)
        => WebhookEndpoints.ValidateTarget(
            targetType, url, secret, signatureHeader, gitHubEventType, requireSecret,
            messageTemplate, messageTitle);

    [Fact]
    public void UnknownTargetType_IsRejected()
        => Assert.Contains("targetType", Validate("gitlab"));

    // ── generic ─────────────────────────────────────────────────────────────

    [Fact]
    public void Generic_NeedsNothingBeyondAUrl()
        => Assert.Null(Validate(WebhookTargetTypes.Generic));

    [Theory]
    [InlineData("X-Hub-Signature", null)]
    [InlineData(null, "infrapilot")]
    public void Generic_RejectsFieldsThatBelongToOtherTargets(string? header, string? eventType)
        => Assert.NotNull(Validate(
            WebhookTargetTypes.Generic, signatureHeader: header, gitHubEventType: eventType));

    // ── azure devops ────────────────────────────────────────────────────────

    [Fact]
    public void AzureDevOps_AcceptsASecretAndOptionalHeader()
    {
        Assert.Null(Validate(WebhookTargetTypes.AzureDevOps, AdoUrl, secret: "s3cret"));
        Assert.Null(Validate(
            WebhookTargetTypes.AzureDevOps, AdoUrl, secret: "s3cret", signatureHeader: "X-WH-Checksum"));
    }

    [Fact]
    public void AzureDevOps_RequiresASecretOnCreate()
        => Assert.Contains("secret is required", Validate(WebhookTargetTypes.AzureDevOps, AdoUrl));

    [Fact]
    public void AzureDevOps_SecretIsOptionalOnUpdate_WhereOmittingItKeepsTheStoredOne()
        => Assert.Null(Validate(WebhookTargetTypes.AzureDevOps, AdoUrl, requireSecret: false));

    [Theory]
    [InlineData("X-Bad Header")]           // space
    [InlineData("X-Bad:Header")]           // colon terminates a header name
    [InlineData("X-Bad\r\nInjected: 1")]   // CRLF injection
    public void AzureDevOps_RejectsHeaderNamesThatAreNotHttpTokens(string header)
        => Assert.Contains("header name", Validate(
            WebhookTargetTypes.AzureDevOps, AdoUrl, secret: "s3cret", signatureHeader: header));

    // ── github ──────────────────────────────────────────────────────────────

    [Fact]
    public void GitHub_AcceptsADispatchUrlWithAToken()
        => Assert.Null(Validate(WebhookTargetTypes.GitHub, GitHubUrl, secret: "ghp_token"));

    [Fact]
    public void GitHub_RequiresAToken()
        => Assert.Contains("secret is required", Validate(WebhookTargetTypes.GitHub, GitHubUrl));

    [Theory]
    [InlineData("http://api.github.com/repos/acme/infra/dispatches")]  // token would go in the clear
    [InlineData("not-a-url")]
    public void GitHub_RequiresHttps(string url)
        => Assert.Contains("https", Validate(WebhookTargetTypes.GitHub, url, secret: "ghp_token"));

    [Fact]
    public void GitHub_RejectsAUrlThatIsNotARepositoryDispatchEndpoint()
        => Assert.Contains("repository_dispatch", Validate(
            WebhookTargetTypes.GitHub, "https://api.github.com/repos/acme/infra", secret: "ghp_token"));

    [Fact]
    public void GitHub_RejectsATokenCarryingControlCharacters()
        => Assert.Contains("control characters", Validate(
            WebhookTargetTypes.GitHub, GitHubUrl, secret: "ghp_token\r\nX-Injected: 1"));

    [Fact]
    public void GitHub_RejectsAnOverlongEventType()
        => Assert.Contains("githubEventType", Validate(
            WebhookTargetTypes.GitHub, GitHubUrl, secret: "ghp_token",
            gitHubEventType: new string('e', 101)));

    // ── messaging targets ───────────────────────────────────────────────────
    // The rules invert here: there is no credential to supply, and a message template is the thing
    // that must hold up. Accepting a secret would imply the URL is safe to share, which it is not.

    [Fact]
    public void Teams_NeedsNothingBeyondAnHttpsUrl()
        => Assert.Null(Validate(WebhookTargetTypes.MicrosoftTeams, TeamsUrl));

    [Fact]
    public void Discord_NeedsNothingBeyondAWebhookUrl()
        => Assert.Null(Validate(WebhookTargetTypes.Discord, DiscordUrl));

    [Theory]
    [InlineData(WebhookTargetTypes.MicrosoftTeams, TeamsUrl)]
    [InlineData(WebhookTargetTypes.Discord, DiscordUrl)]
    public void Messaging_RejectsASecret(string targetType, string url)
        => Assert.Contains("secret does not apply", Validate(targetType, url, secret: "whsec_nope"));

    [Theory]
    [InlineData(WebhookTargetTypes.MicrosoftTeams, "http://prod-12.westeurope.logic.azure.com/workflows/abc")]
    [InlineData(WebhookTargetTypes.Discord, "http://discord.com/api/webhooks/1/t")]
    [InlineData(WebhookTargetTypes.MicrosoftTeams, "not-a-url")]
    public void Messaging_RequiresHttps(string targetType, string url)
        => Assert.Contains("https", Validate(targetType, url));

    [Fact]
    public void Discord_RejectsAChannelLinkMistakenForAWebhookUrl()
        => Assert.Contains("channel webhook", Validate(
            WebhookTargetTypes.Discord, "https://discord.com/channels/123/456"));

    /// <summary>A proxy in front of Discord still speaks the same body shape, so the host is not pinned.</summary>
    [Fact]
    public void Discord_AcceptsAProxiedWebhookPath()
        => Assert.Null(Validate(
            WebhookTargetTypes.Discord, "https://gateway.acme.test/api/webhooks/1234/token"));

    [Theory]
    [InlineData(WebhookTargetTypes.MicrosoftTeams, TeamsUrl)]
    [InlineData(WebhookTargetTypes.Discord, DiscordUrl)]
    public void Messaging_AcceptsAValidTemplate(string targetType, string url)
        => Assert.Null(Validate(
            targetType, url,
            messageTemplate: "{{data.service}} {{#if data.runUrl}}[run]({{data.runUrl}}){{/if}}",
            messageTitle: "{{eventType}}"));

    [Fact]
    public void Messaging_RejectsATemplateThatDoesNotCompile()
        => Assert.Contains("not a valid template", Validate(
            WebhookTargetTypes.Discord, DiscordUrl, messageTemplate: "{{#if data.x}}unclosed"));

    [Fact]
    public void Messaging_RejectsAnUnparseableTitleTemplate()
        => Assert.Contains("messageTitle", Validate(
            WebhookTargetTypes.MicrosoftTeams, TeamsUrl, messageTitle: "{{#each data.items}}oops"));

    [Fact]
    public void Messaging_RejectsAnOverlongTemplate()
        => Assert.Contains("messageTemplate must be", Validate(
            WebhookTargetTypes.Discord, DiscordUrl, messageTemplate: new string('x', 8001)));

    /// <summary>
    /// Storing a template on a target that never renders one would leave a setting that silently does
    /// nothing — the operator's mistake is worth a rejection rather than a shrug.
    /// </summary>
    [Theory]
    [InlineData(WebhookTargetTypes.Generic)]
    [InlineData(WebhookTargetTypes.AzureDevOps)]
    public void NonMessaging_RejectsAMessageTemplate(string targetType)
        => Assert.Contains("messageTemplate applies only", Validate(
            targetType, AdoUrl, secret: "s3cret", messageTemplate: "{{eventType}}"));
}
