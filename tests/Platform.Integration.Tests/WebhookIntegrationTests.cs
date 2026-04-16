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

    // ── Helpers ─────────────────────────────────────────────────────────────

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
