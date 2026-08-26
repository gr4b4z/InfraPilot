using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Platform.Api.Infrastructure.Persistence;

namespace Platform.Integration.Tests;

/// <summary>
/// Integration tests for the request lifecycle: creation, submission, cancellation,
/// and approval actions through the full HTTP pipeline.
/// </summary>
public class RequestLifecycleIntegrationTests
    : IClassFixture<RequestLifecycleIntegrationTests.RequestFactory>, IDisposable
{
    private readonly RequestFactory _factory;
    private readonly HttpClient _adminClient;
    private readonly HttpClient _userClient;

    private const string CatalogYaml = """
        id: test-service
        name: Test Service
        category: infrastructure
        executor:
          type: manual
        approval:
          required: true
          strategy: any
        inputs:
          - id: environment
            component: select
            label: Environment
            required: true
            options:
              - id: dev
                label: Dev
        """;

    public RequestLifecycleIntegrationTests(RequestFactory factory)
    {
        _factory = factory;
        _adminClient = CreateAuthenticatedClient("admin@localhost", "admin123");
        _userClient = CreateAuthenticatedClient("user@localhost", "user123");
    }

    public void Dispose()
    {
        _adminClient.Dispose();
        _userClient.Dispose();
    }

    // -- Helpers ----------------------------------------------------------------

    private HttpClient CreateAuthenticatedClient(string email, string password)
    {
        var client = _factory.CreateClient();
        var loginResponse = client.PostAsJsonAsync("/api/auth/login", new { email, password })
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

    /// <summary>
    /// Seeds a catalog item via the admin endpoint and returns its slug.
    /// </summary>
    private async Task<string> SeedCatalogItemAsync()
    {
        var response = await _adminClient.PostAsJsonAsync(
            "/api/catalog/admin",
            new { yamlContent = CatalogYaml });

        // If the item already exists from a prior test in the same fixture, just return the slug.
        if (response.StatusCode == HttpStatusCode.Conflict || response.IsSuccessStatusCode)
        {
            if (response.IsSuccessStatusCode)
            {
                var body = await Deserialize(response);
                return body.GetProperty("item").GetProperty("slug").GetString()!;
            }
        }

        // Fallback: item may already exist, fetch it by known slug.
        return "test-service";
    }

    /// <summary>
    /// Creates a new request for the given catalog item slug and returns its ID.
    /// </summary>
    private async Task<Guid> CreateRequestAsync(string catalogSlug)
    {
        var response = await _adminClient.PostAsJsonAsync("/api/requests", new
        {
            catalogItemId = catalogSlug,
            inputs = new Dictionary<string, object> { ["environment"] = "dev" },
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var body = await Deserialize(response);
        return Guid.Parse(body.GetProperty("id").GetString()!);
    }

    /// <summary>
    /// Creates a request, submits it, waits for auto-transition to AwaitingApproval,
    /// and returns the approval ID by listing the approvals endpoint.
    /// </summary>
    private async Task<(Guid RequestId, Guid ApprovalId)> CreateAndSubmitForApprovalAsync()
    {
        var slug = await SeedCatalogItemAsync();
        var requestId = await CreateRequestAsync(slug);

        // Submit triggers Validating -> AwaitingApproval (auto-transition for approval-required items).
        var submitResponse = await _adminClient.PostAsync($"/api/requests/{requestId}/submit", null);
        submitResponse.EnsureSuccessStatusCode();

        // Fetch the request to get the linked approval.
        var getResponse = await _adminClient.GetAsync($"/api/requests/{requestId}");
        getResponse.EnsureSuccessStatusCode();
        var requestBody = await Deserialize(getResponse);
        var request = requestBody.GetProperty("request");

        // The approval should be available via the approvals list.
        var approvalsResponse = await _adminClient.GetAsync("/api/approvals/");
        approvalsResponse.EnsureSuccessStatusCode();
        var approvalsBody = await Deserialize(approvalsResponse);
        var items = approvalsBody.GetProperty("items");

        // Find the approval linked to our request.
        Guid? approvalId = null;
        foreach (var item in items.EnumerateArray())
        {
            if (item.GetProperty("serviceRequestId").GetString() == requestId.ToString())
            {
                approvalId = Guid.Parse(item.GetProperty("id").GetString()!);
                break;
            }
        }

        Assert.NotNull(approvalId);
        return (requestId, approvalId!.Value);
    }

    // -- Tests ------------------------------------------------------------------

    [Fact]
    public async Task CreateRequest_WithValidCatalogItem_Returns201()
    {
        var slug = await SeedCatalogItemAsync();

        var response = await _adminClient.PostAsJsonAsync("/api/requests", new
        {
            catalogItemId = slug,
            inputs = new Dictionary<string, object> { ["environment"] = "dev" },
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var body = await Deserialize(response);
        Assert.True(Guid.TryParse(body.GetProperty("id").GetString(), out _));
    }

    [Fact]
    public async Task CreateRequest_InvalidCatalogItem_Returns404()
    {
        var response = await _adminClient.PostAsJsonAsync("/api/requests", new
        {
            catalogItemId = "nonexistent-service-slug",
            inputs = new Dictionary<string, object> { ["environment"] = "dev" },
        });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetRequest_ReturnsRequestWithStatus()
    {
        var slug = await SeedCatalogItemAsync();
        var requestId = await CreateRequestAsync(slug);

        var response = await _adminClient.GetAsync($"/api/requests/{requestId}");
        response.EnsureSuccessStatusCode();

        var body = await Deserialize(response);
        var request = body.GetProperty("request");
        var status = request.GetProperty("status").ToString();

        // Newly created requests start in Draft.
        Assert.Equal("Draft", status, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SubmitRequest_TransitionsFromDraft()
    {
        var slug = await SeedCatalogItemAsync();
        var requestId = await CreateRequestAsync(slug);

        var submitResponse = await _adminClient.PostAsync($"/api/requests/{requestId}/submit", null);
        submitResponse.EnsureSuccessStatusCode();

        var body = await Deserialize(submitResponse);
        Assert.Equal("Request submitted for validation", body.GetProperty("message").GetString());

        // Verify the status changed from Draft.
        var getResponse = await _adminClient.GetAsync($"/api/requests/{requestId}");
        getResponse.EnsureSuccessStatusCode();

        var getBody = await Deserialize(getResponse);
        var status = getBody.GetProperty("request").GetProperty("status").ToString();

        // With approval required, auto-transition lands on AwaitingApproval.
        Assert.NotEqual("Draft", status, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CancelRequest_FromDraft_Succeeds()
    {
        var slug = await SeedCatalogItemAsync();
        var requestId = await CreateRequestAsync(slug);

        var cancelResponse = await _adminClient.PostAsync($"/api/requests/{requestId}/cancel", null);
        cancelResponse.EnsureSuccessStatusCode();

        var body = await Deserialize(cancelResponse);
        Assert.Equal("Request cancelled", body.GetProperty("message").GetString());

        // Verify status is now Cancelled.
        var getResponse = await _adminClient.GetAsync($"/api/requests/{requestId}");
        getResponse.EnsureSuccessStatusCode();

        var getBody = await Deserialize(getResponse);
        var status = getBody.GetProperty("request").GetProperty("status").ToString();
        Assert.Equal("Cancelled", status, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RejectApproval_RequiresComment()
    {
        var (_, approvalId) = await CreateAndSubmitForApprovalAsync();

        // Reject without a comment (empty string).
        var response = await _adminClient.PostAsJsonAsync(
            $"/api/approvals/{approvalId}/reject",
            new { comment = "" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task RejectApproval_WithComment_Succeeds()
    {
        var (_, approvalId) = await CreateAndSubmitForApprovalAsync();

        var response = await _adminClient.PostAsJsonAsync(
            $"/api/approvals/{approvalId}/reject",
            new { comment = "Does not meet security requirements" });

        response.EnsureSuccessStatusCode();

        var body = await Deserialize(response);
        Assert.Equal("Rejected", body.GetProperty("message").GetString());
    }

    [Fact]
    public async Task RequestChanges_RequiresComment()
    {
        var (_, approvalId) = await CreateAndSubmitForApprovalAsync();

        // Request changes without a comment (empty string).
        var response = await _adminClient.PostAsJsonAsync(
            $"/api/approvals/{approvalId}/request-changes",
            new { comment = "" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // -- Factory ----------------------------------------------------------------

    public class RequestFactory : TestFactory;
}
