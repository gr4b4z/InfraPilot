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

    private static string? Validate(
        string targetType,
        string url = "https://example.test/hook",
        string? secret = null,
        string? signatureHeader = null,
        string? gitHubEventType = null,
        bool requireSecret = true)
        => WebhookEndpoints.ValidateTarget(
            targetType, url, secret, signatureHeader, gitHubEventType, requireSecret);

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
}
