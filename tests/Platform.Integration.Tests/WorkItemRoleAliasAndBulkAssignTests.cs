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
using Platform.Api.Features.Settings;
using Platform.Api.Infrastructure.Persistence;

namespace Platform.Integration.Tests;

/// <summary>
/// Three additions to the participant-role vocabulary (Settings → Participant Roles) and the
/// assignment flows they feed:
/// <list type="bullet">
///   <item><b>Aliases</b> — Jira's "QA" arriving as <c>qa</c> on a ticket whose policy requires a
///         <c>qa-owner</c>. Listing <c>qa</c> as an alias makes the ticket complete on every surface
///         without rewriting it, and assignment replaces the aliased entry instead of adding a second
///         person.</item>
///   <item><b>Assignee groups</b> — the person search narrows to a role's groups.</item>
///   <item><b>Assign to all</b> — one write puts somebody in a role on every work item of a
///         promotion.</item>
/// </list>
/// The settings row is global to the shared fixture, so every test that changes it resets it.
/// </summary>
public class WorkItemRoleAliasAndBulkAssignTests
    : IClassFixture<WorkItemRoleAliasAndBulkAssignTests.AliasFactory>, IDisposable
{
    private const string QaOwner = "qa-owner";

    private readonly AliasFactory _factory;
    private readonly HttpClient _apiKeyClient;
    private readonly HttpClient _adminClient;

    public WorkItemRoleAliasAndBulkAssignTests(AliasFactory factory)
    {
        _factory = factory;
        _apiKeyClient = factory.CreateClient();
        _apiKeyClient.DefaultRequestHeaders.Add("X-Api-Key", AliasFactory.TestApiKey);
        _adminClient = CreateAuthenticatedClient("admin@localhost", "admin123");
    }

    public void Dispose()
    {
        _apiKeyClient.Dispose();
        _adminClient.Dispose();
    }

    // ── Saving aliases and groups ────────────────────────────────────────────

    [Fact]
    public async Task Settings_AliasesAndGroups_AreCleanedAndRoundTrip()
    {
        try
        {
            var put = await SaveRolesAsync(
                Role("reviewer", "Reviewer"),
                Role(QaOwner, "QA owner",
                    aliases: ["QA", "Quality Assurance", "qa-owner", "qa", " "],
                    groups: [new { id = " grp-qa ", name = "" }, new { id = "grp-qa", name = "dupe" }]));
            Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);

            var settings = await Deserialize(await _adminClient.GetAsync("/api/settings"));
            var owner = settings.GetProperty("roles").EnumerateArray()
                .Single(r => r.GetProperty("key").GetString() == QaOwner);
            // Canonicalised, deduped, and the role's own key dropped.
            Assert.Equal(new[] { "qa", "quality-assurance" }, Strings(owner, "aliases"));
            var group = Assert.Single(owner.GetProperty("assigneeGroups").EnumerateArray());
            Assert.Equal("grp-qa", group.GetProperty("id").GetString());
            Assert.Equal("grp-qa", group.GetProperty("name").GetString());
        }
        finally
        {
            await ResetSettingsAsync();
        }
    }

    [Fact]
    public async Task Settings_AliasThatIsAlsoARole_IsRejectedAndSaysHowToFoldIt()
    {
        try
        {
            var put = await SaveRolesAsync(Role("qa", "QA"), Role(QaOwner, "QA owner", aliases: ["qa"]));

            Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
            var error = (await Deserialize(put)).GetProperty("error").GetString() ?? "";
            Assert.Contains("'qa'", error);
            Assert.Contains("Remove the 'qa' role", error);
        }
        finally
        {
            await ResetSettingsAsync();
        }
    }

    [Fact]
    public async Task Settings_SameAliasOnTwoRoles_IsRejected()
    {
        try
        {
            var put = await SaveRolesAsync(
                Role(QaOwner, "QA owner", aliases: ["tester"]),
                Role("reviewer", "Reviewer", aliases: ["Tester"]));

            Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
            Assert.Contains("both", (await Deserialize(put)).GetProperty("error").GetString());
        }
        finally
        {
            await ResetSettingsAsync();
        }
    }

    // ── An alias fills the role it names, everywhere completeness is read ────

    [Fact]
    public async Task Alias_FillsTheRequiredRole_OnThePromotionTheWorkItemAndTheQueue()
    {
        var product = NewProduct();
        await SeedPolicyAsync(product, requiredRoles: [QaOwner]);
        var candidateId = await CreatePromotionAsync(product, "svc-alias", ["ALIAS-1"],
            new() { ["ALIAS-1"] = [new { role = "qa", displayName = "Quinn", email = "quinn@example.com" }] });

        // Before the alias, Jira's "qa" is a different role and the ticket needs a QA owner.
        Assert.Single(RoleGaps(await GetCandidateAsync(candidateId)));

        try
        {
            await FoldQaIntoQaOwnerAsync();

            Assert.Empty(RoleGaps(await GetCandidateAsync(candidateId)));
            Assert.Empty(RoleGaps((await ListCandidatesAsync(product))[candidateId]));

            var detail = await GetDetailAsync("ALIAS-1", product, "svc-alias");
            Assert.Empty(Strings(detail, "missingRoles"));

            // "Not assigned" no longer lists it, and filtering by the person in a required role does.
            Assert.DoesNotContain("ALIAS-1", await GetPendingKeysAsync(roleRequirement: "missing"));
            Assert.Contains("ALIAS-1",
                await GetPendingKeysAsync(assignee: "quinn@example.com", roleRequirement: "assigned"));
        }
        finally
        {
            await ResetSettingsAsync();
        }
    }

    [Fact]
    public async Task Alias_InAPolicysRequiredRoles_RequiresTheRoleItNames()
    {
        // A policy saved as requiring "qa" before "qa" was folded into "qa-owner".
        var product = NewProduct();
        await SeedPolicyAsync(product, requiredRoles: ["qa"]);
        var candidateId = await CreatePromotionAsync(product, "svc-req-alias", ["REQ-ALIAS-1"],
            new() { ["REQ-ALIAS-1"] = [new { role = QaOwner, displayName = "Ola", email = "ola@example.com" }] });

        try
        {
            await FoldQaIntoQaOwnerAsync();

            var candidate = await GetCandidateAsync(candidateId);
            Assert.Equal(new[] { QaOwner }, Strings(candidate, "requiredWorkItemRoles"));
            Assert.Empty(RoleGaps(candidate));
        }
        finally
        {
            await ResetSettingsAsync();
        }
    }

    [Fact]
    public async Task Assigning_ReplacesTheAliasedEntry_AndStoresTheCanonicalRole()
    {
        var product = NewProduct();
        await SeedPolicyAsync(product, requiredRoles: [QaOwner]);
        var candidateId = await CreatePromotionAsync(product, "svc-replace", ["REPLACE-1"],
            new() { ["REPLACE-1"] = [new { role = "qa", displayName = "Quinn", email = "quinn@example.com" }] });

        try
        {
            await FoldQaIntoQaOwnerAsync();

            // Assigned through the alias — the chip the UI shows for Quinn carries role "qa".
            var resp = await _adminClient.PatchAsJsonAsync(
                $"/api/promotions/{candidateId}/references/REPLACE-1/participants",
                new { role = "qa", assignee = new { email = "ola@example.com", displayName = "Ola" } });
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

            var participants = (await Deserialize(resp)).GetProperty("participants").EnumerateArray().ToList();
            var owner = Assert.Single(participants);
            Assert.Equal(QaOwner, owner.GetProperty("role").GetString());
            Assert.Equal("ola@example.com", owner.GetProperty("email").GetString());
        }
        finally
        {
            await ResetSettingsAsync();
        }
    }

    // ── Assign to all ────────────────────────────────────────────────────────

    [Fact]
    public async Task AssignToAll_PutsThePersonOnEveryWorkItem_ReplacingWhoeverHeldTheRole()
    {
        var product = NewProduct();
        await SeedPolicyAsync(product, requiredRoles: [QaOwner]);
        var candidateId = await CreatePromotionAsync(product, "svc-bulk", ["BULK-1", "BULK-2", "BULK-3"],
            new()
            {
                ["BULK-1"] = [new { role = QaOwner, displayName = "Old", email = "old@example.com" }],
                ["BULK-2"] = [new { role = "reviewer", displayName = "Rita", email = "rita@example.com" }],
            });

        var resp = await BulkAssignAsync(candidateId, QaOwner, "zed@example.com", "Zed");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await Deserialize(resp);
        Assert.Equal(new[] { "BULK-1", "BULK-2", "BULK-3" }, Strings(body, "updated"));

        var candidate = await GetCandidateAsync(candidateId);
        Assert.Empty(RoleGaps(candidate));
        foreach (var key in new[] { "BULK-1", "BULK-2", "BULK-3" })
        {
            var owners = ReferenceParticipants(candidate, key).Where(p => p.Role == QaOwner).ToList();
            Assert.Equal("zed@example.com", Assert.Single(owners).Email);
        }
        // Other roles on a ticket are left alone.
        Assert.Contains(ReferenceParticipants(candidate, "BULK-2"), p => p.Role == "reviewer");

        // Running it again changes nothing, and says so.
        var again = await Deserialize(await BulkAssignAsync(candidateId, QaOwner, "zed@example.com", "Zed"));
        Assert.Empty(Strings(again, "updated"));
        Assert.Equal(3, Strings(again, "unchanged").Length);
    }

    [Fact]
    public async Task AssignToAll_OnlyMissing_LeavesTicketsThatHaveSomebodyAlone()
    {
        var product = NewProduct();
        await SeedPolicyAsync(product, requiredRoles: [QaOwner]);
        var candidateId = await CreatePromotionAsync(product, "svc-gaps", ["GAP-1", "GAP-2", "GAP-3"],
            new()
            {
                ["GAP-1"] = [new { role = QaOwner, displayName = "Kept", email = "kept@example.com" }],
                ["GAP-2"] = [new { role = "qa", displayName = "Aliased", email = "aliased@example.com" }],
            });

        try
        {
            await FoldQaIntoQaOwnerAsync();

            var body = await Deserialize(
                await BulkAssignAsync(candidateId, QaOwner, "zed@example.com", "Zed", onlyMissing: true));

            Assert.Equal(new[] { "GAP-3" }, Strings(body, "updated"));
            // GAP-2's "qa" counts as a QA owner through the alias.
            Assert.Equal(new[] { "GAP-1", "GAP-2" }, Strings(body, "skipped"));

            var candidate = await GetCandidateAsync(candidateId);
            Assert.Contains(ReferenceParticipants(candidate, "GAP-1"), p => p.Email == "kept@example.com");
            Assert.Contains(ReferenceParticipants(candidate, "GAP-2"), p => p.Email == "aliased@example.com");
        }
        finally
        {
            await ResetSettingsAsync();
        }
    }

    [Fact]
    public async Task AssignToAll_WithNoAssignee_ClearsTheRoleEverywhere()
    {
        var product = NewProduct();
        await SeedPolicyAsync(product, requiredRoles: [QaOwner]);
        var candidateId = await CreatePromotionAsync(product, "svc-clear-all", ["CLR-1", "CLR-2"],
            new() { ["CLR-1"] = [new { role = QaOwner, displayName = "Old", email = "old@example.com" }] });

        var resp = await _adminClient.PatchAsJsonAsync(
            $"/api/promotions/{candidateId}/work-items/participants",
            new { role = QaOwner, assignee = (object?)null });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await Deserialize(resp);
        Assert.Equal(new[] { "CLR-1" }, Strings(body, "updated"));
        Assert.Equal(new[] { "CLR-2" }, Strings(body, "unchanged"));

        Assert.Equal(2, RoleGaps(await GetCandidateAsync(candidateId)).Length);
    }

    [Fact]
    public async Task AssignToAll_UnconfiguredRole_IsRefused()
    {
        var product = NewProduct();
        await SeedPolicyAsync(product, requiredRoles: [QaOwner]);
        var candidateId = await CreatePromotionAsync(product, "svc-bulk-bad", ["BAD-1"], new());

        var resp = await BulkAssignAsync(candidateId, "release-captain", "zed@example.com", "Zed");

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("Participant Roles", (await Deserialize(resp)).GetProperty("error").GetString());
    }

    [Fact]
    public async Task AssignToAll_RequiresQaOrAdmin()
    {
        var product = NewProduct();
        await SeedPolicyAsync(product, requiredRoles: [QaOwner]);
        var candidateId = await CreatePromotionAsync(product, "svc-bulk-auth", ["AUTH-1"], new());

        using var viewer = CreateAuthenticatedClient("viewer@localhost", "viewer123");
        var resp = await viewer.PatchAsJsonAsync(
            $"/api/promotions/{candidateId}/work-items/participants",
            new { role = QaOwner, assignee = new { email = "zed@example.com", displayName = "Zed" } });

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    // ── The person picker narrows to a role's groups ─────────────────────────

    [Fact]
    public async Task UserSearch_ForARoleWithAssigneeGroups_ReturnsOnlyTheirMembers()
    {
        try
        {
            var put = await SaveRolesAsync(
                Role("reviewer", "Reviewer"),
                Role(QaOwner, "QA owner", aliases: ["qa"],
                    groups: [new { id = "InfraPortal.QA", name = "InfraPortal.QA" }]));
            Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);

            // Local identity: a group is a role name on the user (qa@ and user@ carry InfraPortal.QA).
            var restricted = await SearchUsersAsync("localhost", QaOwner);
            Assert.Equal(new[] { "qa@localhost", "user@localhost" }, Emails(restricted).Order().ToArray());
            Assert.Equal("InfraPortal.QA",
                Assert.Single(restricted.GetProperty("restrictedTo").EnumerateArray()).GetProperty("id").GetString());

            // The alias is the same role, so the same restriction.
            Assert.Equal(Emails(restricted).Order(), Emails(await SearchUsersAsync("localhost", "qa")).Order());

            // A role without groups, or no role at all, searches everybody.
            var open = await SearchUsersAsync("localhost", "reviewer");
            Assert.Contains("admin@localhost", Emails(open));
            Assert.Empty(open.GetProperty("restrictedTo").EnumerateArray());
            Assert.Contains("admin@localhost", Emails(await SearchUsersAsync("localhost", role: null)));
        }
        finally
        {
            await ResetSettingsAsync();
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static string NewProduct() => $"alias-{Guid.NewGuid():N}"[..18];

    private sealed record Participant(string? Role, string? Email);

    private static object Role(
        string key, string displayName, string[]? aliases = null, object[]? groups = null)
        => new { key, displayName, aliases = aliases ?? [], assigneeGroups = groups ?? [] };

    /// <summary>
    /// The default vocabulary with the built-in <c>qa</c> role removed and listed as an alias of
    /// <c>qa-owner</c> — what an admin does to fix Jira's "QA".
    /// </summary>
    private async Task FoldQaIntoQaOwnerAsync()
    {
        var resp = await SaveRolesAsync(
            Role("triggered-by", "Triggered by"),
            Role("author", "Author"),
            Role("reviewer", "Reviewer"),
            Role(QaOwner, "QA owner", aliases: ["qa"]),
            Role("assignee", "Assignee"),
            Role("reporter", "Reporter"));
        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);
    }

    /// <summary>Replaces the configured roles; environments and the activity template are sent as read.</summary>
    private async Task<HttpResponseMessage> SaveRolesAsync(params object[] roles)
    {
        var current = await Deserialize(await _adminClient.GetAsync("/api/settings"));
        var payload = new
        {
            environments = current.GetProperty("environments").EnumerateArray()
                .Select(e => new
                {
                    key = e.GetProperty("key").GetString(),
                    displayName = e.GetProperty("displayName").GetString(),
                }).ToArray(),
            roles,
            activityTemplate = current.GetProperty("activityTemplate").EnumerateArray()
                .Select(l => new
                {
                    template = l.GetProperty("template").GetString(),
                    style = l.GetProperty("style").GetString(),
                }).ToArray(),
        };
        return await _adminClient.PutAsJsonAsync("/api/settings", payload);
    }

    private async Task ResetSettingsAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var existing = await db.PlatformSettings
            .FirstOrDefaultAsync(s => s.Key == AppSettingsService.SettingsKey);
        if (existing is not null)
        {
            db.PlatformSettings.Remove(existing);
            await db.SaveChangesAsync();
        }
    }

    private Task<HttpResponseMessage> BulkAssignAsync(
        string candidateId, string role, string email, string displayName, bool onlyMissing = false)
        => _adminClient.PatchAsJsonAsync(
            $"/api/promotions/{candidateId}/work-items/participants",
            new { role, assignee = new { email, displayName }, onlyMissing });

    private async Task<JsonElement> SearchUsersAsync(string q, string? role)
    {
        var url = $"/api/promotions/users/search?q={Uri.EscapeDataString(q)}"
                  + (role is null ? "" : $"&role={Uri.EscapeDataString(role)}");
        var resp = await _adminClient.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return await Deserialize(resp);
    }

    private static IEnumerable<string> Emails(JsonElement search)
        => search.GetProperty("users").EnumerateArray().Select(u => u.GetProperty("email").GetString()!);

    private static string[] Strings(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Select(v => v.GetString()!).ToArray()
            : Array.Empty<string>();

    private static JsonElement[] RoleGaps(JsonElement candidate)
        => candidate.GetProperty("workItemRoleGaps").EnumerateArray().ToArray();

    private static List<Participant> ReferenceParticipants(JsonElement candidate, string key)
    {
        var reference = candidate.GetProperty("sourceEventReferences").EnumerateArray()
            .First(r => r.GetProperty("type").GetString() == "work-item" && r.GetProperty("key").GetString() == key);
        return reference.TryGetProperty("participants", out var ps) && ps.ValueKind == JsonValueKind.Array
            ? ps.EnumerateArray()
                .Select(p => new Participant(p.GetProperty("role").GetString(), p.GetProperty("email").GetString()))
                .ToList()
            : new();
    }

    private async Task<Dictionary<string, JsonElement>> ListCandidatesAsync(string product)
    {
        var resp = await _adminClient.GetAsync(
            $"/api/promotions?product={Uri.EscapeDataString(product)}&status=Pending");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return (await Deserialize(resp)).GetProperty("candidates").EnumerateArray()
            .ToDictionary(c => c.GetProperty("id").GetString()!, c => c);
    }

    private async Task<JsonElement> GetCandidateAsync(string candidateId)
    {
        var resp = await _adminClient.GetAsync($"/api/promotions/{candidateId}");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return (await Deserialize(resp)).GetProperty("candidate");
    }

    private async Task<JsonElement> GetDetailAsync(string key, string product, string service)
    {
        var resp = await _adminClient.GetAsync(
            $"/api/work-items/{Uri.EscapeDataString(key)}/detail"
            + $"?product={Uri.EscapeDataString(product)}&service={Uri.EscapeDataString(service)}&targetEnv=prod");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return await Deserialize(resp);
    }

    private async Task<List<string>> GetPendingKeysAsync(string? assignee = null, string? roleRequirement = null)
    {
        var query = new List<string>();
        if (assignee is not null) query.Add($"assignee={Uri.EscapeDataString(assignee)}");
        if (roleRequirement is not null) query.Add($"roleRequirement={Uri.EscapeDataString(roleRequirement)}");
        var resp = await _adminClient.GetAsync(
            "/api/work-items/me/pending" + (query.Count == 0 ? "" : "?" + string.Join("&", query)));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return (await Deserialize(resp)).GetProperty("tickets").EnumerateArray()
            .Select(t => t.GetProperty("workItemKey").GetString()!)
            .ToList();
    }

    /// <summary>
    /// A Pending staging→prod candidate carrying one work-item reference per key, each with the
    /// participants given for it. Returns the candidate id.
    /// </summary>
    private async Task<string> CreatePromotionAsync(
        string product, string service, string[] keys, Dictionary<string, object[]> participantsByKey)
    {
        var version = $"v{Guid.NewGuid():N}"[..10];
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

        var references = keys.Select(key => new
        {
            type = "work-item",
            provider = "jira",
            key,
            title = $"Alias test {key}",
            participants = participantsByKey.GetValueOrDefault(key) ?? [],
        }).ToArray();

        var create = await _apiKeyClient.PostAsJsonAsync("/api/promotions", new
        {
            product,
            service,
            sourceEnv = "staging",
            targetEnv = "prod",
            version,
            references,
        });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        return (await Deserialize(create)).GetProperty("id").GetString()!;
    }

    private async Task SeedPolicyAsync(string product, string[] requiredRoles)
    {
        await _adminClient.PutAsJsonAsync("/api/features/features.promotions", new { enabled = true });
        var resp = await _adminClient.PostAsJsonAsync("/api/promotions/admin/policies", new
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
            tracksWorkItems = true,
            requiredWorkItemRoles = requiredRoles,
            escalationGroup = (string?)null,
        });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
    }

    private HttpClient CreateAuthenticatedClient(string email, string password)
    {
        var client = _factory.CreateClient();
        var loginResponse = client.PostAsJsonAsync("/api/auth/login", new { email, password })
            .GetAwaiter().GetResult();
        loginResponse.EnsureSuccessStatusCode();
        var token = Deserialize(loginResponse).GetAwaiter().GetResult().GetProperty("token").GetString()!;
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

    public class AliasFactory : WebApplicationFactory<Program>
    {
        public const string TestApiKey = "role-alias-test-api-key-24680";

        private readonly SqliteConnection _connection;

        public AliasFactory()
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

            builder.UseSetting("Deployments:ApiKeys:0:Name", "role-alias-integration-test");
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
            var descriptor = services.FirstOrDefault(d => d.ServiceType == typeof(T));
            if (descriptor is not null) services.Remove(descriptor);
        }
    }
}
