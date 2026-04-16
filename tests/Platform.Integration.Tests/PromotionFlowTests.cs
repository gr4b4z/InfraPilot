using System.Data.Common;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Platform.Api.Features.Webhooks;
using Platform.Api.Infrastructure.Persistence;

namespace Platform.Integration.Tests;

/// <summary>
/// End-to-end integration tests for the deployment ingest → promotion flow.
/// Exercises the full HTTP pipeline: API key auth → ingest → promotion hook →
/// candidate creation → webhook dispatch → approve/reject → completion matching.
/// </summary>
public class PromotionFlowTests : IClassFixture<PromotionFlowTests.FlowFactory>, IDisposable
{
    private readonly FlowFactory _factory;
    private readonly HttpClient _apiKeyClient;
    private readonly HttpClient _adminClient;

    public PromotionFlowTests(FlowFactory factory)
    {
        _factory = factory;

        // API-key client for deploy event ingest.
        _apiKeyClient = factory.CreateClient();
        _apiKeyClient.DefaultRequestHeaders.Add("X-Api-Key", FlowFactory.TestApiKey);

        // Admin client for promotion actions.
        _adminClient = CreateAuthenticatedClient("admin@localhost", "admin123");
    }

    public void Dispose()
    {
        _apiKeyClient.Dispose();
        _adminClient.Dispose();
    }

    // ── Setup helpers ───────────────────────────────────────────────────────

    private async Task SeedTopologyAndPoliciesAsync()
    {
        // Save topology: dev → staging → prod
        await _adminClient.PutAsJsonAsync("/api/promotions/admin/topology", new
        {
            environments = new[] { "dev", "staging", "prod" },
            edges = new[]
            {
                new { from = "dev", to = "staging" },
                new { from = "staging", to = "prod" },
            },
        });

        // Enable the promotions feature flag.
        await _adminClient.PutAsJsonAsync("/api/features/features.promotions", new { enabled = true });

        // Policy: dev → staging = auto-approve (no approver group).
        await _adminClient.PostAsJsonAsync("/api/promotions/admin/policies", new
        {
            product = "acme",
            service = (string?)null,
            targetEnv = "staging",
            approverGroup = (string?)null,
            strategy = "Any",
            minApprovers = 0,
            excludeDeployer = false,
            timeoutHours = 24,
            escalationGroup = (string?)null,
        });

        // Policy: staging → prod = gated, 1 approver required.
        await _adminClient.PostAsJsonAsync("/api/promotions/admin/policies", new
        {
            product = "acme",
            service = (string?)null,
            targetEnv = "prod",
            approverGroup = "InfraPortal.Admin",
            strategy = "Any",
            minApprovers = 1,
            excludeDeployer = false,
            timeoutHours = 48,
            escalationGroup = (string?)null,
        });
    }

    private object MakeDeployPayload(
        string env,
        string version = "v1.0.0",
        string status = "succeeded",
        string product = "acme",
        string service = "api") =>
        new
        {
            product,
            service,
            environment = env,
            version,
            source = "integration-test",
            deployedAt = DateTimeOffset.UtcNow,
            status,
            participants = new[]
            {
                new { role = "PR Author", displayName = "Bob Builder", email = "bob@example.com" },
            },
        };

    // ── Tests ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Ingest_DispatchesDeploymentCreatedWebhook()
    {
        // Act: ingest a deploy event.
        var response = await _apiKeyClient.PostAsJsonAsync("/api/deployments/events", MakeDeployPayload("dev"));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        // Assert: deployment.created webhook was dispatched.
        await _factory.WebhookDispatcher.Received().DispatchAsync(
            "deployment.created",
            Arg.Any<object>(),
            Arg.Any<WebhookEventFilters>());
    }

    [Fact]
    public async Task Ingest_WithTopologyAndPolicy_CreatesPromotionCandidate()
    {
        await SeedTopologyAndPoliciesAsync();

        // Act: ingest a succeeded deploy to dev → should create a promotion candidate for staging.
        var ingestResponse = await _apiKeyClient.PostAsJsonAsync(
            "/api/deployments/events",
            MakeDeployPayload("dev", version: "v2.0.0"));
        Assert.Equal(HttpStatusCode.Created, ingestResponse.StatusCode);

        // Assert: a promotion candidate exists for staging.
        var listResponse = await _adminClient.GetAsync("/api/promotions/?product=acme&targetEnv=staging");
        listResponse.EnsureSuccessStatusCode();

        var body = await Deserialize(listResponse);
        var candidates = body.GetProperty("candidates");
        var match = FindCandidate(candidates, "v2.0.0", "staging");
        Assert.NotNull(match);

        // Auto-approve policy → candidate is born Approved.
        Assert.Equal("Approved", match.Value.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Ingest_WithoutPolicy_DoesNotCreateCandidate()
    {
        // Topology exists from prior test, but use a product with no policy.
        await SeedTopologyAndPoliciesAsync();

        var ingestResponse = await _apiKeyClient.PostAsJsonAsync(
            "/api/deployments/events",
            MakeDeployPayload("dev", version: "v3.0.0", product: "no-policy-product"));
        Assert.Equal(HttpStatusCode.Created, ingestResponse.StatusCode);

        // Assert: no candidates for this product.
        var listResponse = await _adminClient.GetAsync("/api/promotions/?product=no-policy-product");
        listResponse.EnsureSuccessStatusCode();

        var body = await Deserialize(listResponse);
        var candidates = body.GetProperty("candidates");
        Assert.Equal(0, candidates.GetArrayLength());
    }

    [Fact]
    public async Task Ingest_AutoApprovePolicy_DispatchesPromotionApprovedWebhook()
    {
        await SeedTopologyAndPoliciesAsync();
        _factory.WebhookDispatcher.ClearReceivedCalls();

        // Act: ingest to dev → auto-approve creates candidate for staging.
        await _apiKeyClient.PostAsJsonAsync(
            "/api/deployments/events",
            MakeDeployPayload("dev", version: "v4.0.0"));

        // Assert: promotion.approved was dispatched (auto-approve).
        await _factory.WebhookDispatcher.Received().DispatchAsync(
            "promotion.approved",
            Arg.Any<object>(),
            Arg.Any<WebhookEventFilters>());
    }

    [Fact]
    public async Task Ingest_GatedPolicy_ApproveEmitsWebhook()
    {
        await SeedTopologyAndPoliciesAsync();

        // Use a unique service to avoid interference with other tests.
        var service = $"approve-svc-{Guid.NewGuid():N}"[..20];

        // Ingest to staging → creates a Pending candidate for prod (gated policy).
        await _apiKeyClient.PostAsJsonAsync(
            "/api/deployments/events",
            MakeDeployPayload("staging", version: "v5.0.0", service: service));

        // Find the pending candidate.
        var listResponse = await _adminClient.GetAsync("/api/promotions/?product=acme&targetEnv=prod&status=Pending");
        listResponse.EnsureSuccessStatusCode();
        var body = await Deserialize(listResponse);
        var candidate = FindCandidate(body.GetProperty("candidates"), "v5.0.0", "prod");
        Assert.NotNull(candidate);
        var candidateId = candidate.Value.GetProperty("id").GetString()!;

        _factory.WebhookDispatcher.ClearReceivedCalls();

        // Act: admin approves the candidate.
        var approveResponse = await _adminClient.PostAsJsonAsync(
            $"/api/promotions/{candidateId}/approve",
            new { comment = "ship it" });
        approveResponse.EnsureSuccessStatusCode();

        // Assert: promotion.approved webhook was dispatched.
        await _factory.WebhookDispatcher.Received().DispatchAsync(
            "promotion.approved",
            Arg.Any<object>(),
            Arg.Any<WebhookEventFilters>());
    }

    [Fact]
    public async Task Ingest_GatedPolicy_RejectEmitsWebhook()
    {
        await SeedTopologyAndPoliciesAsync();

        // Use a unique service name to avoid interference with other tests' candidates.
        var service = $"reject-svc-{Guid.NewGuid():N}"[..20];

        // Ingest to staging → creates Pending candidate for prod.
        await _apiKeyClient.PostAsJsonAsync(
            "/api/deployments/events",
            MakeDeployPayload("staging", version: "v6.0.0", service: service));

        var listResponse = await _adminClient.GetAsync("/api/promotions/?product=acme&targetEnv=prod&status=Pending");
        listResponse.EnsureSuccessStatusCode();
        var body = await Deserialize(listResponse);
        var candidate = FindCandidate(body.GetProperty("candidates"), "v6.0.0", "prod");
        Assert.NotNull(candidate);
        var candidateId = candidate.Value.GetProperty("id").GetString()!;

        _factory.WebhookDispatcher.ClearReceivedCalls();

        // Act: admin rejects.
        var rejectResponse = await _adminClient.PostAsJsonAsync(
            $"/api/promotions/{candidateId}/reject",
            new { comment = "not ready" });
        rejectResponse.EnsureSuccessStatusCode();

        // Assert: promotion.rejected webhook dispatched.
        await _factory.WebhookDispatcher.Received().DispatchAsync(
            "promotion.rejected",
            Arg.Any<object>(),
            Arg.Any<WebhookEventFilters>());
    }

    [Fact]
    public async Task Ingest_InTargetEnv_ClosesPromotionAsDeployed()
    {
        await SeedTopologyAndPoliciesAsync();

        // Use a unique service to avoid interference with other tests.
        var service = $"close-svc-{Guid.NewGuid():N}"[..20];

        // 1. Ingest to staging → creates Pending candidate for prod.
        await _apiKeyClient.PostAsJsonAsync(
            "/api/deployments/events",
            MakeDeployPayload("staging", version: "v7.0.0", service: service));

        // 2. Find and approve the candidate.
        var listResponse = await _adminClient.GetAsync("/api/promotions/?product=acme&targetEnv=prod&status=Pending");
        var body = await Deserialize(listResponse);
        var candidate = FindCandidate(body.GetProperty("candidates"), "v7.0.0", "prod");
        Assert.NotNull(candidate);
        var candidateId = candidate.Value.GetProperty("id").GetString()!;

        await _adminClient.PostAsJsonAsync(
            $"/api/promotions/{candidateId}/approve",
            new { comment = "approved" });

        // 3. Ingest the same version landing in prod → should close the candidate.
        await _apiKeyClient.PostAsJsonAsync(
            "/api/deployments/events",
            MakeDeployPayload("prod", version: "v7.0.0", service: service));

        // 4. Assert: candidate is now Deployed.
        var detailResponse = await _adminClient.GetAsync($"/api/promotions/{candidateId}");
        detailResponse.EnsureSuccessStatusCode();
        var detail = await Deserialize(detailResponse);
        var status = detail.GetProperty("candidate").GetProperty("status").GetString();
        Assert.Equal("Deployed", status);
    }

    // ── Utility ─────────────────────────────────────────────────────────────

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

    private static JsonElement? FindCandidate(JsonElement candidates, string version, string targetEnv)
    {
        foreach (var c in candidates.EnumerateArray())
        {
            if (c.GetProperty("version").GetString() == version &&
                c.GetProperty("targetEnv").GetString() == targetEnv)
                return c;
        }
        return null;
    }

    // ── Factory ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Configures the test server with an in-memory SQLite database, local-JWT auth,
    /// a test API key for deployment ingest, and a captured <see cref="IWebhookDispatcher"/>
    /// mock so tests can verify webhook calls.
    /// </summary>
    public class FlowFactory : WebApplicationFactory<Program>
    {
        public const string TestApiKey = "test-integration-key-12345";

        /// <summary>
        /// Exposed so tests can assert which webhook events were dispatched.
        /// </summary>
        public IWebhookDispatcher WebhookDispatcher { get; } = Substitute.For<IWebhookDispatcher>();

        private readonly SqliteConnection _connection;

        public FlowFactory()
        {
            _connection = new SqliteConnection("DataSource=:memory:");
            _connection.Open();

            var options = new DbContextOptionsBuilder<SqliteTestDbContext>()
                .UseSqlite(_connection)
                .Options;
            using var db = new SqliteTestDbContext(options);
            db.Database.EnsureCreated();
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");

            // Add a test API key for deployment ingest.
            builder.UseSetting("Deployments:ApiKeys:0:Name", "integration-test");
            builder.UseSetting("Deployments:ApiKeys:0:Key", TestApiKey);
            builder.UseSetting("Deployments:ApiKeys:0:Roles:0", "InfraPortal.Admin");

            builder.ConfigureServices(services =>
            {
                // Remove the real DB registrations.
                RemoveService<DbContextOptions<PostgresPlatformDbContext>>(services);
                RemoveService<DbContextOptions<SqlServerPlatformDbContext>>(services);
                RemoveService<DbContextOptions<PlatformDbContext>>(services);
                RemoveService<PostgresPlatformDbContext>(services);
                RemoveService<SqlServerPlatformDbContext>(services);
                RemoveService<PlatformDbContext>(services);

                services.AddSingleton<DbConnection>(_connection);
                // Register SqliteTestDbContext (with DateTimeOffset→long conversion) as PlatformDbContext.
                services.AddDbContext<PlatformDbContext, SqliteTestDbContext>((sp, options) =>
                    options.UseSqlite(sp.GetRequiredService<DbConnection>()));

                // Replace the webhook dispatcher with our captured mock.
                RemoveService<IWebhookDispatcher>(services);
                services.AddSingleton(WebhookDispatcher);
            });
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing) _connection.Dispose();
        }

        private static void RemoveService<T>(IServiceCollection services)
        {
            var descriptors = services.Where(d => d.ServiceType == typeof(T)).ToList();
            foreach (var d in descriptors) services.Remove(d);
        }
    }
}

// SqliteTestDbContext is now defined in TestInfrastructure.cs
