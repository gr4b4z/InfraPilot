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
/// Tests covering operator routing overrides on a deploy event's reference. Exercises:
/// <list type="bullet">
///   <item>Assign on an empty slot — read returns the assignee with <c>isOverride=true</c>.</item>
///   <item>Assign on a Jira-supplied slot — override beats reference-level on read.</item>
///   <item>Reassign — second PATCH wins; only one override row exists.</item>
///   <item>Clear (tombstone) — hides the Jira-supplied participant from the read path.</item>
///   <item>Excluded-role honours overrides — an overridden QA who triggered the deploy
///         can't approve.</item>
///   <item>Excluded-role honours tombstones — clearing the deployer slot lets that user
///         approve (consistent with the suppression rule even if the result is unusual).</item>
///   <item>404 when refKey is not on the event's <c>ReferencesJson</c>.</item>
///   <item>400 on bad email shape.</item>
/// </list>
/// </summary>
public class ReferenceParticipantOverridesTests
    : IClassFixture<ReferenceParticipantOverridesTests.OverrideFactory>, IDisposable
{
    private readonly OverrideFactory _factory;
    private readonly HttpClient _apiKeyClient;
    private readonly HttpClient _adminClient;

    public ReferenceParticipantOverridesTests(OverrideFactory factory)
    {
        _factory = factory;
        _apiKeyClient = factory.CreateClient();
        _apiKeyClient.DefaultRequestHeaders.Add("X-Api-Key", OverrideFactory.TestApiKey);
        _adminClient = CreateAuthenticatedClient("admin@localhost", "admin123");
    }

    public void Dispose()
    {
        _apiKeyClient.Dispose();
        _adminClient.Dispose();
    }

    // ── 1. Assign on empty slot ─────────────────────────────────────────────

    [Fact]
    public async Task Assign_OnEmptySlot_ReadReturnsAssigneeWithIsOverrideTrue()
    {
        var service = $"ov-empty-{Guid.NewGuid():N}"[..20];
        await SeedTopologyAndPolicyAsync(service);
        var (eventId, candidateId) = await IngestDeployWithReference(service, "EM-1", participants: null);

        var patch = await _adminClient.PatchAsJsonAsync(
            $"/api/deployments/{eventId}/references/EM-1/participants",
            new { role = "qa", assignee = new { email = "qa-new@example.com", displayName = "QA New" } });
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);

        var detail = await Deserialize(await _adminClient.GetAsync($"/api/promotions/{candidateId}"));
        var refs = detail.GetProperty("sourceEvent").GetProperty("references");
        var workItem = FindReference(refs, "EM-1");
        Assert.NotNull(workItem);

        var nested = workItem.Value.GetProperty("participants");
        Assert.Equal(1, nested.GetArrayLength());
        var p = nested[0];
        Assert.Equal("qa", p.GetProperty("role").GetString());
        Assert.Equal("qa-new@example.com", p.GetProperty("email").GetString());
        Assert.True(p.GetProperty("isOverride").GetBoolean());
        // assignedBy is the display name of the user who made the override (admin@localhost
        // logs in as "Admin User" per the seeded local user).
        Assert.Equal("Admin User", p.GetProperty("assignedBy").GetString());
    }

    // ── 2. Assign on a Jira-supplied slot wins ──────────────────────────────

    [Fact]
    public async Task Assign_OnExistingJiraParticipant_OverrideWinsOnRead()
    {
        var service = $"ov-replace-{Guid.NewGuid():N}"[..20];
        await SeedTopologyAndPolicyAsync(service);
        var (eventId, candidateId) = await IngestDeployWithReference(
            service, "RP-1",
            participants: new[]
            {
                new { role = "qa", displayName = "QA Original", email = "qa-original@example.com" },
            });

        await _adminClient.PatchAsJsonAsync(
            $"/api/deployments/{eventId}/references/RP-1/participants",
            new { role = "qa", assignee = new { email = "qa-override@example.com", displayName = "QA Override" } });

        var detail = await Deserialize(await _adminClient.GetAsync($"/api/promotions/{candidateId}"));
        var nested = FindReference(detail.GetProperty("sourceEvent").GetProperty("references"), "RP-1")!.Value
            .GetProperty("participants");
        Assert.Equal(1, nested.GetArrayLength());
        Assert.Equal("qa-override@example.com", nested[0].GetProperty("email").GetString());
        Assert.True(nested[0].GetProperty("isOverride").GetBoolean());
    }

    // ── 3. Reassign — second wins, only one row ────────────────────────────

    [Fact]
    public async Task Reassign_TwiceWithDifferentAssignees_SecondWinsAndOnlyOneRow()
    {
        var service = $"ov-reasn-{Guid.NewGuid():N}"[..20];
        await SeedTopologyAndPolicyAsync(service);
        var (eventId, candidateId) = await IngestDeployWithReference(service, "RA-1", participants: null);

        await _adminClient.PatchAsJsonAsync(
            $"/api/deployments/{eventId}/references/RA-1/participants",
            new { role = "qa", assignee = new { email = "first@example.com", displayName = "First" } });

        await _adminClient.PatchAsJsonAsync(
            $"/api/deployments/{eventId}/references/RA-1/participants",
            new { role = "qa", assignee = new { email = "second@example.com", displayName = "Second" } });

        var detail = await Deserialize(await _adminClient.GetAsync($"/api/promotions/{candidateId}"));
        var nested = FindReference(detail.GetProperty("sourceEvent").GetProperty("references"), "RA-1")!.Value
            .GetProperty("participants");
        Assert.Equal(1, nested.GetArrayLength());
        Assert.Equal("second@example.com", nested[0].GetProperty("email").GetString());

        // Confirm at the DB level: only one row for (eventId, refKey, role).
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var rows = await db.ReferenceParticipantOverrides
            .Where(o => o.DeployEventId == eventId && o.ReferenceKey == "RA-1" && o.Role == "qa")
            .ToListAsync();
        Assert.Single(rows);
        Assert.Equal("second@example.com", rows[0].AssigneeEmail);
    }

    // ── 4. Clear / tombstone hides Jira-supplied participant ────────────────

    [Fact]
    public async Task Clear_TombstoneHidesJiraParticipant()
    {
        var service = $"ov-clear-{Guid.NewGuid():N}"[..20];
        await SeedTopologyAndPolicyAsync(service);
        var (eventId, candidateId) = await IngestDeployWithReference(
            service, "CL-1",
            participants: new[]
            {
                new { role = "qa", displayName = "Jira QA", email = "jira-qa@example.com" },
            });

        var patch = await _adminClient.PatchAsJsonAsync(
            $"/api/deployments/{eventId}/references/CL-1/participants",
            new { role = "qa", assignee = (object?)null });
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);
        var patchBody = await Deserialize(patch);
        Assert.True(patchBody.GetProperty("tombstone").GetBoolean());

        var detail = await Deserialize(await _adminClient.GetAsync($"/api/promotions/{candidateId}"));
        var refNode = FindReference(detail.GetProperty("sourceEvent").GetProperty("references"), "CL-1")!.Value;
        if (refNode.TryGetProperty("participants", out var nested) && nested.ValueKind != JsonValueKind.Null)
        {
            Assert.Equal(0, nested.GetArrayLength());
        }
    }

    // ── 5. Excluded-role check honours override ─────────────────────────────

    [Fact]
    public async Task Override_PinningCurrentUserAsQA_BlocksApproval()
    {
        var service = $"ov-excl-{Guid.NewGuid():N}"[..20];
        await SeedTopologyAsync();
        await CreatePolicyAsync(targetEnv: "prod", approverGroup: "InfraPortal.Admin", excludeRole: "qa", service: service);
        var (eventId, candidateId) = await IngestDeployWithReference(service, "EX-1", participants: null);

        // Pin admin@localhost as the QA via override — they shouldn't be able to approve.
        var patchResp = await _adminClient.PatchAsJsonAsync(
            $"/api/deployments/{eventId}/references/EX-1/participants",
            new { role = "qa", assignee = new { email = "admin@localhost", displayName = "Admin" } });
        Assert.Equal(HttpStatusCode.OK, patchResp.StatusCode);

        // Sanity: confirm the override row landed with the canonicalised role + matching key.
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            var rows = await db.ReferenceParticipantOverrides
                .Where(o => o.DeployEventId == eventId)
                .ToListAsync();
            Assert.Single(rows);
            Assert.Equal("EX-1", rows[0].ReferenceKey);
            Assert.Equal("qa", rows[0].Role);
            Assert.Equal("admin@localhost", rows[0].AssigneeEmail);
        }

        var listResp = await _adminClient.GetAsync(
            $"/api/promotions/?product=acme&service={service}&targetEnv=prod&status=Pending");
        var list = await Deserialize(listResp);
        var candidate = FindCandidateById(list.GetProperty("candidates"), candidateId);
        Assert.NotNull(candidate);
        Assert.False(candidate.Value.GetProperty("canApprove").GetBoolean());

        var approveResp = await _adminClient.PostAsJsonAsync(
            $"/api/promotions/{candidateId}/approve", new { comment = "should be blocked" });
        Assert.Equal(HttpStatusCode.Forbidden, approveResp.StatusCode);
    }

    // ── 6. Excluded-role check honours tombstone ────────────────────────────

    [Fact]
    public async Task Tombstone_OnExcludedRole_ClearsExclusionForThatReference()
    {
        var service = $"ov-tomb-{Guid.NewGuid():N}"[..20];
        await SeedTopologyAsync();
        await CreatePolicyAsync(targetEnv: "prod", approverGroup: "InfraPortal.Admin", excludeRole: "qa", service: service);

        // The Jira-supplied QA matches admin@localhost, so without the tombstone they couldn't approve.
        var (eventId, candidateId) = await IngestDeployWithReference(
            service, "TS-1",
            participants: new[]
            {
                new { role = "qa", displayName = "Admin", email = "admin@localhost" },
            });

        // Clear the slot — tombstone suppresses the Jira-supplied admin@localhost match.
        await _adminClient.PatchAsJsonAsync(
            $"/api/deployments/{eventId}/references/TS-1/participants",
            new { role = "qa", assignee = (object?)null });

        // Now admin can approve. Re-pull the list to refresh canApprove.
        var listResp = await _adminClient.GetAsync(
            $"/api/promotions/?product=acme&service={service}&targetEnv=prod&status=Pending");
        var list = await Deserialize(listResp);
        var candidate = FindCandidateById(list.GetProperty("candidates"), candidateId);
        Assert.NotNull(candidate);
        Assert.True(candidate.Value.GetProperty("canApprove").GetBoolean());
    }

    // ── 7. 404 when refKey not on ReferencesJson ───────────────────────────

    [Fact]
    public async Task Patch_OnUnknownReferenceKey_Returns404()
    {
        var service = $"ov-404-{Guid.NewGuid():N}"[..20];
        await SeedTopologyAndPolicyAsync(service);
        var (eventId, _) = await IngestDeployWithReference(service, "EXIST-1", participants: null);

        var resp = await _adminClient.PatchAsJsonAsync(
            $"/api/deployments/{eventId}/references/NOPE-1/participants",
            new { role = "qa", assignee = new { email = "x@example.com", displayName = "X" } });
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    // ── 8. Bad email shape → 400 ───────────────────────────────────────────

    [Fact]
    public async Task Patch_WithBadEmailShape_Returns400()
    {
        var service = $"ov-bad-{Guid.NewGuid():N}"[..20];
        await SeedTopologyAndPolicyAsync(service);
        var (eventId, _) = await IngestDeployWithReference(service, "BD-1", participants: null);

        var resp = await _adminClient.PatchAsJsonAsync(
            $"/api/deployments/{eventId}/references/BD-1/participants",
            new { role = "qa", assignee = new { email = "not-an-email", displayName = "X" } });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private async Task<(Guid eventId, string candidateId)> IngestDeployWithReference(
        string service, string refKey, object[]? participants)
    {
        var refsArr = participants is null
            ? (object[])new[]
            {
                new { type = "work-item", provider = "jira", key = refKey, title = "Override test" },
            }
            : new[]
            {
                new { type = "work-item", provider = "jira", key = refKey, title = "Override test", participants },
            };

        var version = $"v{Guid.NewGuid():N}"[..10];
        var payload = new
        {
            product = "acme",
            service,
            environment = "staging",
            version,
            source = "integration-test",
            deployedAt = DateTimeOffset.UtcNow,
            status = "succeeded",
            references = refsArr,
            // Always include a triggered-by event-level participant — many tests need a
            // candidate to exist (which requires the policy + topology + a non-empty bundle).
            participants = new[]
            {
                new { role = "triggered-by", displayName = "Bob", email = "bob@example.com" },
            },
        };

        var ingest = await _apiKeyClient.PostAsJsonAsync("/api/deployments/events", payload);
        Assert.Equal(HttpStatusCode.Created, ingest.StatusCode);
        var ingestBody = await Deserialize(ingest);
        var eventId = Guid.Parse(ingestBody.GetProperty("id").GetString()!);

        // Find the Pending candidate for this version.
        var listResp = await _adminClient.GetAsync(
            $"/api/promotions/?product=acme&service={service}&targetEnv=prod&status=Pending");
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

    private static JsonElement? FindCandidateById(JsonElement candidates, string id)
    {
        foreach (var c in candidates.EnumerateArray())
        {
            if (c.GetProperty("id").GetString() == id) return c;
        }
        return null;
    }

    private static JsonElement? FindReference(JsonElement references, string key)
    {
        foreach (var r in references.EnumerateArray())
        {
            if (r.TryGetProperty("key", out var k) && k.GetString() == key) return r;
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

    private async Task SeedTopologyAndPolicyAsync(string? service = null)
    {
        await SeedTopologyAsync();
        await CreatePolicyAsync(targetEnv: "prod", approverGroup: "InfraPortal.Admin", excludeRole: null, service: service);
    }

    private async Task CreatePolicyAsync(string targetEnv, string? approverGroup, string? excludeRole, string? service = null)
    {
        // Per-test policies: each test passes its unique service so the policy is per
        // (product, service, env) and doesn't collide on the unique index with sibling tests
        // sharing the same factory instance.
        await _adminClient.PostAsJsonAsync("/api/promotions/admin/policies", new
        {
            product = "acme",
            service,
            targetEnv,
            approverGroup,
            strategy = "Any",
            minApprovers = approverGroup is null ? 0 : 1,
            excludeRole,
            timeoutHours = 24,
            escalationGroup = (string?)null,
        });
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

    // ── Factory ─────────────────────────────────────────────────────────────

    public class OverrideFactory : WebApplicationFactory<Program>
    {
        public const string TestApiKey = "ov-test-api-key-13579";

        private readonly SqliteConnection _connection;

        public OverrideFactory()
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

            builder.UseSetting("Deployments:ApiKeys:0:Name", "ov-integration-test");
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
