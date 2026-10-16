using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Platform.Api.Infrastructure.Identity;

namespace Platform.Integration.Tests;

/// <summary>
/// Mass approve, end to end over HTTP: the list says which gates are the caller's, bulk approve signs
/// one named gate across a batch, and an administrator can bypass a batch with one reason. These are
/// the three pieces the Promotions page's "Mass approve" dialog is built from.
///
/// <para>Every test seeds its own product on a staging → prod edge with two gates. QA Review is
/// signed by the <c>InfraPortal.QA</c> role (qa@localhost) and Release Approval by
/// <c>InfraPortal.Admin</c>. An admin qualifies for every group-backed requirement, so admin@localhost
/// can sign both, and qa@localhost only the first.</para>
/// </summary>
public class PromotionMassApproveTests : IClassFixture<PromotionMassApproveTests.MassApproveFactory>, IDisposable
{
    private const string TestApiKey = "test-promotion-mass-approve-key-1";
    private const string QaGate = "QA Review";
    private const string ReleaseGate = "Release Approval";

    private readonly MassApproveFactory _factory;
    private readonly HttpClient _apiKeyClient;
    private readonly HttpClient _adminClient;
    private readonly HttpClient _qaClient;

    public PromotionMassApproveTests(MassApproveFactory factory)
    {
        _factory = factory;
        _apiKeyClient = factory.CreateClient();
        _apiKeyClient.DefaultRequestHeaders.Add("X-Api-Key", TestApiKey);
        _adminClient = factory.CreateAdminClient();
        _qaClient = factory.CreateAuthenticatedClient("qa@localhost", "qa123");
    }

    public void Dispose()
    {
        _apiKeyClient.Dispose();
        _adminClient.Dispose();
        _qaClient.Dispose();
    }

    // ── The list: which gates are yours ─────────────────────────────────────

    [Fact]
    public async Task List_NamesTheGatesTheCallerCanApprove()
    {
        var product = NewProduct();
        await SeedTwoGatePolicyAsync(product);
        var id = await CreatePromotionAsync(product, "api", "v1.0.0");

        var asQa = await ListRowAsync(_qaClient, product, id);
        Assert.Equal(new[] { QaGate, ReleaseGate }, Strings(asQa, "pendingGates"));
        Assert.Equal(new[] { QaGate }, Strings(asQa, "approvableGates"));
        Assert.True(asQa.GetProperty("canApprove").GetBoolean());

        var asAdmin = await ListRowAsync(_adminClient, product, id);
        Assert.Equal(new[] { QaGate, ReleaseGate }, Strings(asAdmin, "approvableGates"));
    }

    [Fact]
    public async Task List_DropsAGateOnceTheCallerHasSignedIt()
    {
        var product = NewProduct();
        await SeedTwoGatePolicyAsync(product);
        var id = await CreatePromotionAsync(product, "api", "v1.0.0");

        await BulkApproveAsync(_adminClient, new[] { id }, QaGate);

        var asAdmin = await ListRowAsync(_adminClient, product, id);
        Assert.Equal(new[] { ReleaseGate }, Strings(asAdmin, "pendingGates"));
        Assert.Equal(new[] { ReleaseGate }, Strings(asAdmin, "approvableGates"));
    }

    // ── Bulk approve with a gate ────────────────────────────────────────────

    [Fact]
    public async Task BulkApprove_WithAGate_SignsThatGateOnEveryRow_AndLeavesTheOtherOpen()
    {
        var product = NewProduct();
        await SeedTwoGatePolicyAsync(product);
        var ids = new[]
        {
            await CreatePromotionAsync(product, "api", "v1.0.0"),
            await CreatePromotionAsync(product, "web", "v2.0.0"),
            await CreatePromotionAsync(product, "worker", "v3.0.0"),
        };

        var results = await BulkApproveAsync(_qaClient, ids, QaGate, comment: "regression pack green");

        Assert.Equal(3, results.Count);
        foreach (var row in results)
        {
            Assert.True(row.GetProperty("ok").GetBoolean(), row.ToString());
            // The release gate is still open, so the promotion stays Pending and says what is left.
            Assert.Equal("Pending", row.GetProperty("status").GetString());
            Assert.Equal(new[] { "QA" }, Strings(row, "approvedAs"));
            Assert.Equal(new[] { ReleaseGate }, Strings(row, "pendingGates"));
        }

        // The second gate, signed in bulk by the release manager, finishes every one of them.
        var release = await BulkApproveAsync(_adminClient, ids, ReleaseGate);
        Assert.All(release, row =>
        {
            Assert.True(row.GetProperty("ok").GetBoolean(), row.ToString());
            Assert.Equal("Approved", row.GetProperty("status").GetString());
            Assert.Empty(Strings(row, "pendingGates"));
        });
    }

    [Fact]
    public async Task BulkApprove_WithAGate_ReportsEachRowItCannotSign_AndCarriesOnWithTheRest()
    {
        var product = NewProduct();
        await SeedTwoGatePolicyAsync(product);
        var open = await CreatePromotionAsync(product, "api", "v1.0.0");
        var alreadySigned = await CreatePromotionAsync(product, "web", "v1.0.0");
        var missing = Guid.NewGuid();
        await BulkApproveAsync(_qaClient, new[] { alreadySigned }, QaGate);

        var results = await BulkApproveAsync(_qaClient, new[] { alreadySigned, missing, open }, QaGate);

        Assert.False(Row(results, alreadySigned).GetProperty("ok").GetBoolean());
        Assert.Contains("already approved", Row(results, alreadySigned).GetProperty("error").GetString());
        Assert.False(Row(results, missing).GetProperty("ok").GetBoolean());
        Assert.True(Row(results, open).GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task BulkApprove_WithAGateTheCallerIsNotAnApproverFor_FailsEveryRowWithTheReason()
    {
        var product = NewProduct();
        await SeedTwoGatePolicyAsync(product);
        var id = await CreatePromotionAsync(product, "api", "v1.0.0");

        var row = Assert.Single(await BulkApproveAsync(_qaClient, new[] { id }, ReleaseGate));

        Assert.False(row.GetProperty("ok").GetBoolean());
        Assert.Contains("not an approver for 'Release Approval'", row.GetProperty("error").GetString());
        Assert.Equal("Pending", await StatusAsync(id));
    }

    [Fact]
    public async Task BulkApprove_WithAGateThePromotionDoesNotHave_SaysSo()
    {
        var product = NewProduct();
        await SeedTwoGatePolicyAsync(product);
        var id = await CreatePromotionAsync(product, "api", "v1.0.0");

        var row = Assert.Single(await BulkApproveAsync(_adminClient, new[] { id }, "Security Sign-off"));

        Assert.False(row.GetProperty("ok").GetBoolean());
        Assert.Contains("no 'Security Sign-off' gate", row.GetProperty("error").GetString());
    }

    [Fact]
    public async Task BulkApprove_WithAGate_SignsEveryRequirementOfItTheCallerIsEligibleFor()
    {
        // One gate, two requirements, and an admin eligible for both: approving "the gate" approves
        // both, which is what clears it. One call per requirement on the detail page; one row here.
        var product = NewProduct();
        await SeedPolicyAsync(product, new
        {
            name = ReleaseGate,
            requirements = new[]
            {
                Requirement("Release Manager", "InfraPortal.Admin"),
                Requirement("Product Owner", "InfraPortal.Admin"),
            },
        });
        var id = await CreatePromotionAsync(product, "api", "v1.0.0");

        var row = Assert.Single(await BulkApproveAsync(_adminClient, new[] { id }, ReleaseGate));

        Assert.True(row.GetProperty("ok").GetBoolean(), row.ToString());
        Assert.Equal(new[] { "Release Manager", "Product Owner" }, Strings(row, "approvedAs"));
        Assert.Equal("Approved", row.GetProperty("status").GetString());
    }

    [Fact]
    public async Task BulkApprove_GateNameIgnoresCase()
    {
        var product = NewProduct();
        await SeedTwoGatePolicyAsync(product);
        var id = await CreatePromotionAsync(product, "api", "v1.0.0");

        var row = Assert.Single(await BulkApproveAsync(_qaClient, new[] { id }, "qa review"));

        Assert.True(row.GetProperty("ok").GetBoolean(), row.ToString());
    }

    // ── Bulk bypass ─────────────────────────────────────────────────────────

    [Fact]
    public async Task BulkBypass_ApprovesEveryRow_AndRecordsTheReasonOnEach()
    {
        var product = NewProduct();
        await SeedTwoGatePolicyAsync(product);
        var first = await CreatePromotionAsync(product, "api", "v1.0.0");
        var second = await CreatePromotionAsync(product, "web", "v1.0.0");

        var response = await _adminClient.PostAsJsonAsync("/api/promotions/admin/candidates/bulk/bypass",
            new { ids = new[] { first, second }, reason = "release night, QA signed off offline" });
        response.EnsureSuccessStatusCode();
        var results = await ResultsAsync(response);

        Assert.All(results, row =>
        {
            Assert.True(row.GetProperty("ok").GetBoolean(), row.ToString());
            Assert.Equal("Approved", row.GetProperty("status").GetString());
        });
        foreach (var id in new[] { first, second })
        {
            var detail = await DetailAsync(id);
            Assert.Equal("Approved", detail.GetProperty("candidate").GetProperty("status").GetString());
            Assert.Equal("release night, QA signed off offline",
                detail.GetProperty("bypass").GetProperty("reason").GetString());
        }
    }

    [Fact]
    public async Task BulkBypass_ReportsARowThatIsNoLongerPending_AndBypassesTheRest()
    {
        var product = NewProduct();
        await SeedTwoGatePolicyAsync(product);
        var done = await CreatePromotionAsync(product, "api", "v1.0.0");
        var open = await CreatePromotionAsync(product, "web", "v1.0.0");
        await BulkApproveAsync(_adminClient, new[] { done }, QaGate);
        await BulkApproveAsync(_adminClient, new[] { done }, ReleaseGate);

        var response = await _adminClient.PostAsJsonAsync("/api/promotions/admin/candidates/bulk/bypass",
            new { ids = new[] { done, open }, reason = "release night" });
        var results = await ResultsAsync(response);

        Assert.False(Row(results, done).GetProperty("ok").GetBoolean());
        Assert.Contains("no longer accepting decisions", Row(results, done).GetProperty("error").GetString());
        Assert.True(Row(results, open).GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task BulkBypass_RequiresAReason()
    {
        var product = NewProduct();
        await SeedTwoGatePolicyAsync(product);
        var id = await CreatePromotionAsync(product, "api", "v1.0.0");

        var response = await _adminClient.PostAsJsonAsync("/api/promotions/admin/candidates/bulk/bypass",
            new { ids = new[] { id }, reason = "   " });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("Pending", await StatusAsync(id));
    }

    [Fact]
    public async Task BulkBypass_IsAdministratorsOnly()
    {
        var product = NewProduct();
        await SeedTwoGatePolicyAsync(product);
        var id = await CreatePromotionAsync(product, "api", "v1.0.0");

        var response = await _qaClient.PostAsJsonAsync("/api/promotions/admin/candidates/bulk/bypass",
            new { ids = new[] { id }, reason = "trying my luck" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("Pending", await StatusAsync(id));
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    /// <summary>A product name no other test in the class shares. Short — the column is bounded.</summary>
    private static string NewProduct() => $"mass{Guid.NewGuid():N}"[..12];

    private static object Requirement(string name, string group) => new
    {
        name,
        groups = new[] { group },
        users = Array.Empty<string>(),
        minApprovers = 1,
    };

    private Task SeedTwoGatePolicyAsync(string product) => SeedPolicyAsync(
        product,
        new { name = QaGate, requirements = new[] { Requirement("QA", "InfraPortal.QA") } },
        new { name = ReleaseGate, requirements = new[] { Requirement("Release Manager", "InfraPortal.Admin") } });

    private async Task SeedPolicyAsync(string product, params object[] steps)
    {
        await _adminClient.PutAsJsonAsync("/api/features/features.promotions", new { enabled = true });
        var response = await _adminClient.PostAsJsonAsync("/api/promotions/admin/policies", new
        {
            product,
            service = (string?)null,
            sourceEnv = "staging",
            targetEnv = "prod",
            steps,
            escalationGroup = (string?)null,
        });
        response.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Creates a candidate the way CI does: a succeeded deploy in staging, which external create
    /// validates against, then the promotion to prod.
    /// </summary>
    private async Task<Guid> CreatePromotionAsync(string product, string service, string version)
    {
        var deploy = await _apiKeyClient.PostAsJsonAsync("/api/deployments/events", new
        {
            product,
            service,
            environment = "staging",
            version,
            source = "integration-test",
            deployedAt = DateTimeOffset.UtcNow,
            status = "succeeded",
        });
        Assert.Equal(HttpStatusCode.Created, deploy.StatusCode);

        var created = await _apiKeyClient.PostAsJsonAsync("/api/promotions", new
        {
            product,
            service,
            sourceEnv = "staging",
            targetEnv = "prod",
            version,
            references = Array.Empty<object>(),
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var body = await created.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("id").GetGuid();
    }

    private static async Task<List<JsonElement>> BulkApproveAsync(
        HttpClient client, Guid[] ids, string gate, string? comment = null)
    {
        var response = await client.PostAsJsonAsync("/api/promotions/bulk/approve", new { ids, gate, comment });
        response.EnsureSuccessStatusCode();
        return await ResultsAsync(response);
    }

    private static async Task<List<JsonElement>> ResultsAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("results").EnumerateArray().ToList();
    }

    private static JsonElement Row(IEnumerable<JsonElement> results, Guid id) =>
        results.Single(r => r.GetProperty("id").GetGuid() == id);

    private static async Task<JsonElement> ListRowAsync(HttpClient client, string product, Guid id)
    {
        var response = await client.GetAsync($"/api/promotions/?status=Pending&product={product}");
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("candidates").EnumerateArray().Single(c => c.GetProperty("id").GetGuid() == id);
    }

    private async Task<JsonElement> DetailAsync(Guid id)
    {
        var response = await _adminClient.GetAsync($"/api/promotions/{id}");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task<string?> StatusAsync(Guid id) =>
        (await DetailAsync(id)).GetProperty("candidate").GetProperty("status").GetString();

    private static string[] Strings(JsonElement element, string property) =>
        element.GetProperty(property).EnumerateArray().Select(e => e.GetString()!).ToArray();

    public class MassApproveFactory : TestFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("Deployments:ApiKeys:0:Name", "promotion-mass-approve-test");
            builder.UseSetting("Deployments:ApiKeys:0:Key", TestApiKey);
            builder.UseSetting("Deployments:ApiKeys:0:Roles:0", "InfraPortal.Admin");
            // The stub identity service answers every group lookup with everyone, which would make
            // qa@localhost a release manager too. Group membership has to come from role claims alone.
            builder.ConfigureServices(services =>
            {
                RemoveService<IIdentityService>(services);
                services.AddScoped<IIdentityService, WorkItemApprovalTests.EmptyIdentityService>();
            });
        }
    }
}
