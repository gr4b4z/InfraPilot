using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;

namespace Platform.Integration.Tests;

/// <summary>
/// Integration tests for the webhook subscription CRUD endpoints.
/// All webhook endpoints require the CatalogAdmin policy (admin only).
/// </summary>
public class WebhookIntegrationTests : IClassFixture<WebhookIntegrationTests.WebhookFactory>, IDisposable
{
    private readonly WebhookFactory _factory;
    private readonly HttpClient _adminClient;
    private readonly HttpClient _userClient;

    public WebhookIntegrationTests(WebhookFactory factory)
    {
        _factory = factory;
        _adminClient = factory.CreateAdminClient();
        _userClient = CreateUserClient();
    }

    public void Dispose()
    {
        _adminClient.Dispose();
        _userClient.Dispose();
    }

    // ── Tests ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateWebhook_ReturnsCreatedWithSecret()
    {
        var response = await _adminClient.PostAsJsonAsync("/api/webhooks", new
        {
            name = "My Hook",
            url = "https://example.com/hook",
            events = new[] { "deployment.created" },
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var body = await Deserialize(response);
        Assert.True(Guid.TryParse(body.GetProperty("id").GetString(), out _));
        Assert.Equal("My Hook", body.GetProperty("name").GetString());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("secret").GetString()));
    }

    [Fact]
    public async Task ListWebhooks_ReturnsCreatedSubscription()
    {
        // Arrange: create a webhook.
        var createResponse = await _adminClient.PostAsJsonAsync("/api/webhooks", new
        {
            name = "Listed Hook",
            url = "https://example.com/listed",
            events = new[] { "deployment.created" },
        });
        createResponse.EnsureSuccessStatusCode();
        var created = await Deserialize(createResponse);
        var createdId = created.GetProperty("id").GetString();

        // Act: list all webhooks.
        var listResponse = await _adminClient.GetAsync("/api/webhooks");
        listResponse.EnsureSuccessStatusCode();

        var body = await Deserialize(listResponse);
        Assert.Equal(JsonValueKind.Array, body.ValueKind);

        var ids = new List<string>();
        foreach (var item in body.EnumerateArray())
            ids.Add(item.GetProperty("id").GetString()!);

        Assert.Contains(createdId, ids);
    }

    [Fact]
    public async Task GetWebhook_ReturnsDetailsWithDeliveries()
    {
        // Arrange: create a webhook.
        var createResponse = await _adminClient.PostAsJsonAsync("/api/webhooks", new
        {
            name = "Detail Hook",
            url = "https://example.com/detail",
            events = new[] { "deployment.created" },
        });
        createResponse.EnsureSuccessStatusCode();
        var created = await Deserialize(createResponse);
        var id = created.GetProperty("id").GetString();

        // Act: get by id.
        var getResponse = await _adminClient.GetAsync($"/api/webhooks/{id}");
        getResponse.EnsureSuccessStatusCode();

        var body = await Deserialize(getResponse);
        Assert.Equal("Detail Hook", body.GetProperty("name").GetString());
        Assert.Equal("https://example.com/detail", body.GetProperty("url").GetString());
        Assert.True(body.GetProperty("active").GetBoolean());
        Assert.True(body.TryGetProperty("recentDeliveries", out var deliveries));
        Assert.Equal(JsonValueKind.Array, deliveries.ValueKind);
    }

    [Fact]
    public async Task UpdateWebhook_ChangesNameAndActive()
    {
        // Arrange: create a webhook.
        var createResponse = await _adminClient.PostAsJsonAsync("/api/webhooks", new
        {
            name = "Original Name",
            url = "https://example.com/update",
            events = new[] { "deployment.created" },
        });
        createResponse.EnsureSuccessStatusCode();
        var created = await Deserialize(createResponse);
        var id = created.GetProperty("id").GetString();

        // Act: update name and deactivate.
        var updateResponse = await _adminClient.PutAsJsonAsync($"/api/webhooks/{id}", new
        {
            name = "Updated",
            active = false,
        });

        Assert.Equal(HttpStatusCode.OK, updateResponse.StatusCode);

        var body = await Deserialize(updateResponse);
        Assert.Equal("Updated", body.GetProperty("name").GetString());
        Assert.False(body.GetProperty("active").GetBoolean());
    }

    [Fact]
    public async Task DeleteWebhook_ReturnsNoContent()
    {
        // Arrange: create a webhook.
        var createResponse = await _adminClient.PostAsJsonAsync("/api/webhooks", new
        {
            name = "Deletable Hook",
            url = "https://example.com/delete",
            events = new[] { "deployment.created" },
        });
        createResponse.EnsureSuccessStatusCode();
        var created = await Deserialize(createResponse);
        var id = created.GetProperty("id").GetString();

        // Act: delete.
        var deleteResponse = await _adminClient.DeleteAsync($"/api/webhooks/{id}");
        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);

        // Assert: GET now returns 404.
        var getResponse = await _adminClient.GetAsync($"/api/webhooks/{id}");
        Assert.Equal(HttpStatusCode.NotFound, getResponse.StatusCode);
    }

    [Fact]
    public async Task TestWebhook_CreatesTestDelivery()
    {
        // Arrange: create a webhook.
        var createResponse = await _adminClient.PostAsJsonAsync("/api/webhooks", new
        {
            name = "Testable Hook",
            url = "https://example.com/test",
            events = new[] { "deployment.created" },
        });
        createResponse.EnsureSuccessStatusCode();
        var created = await Deserialize(createResponse);
        var id = created.GetProperty("id").GetString();

        // Act: send test delivery.
        var testResponse = await _adminClient.PostAsync($"/api/webhooks/{id}/test", null);
        Assert.Equal(HttpStatusCode.OK, testResponse.StatusCode);

        var body = await Deserialize(testResponse);
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("message").GetString()));
        Assert.True(Guid.TryParse(body.GetProperty("deliveryId").GetString(), out _));
    }

    [Fact]
    public async Task NonAdmin_CannotAccessWebhooks()
    {
        var response = await _userClient.GetAsync("/api/webhooks");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ── Target types ────────────────────────────────────────────────────────
    // Azure DevOps and GitHub reuse a credential the receiving system already holds, so unlike the
    // generic target they take a caller-supplied secret and never hand one back.

    [Fact]
    public async Task CreateWebhook_DefaultsToTheGenericTarget()
    {
        var response = await _adminClient.PostAsJsonAsync("/api/webhooks", new
        {
            name = "Untyped Hook",
            url = "https://example.com/hook",
            events = new[] { "deployment.created" },
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await Deserialize(response);
        Assert.Equal("generic", body.GetProperty("targetType").GetString());
    }

    [Fact]
    public async Task CreateAzureDevOpsWebhook_StoresTheDefaultSignatureHeader_AndWithholdsTheSecret()
    {
        var response = await _adminClient.PostAsJsonAsync("/api/webhooks", new
        {
            name = "ADO Pipeline",
            url = AzureDevOpsUrl,
            events = new[] { "promotion.approved" },
            targetType = "azure_devops",
            secret = "ado-connection-secret",
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await Deserialize(response);
        Assert.Equal("azure_devops", body.GetProperty("targetType").GetString());
        Assert.Equal("X-Hub-Signature", body.GetProperty("signatureHeader").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("secret").ValueKind);
    }

    [Fact]
    public async Task CreateAzureDevOpsWebhook_WithoutSecret_IsRejected()
    {
        var response = await _adminClient.PostAsJsonAsync("/api/webhooks", new
        {
            name = "ADO No Secret",
            url = AzureDevOpsUrl,
            events = new[] { "promotion.approved" },
            targetType = "azure_devops",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task CreateGitHubWebhook_KeepsTheEventTypeOverride()
    {
        var response = await _adminClient.PostAsJsonAsync("/api/webhooks", new
        {
            name = "GitHub Actions",
            url = GitHubUrl,
            events = new[] { "rollback.approved" },
            targetType = "github",
            secret = "ghp_token",
            gitHubEventType = "infrapilot",
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await Deserialize(response);
        Assert.Equal("github", body.GetProperty("targetType").GetString());
        Assert.Equal("infrapilot", body.GetProperty("githubEventType").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("secret").ValueKind);
    }

    [Fact]
    public async Task CreateGitHubWebhook_WithANonDispatchUrl_IsRejected()
    {
        var response = await _adminClient.PostAsJsonAsync("/api/webhooks", new
        {
            name = "GitHub Wrong URL",
            url = "https://api.github.com/repos/acme/infra",
            events = new[] { "rollback.approved" },
            targetType = "github",
            secret = "ghp_token",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task UpdateWebhook_CannotChangeTargetType()
    {
        var created = await CreateAsync(new
        {
            name = "Locked Target",
            url = "https://example.com/hook",
            events = new[] { "deployment.created" },
        });
        var id = created.GetProperty("id").GetString();

        var response = await _adminClient.PutAsJsonAsync($"/api/webhooks/{id}", new { targetType = "github" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task UpdateWebhook_RotatesTheSecretAndSignatureHeader()
    {
        var created = await CreateAsync(new
        {
            name = "ADO Rotatable",
            url = AzureDevOpsUrl,
            events = new[] { "promotion.approved" },
            targetType = "azure_devops",
            secret = "original-secret",
        });
        var id = created.GetProperty("id").GetString();

        var response = await _adminClient.PutAsJsonAsync($"/api/webhooks/{id}", new
        {
            secret = "rotated-secret",
            signatureHeader = "X-WH-Checksum",
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await Deserialize(response);
        Assert.Equal("X-WH-Checksum", body.GetProperty("signatureHeader").GetString());
        // The rotated secret is never echoed back, on this or any other read.
        Assert.False(body.TryGetProperty("secret", out _));
    }

    [Fact]
    public async Task UpdateWebhook_RejectsASignatureHeaderThatIsNotAnHttpToken()
    {
        var created = await CreateAsync(new
        {
            name = "ADO Header Guard",
            url = AzureDevOpsUrl,
            events = new[] { "promotion.approved" },
            targetType = "azure_devops",
            secret = "original-secret",
        });
        var id = created.GetProperty("id").GetString();

        var response = await _adminClient.PutAsJsonAsync($"/api/webhooks/{id}", new
        {
            signatureHeader = "X-Bad\r\nInjected: 1",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ── Chat notification subscriptions ─────────────────────────────────────
    // The messaging targets invert two of the rules above: no secret is minted or accepted, and the
    // message template is the payload. These cover the wiring end to end — route, body binding,
    // renderer resolution and persistence — which the unit tests around each piece cannot.

    [Fact]
    public async Task CreateTeamsNotification_PersistsTheTemplateAndMintsNoSecret()
    {
        var response = await _adminClient.PostAsJsonAsync("/api/webhooks", new
        {
            name = "Release channel",
            url = TeamsUrl,
            events = new[] { "release_note.generated" },
            targetType = "msteams",
            messageTemplate = "{{data.renderedContent}}",
            messageTitle = "Release notes — {{data.product}}",
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var body = await Deserialize(response);
        Assert.Equal("msteams", body.GetProperty("targetType").GetString());
        Assert.Equal("{{data.renderedContent}}", body.GetProperty("messageTemplate").GetString());
        Assert.Equal("Release notes — {{data.product}}", body.GetProperty("messageTitle").GetString());
        // Nothing was generated, so there is nothing to show once: the URL is the credential.
        Assert.Equal(JsonValueKind.Null, body.GetProperty("secret").ValueKind);

        // And it reads back the same way, rather than only echoing the create request.
        var id = body.GetProperty("id").GetString();
        var fetched = await Deserialize(await _adminClient.GetAsync($"/api/webhooks/{id}"));
        Assert.Equal("{{data.renderedContent}}", fetched.GetProperty("messageTemplate").GetString());
    }

    [Fact]
    public async Task CreateNotification_WithoutATemplate_IsAcceptedAndUsesTheEventDefault()
    {
        var created = await CreateAsync(new
        {
            name = "Deploys",
            url = DiscordUrl,
            events = new[] { "deployment.created" },
            targetType = "discord",
        });

        // Null means "fall back to the per-event default", which is what makes a notification useful
        // before anyone writes a template.
        Assert.Equal(JsonValueKind.Null, created.GetProperty("messageTemplate").ValueKind);
    }

    [Fact]
    public async Task CreateNotification_RejectsASecret()
    {
        var response = await _adminClient.PostAsJsonAsync("/api/webhooks", new
        {
            name = "Nope",
            url = DiscordUrl,
            events = new[] { "deployment.created" },
            targetType = "discord",
            secret = "whsec_should_not_be_here",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task CreateNotification_RejectsATemplateThatDoesNotCompile()
    {
        var response = await _adminClient.PostAsJsonAsync("/api/webhooks", new
        {
            name = "Broken",
            url = DiscordUrl,
            events = new[] { "deployment.created" },
            targetType = "discord",
            messageTemplate = "{{#if data.x}}never closed",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task UpdateNotification_ReplacesTheMessageTemplate()
    {
        var created = await CreateAsync(new
        {
            name = "Deploys",
            url = DiscordUrl,
            events = new[] { "deployment.created" },
            targetType = "discord",
            messageTemplate = "{{data.service}}",
        });
        var id = created.GetProperty("id").GetString();

        var response = await _adminClient.PutAsJsonAsync($"/api/webhooks/{id}", new
        {
            messageTemplate = "{{data.service}} → {{data.environment}}",
            messageTitle = "",
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await Deserialize(response);
        Assert.Equal("{{data.service}} → {{data.environment}}", body.GetProperty("messageTemplate").GetString());
        // An explicitly blank heading is kept as blank — that is how "post without a heading" is said.
        Assert.Equal("", body.GetProperty("messageTitle").GetString());
    }

    [Fact]
    public async Task PreviewMessage_RendersTheTemplateAndTheTeamsRequestBody()
    {
        var response = await _adminClient.PostAsJsonAsync("/api/webhooks/preview-message", new
        {
            targetType = "msteams",
            eventType = "deployment.created",
            messageTemplate = "{{data.service}} {{data.version}} → {{data.environment}}",
            messageTitle = "Deployed",
            url = TeamsUrl,
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await Deserialize(response);
        Assert.Equal("Deployed", body.GetProperty("title").GetString());
        // Rendered against the sample payload for this event, so the fields resolve to real values.
        Assert.Equal("api 4.12.0 → production", body.GetProperty("text").GetString());

        // The framed body is what a delivery would actually POST — an Adaptive Card for a Workflows URL.
        var requestBody = body.GetProperty("requestBody").GetString()!;
        Assert.Contains("application/vnd.microsoft.card.adaptive", requestBody);
        Assert.Contains("api 4.12.0", requestBody);
    }

    [Fact]
    public async Task PreviewMessage_WithNoTemplate_FallsBackToTheEventDefault()
    {
        var body = await Deserialize(await _adminClient.PostAsJsonAsync("/api/webhooks/preview-message", new
        {
            targetType = "discord",
            eventType = "release_note.generated",
        }));

        // The release-note default forwards the already-rendered note — the reason this path can
        // replace a relay that reformatted it.
        Assert.Contains("billing-platform", body.GetProperty("text").GetString()!);
        Assert.Contains("Release notes", body.GetProperty("title").GetString()!);
    }

    [Fact]
    public async Task PreviewMessage_RejectsANonMessagingTarget()
    {
        var response = await _adminClient.PostAsJsonAsync("/api/webhooks/preview-message", new
        {
            targetType = "generic",
            eventType = "deployment.created",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PreviewMessage_IsAdminOnly()
    {
        var response = await _userClient.PostAsJsonAsync("/api/webhooks/preview-message", new
        {
            targetType = "discord",
            eventType = "ping",
        });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private const string TeamsUrl =
        "https://prod-12.westeurope.logic.azure.com:443/workflows/abc/triggers/manual/paths/invoke";

    private const string DiscordUrl = "https://discord.com/api/webhooks/1234567890/aBcDeF-token";

    private const string AzureDevOpsUrl =
        "https://dev.azure.com/acme/_apis/public/distributedtask/webhooks/deploy?api-version=6.0-preview";

    private const string GitHubUrl = "https://api.github.com/repos/acme/infra/dispatches";

    private async Task<JsonElement> CreateAsync(object request)
    {
        var response = await _adminClient.PostAsJsonAsync("/api/webhooks", request);
        response.EnsureSuccessStatusCode();
        return await Deserialize(response);
    }

    private HttpClient CreateUserClient()
    {
        var client = _factory.CreateClient();
        var loginResponse = client.PostAsJsonAsync("/api/auth/login", new { email = "user@localhost", password = "user123" })
            .GetAwaiter().GetResult();
        loginResponse.EnsureSuccessStatusCode();
        var loginBody = Deserialize(loginResponse).GetAwaiter().GetResult();
        var token = loginBody.GetProperty("token").GetString()!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static async Task<JsonElement> Deserialize(HttpResponseMessage response)
    {
        var stream = await response.Content.ReadAsStreamAsync();
        var doc = await JsonDocument.ParseAsync(stream);
        return doc.RootElement;
    }

    // ── Factory ─────────────────────────────────────────────────────────────

    public class WebhookFactory : TestFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
        }
    }
}
