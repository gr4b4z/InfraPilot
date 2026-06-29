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
/// Tests covering the two-level participant model added on top of the existing event-level
/// list: senders may now nest <c>participants[]</c> under each <c>references[]</c> entry.
/// Asserts:
///
/// <list type="bullet">
///   <item>Roundtrip — nested participants survive ingest and surface back through the
///         promotion detail endpoint on the <c>sourceEvent.references</c> list.</item>
///   <item>Backwards compat — payloads with no nested participants still ingest and read
///         back as before (no new mandatory fields, no shape drift).</item>
///   <item>Excluded-role gate — when a policy excludes a role and that role's email lives
///         on a reference (not on the event-level list), the user is still blocked from
///         approving. Closes the loophole that the legacy <c>EmailMatchesExcludedRole</c>
///         only walked event-level participants.</item>
/// </list>
/// </summary>
public class ReferenceParticipantsTests
    : IClassFixture<ReferenceParticipantsTests.RpFactory>, IDisposable
{
    private readonly RpFactory _factory;
    private readonly HttpClient _apiKeyClient;
    private readonly HttpClient _adminClient;

    public ReferenceParticipantsTests(RpFactory factory)
    {
        _factory = factory;
        _apiKeyClient = factory.CreateClient();
        _apiKeyClient.DefaultRequestHeaders.Add("X-Api-Key", RpFactory.TestApiKey);
        _adminClient = CreateAuthenticatedClient("admin@localhost", "admin123");
    }

    public void Dispose()
    {
        _apiKeyClient.Dispose();
        _adminClient.Dispose();
    }

    // ── Roundtrip ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Create_WithNestedReferenceParticipants_RoundtripsThroughPromotionDetail()
    {
        // Was Ingest_WithNestedReferenceParticipants_RoundtripsThroughPromotionDetail. Candidates
        // are self-contained now (D19): references (with nested participants) live on the candidate
        // payload, not on a linked deploy event. The promotion detail's sourceEvent.references is a
        // projection of candidate.References. We create via POST /api/promotions and assert the
        // nested participants round-trip.
        await SeedPolicyAsync();

        var service = $"rp-rt-{Guid.NewGuid():N}"[..20];

        // Create a promotion candidate whose references each carry their own participants.
        var payload = new
        {
            product = "acme",
            service,
            sourceEnv = "staging",
            targetEnv = "prod",
            version = "rp-rt-1.0.0",
            references = new object[]
            {
                new
                {
                    type = "work-item",
                    provider = "jira",
                    key = "RP-1",
                    title = "Add idempotency",
                    participants = new object[]
                    {
                        new { role = "qa", displayName = "QA Eve", email = "qa@example.com" },
                        new { role = "assignee", displayName = "Dev Dan", email = "dan@example.com" },
                    },
                },
                new
                {
                    type = "pull-request",
                    provider = "github",
                    key = "42",
                    participants = new object[]
                    {
                        new { role = "author", displayName = "Author Al", email = "al@example.com" },
                    },
                },
            },
            participants = new object[]
            {
                new { role = "triggered-by", displayName = "Bob", email = "bob@example.com" },
            },
        };

        var createResponse = await _apiKeyClient.PostAsJsonAsync("/api/promotions", payload);
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);

        // Read back via the promotion detail endpoint, which surfaces sourceEvent.references
        // (a projection of the candidate's own references with their nested participants).
        var listResponse = await _adminClient.GetAsync(
            $"/api/promotions/?product=acme&service={service}&targetEnv=prod&status=Pending");
        listResponse.EnsureSuccessStatusCode();
        var list = await Deserialize(listResponse);
        var candidate = FindCandidate(list.GetProperty("candidates"), "rp-rt-1.0.0", "prod");
        Assert.NotNull(candidate);
        var candidateId = candidate.Value.GetProperty("id").GetString()!;

        var detailResponse = await _adminClient.GetAsync($"/api/promotions/{candidateId}");
        detailResponse.EnsureSuccessStatusCode();
        var detail = await Deserialize(detailResponse);

        var refs = detail.GetProperty("sourceEvent").GetProperty("references");
        Assert.Equal(2, refs.GetArrayLength());

        // Find the work-item reference and verify nested participants survived.
        JsonElement? workItem = null;
        foreach (var r in refs.EnumerateArray())
        {
            if (r.GetProperty("type").GetString() == "work-item")
            {
                workItem = r;
                break;
            }
        }
        Assert.NotNull(workItem);
        var nested = workItem.Value.GetProperty("participants");
        Assert.Equal(2, nested.GetArrayLength());

        var roles = new List<string?>();
        var emails = new List<string?>();
        foreach (var p in nested.EnumerateArray())
        {
            roles.Add(p.GetProperty("role").GetString());
            emails.Add(p.GetProperty("email").GetString());
        }
        Assert.Contains("qa", roles);
        Assert.Contains("assignee", roles);
        Assert.Contains("qa@example.com", emails);
        Assert.Contains("dan@example.com", emails);
    }

    // ── Backwards compatibility ─────────────────────────────────────────────

    [Fact]
    public async Task Create_WithoutNestedParticipants_StillSucceedsAndReadsBack()
    {
        // Was Ingest_WithoutNestedParticipants_StillSucceedsAndReadsBack. Same back-compat intent,
        // now exercised through the external-create path: a reference with no nested participants
        // must create cleanly and read back without throwing.
        await SeedPolicyAsync();

        var service = $"rp-bc-{Guid.NewGuid():N}"[..20];

        // References carry no nested participants; only a promotion-level participant is supplied.
        var payload = new
        {
            product = "acme",
            service,
            sourceEnv = "staging",
            targetEnv = "prod",
            version = "rp-bc-1.0.0",
            references = new object[]
            {
                new { type = "work-item", provider = "jira", key = "BC-1", title = "Old shape" },
            },
            participants = new object[]
            {
                new { role = "triggered-by", displayName = "Bob", email = "bob@example.com" },
            },
        };

        var createResponse = await _apiKeyClient.PostAsJsonAsync("/api/promotions", payload);
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);

        var listResponse = await _adminClient.GetAsync(
            $"/api/promotions/?product=acme&service={service}&targetEnv=prod&status=Pending");
        listResponse.EnsureSuccessStatusCode();
        var list = await Deserialize(listResponse);
        var candidate = FindCandidate(list.GetProperty("candidates"), "rp-bc-1.0.0", "prod");
        Assert.NotNull(candidate);
        var candidateId = candidate.Value.GetProperty("id").GetString()!;

        var detailResponse = await _adminClient.GetAsync($"/api/promotions/{candidateId}");
        detailResponse.EnsureSuccessStatusCode();
        var detail = await Deserialize(detailResponse);
        var refs = detail.GetProperty("sourceEvent").GetProperty("references");
        Assert.Equal(1, refs.GetArrayLength());

        var only = refs[0];
        // `participants` is optional on a reference. When absent, it must either be missing
        // entirely or null/empty — never throw.
        if (only.TryGetProperty("participants", out var nested) &&
            nested.ValueKind != JsonValueKind.Null)
        {
            Assert.Equal(0, nested.GetArrayLength());
        }
    }

    // Deleted ExcludeRole_OnReferenceLevelParticipant_BlocksApproval: the excluded-role /
    // separation-of-duties machinery was removed (D17). Anyone authorized for a promotion may
    // approve it, including a participant on the change set. There is no replacement concept yet
    // (the plan notes any future SoD will be payload-driven), so this test asserts behaviour that
    // no longer exists.

    // ── Validation ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Ingest_NestedParticipantWithEmptyRole_Returns400WithIndexedPath()
    {
        var payload = new
        {
            product = "acme",
            service = "validation-svc",
            environment = "staging",
            version = "v1.0.0",
            source = "integration-test",
            deployedAt = DateTimeOffset.UtcNow,
            status = "succeeded",
            references = new object[]
            {
                new
                {
                    type = "work-item",
                    key = "OK-1",
                    participants = new object[]
                    {
                        new { role = "qa", email = "qa@example.com" },
                    },
                },
                new
                {
                    type = "work-item",
                    key = "BAD-1",
                    // Missing role on the second ref's first participant.
                    participants = new object[]
                    {
                        new { role = "", email = "x@example.com" },
                    },
                },
            },
        };

        var ingestResponse = await _apiKeyClient.PostAsJsonAsync("/api/deployments/events", payload);
        Assert.Equal(HttpStatusCode.BadRequest, ingestResponse.StatusCode);

        var body = await Deserialize(ingestResponse);
        var errors = body.GetProperty("errors");
        var found = false;
        foreach (var e in errors.EnumerateArray())
        {
            var s = e.GetString() ?? "";
            if (s.Contains("references[1].participants[0].role")) { found = true; break; }
        }
        Assert.True(found, "Expected indexed validation error for the offending nested participant.");
    }

    // ── Setup helpers ───────────────────────────────────────────────────────

    // Topology was removed (D19); policy resolution is the edge guard. Enable the flag and seed a
    // gated step-tree policy on prod (one InfraPortal.Admin approver) so created candidates are
    // born Pending.
    private async Task SeedPolicyAsync()
    {
        await _adminClient.PutAsJsonAsync("/api/features/features.promotions", new { enabled = true });
        await _adminClient.PostAsJsonAsync("/api/promotions/admin/policies", new
        {
            product = "acme",
            service = (string?)null,
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
            gate = "PromotionOnly",
            timeoutHours = 24,
            escalationGroup = (string?)null,
        });
    }

    // ── Utility ────────────────────────────────────────────────────────────

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

    private static JsonElement? FindCandidate(JsonElement candidates, string version, string targetEnv)
    {
        foreach (var c in candidates.EnumerateArray())
        {
            if (c.GetProperty("version").GetString() == version &&
                c.GetProperty("targetEnv").GetString() == targetEnv)
                return c;
        }
        return null;
    }

    // ── Factory ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Test host: SQLite in-memory + local-JWT auth + a deploy-ingest API key. Mirrors
    /// <see cref="PromotionFlowTests.FlowFactory"/> minus the captured webhook dispatcher
    /// (these tests don't assert on webhooks).
    /// </summary>
    public class RpFactory : WebApplicationFactory<Program>
    {
        public const string TestApiKey = "rp-test-api-key-67890";

        private readonly SqliteConnection _connection;

        public RpFactory()
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

            builder.UseSetting("Deployments:ApiKeys:0:Name", "rp-integration-test");
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
