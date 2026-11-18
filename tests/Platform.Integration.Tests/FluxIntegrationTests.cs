using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;

namespace Platform.Integration.Tests;

/// <summary>
/// <c>POST /api/integrations/flux/{client}/events</c>: HMAC-signed Flux notifications become deploy
/// events, carrying the change set of the promotion candidate they deliver.
/// </summary>
public class FluxIntegrationTests : IClassFixture<FluxIntegrationTests.FluxFactory>, IDisposable
{
    public class FluxFactory : TestFactory
    {
        public const string ApiKey = "flux-test-pipeline-key";
        public const string Client = "staging-weu-1";
        public const string Secret = "flux-test-secret";
        public const string ScopedClient = "scoped-cluster";
        public const string ScopedSecret = "flux-scoped-secret";

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("Deployments:ApiKeys:0:Name", "flux-test-pipeline");
            builder.UseSetting("Deployments:ApiKeys:0:Key", ApiKey);
            builder.UseSetting("Integrations:Flux:Enabled", "true");
            builder.UseSetting("Integrations:Flux:Clients:0:Name", Client);
            builder.UseSetting("Integrations:Flux:Clients:0:Secret", Secret);
            builder.UseSetting("Integrations:Flux:Clients:1:Name", ScopedClient);
            builder.UseSetting("Integrations:Flux:Clients:1:Secret", ScopedSecret);
            builder.UseSetting("Integrations:Flux:Clients:1:AllowedProducts:0", "only-this-product");
        }
    }

    private readonly FluxFactory _factory;
    private readonly HttpClient _client;
    private readonly HttpClient _apiKeyClient;
    private readonly HttpClient _adminClient;

    public FluxIntegrationTests(FluxFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
        _apiKeyClient = factory.CreateClient();
        _apiKeyClient.DefaultRequestHeaders.Add("X-Api-Key", FluxFactory.ApiKey);
        _adminClient = factory.CreateAdminClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _apiKeyClient.Dispose();
        _adminClient.Dispose();
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private static object FluxEvent(
        string product, string service, string environment, string version,
        string reason = "UpgradeSucceeded", DateTimeOffset? timestamp = null) => new
    {
        involvedObject = new { kind = "HelmRelease", @namespace = product, name = service },
        severity = reason.EndsWith("Failed") ? "error" : "info",
        timestamp = timestamp ?? DateTimeOffset.UtcNow,
        message = $"Helm {reason} for release {product}/{service} with chart {service}@{version}",
        reason,
        reportingController = "helm-controller",
        metadata = new Dictionary<string, string>
        {
            ["revision"] = version,
            ["environment"] = environment,
            ["cluster"] = FluxFactory.Client,
            ["product"] = product,
            ["service"] = service,
        },
    };

    private Task<HttpResponseMessage> PostFluxAsync(
        object payload, string client = FluxFactory.Client, string secret = FluxFactory.Secret,
        string? signatureOverride = null, bool sign = true)
    {
        var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload));
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/integrations/flux/{client}/events")
        {
            Content = new ByteArrayContent(body),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        if (sign)
        {
            var mac = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body);
            request.Headers.Add("X-Signature",
                signatureOverride ?? $"sha256={Convert.ToHexString(mac).ToLowerInvariant()}");
        }
        return _client.SendAsync(request);
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    /// <summary>An auto-approved candidate for <c>test → staging</c> carrying two references.</summary>
    private async Task<string> CreateCandidateAsync(string product, string service, string version)
    {
        await _adminClient.PutAsJsonAsync("/api/features/features.promotions", new { enabled = true });
        var policy = await _adminClient.PostAsJsonAsync("/api/promotions/admin/policies", new
        {
            product,
            service = (string?)null,
            sourceEnv = "test",
            targetEnv = "staging",
            steps = Array.Empty<object>(),
            escalationGroup = (string?)null,
            sourceRequiresDeploy = false,
        });
        Assert.Equal(HttpStatusCode.Created, policy.StatusCode);

        var created = await _apiKeyClient.PostAsJsonAsync("/api/promotions", new
        {
            product,
            service,
            sourceEnv = "test",
            targetEnv = "staging",
            version,
            toRevision = "abc1234",
            references = new object[]
            {
                new { type = "commit", provider = "github", key = "abc1234", title = "feat: checkout retries" },
                new { type = "work-item", provider = "jira", key = "CHK-451", title = "Retry checkout", commits = new[] { "abc1234" } },
            },
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return (await Json(created)).GetProperty("id").GetString()!;
    }

    private async Task<string> CandidateStatusAsync(string candidateId) =>
        (await _adminClient.GetFromJsonAsync<JsonElement>($"/api/promotions/{candidateId}"))
            .GetProperty("candidate").GetProperty("status").GetString()!;

    private async Task<JsonElement> EventAsync(string eventId) =>
        (await _adminClient.GetFromJsonAsync<JsonElement>($"/api/deployments/events/{eventId}"))
            .GetProperty("event");

    // ── tests ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task SucceededEvent_CopiesCandidateReferences_AndClosesCandidate()
    {
        var candidateId = await CreateCandidateAsync("flux-acme", "api", "1.3.0");

        var response = await PostFluxAsync(FluxEvent("flux-acme", "api", "staging", "1.3.0"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await Json(response);
        Assert.Equal(candidateId, body.GetProperty("promotionId").GetString());

        var evt = await EventAsync(body.GetProperty("id").GetString()!);
        Assert.Equal("helm-deploy", evt.GetProperty("source").GetString());
        Assert.Equal("succeeded", evt.GetProperty("status").GetString());
        var keys = evt.GetProperty("references").EnumerateArray()
            .Select(r => r.GetProperty("key").GetString()).ToList();
        Assert.Contains("CHK-451", keys);
        Assert.Contains("abc1234", keys);

        Assert.Equal("Deployed", await CandidateStatusAsync(candidateId));
    }

    [Fact]
    public async Task FailedEvent_CopiesReferences_AndLeavesCandidateOpen()
    {
        var candidateId = await CreateCandidateAsync("flux-fail", "api", "2.0.0");

        var response = await PostFluxAsync(FluxEvent("flux-fail", "api", "staging", "2.0.0", reason: "UpgradeFailed"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await Json(response);
        var evt = await EventAsync(body.GetProperty("id").GetString()!);
        Assert.Equal("failed", evt.GetProperty("status").GetString());
        Assert.Contains(evt.GetProperty("references").EnumerateArray(),
            r => r.GetProperty("key").GetString() == "CHK-451");

        Assert.NotEqual("Deployed", await CandidateStatusAsync(candidateId));
    }

    [Fact]
    public async Task NoCandidate_IngestsWithoutReferences()
    {
        var response = await PostFluxAsync(FluxEvent("flux-lone", "api", "staging", "0.9.0"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await Json(response);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("promotionId").ValueKind);
        var evt = await EventAsync(body.GetProperty("id").GetString()!);
        Assert.Empty(evt.GetProperty("references").EnumerateArray());
    }

    [Fact]
    public async Task SameEventTwice_IsAReplay()
    {
        var at = DateTimeOffset.UtcNow.AddMinutes(-3);
        var payload = FluxEvent("flux-retry", "api", "staging", "1.0.0", timestamp: at);

        var first = await PostFluxAsync(payload);
        var second = await PostFluxAsync(payload);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var replay = await Json(second);
        Assert.True(replay.GetProperty("replayed").GetBoolean());
        Assert.Equal((await Json(first)).GetProperty("id").GetString(), replay.GetProperty("id").GetString());
    }

    [Fact]
    public async Task NotAnInstallOutcome_IsAcceptedAndIgnored()
    {
        var response = await PostFluxAsync(FluxEvent("flux-noise", "api", "staging", "1.0.0", reason: "Progressing"));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.True((await Json(response)).GetProperty("ignored").GetBoolean());
    }

    [Fact]
    public async Task MissingProduct_IsBadRequest()
    {
        var payload = new
        {
            involvedObject = new { kind = "HelmRelease", @namespace = "x", name = "api" },
            timestamp = DateTimeOffset.UtcNow,
            reason = "UpgradeSucceeded",
            metadata = new Dictionary<string, string> { ["revision"] = "1.0.0", ["environment"] = "staging" },
        };

        var response = await PostFluxAsync(payload);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task WrongSecret_IsUnauthorized()
    {
        var response = await PostFluxAsync(FluxEvent("flux-auth", "api", "staging", "1.0.0"), secret: "not-the-secret");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task MissingSignature_IsUnauthorized()
    {
        var response = await PostFluxAsync(FluxEvent("flux-auth", "api", "staging", "1.0.0"), sign: false);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task UnknownClient_IsUnauthorized()
    {
        var response = await PostFluxAsync(FluxEvent("flux-auth", "api", "staging", "1.0.0"), client: "nobody");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ApiKey_CannotPostFluxEvents()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/integrations/flux/{FluxFactory.Client}/events")
        {
            Content = JsonContent.Create(FluxEvent("flux-auth", "api", "staging", "1.0.0")),
        };
        var response = await _apiKeyClient.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ProductOutsideClientScope_IsForbidden()
    {
        var response = await PostFluxAsync(
            FluxEvent("another-product", "api", "staging", "1.0.0"),
            client: FluxFactory.ScopedClient, secret: FluxFactory.ScopedSecret);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task ProductInsideClientScope_IsIngested()
    {
        var response = await PostFluxAsync(
            FluxEvent("only-this-product", "api", "staging", "1.0.0"),
            client: FluxFactory.ScopedClient, secret: FluxFactory.ScopedSecret);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }
}

/// <summary>A portal that never enabled the Flux integration does not expose its route.</summary>
public class FluxIntegrationDisabledTests : IClassFixture<TestFactory>
{
    private readonly TestFactory _factory;

    public FluxIntegrationDisabledTests(TestFactory factory) => _factory = factory;

    [Fact]
    public async Task Disabled_ReturnsNotFound()
    {
        using var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/integrations/flux/any/events", new { reason = "UpgradeSucceeded" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
