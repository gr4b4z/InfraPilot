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
using Platform.Api.Features.Promotions;
using Platform.Api.Infrastructure.Features;
using Platform.Api.Infrastructure.Persistence;

namespace Platform.Integration.Tests;

/// <summary>
/// Tests covering the assignee filter on <c>GET /api/work-items/me/pending</c>. The filter is
/// a display-only narrowing — server-side authorisation (group membership, excluded role,
/// not-yet-decided) is unchanged. Each test seeds its own service / candidate and asserts the
/// filter returns the expected subset of the user's authorized queue.
/// </summary>
public class PromotionQueueAssigneeFilterTests
    : IClassFixture<PromotionQueueAssigneeFilterTests.AssigneeFilterFactory>, IDisposable
{
    private readonly AssigneeFilterFactory _factory;
    private readonly HttpClient _apiKeyClient;
    private readonly HttpClient _adminClient;

    public PromotionQueueAssigneeFilterTests(AssigneeFilterFactory factory)
    {
        _factory = factory;
        _apiKeyClient = factory.CreateClient();
        _apiKeyClient.DefaultRequestHeaders.Add("X-Api-Key", AssigneeFilterFactory.TestApiKey);
        _adminClient = CreateAuthenticatedClient("admin@localhost", "admin123");
    }

    public void Dispose()
    {
        _apiKeyClient.Dispose();
        _adminClient.Dispose();
    }

    // ── 1. assignee=<my email> returns only candidates where I'm a named assignee ──

    [Fact]
    public async Task AssigneeFilter_ByCurrentUserEmail_ReturnsOnlyCandidatesAssignedToMe()
    {
        var product = NewProduct();
        await SeedTopologyAndPolicyAsync(product);

        // Mine: I'm the QA on a reference participant.
        var (eventMine, _) = await IngestDeployAsync(
            product,
            service: "svc-mine",
            referenceKey: "MINE-1",
            referenceParticipants: new[]
            {
                new { role = "qa", displayName = "Admin", email = "admin@localhost" },
            });

        // Theirs: someone else is the QA, I'm not on the candidate.
        var (eventTheirs, _) = await IngestDeployAsync(
            product,
            service: "svc-theirs",
            referenceKey: "THEIRS-1",
            referenceParticipants: new[]
            {
                new { role = "qa", displayName = "Other", email = "other@example.com" },
            });

        var mine = await GetPendingAsync(assignee: "admin@localhost");
        Assert.Contains(mine, t => t.WorkItemKey == "MINE-1");
        Assert.DoesNotContain(mine, t => t.WorkItemKey == "THEIRS-1");

        // Sanity: with no filter, both rows are present (authorisation is the same).
        var unfiltered = await GetPendingAsync(assignee: null);
        Assert.Contains(unfiltered, t => t.WorkItemKey == "MINE-1");
        Assert.Contains(unfiltered, t => t.WorkItemKey == "THEIRS-1");

        // Touch the local variables so unused-warning analyzers don't trip.
        Assert.NotEqual(Guid.Empty, eventMine);
        Assert.NotEqual(Guid.Empty, eventTheirs);
    }

    // ── 2. assignee=<other email> returns their candidates, none of mine ──

    [Fact]
    public async Task AssigneeFilter_ByOtherEmail_ReturnsTheirCandidatesNotMine()
    {
        var product = NewProduct();
        await SeedTopologyAndPolicyAsync(product);

        await IngestDeployAsync(
            product,
            service: "svc-mine-2",
            referenceKey: "MINE2-1",
            referenceParticipants: new[]
            {
                new { role = "qa", displayName = "Admin", email = "admin@localhost" },
            });

        await IngestDeployAsync(
            product,
            service: "svc-them",
            referenceKey: "THEM-1",
            referenceParticipants: new[]
            {
                new { role = "reviewer", displayName = "Other", email = "other@example.com" },
            });

        var theirs = await GetPendingAsync(assignee: "other@example.com");
        Assert.Contains(theirs, t => t.WorkItemKey == "THEM-1");
        Assert.DoesNotContain(theirs, t => t.WorkItemKey == "MINE2-1");
    }

    // ── 3. assignee=unassigned returns candidates with no participant in any assignee role ──

    [Fact]
    public async Task AssigneeFilter_Unassigned_ReturnsCandidatesWithoutNamedAssignees()
    {
        var product = NewProduct();
        await SeedTopologyAndPolicyAsync(product);

        // Has a QA participant — should NOT show up as unassigned.
        await IngestDeployAsync(
            product,
            service: "svc-named",
            referenceKey: "NAMED-1",
            referenceParticipants: new[]
            {
                new { role = "qa", displayName = "Other", email = "other@example.com" },
            });

        // No participants in any assignee role (only triggered-by event-level participant
        // which is not in the default assignee role set).
        await IngestDeployAsync(
            product,
            service: "svc-empty",
            referenceKey: "EMPTY-1",
            referenceParticipants: null);

        var unassigned = await GetPendingAsync(assignee: "unassigned");
        Assert.Contains(unassigned, t => t.WorkItemKey == "EMPTY-1");
        Assert.DoesNotContain(unassigned, t => t.WorkItemKey == "NAMED-1");
    }

    // ── 4. Setting override changes which roles count as "assigned" ──

    [Fact]
    public async Task AssigneeFilter_RoleSetOverride_RestrictsAssigneeRoles()
    {
        var product = NewProduct();
        await SeedTopologyAndPolicyAsync(product);

        // Reviewer-only — with the default role set ["qa","reviewer","assignee"] this
        // candidate is "assigned"; with the override ["qa"] it's NOT, so it should appear
        // under the unassigned filter once we override.
        await IngestDeployAsync(
            product,
            service: "svc-rev-only",
            referenceKey: "REV-1",
            referenceParticipants: new[]
            {
                new { role = "reviewer", displayName = "Other", email = "other@example.com" },
            });

        // Sanity: with the default set, the reviewer-only candidate is NOT unassigned.
        var beforeOverride = await GetPendingAsync(assignee: "unassigned");
        Assert.DoesNotContain(beforeOverride, t => t.WorkItemKey == "REV-1");

        // Insert / update the platform_settings row so only "qa" counts as an assignee role.
        await SetAssigneeRoleSettingAsync("[\"qa\"]");

        var afterOverride = await GetPendingAsync(assignee: "unassigned");
        Assert.Contains(afterOverride, t => t.WorkItemKey == "REV-1");

        // Reset for any sibling tests that expect the default. The fixture is shared.
        await ResetAssigneeRoleSettingAsync();
    }

    // ── 5. Tombstone interaction — overridden-cleared QA → treated as unassigned ──

    [Fact]
    public async Task AssigneeFilter_TombstonedAssignee_IsTreatedAsUnassigned()
    {
        var product = NewProduct();
        await SeedTopologyAndPolicyAsync(product);

        var (eventId, _) = await IngestDeployAsync(
            product,
            service: "svc-tomb",
            referenceKey: "TOMB-1",
            referenceParticipants: new[]
            {
                new { role = "qa", displayName = "Other", email = "other@example.com" },
            });

        // Tombstone — clears the QA slot, so the merged view should treat the candidate as
        // having no participant in any assignee role.
        var patch = await _adminClient.PatchAsJsonAsync(
            $"/api/deployments/{eventId}/references/TOMB-1/participants",
            new { role = "qa", assignee = (object?)null });
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);

        var unassigned = await GetPendingAsync(assignee: "unassigned");
        Assert.Contains(unassigned, t => t.WorkItemKey == "TOMB-1");

        // And conversely, filtering by the now-tombstoned email should NOT return the row
        // (the override merge has dropped the tombstoned participant from the merged view).
        var byOldEmail = await GetPendingAsync(assignee: "other@example.com");
        Assert.DoesNotContain(byOldEmail, t => t.WorkItemKey == "TOMB-1");
    }

    // ── 6. Email match is case-insensitive ──

    [Fact]
    public async Task AssigneeFilter_EmailMatch_IsCaseInsensitive()
    {
        var product = NewProduct();
        await SeedTopologyAndPolicyAsync(product);

        await IngestDeployAsync(
            product,
            service: "svc-case",
            referenceKey: "CASE-1",
            referenceParticipants: new[]
            {
                new { role = "qa", displayName = "Mixed Case", email = "Mixed.Case@Example.COM" },
            });

        var upper = await GetPendingAsync(assignee: "MIXED.CASE@EXAMPLE.COM");
        Assert.Contains(upper, t => t.WorkItemKey == "CASE-1");

        var lower = await GetPendingAsync(assignee: "mixed.case@example.com");
        Assert.Contains(lower, t => t.WorkItemKey == "CASE-1");
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static string NewProduct() => $"acme-{Guid.NewGuid():N}"[..18];

    private async Task<List<PendingTicketDto>> GetPendingAsync(string? assignee)
    {
        var url = string.IsNullOrEmpty(assignee)
            ? "/api/work-items/me/pending"
            : $"/api/work-items/me/pending?assignee={Uri.EscapeDataString(assignee)}";
        var resp = await _adminClient.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await Deserialize(resp);
        var tickets = new List<PendingTicketDto>();
        foreach (var t in body.GetProperty("tickets").EnumerateArray())
        {
            tickets.Add(new PendingTicketDto(
                WorkItemKey: t.GetProperty("workItemKey").GetString()!,
                Product: t.GetProperty("product").GetString()!,
                TargetEnv: t.GetProperty("targetEnv").GetString()!));
        }
        return tickets;
    }

    private async Task<(Guid eventId, string candidateId)> IngestDeployAsync(
        string product,
        string service,
        string referenceKey,
        object[]? referenceParticipants)
    {
        var refsArr = referenceParticipants is null
            ? (object[])new[]
            {
                new { type = "work-item", provider = "jira", key = referenceKey, title = "Assignee filter test" },
            }
            : new[]
            {
                new
                {
                    type = "work-item",
                    provider = "jira",
                    key = referenceKey,
                    title = "Assignee filter test",
                    participants = referenceParticipants,
                },
            };

        var version = $"v{Guid.NewGuid():N}"[..10];
        var payload = new
        {
            product,
            service,
            environment = "staging",
            version,
            source = "integration-test",
            deployedAt = DateTimeOffset.UtcNow,
            status = "succeeded",
            references = refsArr,
            // triggered-by is intentionally NOT in the default assignee role set, so it
            // doesn't trip the "assigned" check.
            participants = new[]
            {
                new { role = "triggered-by", displayName = "Bob", email = "bob@example.com" },
            },
        };

        var ingest = await _apiKeyClient.PostAsJsonAsync("/api/deployments/events", payload);
        Assert.Equal(HttpStatusCode.Created, ingest.StatusCode);
        var ingestBody = await Deserialize(ingest);
        var eventId = Guid.Parse(ingestBody.GetProperty("id").GetString()!);

        var listResp = await _adminClient.GetAsync(
            $"/api/promotions/?product={product}&service={service}&targetEnv=prod&status=Pending");
        var list = await Deserialize(listResp);
        var candidate = FindCandidateByVersion(list.GetProperty("candidates"), version, "prod");
        Assert.NotNull(candidate);
        var candidateId = candidate.Value.GetProperty("id").GetString()!;
        return (eventId, candidateId);
    }

    private static JsonElement? FindCandidateByVersion(JsonElement candidates, string version, string targetEnv)
    {
        foreach (var c in candidates.EnumerateArray())
        {
            if (c.GetProperty("version").GetString() == version
                && c.GetProperty("targetEnv").GetString() == targetEnv)
                return c;
        }
        return null;
    }

    private async Task SeedTopologyAsync()
    {
        await _adminClient.PutAsJsonAsync("/api/promotions/admin/topology", new
        {
            environments = new[] { "dev", "staging", "prod" },
            edges = new[]
            {
                new { from = "dev", to = "staging" },
                new { from = "staging", to = "prod" },
            },
        });
        await _adminClient.PutAsJsonAsync("/api/features/features.promotions", new { enabled = true });
    }

    private async Task SeedTopologyAndPolicyAsync(string product)
    {
        await SeedTopologyAsync();
        // Per-product / per-target-env policy with no excluded role and InfraPortal.Admin
        // as the approver group — admin@localhost is in that group via the seeded local user.
        await _adminClient.PostAsJsonAsync("/api/promotions/admin/policies", new
        {
            product,
            service = (string?)null,
            targetEnv = "prod",
            approverGroup = "InfraPortal.Admin",
            strategy = "Any",
            minApprovers = 1,
            excludeRole = (string?)null,
            timeoutHours = 24,
            escalationGroup = (string?)null,
        });
    }

    /// <summary>
    /// Inserts (or updates) the <c>promotions.assignee_roles</c> setting directly via the DB
    /// — no admin endpoint exists for this setting on purpose. Mirrors the operator workflow
    /// documented on <see cref="PromotionAssigneeRoleSettings"/>.
    /// </summary>
    private async Task SetAssigneeRoleSettingAsync(string json)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var existing = await db.PlatformSettings
            .FirstOrDefaultAsync(s => s.Key == PromotionAssigneeRoleSettings.SettingKey);
        if (existing is null)
        {
            db.PlatformSettings.Add(new PlatformSetting
            {
                Key = PromotionAssigneeRoleSettings.SettingKey,
                Value = json,
                UpdatedAt = DateTimeOffset.UtcNow,
                UpdatedBy = "test",
            });
        }
        else
        {
            existing.Value = json;
            existing.UpdatedAt = DateTimeOffset.UtcNow;
            existing.UpdatedBy = "test";
        }
        await db.SaveChangesAsync();
    }

    private async Task ResetAssigneeRoleSettingAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var existing = await db.PlatformSettings
            .FirstOrDefaultAsync(s => s.Key == PromotionAssigneeRoleSettings.SettingKey);
        if (existing is not null)
        {
            db.PlatformSettings.Remove(existing);
            await db.SaveChangesAsync();
        }
    }

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

    private record PendingTicketDto(string WorkItemKey, string Product, string TargetEnv);

    // ── Factory ─────────────────────────────────────────────────────────────

    public class AssigneeFilterFactory : WebApplicationFactory<Program>
    {
        public const string TestApiKey = "assignee-filter-test-api-key-24680";

        private readonly SqliteConnection _connection;

        public AssigneeFilterFactory()
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

            builder.UseSetting("Deployments:ApiKeys:0:Name", "assignee-integration-test");
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
