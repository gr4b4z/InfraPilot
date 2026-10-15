using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Platform.Api.Infrastructure.Identity;

namespace Platform.Integration.Tests;

/// <summary>
/// Administrator reject, end to end over HTTP: <c>POST /api/promotions/admin/candidates/{id}/reject</c>
/// and its bulk form. An administrator — a person or an admin API key — rejects a Pending promotion
/// they are not an approver for, with a reason, while the approver route stays exactly as it was.
///
/// <para>Every test seeds its own product on a staging → prod edge whose only gate names one person
/// by email. Nobody here is that person, and an admin's "member of every group" shortcut does not
/// reach a users-only requirement, so the approver route refuses everyone and the admin override is
/// the only way to reject.</para>
/// </summary>
public class PromotionAdminRejectTests : IClassFixture<PromotionAdminRejectTests.AdminRejectFactory>, IDisposable
{
    private const string AdminApiKey = "test-promotion-admin-reject-key-1";
    private const string UserApiKey = "test-promotion-admin-reject-key-2";
    private const string AdminKeyName = "promotion-admin-reject-agent";

    private readonly HttpClient _adminKeyClient;
    private readonly HttpClient _userKeyClient;
    private readonly HttpClient _adminClient;
    private readonly HttpClient _qaClient;

    public PromotionAdminRejectTests(AdminRejectFactory factory)
    {
        _adminKeyClient = factory.CreateClient();
        _adminKeyClient.DefaultRequestHeaders.Add("X-Api-Key", AdminApiKey);
        _userKeyClient = factory.CreateClient();
        _userKeyClient.DefaultRequestHeaders.Add("X-Api-Key", UserApiKey);
        _adminClient = factory.CreateAdminClient();
        _qaClient = factory.CreateAuthenticatedClient("qa@localhost", "qa123");
    }

    public void Dispose()
    {
        _adminKeyClient.Dispose();
        _userKeyClient.Dispose();
        _adminClient.Dispose();
        _qaClient.Dispose();
    }

    // ── Single ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task AdminReject_RejectsAPromotionTheAdminCannotRejectAsAnApprover()
    {
        var product = NewProduct();
        await SeedUsersOnlyPolicyAsync(product);
        var id = await CreatePromotionAsync(product, "api", "v1.0.0");

        // The approver route is unchanged: an admin who is not an approver is refused.
        var asApprover = await _adminClient.PostAsJsonAsync($"/api/promotions/{id}/reject", new { comment = "stale" });
        Assert.Equal(HttpStatusCode.Forbidden, asApprover.StatusCode);

        var response = await _adminClient.PostAsJsonAsync(
            $"/api/promotions/admin/candidates/{id}/reject", new { reason = "  stale after the track migration  " });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(id, body.GetProperty("id").GetGuid());
        Assert.Equal("Rejected", body.GetProperty("status").GetString());

        var detail = await DetailAsync(id);
        Assert.Equal("Rejected", detail.GetProperty("candidate").GetProperty("status").GetString());
        // The decision row is what the page's "Other decisions" renders: who, and why.
        var decision = Assert.Single(detail.GetProperty("approvals").EnumerateArray());
        Assert.Equal("Rejected", decision.GetProperty("decision").GetString());
        Assert.Equal("admin@localhost", decision.GetProperty("approverEmail").GetString());
        Assert.Equal("stale after the track migration", decision.GetProperty("comment").GetString());
        Assert.Contains(detail.GetProperty("comments").EnumerateArray(), c =>
            c.GetProperty("body").GetString()!.Contains("rejected this promotion as an administrator"));
    }

    [Fact]
    public async Task AdminReject_WorksWithAnAdminApiKey()
    {
        var product = NewProduct();
        await SeedUsersOnlyPolicyAsync(product);
        var id = await CreatePromotionAsync(product, "api", "v1.0.0");

        var response = await _adminKeyClient.PostAsJsonAsync(
            $"/api/promotions/admin/candidates/{id}/reject", new { reason = "superseded by the main-track retirement" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var decision = Assert.Single((await DetailAsync(id)).GetProperty("approvals").EnumerateArray());
        Assert.Equal(AdminKeyName, decision.GetProperty("approverName").GetString());
        Assert.Equal("superseded by the main-track retirement", decision.GetProperty("comment").GetString());
    }

    [Fact]
    public async Task AdminReject_IsAdministratorsOnly()
    {
        var product = NewProduct();
        await SeedUsersOnlyPolicyAsync(product);
        var id = await CreatePromotionAsync(product, "api", "v1.0.0");

        var asQa = await _qaClient.PostAsJsonAsync(
            $"/api/promotions/admin/candidates/{id}/reject", new { reason = "trying my luck" });
        var asUserKey = await _userKeyClient.PostAsJsonAsync(
            $"/api/promotions/admin/candidates/{id}/reject", new { reason = "trying my luck" });

        Assert.Equal(HttpStatusCode.Forbidden, asQa.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, asUserKey.StatusCode);
        Assert.Equal("Pending", await StatusAsync(id));
    }

    [Fact]
    public async Task AdminReject_RequiresAReason()
    {
        var product = NewProduct();
        await SeedUsersOnlyPolicyAsync(product);
        var id = await CreatePromotionAsync(product, "api", "v1.0.0");

        var blank = await _adminClient.PostAsJsonAsync(
            $"/api/promotions/admin/candidates/{id}/reject", new { reason = "   " });
        var missing = await _adminClient.PostAsJsonAsync(
            $"/api/promotions/admin/candidates/{id}/reject", new { });

        Assert.Equal(HttpStatusCode.BadRequest, blank.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Equal("Pending", await StatusAsync(id));
    }

    [Fact]
    public async Task AdminReject_RefusesANonPendingPromotion_AndAnUnknownOne()
    {
        var product = NewProduct();
        await SeedUsersOnlyPolicyAsync(product);
        var id = await CreatePromotionAsync(product, "api", "v1.0.0");
        var bypass = await _adminClient.PostAsJsonAsync(
            $"/api/promotions/admin/candidates/{id}/bypass", new { reason = "hotfix" });
        bypass.EnsureSuccessStatusCode();

        var approved = await _adminClient.PostAsJsonAsync(
            $"/api/promotions/admin/candidates/{id}/reject", new { reason = "changed my mind" });
        var unknown = await _adminClient.PostAsJsonAsync(
            $"/api/promotions/admin/candidates/{Guid.NewGuid()}/reject", new { reason = "stale" });

        Assert.Equal(HttpStatusCode.BadRequest, approved.StatusCode);
        Assert.Equal("Approved", await StatusAsync(id));
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [Fact]
    public async Task AdminReject_IsARejectionInTheAuditFeed_MarkedAsTheAdminOverride()
    {
        var product = NewProduct();
        await SeedUsersOnlyPolicyAsync(product);
        var id = await CreatePromotionAsync(product, "api", "v1.0.0");

        var response = await _adminClient.PostAsJsonAsync(
            $"/api/promotions/admin/candidates/{id}/reject", new { reason = "stale" });
        response.EnsureSuccessStatusCode();

        var feed = await _adminClient.GetFromJsonAsync<JsonElement>(
            $"/api/promotions/audit?product={product}&category=rejected");
        var entry = Assert.Single(feed.GetProperty("entries").EnumerateArray());
        Assert.Equal("promotion.rejected", entry.GetProperty("action").GetString());
        Assert.Equal("rejected", entry.GetProperty("category").GetString());
        Assert.Equal("administrator-reject", entry.GetProperty("trigger").GetString());
        Assert.Equal("stale", entry.GetProperty("reason").GetString());
    }

    // ── Bulk ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task BulkReject_RejectsEveryPendingRow_AndReportsTheOnesItCannot()
    {
        var product = NewProduct();
        await SeedUsersOnlyPolicyAsync(product);
        var first = await CreatePromotionAsync(product, "api", "v1.0.0");
        var second = await CreatePromotionAsync(product, "web", "v1.0.0");
        var approved = await CreatePromotionAsync(product, "worker", "v1.0.0");
        (await _adminClient.PostAsJsonAsync(
            $"/api/promotions/admin/candidates/{approved}/bypass", new { reason = "hotfix" })).EnsureSuccessStatusCode();
        var missing = Guid.NewGuid();

        var response = await _adminKeyClient.PostAsJsonAsync("/api/promotions/admin/candidates/bulk/reject",
            new { ids = new[] { first, approved, missing, second }, reason = "stale after the track migration" });

        response.EnsureSuccessStatusCode();
        var results = await ResultsAsync(response);
        Assert.Equal(4, results.Count);
        foreach (var id in new[] { first, second })
        {
            Assert.True(Row(results, id).GetProperty("ok").GetBoolean(), Row(results, id).ToString());
            Assert.Equal("Rejected", Row(results, id).GetProperty("status").GetString());
            Assert.Equal("Rejected", await StatusAsync(id));
        }
        Assert.False(Row(results, approved).GetProperty("ok").GetBoolean());
        Assert.Contains("no longer accepting decisions", Row(results, approved).GetProperty("error").GetString());
        Assert.Equal("Approved", await StatusAsync(approved));
        Assert.False(Row(results, missing).GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task BulkReject_RequiresAReason_AndRejectsNothingWithoutOne()
    {
        var product = NewProduct();
        await SeedUsersOnlyPolicyAsync(product);
        var id = await CreatePromotionAsync(product, "api", "v1.0.0");

        var response = await _adminClient.PostAsJsonAsync("/api/promotions/admin/candidates/bulk/reject",
            new { ids = new[] { id }, reason = "  " });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("Pending", await StatusAsync(id));
    }

    [Fact]
    public async Task BulkReject_IsAdministratorsOnly()
    {
        var product = NewProduct();
        await SeedUsersOnlyPolicyAsync(product);
        var id = await CreatePromotionAsync(product, "api", "v1.0.0");

        var response = await _qaClient.PostAsJsonAsync("/api/promotions/admin/candidates/bulk/reject",
            new { ids = new[] { id }, reason = "trying my luck" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("Pending", await StatusAsync(id));
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    /// <summary>A product name no other test in the class shares. Short — the column is bounded.</summary>
    private static string NewProduct() => $"rej{Guid.NewGuid():N}"[..12];

    /// <summary>One gate, approvable only by a named person nobody in these tests signs in as.</summary>
    private async Task SeedUsersOnlyPolicyAsync(string product)
    {
        await _adminClient.PutAsJsonAsync("/api/features/features.promotions", new { enabled = true });
        var response = await _adminClient.PostAsJsonAsync("/api/promotions/admin/policies", new
        {
            product,
            service = (string?)null,
            sourceEnv = "staging",
            targetEnv = "prod",
            steps = new[]
            {
                new
                {
                    name = "Release Approval",
                    requirements = new[]
                    {
                        new
                        {
                            name = "Release Manager",
                            groups = Array.Empty<object>(),
                            users = new[] { "release.manager@example.com" },
                            minApprovers = 1,
                        },
                    },
                },
            },
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
        var deploy = await _adminKeyClient.PostAsJsonAsync("/api/deployments/events", new
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

        var created = await _adminKeyClient.PostAsJsonAsync("/api/promotions", new
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

    private static async Task<List<JsonElement>> ResultsAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("results").EnumerateArray().ToList();
    }

    private static JsonElement Row(IEnumerable<JsonElement> results, Guid id) =>
        results.Single(r => r.GetProperty("id").GetGuid() == id);

    private async Task<JsonElement> DetailAsync(Guid id)
    {
        var response = await _adminClient.GetAsync($"/api/promotions/{id}");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task<string?> StatusAsync(Guid id) =>
        (await DetailAsync(id)).GetProperty("candidate").GetProperty("status").GetString();

    public class AdminRejectFactory : TestFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            // The automation agent's key: admin, so it may call /api/promotions/admin/*.
            builder.UseSetting("Deployments:ApiKeys:0:Name", AdminKeyName);
            builder.UseSetting("Deployments:ApiKeys:0:Key", AdminApiKey);
            builder.UseSetting("Deployments:ApiKeys:0:Roles:0", "InfraPortal.Admin");
            // An ordinary producer key: no roles configured, so InfraPortal.User only.
            builder.UseSetting("Deployments:ApiKeys:1:Name", "promotion-admin-reject-producer");
            builder.UseSetting("Deployments:ApiKeys:1:Key", UserApiKey);
            // The stub identity service answers every group lookup with everyone, which would make
            // anybody an approver. Membership has to come from role claims alone.
            builder.ConfigureServices(services =>
            {
                RemoveService<IIdentityService>(services);
                services.AddScoped<IIdentityService, WorkItemApprovalTests.EmptyIdentityService>();
            });
        }
    }
}
