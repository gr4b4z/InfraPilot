using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;

namespace Platform.Integration.Tests;

/// <summary>
/// Covers the two CI-resilience contracts added for pipeline integrations:
/// <list type="bullet">
///   <item>Deploy-event ingest is idempotent on the natural key
///         (product, service, environment, version, deployedAt, source) — a retried POST returns
///         the existing row (200 + replayed=true) instead of inserting a duplicate.</item>
///   <item>Promotion policies can opt out of the source-deploy requirement
///         (<c>sourceRequiresDeploy=false</c>) for landing-zone edges, and the create endpoint's
///         422 responses carry machine-readable <c>code</c> values.</item>
/// </list>
/// </summary>
public class IngestIdempotencyAndSourcePolicyTests
    : IClassFixture<IngestIdempotencyAndSourcePolicyTests.IdemFactory>, IDisposable
{
    public class IdemFactory : TestFactory
    {
        public const string TestApiKey = "idem-test-key-12345";

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("Deployments:ApiKeys:0:Name", "idem-test");
            builder.UseSetting("Deployments:ApiKeys:0:Key", TestApiKey);
        }
    }

    private readonly IdemFactory _factory;
    private readonly HttpClient _apiKeyClient;
    private readonly HttpClient _adminClient;

    public IngestIdempotencyAndSourcePolicyTests(IdemFactory factory)
    {
        _factory = factory;
        _apiKeyClient = factory.CreateClient();
        _apiKeyClient.DefaultRequestHeaders.Add("X-Api-Key", IdemFactory.TestApiKey);
        _adminClient = CreateAuthenticatedClient("admin@localhost", "admin123");
    }

    public void Dispose()
    {
        _apiKeyClient.Dispose();
        _adminClient.Dispose();
    }

    // ── Deploy-event idempotency ────────────────────────────────────────────

    private static object MakeDeployPayload(
        string product, string service, string environment, string version, DateTimeOffset deployedAt) =>
        new
        {
            product,
            service,
            environment,
            version,
            source = "integration-test",
            deployedAt,
            status = "succeeded",
        };

    [Fact]
    public async Task PostSameDeployEventTwice_ReturnsExistingRowInsteadOfDuplicating()
    {
        var deployedAt = DateTimeOffset.UtcNow;
        var payload = MakeDeployPayload("idem-acme", "api", "dev", "1.0.0", deployedAt);

        var first = await _apiKeyClient.PostAsJsonAsync("/api/deployments/events", payload);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var firstBody = await Deserialize(first);
        Assert.False(firstBody.GetProperty("replayed").GetBoolean());
        var firstId = firstBody.GetProperty("id").GetString();

        var second = await _apiKeyClient.PostAsJsonAsync("/api/deployments/events", payload);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var secondBody = await Deserialize(second);
        Assert.True(secondBody.GetProperty("replayed").GetBoolean());
        Assert.Equal(firstId, secondBody.GetProperty("id").GetString());

        // History confirms only one row was stored.
        var history = await _adminClient.GetFromJsonAsync<JsonElement>(
            "/api/deployments/history/idem-acme/api?environment=dev");
        Assert.Single(history.EnumerateArray());
    }

    [Fact]
    public async Task PostSameVersionWithDifferentTimestamp_CreatesSecondEvent()
    {
        var deployedAt = DateTimeOffset.UtcNow;
        var first = await _apiKeyClient.PostAsJsonAsync("/api/deployments/events",
            MakeDeployPayload("idem-acme", "web", "dev", "2.0.0", deployedAt));
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        // A genuine redeploy of the same version has a new timestamp — must NOT be deduped.
        var second = await _apiKeyClient.PostAsJsonAsync("/api/deployments/events",
            MakeDeployPayload("idem-acme", "web", "dev", "2.0.0", deployedAt.AddMinutes(5)));
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);

        var firstId = (await Deserialize(first)).GetProperty("id").GetString();
        var secondId = (await Deserialize(second)).GetProperty("id").GetString();
        Assert.NotEqual(firstId, secondId);
    }

    // ── sourceRequiresDeploy policy flag + 422 error codes ─────────────────

    /// <summary>
    /// Creates a product-default policy for the edge and returns its id. <paramref name="steps"/>
    /// defaults to an empty tree (auto-approve); pass a tree to make candidates park in Pending.
    /// </summary>
    private async Task<string> SeedPolicyAsync(
        string product, string sourceEnv, string targetEnv, bool sourceRequiresDeploy,
        object[]? steps = null)
    {
        await _adminClient.PutAsJsonAsync("/api/features/features.promotions", new { enabled = true });

        var response = await _adminClient.PostAsJsonAsync("/api/promotions/admin/policies", new
        {
            product,
            service = (string?)null,
            sourceEnv,
            targetEnv,
            steps = steps ?? Array.Empty<object>(),
            escalationGroup = (string?)null,
            sourceRequiresDeploy,
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await Deserialize(response);
        Assert.Equal(sourceRequiresDeploy, body.GetProperty("sourceRequiresDeploy").GetBoolean());
        return body.GetProperty("id").GetString()!;
    }

    private async Task<HttpResponseMessage> PostPromotionAsync(
        string product, string sourceEnv, string targetEnv, string version) =>
        await _apiKeyClient.PostAsJsonAsync("/api/promotions", new
        {
            product,
            service = "api",
            sourceEnv,
            targetEnv,
            version,
            references = Array.Empty<object>(),
        });

    [Fact]
    public async Task CreatePromotion_LandingZoneSource_SucceedsWithoutSourceDeployEvent()
    {
        await SeedPolicyAsync("lz-acme", "stable", "staging", sourceRequiresDeploy: false);

        // No deploy event was ever ingested for 'stable' — the flag makes that acceptable.
        var response = await PostPromotionAsync("lz-acme", "stable", "staging", "3.0.0");
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task CreatePromotion_MissingSourceDeploy_Returns422WithCode()
    {
        await SeedPolicyAsync("strict-acme", "dev", "test", sourceRequiresDeploy: true);

        var response = await PostPromotionAsync("strict-acme", "dev", "test", "4.0.0");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var body = await Deserialize(response);
        Assert.Equal("source_deploy_missing", body.GetProperty("code").GetString());
    }

    [Fact]
    public async Task CreatePromotion_NoPolicy_Returns422WithCode()
    {
        var response = await PostPromotionAsync("unenrolled-acme", "dev", "test", "1.0.0");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var body = await Deserialize(response);
        Assert.Equal("policy_missing", body.GetProperty("code").GetString());
    }

    [Fact]
    public async Task CreatePromotion_TargetAlreadyAtVersion_Returns422WithCode()
    {
        await SeedPolicyAsync("current-acme", "staging", "prod", sourceRequiresDeploy: true);

        var deployedAt = DateTimeOffset.UtcNow;
        await _apiKeyClient.PostAsJsonAsync("/api/deployments/events",
            MakeDeployPayload("current-acme", "api", "staging", "5.0.0", deployedAt));
        await _apiKeyClient.PostAsJsonAsync("/api/deployments/events",
            MakeDeployPayload("current-acme", "api", "prod", "5.0.0", deployedAt.AddMinutes(1)));

        var response = await PostPromotionAsync("current-acme", "staging", "prod", "5.0.0");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var body = await Deserialize(response);
        Assert.Equal("target_already_at_version", body.GetProperty("code").GetString());
    }

    /// <summary>
    /// End-to-end proof that a policy edit reaches promotions that already exist: a candidate is
    /// created under a policy that requires a human sign-off (so it parks in Pending), then the
    /// operator removes that requirement. The waiting candidate must be re-gated under the new
    /// settings and promoted — not left stuck on a rule that no longer exists. The PUT response
    /// reports how many in-flight promotions it touched.
    /// </summary>
    [Fact]
    public async Task RelaxingPolicy_ReappliesToPendingPromotionAndApprovesIt()
    {
        var gated = new object[]
        {
            new
            {
                name = "Approval",
                requirements = new[]
                {
                    new
                    {
                        name = "Approvers",
                        groups = new[] { new { id = "Release", name = "Release" } },
                        users = Array.Empty<string>(),
                        minApprovers = 1,
                    },
                },
            },
        };

        var policyId = await SeedPolicyAsync(
            "retro-acme", "stable", "staging", sourceRequiresDeploy: false, steps: gated);

        // In flight: created under the gated policy, so it parks in Pending awaiting a sign-off.
        var created = await PostPromotionAsync("retro-acme", "stable", "staging", "6.0.0");
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var createdBody = await Deserialize(created);
        Assert.Equal("Pending", createdBody.GetProperty("status").GetString());
        var candidateId = createdBody.GetProperty("id").GetString();

        // The requirement is dropped — an empty step tree means auto-approve on this edge.
        var updated = await _adminClient.PutAsJsonAsync($"/api/promotions/admin/policies/{policyId}", new
        {
            product = "retro-acme",
            service = (string?)null,
            sourceEnv = "stable",
            targetEnv = "staging",
            steps = Array.Empty<object>(),
            escalationGroup = (string?)null,
            sourceRequiresDeploy = false,
        });
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        Assert.Equal(1, (await Deserialize(updated)).GetProperty("reappliedCandidates").GetInt32());

        // The promotion that was waiting on the removed requirement is through.
        var detail = await _adminClient.GetFromJsonAsync<JsonElement>($"/api/promotions/{candidateId}");
        Assert.Equal("Approved", detail.GetProperty("candidate").GetProperty("status").GetString());
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

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
        using var doc = await JsonDocument.ParseAsync(stream);
        return doc.RootElement.Clone();
    }
}
