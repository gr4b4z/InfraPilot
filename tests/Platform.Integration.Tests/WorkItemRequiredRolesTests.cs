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
using Platform.Api.Infrastructure.Persistence;

namespace Platform.Integration.Tests;

/// <summary>
/// Covers the promotion policy's <c>requiredWorkItemRoles</c>: the roles every work item on a gated
/// candidate must have somebody in, and the "incomplete work item" signal derived from them.
///
/// <para>The signal is computed on read from the candidate's policy snapshot and its current
/// participants, so these tests exercise all three ways it has to stay correct: at creation time, after
/// somebody is assigned, and after the policy itself is edited (which re-snapshots pending
/// candidates).</para>
/// </summary>
public class WorkItemRequiredRolesTests
    : IClassFixture<WorkItemRequiredRolesTests.RequiredRolesFactory>, IDisposable
{
    private const string QaOwnerRole = "qa-owner";

    private readonly RequiredRolesFactory _factory;
    private readonly HttpClient _apiKeyClient;
    private readonly HttpClient _adminClient;

    public WorkItemRequiredRolesTests(RequiredRolesFactory factory)
    {
        _factory = factory;
        _apiKeyClient = factory.CreateClient();
        _apiKeyClient.DefaultRequestHeaders.Add("X-Api-Key", RequiredRolesFactory.TestApiKey);
        _adminClient = CreateAuthenticatedClient("admin@localhost", "admin123");
    }

    public void Dispose()
    {
        _apiKeyClient.Dispose();
        _adminClient.Dispose();
    }

    // ── The promotion surfaces ───────────────────────────────────────────────

    [Fact]
    public async Task PromotionList_ReportsWorkItemsMissingARequiredRole()
    {
        var product = NewProduct();
        await SeedPolicyAsync(product, requiredRoles: new[] { QaOwnerRole });

        var withOwner = await CreatePromotionAsync(product, "svc-owned", "OWNED-1",
            new[] { new { role = QaOwnerRole, displayName = "Other", email = "other@example.com" } });
        var withoutOwner = await CreatePromotionAsync(product, "svc-unowned", "UNOWNED-1",
            new[] { new { role = "reviewer", displayName = "Other", email = "other@example.com" } });

        var candidates = await ListCandidatesAsync(product);

        var owned = candidates[withOwner];
        Assert.Contains(QaOwnerRole, RequiredRoles(owned));
        Assert.Empty(RoleGaps(owned));

        var unowned = candidates[withoutOwner];
        var gap = Assert.Single(RoleGaps(unowned));
        Assert.Equal("UNOWNED-1", gap.WorkItemKey);
        Assert.Equal(new[] { QaOwnerRole }, gap.MissingRoles);
    }

    [Fact]
    public async Task PromotionList_ReportsNoGapsWhenThePolicyRequiresNoRoles()
    {
        var product = NewProduct();
        await SeedPolicyAsync(product, requiredRoles: Array.Empty<string>());

        var id = await CreatePromotionAsync(product, "svc-norule", "NORULE-1", referenceParticipants: null);

        var candidate = (await ListCandidatesAsync(product))[id];
        Assert.Empty(RequiredRoles(candidate));
        Assert.Empty(RoleGaps(candidate));
    }

    [Fact]
    public async Task WorkItemDetail_NamesTheMissingRole_AndClearsItOnceSomeoneIsAssigned()
    {
        var product = NewProduct();
        await SeedPolicyAsync(product, requiredRoles: new[] { QaOwnerRole });
        var candidateId = await CreatePromotionAsync(
            product, "svc-detail", "DETAIL-1", referenceParticipants: null);

        var before = await GetDetailAsync("DETAIL-1", product);
        Assert.Equal(new[] { QaOwnerRole }, Strings(before, "requiredRoles"));
        Assert.Equal(new[] { QaOwnerRole }, Strings(before, "missingRoles"));

        // Assigning somebody to the role is what resolves it — the same write the UI's Assign performs.
        await AssignAsync(candidateId, "DETAIL-1", QaOwnerRole, "other@example.com", "Other");

        var after = await GetDetailAsync("DETAIL-1", product);
        Assert.Empty(Strings(after, "missingRoles"));
        Assert.Equal(new[] { QaOwnerRole }, Strings(after, "requiredRoles"));
    }

    [Fact]
    public async Task EditingThePolicy_ReGatesPendingCandidates()
    {
        // "This should work ... if Promotion policy changes": the requirement lands on candidates that
        // were created before it existed, because editing a policy re-snapshots pending candidates.
        var product = NewProduct();
        var policyId = await SeedPolicyAsync(product, requiredRoles: Array.Empty<string>());
        var candidateId = await CreatePromotionAsync(
            product, "svc-regate", "REGATE-1", referenceParticipants: null);

        Assert.Empty(RoleGaps((await ListCandidatesAsync(product))[candidateId]));

        await UpdatePolicyAsync(policyId, product, requiredRoles: new[] { QaOwnerRole });

        var gap = Assert.Single(RoleGaps((await ListCandidatesAsync(product))[candidateId]));
        Assert.Equal("REGATE-1", gap.WorkItemKey);
        Assert.Equal(new[] { QaOwnerRole }, gap.MissingRoles);
    }

    // ── The queue filters ────────────────────────────────────────────────────

    [Fact]
    public async Task RoleRequirementMissing_ReturnsOnlyItemsWithAnEmptyRequiredRole()
    {
        var product = NewProduct();
        await SeedPolicyAsync(product, requiredRoles: new[] { QaOwnerRole });

        await CreatePromotionAsync(product, "svc-q-owned", "QOWNED-1",
            new[] { new { role = QaOwnerRole, displayName = "Other", email = "other@example.com" } });
        // A named person in some OTHER role doesn't fill the required one.
        await CreatePromotionAsync(product, "svc-q-other", "QOTHER-1",
            new[] { new { role = "reviewer", displayName = "Other", email = "other@example.com" } });
        await CreatePromotionAsync(product, "svc-q-bare", "QBARE-1", referenceParticipants: null);

        var missing = await GetPendingAsync(roleRequirement: "missing");
        Assert.Contains("QOTHER-1", missing);
        Assert.Contains("QBARE-1", missing);
        Assert.DoesNotContain("QOWNED-1", missing);
    }

    [Fact]
    public async Task RoleRequirementMissing_IgnoresItemsUnderAPolicyThatRequiresNothing()
    {
        var product = NewProduct();
        await SeedPolicyAsync(product, requiredRoles: Array.Empty<string>());
        await CreatePromotionAsync(product, "svc-q-norule", "QNORULE-1", referenceParticipants: null);

        Assert.DoesNotContain("QNORULE-1", await GetPendingAsync(roleRequirement: "missing"));
        // Still in the queue at large — it just isn't incomplete.
        Assert.Contains("QNORULE-1", await GetPendingAsync(roleRequirement: null));
    }

    [Fact]
    public async Task RoleRequirementAssigned_MatchesOnlyTheRolesThePolicyRequires()
    {
        var product = NewProduct();
        await SeedPolicyAsync(product, requiredRoles: new[] { QaOwnerRole });

        // I'm the required role → mine.
        await CreatePromotionAsync(product, "svc-a-owner", "AOWNER-1",
            new[] { new { role = QaOwnerRole, displayName = "Admin", email = "admin@localhost" } });
        // I'm named, but in a role the policy doesn't require → not mine to answer for.
        await CreatePromotionAsync(product, "svc-a-reviewer", "AREVIEWER-1",
            new[] { new { role = "reviewer", displayName = "Admin", email = "admin@localhost" } });

        var mine = await GetPendingAsync(assignee: "admin@localhost", roleRequirement: "assigned");
        Assert.Contains("AOWNER-1", mine);
        Assert.DoesNotContain("AREVIEWER-1", mine);

        // Without the narrowing, being named at all is enough — the plain `assignee` behaviour,
        // still part of the API even though the queue page always couples a person pick with
        // roleRequirement=assigned now.
        var anyRole = await GetPendingAsync(assignee: "admin@localhost", roleRequirement: null);
        Assert.Contains("AREVIEWER-1", anyRole);
    }

    [Fact]
    public async Task RoleRequirementMissing_ParticipantWithoutEmail_DoesNotFillTheRole()
    {
        // A required role with a name but nobody behind it is an empty slot, not an assignment —
        // otherwise "which items need someone?" would skip the items whose owner is a label with
        // no address. Same bar the assignee filter applies.
        var product = NewProduct();
        await SeedPolicyAsync(product, requiredRoles: new[] { QaOwnerRole });

        await CreatePromotionAsync(product, "svc-nameless", "NOEMAIL-1",
            new[] { new { role = QaOwnerRole, displayName = "Unknown Owner", email = (string?)null } });

        Assert.Contains("NOEMAIL-1", await GetPendingAsync(roleRequirement: "missing"));
    }

    [Fact]
    public async Task AssigneeRollup_OffersOnlyRequiredRoleHolders()
    {
        // The rollup backs the queue's person dropdown, whose picks narrow with
        // roleRequirement=assigned — so a person named only in a non-required role would be a
        // choice that filters to nothing, and must not be offered.
        var product = NewProduct();
        await SeedPolicyAsync(product, requiredRoles: new[] { QaOwnerRole });

        var scope = $"rollup-{Guid.NewGuid():N}"[..14];
        var ownerEmail = $"owner-{scope}@example.com";
        var reviewerEmail = $"reviewer-{scope}@example.com";

        await CreatePromotionAsync(product, "svc-rollup", "ROLLUP-1", new object[]
        {
            new { role = QaOwnerRole, displayName = "Ola", email = ownerEmail },
            new { role = "reviewer", displayName = "Rita", email = reviewerEmail },
        });

        var resp = await _adminClient.GetAsync("/api/work-items/me/pending");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var assignees = (await Deserialize(resp)).GetProperty("assignees").EnumerateArray()
            .Select(a => (
                Email: a.GetProperty("email").GetString()!,
                Role: a.GetProperty("role").GetString()!))
            .ToList();

        Assert.Contains(assignees, a => a.Email == ownerEmail && a.Role == QaOwnerRole);
        Assert.DoesNotContain(assignees, a => a.Email == reviewerEmail);
    }

    [Fact]
    public async Task RoleRequirementAssigned_ExcludesItemsUnderAPolicyThatRequiresNothing()
    {
        // An item nobody was ever made answerable for can't be "assigned to me", however many roles
        // carry my name — otherwise the tab would drift back into "items I'm mentioned on".
        var product = NewProduct();
        await SeedPolicyAsync(product, requiredRoles: Array.Empty<string>());
        await CreatePromotionAsync(product, "svc-a-norule", "ANORULE-1",
            new[] { new { role = QaOwnerRole, displayName = "Admin", email = "admin@localhost" } });

        Assert.DoesNotContain(
            "ANORULE-1",
            await GetPendingAsync(assignee: "admin@localhost", roleRequirement: "assigned"));
    }

    [Fact]
    public async Task PendingRows_CarryTheRequiredAndMissingRoles()
    {
        var product = NewProduct();
        await SeedPolicyAsync(product, requiredRoles: new[] { QaOwnerRole });
        await CreatePromotionAsync(product, "svc-rowshape", "ROWSHAPE-1", referenceParticipants: null);

        var row = Assert.Single(
            await GetPendingRowsAsync(roleRequirement: "missing"),
            r => r.GetProperty("workItemKey").GetString() == "ROWSHAPE-1");
        Assert.Equal(new[] { QaOwnerRole }, Strings(row, "requiredRoles"));
        Assert.Equal(new[] { QaOwnerRole }, Strings(row, "missingRoles"));
    }

    [Fact]
    public async Task UnknownRoleRequirementValue_LeavesTheQueueUnnarrowed()
    {
        // The parameter only narrows a read, so a typo returns the full queue rather than a 400 that
        // would blank the page.
        var product = NewProduct();
        await SeedPolicyAsync(product, requiredRoles: new[] { QaOwnerRole });
        await CreatePromotionAsync(product, "svc-typo", "TYPO-1",
            new[] { new { role = QaOwnerRole, displayName = "Other", email = "other@example.com" } });

        Assert.Contains("TYPO-1", await GetPendingAsync(roleRequirement: "nonsense"));
    }

    // ── Edges that don't create work items (TracksWorkItems) ─────────────────

    [Fact]
    public async Task UntrackedEdge_CreatesNoWorkItems_ButKeepsTheChangeSet()
    {
        var product = NewProduct();
        await SeedPolicyAsync(
            product, requiredRoles: new[] { QaOwnerRole }, tracksWorkItems: false);
        var candidateId = await CreatePromotionAsync(
            product, "svc-untracked", "UNTRACKED-1", referenceParticipants: null);

        // Nothing in the queue, and no detail page — there is no work item to sign off.
        Assert.DoesNotContain("UNTRACKED-1", await GetPendingAsync(roleRequirement: null));
        Assert.DoesNotContain("UNTRACKED-1", await GetPendingAsync(roleRequirement: "missing"));
        var detail = await _adminClient.GetAsync(
            $"/api/work-items/UNTRACKED-1/detail?product={Uri.EscapeDataString(product)}&targetEnv=prod");
        Assert.Equal(HttpStatusCode.NotFound, detail.StatusCode);

        // The promotion still records what it carries, and says the edge isn't tracked. Role
        // requirements are reported as none: there are no work items to require anyone on.
        var candidate = (await ListCandidatesAsync(product))[candidateId];
        Assert.False(candidate.GetProperty("tracksWorkItems").GetBoolean());
        Assert.Empty(RequiredRoles(candidate));
        Assert.Empty(RoleGaps(candidate));
        Assert.Contains(
            candidate.GetProperty("sourceEventReferences").EnumerateArray(),
            r => r.GetProperty("key").GetString() == "UNTRACKED-1");
    }

    [Fact]
    public async Task TurningTrackingOff_RemovesWorkItemsFromExistingPendingPromotions()
    {
        var product = NewProduct();
        var policyId = await SeedPolicyAsync(
            product, requiredRoles: new[] { QaOwnerRole }, tracksWorkItems: true);
        var candidateId = await CreatePromotionAsync(
            product, "svc-offswitch", "OFFSWITCH-1", referenceParticipants: null);

        Assert.Contains("OFFSWITCH-1", await GetPendingAsync(roleRequirement: "missing"));

        await UpdatePolicyAsync(
            policyId, product, requiredRoles: new[] { QaOwnerRole }, tracksWorkItems: false);

        Assert.DoesNotContain("OFFSWITCH-1", await GetPendingAsync(roleRequirement: null));
        var candidate = (await ListCandidatesAsync(product))[candidateId];
        Assert.False(candidate.GetProperty("tracksWorkItems").GetBoolean());
        Assert.Empty(RoleGaps(candidate));
    }

    [Fact]
    public async Task TurningTrackingBackOn_RestoresWorkItemsFromTheChangeSet()
    {
        // The other direction: an edge that starts untracked and is later opened up to QA has to
        // produce work items for the promotions already in flight, or they'd never get one.
        var product = NewProduct();
        var policyId = await SeedPolicyAsync(
            product, requiredRoles: new[] { QaOwnerRole }, tracksWorkItems: false);
        await CreatePromotionAsync(product, "svc-onswitch", "ONSWITCH-1", referenceParticipants: null);

        Assert.DoesNotContain("ONSWITCH-1", await GetPendingAsync(roleRequirement: null));

        await UpdatePolicyAsync(
            policyId, product, requiredRoles: new[] { QaOwnerRole }, tracksWorkItems: true);

        Assert.Contains("ONSWITCH-1", await GetPendingAsync(roleRequirement: "missing"));
    }

    [Fact]
    public async Task UntrackedEdge_DoesNotBlockApprovalOnTheWorkItemGate()
    {
        // RequireAllWorkItemsApproved is inert when the edge creates no work items: there is nothing
        // to sign off, so a promotion that would otherwise be held can be approved.
        var product = NewProduct();
        await SeedPolicyAsync(
            product,
            requiredRoles: Array.Empty<string>(),
            tracksWorkItems: false,
            requireAllWorkItemsApproved: true);
        var candidateId = await CreatePromotionAsync(
            product, "svc-nogate", "NOGATE-1", referenceParticipants: null);

        var approve = await _adminClient.PostAsJsonAsync(
            $"/api/promotions/{candidateId}/approve", new { comment = "no work items on this edge" });
        Assert.Equal(HttpStatusCode.OK, approve.StatusCode);
    }

    [Fact]
    public async Task TrackedEdge_IsTheDefaultForACallerThatOmitsTheFlag()
    {
        // The upsert payload defaults TracksWorkItems to true, so a client written before the flag
        // existed keeps creating work items.
        var product = NewProduct();
        await _adminClient.PutAsJsonAsync("/api/features/features.promotions", new { enabled = true });
        var resp = await _adminClient.PostAsJsonAsync("/api/promotions/admin/policies", new
        {
            product,
            service = (string?)null,
            sourceEnv = "staging",
            targetEnv = "prod",
            steps = Array.Empty<object>(),
            escalationGroup = (string?)null,
        });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var body = await Deserialize(resp);
        Assert.True(body.GetProperty("tracksWorkItems").GetBoolean());
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static string NewProduct() => $"req-{Guid.NewGuid():N}"[..18];

    private record RoleGap(string WorkItemKey, string[] MissingRoles);

    private static string[] Strings(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Select(v => v.GetString()!).ToArray()
            : Array.Empty<string>();

    private static string[] RequiredRoles(JsonElement candidate)
        => Strings(candidate, "requiredWorkItemRoles");

    private static RoleGap[] RoleGaps(JsonElement candidate)
        => candidate.GetProperty("workItemRoleGaps").EnumerateArray()
            .Select(g => new RoleGap(
                g.GetProperty("workItemKey").GetString()!,
                Strings(g, "missingRoles")))
            .ToArray();

    /// <summary>Candidates for a product, keyed by id, so a test can pick out the one it created.</summary>
    private async Task<Dictionary<string, JsonElement>> ListCandidatesAsync(string product)
    {
        var resp = await _adminClient.GetAsync(
            $"/api/promotions?product={Uri.EscapeDataString(product)}&status=Pending");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await Deserialize(resp);
        return body.GetProperty("candidates").EnumerateArray()
            .ToDictionary(c => c.GetProperty("id").GetString()!, c => c);
    }

    private async Task<JsonElement> GetDetailAsync(string key, string product)
    {
        var resp = await _adminClient.GetAsync(
            $"/api/work-items/{Uri.EscapeDataString(key)}/detail"
            + $"?product={Uri.EscapeDataString(product)}&targetEnv=prod");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return await Deserialize(resp);
    }

    private async Task<List<JsonElement>> GetPendingRowsAsync(
        string? assignee = null, string? roleRequirement = null)
    {
        var query = new List<string>();
        if (!string.IsNullOrEmpty(assignee)) query.Add($"assignee={Uri.EscapeDataString(assignee)}");
        if (!string.IsNullOrEmpty(roleRequirement))
            query.Add($"roleRequirement={Uri.EscapeDataString(roleRequirement)}");
        var url = query.Count == 0
            ? "/api/work-items/me/pending"
            : $"/api/work-items/me/pending?{string.Join("&", query)}";

        var resp = await _adminClient.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await Deserialize(resp);
        return body.GetProperty("tickets").EnumerateArray().ToList();
    }

    private async Task<List<string>> GetPendingAsync(
        string? assignee = null, string? roleRequirement = null)
        => (await GetPendingRowsAsync(assignee, roleRequirement))
            .Select(t => t.GetProperty("workItemKey").GetString()!)
            .ToList();

    private async Task AssignAsync(
        string candidateId, string referenceKey, string role, string email, string displayName)
    {
        var resp = await _adminClient.PatchAsJsonAsync(
            $"/api/promotions/{candidateId}/references/{Uri.EscapeDataString(referenceKey)}/participants",
            new { role, assignee = new { email, displayName } });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    /// <summary>
    /// Creates a Pending staging→prod candidate carrying one work-item reference, optionally with
    /// participants on it. Returns the candidate id.
    /// </summary>
    private async Task<string> CreatePromotionAsync(
        string product, string service, string referenceKey, object[]? referenceParticipants)
    {
        var version = $"v{Guid.NewGuid():N}"[..10];

        // Source validation requires a succeeded deploy of this version in the source env.
        await _apiKeyClient.PostAsJsonAsync("/api/deployments/events", new
        {
            product,
            service,
            environment = "staging",
            version,
            source = "integration-test",
            deployedAt = DateTimeOffset.UtcNow,
            status = "succeeded",
        });

        object reference = referenceParticipants is null
            ? new { type = "work-item", provider = "jira", key = referenceKey, title = "Required roles test" }
            : new
            {
                type = "work-item",
                provider = "jira",
                key = referenceKey,
                title = "Required roles test",
                participants = referenceParticipants,
            };

        var create = await _apiKeyClient.PostAsJsonAsync("/api/promotions", new
        {
            product,
            service,
            sourceEnv = "staging",
            targetEnv = "prod",
            version,
            references = new[] { reference },
        });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var body = await Deserialize(create);
        return body.GetProperty("id").GetString()!;
    }

    /// <summary>
    /// Enables the promotions flag and seeds a gated product-level policy for staging→prod with the
    /// given required work-item roles. Returns the policy id so a test can edit it.
    /// </summary>
    private async Task<string> SeedPolicyAsync(
        string product,
        string[] requiredRoles,
        bool tracksWorkItems = true,
        bool requireAllWorkItemsApproved = false)
    {
        await _adminClient.PutAsJsonAsync("/api/features/features.promotions", new { enabled = true });
        var resp = await _adminClient.PostAsJsonAsync(
            "/api/promotions/admin/policies",
            PolicyPayload(product, requiredRoles, tracksWorkItems, requireAllWorkItemsApproved));
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var body = await Deserialize(resp);
        return body.GetProperty("id").GetString()!;
    }

    private async Task UpdatePolicyAsync(
        string policyId, string product, string[] requiredRoles, bool tracksWorkItems = true)
    {
        var resp = await _adminClient.PutAsJsonAsync(
            $"/api/promotions/admin/policies/{policyId}",
            PolicyPayload(product, requiredRoles, tracksWorkItems, requireAllWorkItemsApproved: false));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    private static object PolicyPayload(
        string product,
        string[] requiredRoles,
        bool tracksWorkItems,
        bool requireAllWorkItemsApproved) => new
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
                        name = "Approvers",
                        groups = new[] { "InfraPortal.Admin" },
                        users = Array.Empty<string>(),
                        minApprovers = 1,
                    },
                },
            },
        },
        tracksWorkItems,
        requiredWorkItemRoles = requiredRoles,
        requireAllWorkItemsApproved,
        escalationGroup = (string?)null,
    };

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

    // ── Factory ─────────────────────────────────────────────────────────────

    public class RequiredRolesFactory : WebApplicationFactory<Program>
    {
        public const string TestApiKey = "required-roles-test-api-key-13579";

        private readonly SqliteConnection _connection;

        public RequiredRolesFactory()
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

            builder.UseSetting("Deployments:ApiKeys:0:Name", "required-roles-integration-test");
            builder.UseSetting("Deployments:ApiKeys:0:Key", TestApiKey);
            builder.UseSetting("Deployments:ApiKeys:0:Roles:0", "InfraPortal.Admin");

            builder.ConfigureServices(services =>
            {
                RemoveService<DbContextOptions<PostgresPlatformDbContext>>(services);
                RemoveService<DbContextOptions<SqlServerPlatformDbContext>>(services);
                RemoveService<DbContextOptions<PlatformDbContext>>(services);
                RemoveService<PostgresPlatformDbContext>(services);
                RemoveService<SqlServerPlatformDbContext>(services);
                RemoveService<PlatformDbContext>(services);

                services.AddSingleton<DbConnection>(_connection);
                services.AddDbContext<PlatformDbContext, SqliteTestDbContext>((sp, options) =>
                    options.UseSqlite(sp.GetRequiredService<DbConnection>()));
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
