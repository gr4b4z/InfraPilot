using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Platform.Api.Infrastructure.Persistence;

namespace Platform.Integration.Tests;

public class CatalogFactory : TestFactory
{
}

public class CatalogIntegrationTests : IClassFixture<CatalogFactory>, IDisposable
{
    private readonly CatalogFactory _factory;
    private readonly HttpClient _adminClient;
    private readonly HttpClient _anonClient;

    private const string ValidYaml = """
        id: test-item
        name: Test Item
        category: infrastructure
        description: A test catalog item
        executor:
          type: manual
        inputs:
          - id: env
            component: select
            label: Environment
            required: true
            options:
              - id: dev
                label: Development
              - id: prod
                label: Production
        """;

    private const string InvalidYaml = """
        id: ""
        name: ""
        category: ""
        """;

    public CatalogIntegrationTests(CatalogFactory factory)
    {
        _factory = factory;
        _adminClient = factory.CreateAdminClient();
        _anonClient = factory.CreateClient();
    }

    public void Dispose()
    {
        _adminClient.Dispose();
        _anonClient.Dispose();
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static string MakeYaml(string id, string name = "Test Item", string category = "infrastructure") =>
        $"""
        id: {id}
        name: {name}
        category: {category}
        description: A test catalog item
        executor:
          type: manual
        inputs:
          - id: env
            component: select
            label: Environment
            required: true
            options:
              - id: dev
                label: Development
        """;

    private async Task<string> CreateItemAndReturnSlug(string? slug = null)
    {
        slug ??= $"test-{Guid.NewGuid():N}"[..20];
        var yaml = MakeYaml(slug);
        var response = await _adminClient.PostAsJsonAsync("/api/catalog/admin", new { yamlContent = yaml });
        response.EnsureSuccessStatusCode();
        var body = await Deserialize(response);
        return body.GetProperty("item").GetProperty("slug").GetString()!;
    }

    private static async Task<JsonElement> Deserialize(HttpResponseMessage response)
    {
        var stream = await response.Content.ReadAsStreamAsync();
        var doc = await JsonDocument.ParseAsync(stream);
        return doc.RootElement;
    }

    private HttpClient CreateUserClient(string email, string password)
    {
        var client = _factory.CreateClient();
        var loginResponse = client.PostAsJsonAsync("/api/auth/login", new { email, password })
            .GetAwaiter().GetResult();
        loginResponse.EnsureSuccessStatusCode();
        var stream = loginResponse.Content.ReadAsStreamAsync().GetAwaiter().GetResult();
        var doc = JsonDocument.Parse(stream);
        var token = doc.RootElement.GetProperty("token").GetString()!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    // ── Tests ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateItem_ReturnsCreated()
    {
        // Arrange
        var slug = $"create-{Guid.NewGuid():N}"[..20];
        var yaml = MakeYaml(slug);

        // Act
        var response = await _adminClient.PostAsJsonAsync("/api/catalog/admin", new { yamlContent = yaml });

        // Assert
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await Deserialize(response);
        Assert.Equal(slug, body.GetProperty("item").GetProperty("slug").GetString());
    }

    [Fact]
    public async Task CreateItem_InvalidYaml_Returns400()
    {
        // Act
        var response = await _adminClient.PostAsJsonAsync("/api/catalog/admin", new { yamlContent = InvalidYaml });

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await Deserialize(response);
        Assert.True(body.TryGetProperty("errors", out var errors));
        Assert.True(errors.GetArrayLength() > 0);
    }

    [Fact]
    public async Task ValidateYaml_Valid_ReturnsTrue()
    {
        // Act
        var response = await _adminClient.PostAsJsonAsync("/api/catalog/admin/validate", new { yamlContent = ValidYaml });

        // Assert
        response.EnsureSuccessStatusCode();
        var body = await Deserialize(response);
        Assert.True(body.GetProperty("isValid").GetBoolean());
    }

    [Fact]
    public async Task ValidateYaml_Invalid_ReturnsFalse()
    {
        // Act
        var response = await _adminClient.PostAsJsonAsync("/api/catalog/admin/validate", new { yamlContent = InvalidYaml });

        // Assert
        response.EnsureSuccessStatusCode();
        var body = await Deserialize(response);
        Assert.False(body.GetProperty("isValid").GetBoolean());
        Assert.True(body.GetProperty("errors").GetArrayLength() > 0);
    }

    [Fact]
    public async Task ListItems_PublicEndpoint_NoAuthNeeded()
    {
        // Act — unauthenticated client hits the public catalog endpoint.
        var response = await _anonClient.GetAsync("/api/catalog/");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await Deserialize(response);
        Assert.True(body.TryGetProperty("items", out _));
    }

    [Fact]
    public async Task GetItem_BySlug_ReturnsFullDefinition()
    {
        // Arrange
        var slug = await CreateItemAndReturnSlug();

        // Act
        var response = await _anonClient.GetAsync($"/api/catalog/{slug}");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await Deserialize(response);
        Assert.Equal(slug, body.GetProperty("item").GetProperty("slug").GetString());
        Assert.True(body.TryGetProperty("inputs", out _));
        Assert.True(body.TryGetProperty("executor", out _));
    }

    [Fact]
    public async Task UpdateItem_ChangesName()
    {
        // Arrange
        var slug = await CreateItemAndReturnSlug();
        var updatedYaml = MakeYaml(slug, name: "Updated Name");

        // Act
        var response = await _adminClient.PutAsJsonAsync($"/api/catalog/admin/{slug}", new { yamlContent = updatedYaml });

        // Assert
        response.EnsureSuccessStatusCode();
        var body = await Deserialize(response);
        Assert.Equal("Updated Name", body.GetProperty("item").GetProperty("name").GetString());
    }

    [Fact]
    public async Task ToggleActive_DeactivatesItem()
    {
        // Arrange
        var slug = await CreateItemAndReturnSlug();

        // Act — deactivate the item.
        var patchResponse = await _adminClient.PatchAsJsonAsync(
            $"/api/catalog/admin/{slug}/active",
            new { isActive = false });
        patchResponse.EnsureSuccessStatusCode();

        // Assert — item should not appear in the public (active-only) list.
        var listResponse = await _anonClient.GetAsync("/api/catalog/");
        listResponse.EnsureSuccessStatusCode();
        var body = await Deserialize(listResponse);
        var items = body.GetProperty("items");
        foreach (var item in items.EnumerateArray())
        {
            Assert.NotEqual(slug, item.GetProperty("slug").GetString());
        }
    }

    [Fact]
    public async Task DeleteItem_ReturnsNoContent()
    {
        // Arrange
        var slug = await CreateItemAndReturnSlug();

        // Act
        var deleteResponse = await _adminClient.DeleteAsync($"/api/catalog/admin/{slug}");

        // Assert
        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);

        // Verify — GET should return 404.
        var getResponse = await _anonClient.GetAsync($"/api/catalog/{slug}");
        Assert.Equal(HttpStatusCode.NotFound, getResponse.StatusCode);
    }

    [Fact]
    public async Task GetYaml_ReturnsStoredContent()
    {
        // Arrange
        var slug = await CreateItemAndReturnSlug();

        // Act
        var response = await _adminClient.GetAsync($"/api/catalog/admin/{slug}/yaml");

        // Assert
        response.EnsureSuccessStatusCode();
        var body = await Deserialize(response);
        var yamlContent = body.GetProperty("yamlContent").GetString();
        Assert.NotNull(yamlContent);
        Assert.Contains(slug, yamlContent);
    }

    [Fact]
    public async Task AdminEndpoints_NonAdmin_Returns403()
    {
        // Arrange — authenticate as a regular user (no InfraPortal.Admin role).
        using var userClient = CreateUserClient("user@localhost", "user123");

        // Act
        var response = await userClient.PostAsJsonAsync("/api/catalog/admin", new { yamlContent = ValidYaml });

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
