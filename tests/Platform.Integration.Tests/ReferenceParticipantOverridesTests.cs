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
        // The override read path is now the deploy-event history endpoint (promotions are
        // self-contained and no longer surface deploy-event overrides — D19/D14). The override
        // mechanics are otherwise unchanged.
        var service = $"ov-empty-{Guid.NewGuid():N}"[..20];
        var eventId = await IngestDeployWithReference(service, "EM-1", participants: null);

        var patch = await _adminClient.PatchAsJsonAsync(
            $"/api/deployments/{eventId}/references/EM-1/participants",
            new { role = "qa", assignee = new { email = "qa-new@example.com", displayName = "QA New" } });
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);

        var workItem = await ReadEventReferenceAsync(service, eventId, "EM-1");
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
        var eventId = await IngestDeployWithReference(
            service, "RP-1",
            participants: new[]
            {
                new { role = "qa", displayName = "QA Original", email = "qa-original@example.com" },
            });

        await _adminClient.PatchAsJsonAsync(
            $"/api/deployments/{eventId}/references/RP-1/participants",
            new { role = "qa", assignee = new { email = "qa-override@example.com", displayName = "QA Override" } });

        var nested = (await ReadEventReferenceAsync(service, eventId, "RP-1"))!.Value
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
        var eventId = await IngestDeployWithReference(service, "RA-1", participants: null);

        await _adminClient.PatchAsJsonAsync(
            $"/api/deployments/{eventId}/references/RA-1/participants",
            new { role = "qa", assignee = new { email = "first@example.com", displayName = "First" } });

        await _adminClient.PatchAsJsonAsync(
            $"/api/deployments/{eventId}/references/RA-1/participants",
            new { role = "qa", assignee = new { email = "second@example.com", displayName = "Second" } });

        var nested = (await ReadEventReferenceAsync(service, eventId, "RA-1"))!.Value
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
        var eventId = await IngestDeployWithReference(
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

        var refNode = (await ReadEventReferenceAsync(service, eventId, "CL-1"))!.Value;
        if (refNode.TryGetProperty("participants", out var nested) && nested.ValueKind != JsonValueKind.Null)
        {
            Assert.Equal(0, nested.GetArrayLength());
        }
    }

    // Deleted Override_PinningCurrentUserAsQA_BlocksApproval and
    // Tombstone_OnExcludedRole_ClearsExclusionForThatReference: both asserted the excluded-role /
    // separation-of-duties gate (a participant in an excluded role can't approve, a tombstone
    // clears that exclusion). That machinery was removed (D17) — anyone authorized for a promotion
    // may approve, and overrides no longer feed any promotion-approval gate. No replacement concept
    // exists, so these assert behaviour that is intentionally gone.

    // ── 7. 404 when refKey not on ReferencesJson ───────────────────────────

    [Fact]
    public async Task Patch_OnUnknownReferenceKey_Returns404()
    {
        var service = $"ov-404-{Guid.NewGuid():N}"[..20];
        var eventId = await IngestDeployWithReference(service, "EXIST-1", participants: null);

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
        var eventId = await IngestDeployWithReference(service, "BD-1", participants: null);

        var resp = await _adminClient.PatchAsJsonAsync(
            $"/api/deployments/{eventId}/references/BD-1/participants",
            new { role = "qa", assignee = new { email = "not-an-email", displayName = "X" } });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    // Ingest a deploy event carrying one work-item reference. Promotions are no longer derived
    // from ingest (D19), so this just records the deploy event and returns its id. The override
    // machinery and the deploy-event read paths that surface it are unchanged; tests read the
    // overridden reference back via the deploy-event history endpoint, not via a promotion.
    private async Task<Guid> IngestDeployWithReference(
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
            participants = new[]
            {
                new { role = "triggered-by", displayName = "Bob", email = "bob@example.com" },
            },
        };

        var ingest = await _apiKeyClient.PostAsJsonAsync("/api/deployments/events", payload);
        Assert.Equal(HttpStatusCode.Created, ingest.StatusCode);
        var ingestBody = await Deserialize(ingest);
        return Guid.Parse(ingestBody.GetProperty("id").GetString()!);
    }

    // Read a single reference back from the deploy-event history endpoint, which applies operator
    // overrides/tombstones to the reference's participants. Returns null if the reference is absent.
    private async Task<JsonElement?> ReadEventReferenceAsync(string service, Guid eventId, string refKey)
    {
        var resp = await _adminClient.GetAsync($"/api/deployments/history/acme/{service}");
        resp.EnsureSuccessStatusCode();
        var events = await Deserialize(resp);
        foreach (var ev in events.EnumerateArray())
        {
            if (ev.GetProperty("id").GetString() != eventId.ToString()) continue;
            return FindReference(ev.GetProperty("references"), refKey);
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
