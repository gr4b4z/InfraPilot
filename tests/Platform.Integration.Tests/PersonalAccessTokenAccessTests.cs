using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Platform.Api.Features.Promotions.Models;
using Platform.Api.Infrastructure.Persistence;

namespace Platform.Integration.Tests;

/// <summary>
/// Access levels on personal access tokens (<c>access</c> on <c>POST /api/me/tokens</c>):
/// <list type="bullet">
///   <item>Nobody creates a token above their own role: an admin may create Standard, QA or Admin
///         tokens, a QA member Standard or QA, anyone else Standard only.</item>
///   <item>A QA token carries <c>InfraPortal.QA</c> (an admin's included), never Admin, and signs
///         off work items, but approves and rejects nothing else.</item>
///   <item>An Admin token keeps Admin and makes the decisions its owner can; each one it makes is
///         audited as an agent's. Neither does pipeline work or manages tokens.</item>
/// </list>
/// </summary>
public class PersonalAccessTokenAccessTests : IClassFixture<TestFactory>
{
    private readonly TestFactory _factory;

    public PersonalAccessTokenAccessTests(TestFactory factory) => _factory = factory;

    private static async Task<JsonElement> CreateTokenAsync(HttpClient owner, string access, string name = "agent")
    {
        var response = await owner.PostAsJsonAsync("/api/me/tokens", new { name, expiresInDays = 7, access });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static HttpClient TokenClient(TestFactory factory, JsonElement created)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", created.GetProperty("token").GetString());
        return client;
    }

    private static string[] Roles(JsonElement token) =>
        token.GetProperty("roles").EnumerateArray().Select(r => r.GetString()!).ToArray();

    private static async Task<string[]> AllowedAccessAsync(HttpClient owner) =>
        (await owner.GetFromJsonAsync<JsonElement>("/api/me/tokens")).GetProperty("allowedAccess")
            .EnumerateArray().Select(a => a.GetString()!).ToArray();

    // ── Who may create what ─────────────────────────────────────────────────────

    [Fact]
    public async Task Nobody_creates_a_token_above_their_own_role()
    {
        // Its own host: the regular user loses the QA role the seed gives them.
        await using var factory = new TestFactory();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            var row = await db.LocalUsers.SingleAsync(u => u.Email == "user@localhost");
            row.Roles = ["InfraPortal.User"];
            await db.SaveChangesAsync();
        }
        using var admin = factory.CreateAdminClient();
        using var qa = factory.CreateAuthenticatedClient("qa@localhost", "qa123");
        using var user = factory.CreateAuthenticatedClient("user@localhost", "user123");

        Assert.Equal(["Standard", "QA", "Admin"], await AllowedAccessAsync(admin));
        Assert.Equal(["Standard", "QA"], await AllowedAccessAsync(qa));
        Assert.Equal(["Standard"], await AllowedAccessAsync(user));

        var qaAdmin = await qa.PostAsJsonAsync("/api/me/tokens", new { name = "x", expiresInDays = 7, access = "Admin" });
        Assert.Equal(HttpStatusCode.Forbidden, qaAdmin.StatusCode);
        var userQa = await user.PostAsJsonAsync("/api/me/tokens", new { name = "x", expiresInDays = 7, access = "QA" });
        Assert.Equal(HttpStatusCode.Forbidden, userQa.StatusCode);
        var userAdmin = await user.PostAsJsonAsync("/api/me/tokens", new { name = "x", expiresInDays = 7, access = "admin" });
        Assert.Equal(HttpStatusCode.Forbidden, userAdmin.StatusCode);

        // Nothing was created by the refusals.
        Assert.Empty((await qa.GetFromJsonAsync<JsonElement>("/api/me/tokens")).GetProperty("tokens").EnumerateArray());
        Assert.Empty((await user.GetFromJsonAsync<JsonElement>("/api/me/tokens")).GetProperty("tokens").EnumerateArray());
    }

    [Fact]
    public async Task Create_rejects_an_unknown_access_level_and_defaults_to_standard()
    {
        using var admin = _factory.CreateAdminClient();
        var bogus = await admin.PostAsJsonAsync("/api/me/tokens", new { name = "x", expiresInDays = 7, access = "superuser" });
        Assert.Equal(HttpStatusCode.BadRequest, bogus.StatusCode);

        var response = await admin.PostAsJsonAsync("/api/me/tokens", new { name = "no access given", expiresInDays = 7 });
        var created = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Standard", created.GetProperty("access").GetString());
        Assert.DoesNotContain("InfraPortal.Admin", Roles(created));
    }

    // ── QA tokens ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Qa_token_signs_off_work_items_but_approves_no_promotion_and_carries_no_admin()
    {
        using var admin = _factory.CreateAdminClient();
        await admin.PutAsJsonAsync("/api/features/features.promotions", new { enabled = true });
        using var qa = _factory.CreateAuthenticatedClient("qa@localhost", "qa123");
        var created = await CreateTokenAsync(qa, "QA");
        Assert.Equal("QA", created.GetProperty("access").GetString());
        Assert.Contains("InfraPortal.QA", Roles(created));
        using var client = TokenClient(_factory, created);

        var candidateId = await SeedCandidateWithTicketAsync("pat-qa", "PATQA-1");
        var signOff = await client.PostAsJsonAsync("/api/work-items/PATQA-1/approvals",
            new { product = "pat-qa", service = "api", targetEnv = "prod", comment = "verified by the agent" });
        Assert.Equal(HttpStatusCode.OK, signOff.StatusCode);
        var row = await signOff.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("qa@localhost", row.GetProperty("approverEmail").GetString());

        var approve = await client.PostAsJsonAsync($"/api/promotions/{candidateId}/approve", new { comment = "lgtm" });
        Assert.Equal(HttpStatusCode.Forbidden, approve.StatusCode);
        Assert.Contains("Admin personal access token", await approve.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/audit")).StatusCode);
    }

    [Fact]
    public async Task Qa_token_of_an_admin_carries_qa_instead_of_admin()
    {
        using var admin = _factory.CreateAdminClient();
        var created = await CreateTokenAsync(admin, "QA");

        Assert.Contains("InfraPortal.QA", Roles(created));
        Assert.DoesNotContain("InfraPortal.Admin", Roles(created));
        using var client = TokenClient(_factory, created);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/audit")).StatusCode);
    }

    // ── Admin tokens ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Admin_token_keeps_admin_and_its_decisions_are_audited_as_an_agents()
    {
        using var admin = _factory.CreateAdminClient();
        await admin.PutAsJsonAsync("/api/features/features.promotions", new { enabled = true });
        var created = await CreateTokenAsync(admin, "Admin", "ops agent");
        Assert.Equal("Admin", created.GetProperty("access").GetString());
        Assert.Contains("InfraPortal.Admin", Roles(created));
        var tokenId = created.GetProperty("id").GetGuid();
        using var client = TokenClient(_factory, created);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/audit")).StatusCode);

        var candidateId = await SeedCandidateWithTicketAsync("pat-admin", "PATADM-1");
        var reject = await client.PostAsJsonAsync($"/api/promotions/admin/candidates/{candidateId}/reject",
            new { reason = "superseded, cleared by the ops agent" });
        Assert.Equal(HttpStatusCode.OK, reject.StatusCode);
        Assert.Equal("Rejected", (await reject.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var entry = await db.AuditLog.AsNoTracking()
            .SingleAsync(a => a.Action == "personal_token.decision" && a.EntityId == tokenId);
        Assert.Equal("Admin User", entry.ActorName);
        Assert.Contains($"/api/promotions/admin/candidates/{candidateId}/reject", entry.Metadata);
    }

    [Fact]
    public async Task Admin_token_still_does_no_pipeline_work_and_manages_no_tokens()
    {
        using var admin = _factory.CreateAdminClient();
        using var client = TokenClient(_factory, await CreateTokenAsync(admin, "Admin"));

        var ingest = await client.PostAsJsonAsync("/api/deployments/events", new
        {
            product = "pat-shop", service = "api", environment = "staging", version = "1.0.0", source = "test", deployedAt = DateTimeOffset.UtcNow,
        });
        Assert.Equal(HttpStatusCode.Forbidden, ingest.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/me/tokens")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.PostAsJsonAsync("/api/me/tokens", new { name = "child", expiresInDays = 7, access = "Admin" })).StatusCode);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    /// <summary>A Pending staging → prod candidate gated on one approver group, carrying one ticket.</summary>
    private async Task<Guid> SeedCandidateWithTicketAsync(string product, string workItemKey)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var snapshot = new ResolvedPolicySnapshot(PolicyId: Guid.NewGuid(), EscalationGroup: null)
        {
            ApprovalSteps =
            [
                new ApprovalStep("Approval",
                [
                    new ApproverRequirement("Release Manager", [], ["release.manager@example.com"], 1),
                ]),
            ],
        };
        var candidate = new PromotionCandidate
        {
            Id = Guid.NewGuid(),
            Product = product,
            Service = "api",
            SourceEnv = "staging",
            TargetEnv = "prod",
            Version = "v1.0.0",
            Status = PromotionStatus.Pending,
            PolicyId = snapshot.PolicyId,
            ResolvedPolicyJson = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            CreatedAt = DateTimeOffset.UtcNow,
            ParticipantsJson = "[]",
        };
        db.PromotionCandidates.Add(candidate);
        db.PromotionWorkItems.Add(new PromotionWorkItem
        {
            Id = Guid.NewGuid(),
            CandidateId = candidate.Id,
            WorkItemKey = workItemKey,
            Product = product,
            Service = "api",
            TargetEnv = "prod",
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        return candidate.Id;
    }
}
