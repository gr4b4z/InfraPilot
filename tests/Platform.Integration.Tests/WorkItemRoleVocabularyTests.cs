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
/// Tests covering the participant-role vocabulary — the roles an admin has configured under
/// Settings → Participant Roles (<c>ui.app-settings</c>) — and the three places it now governs:
///
/// <list type="bullet">
///   <item><b>The role filter.</b> <c>GET /api/work-items/me/pending?role=X&amp;assignee=unassigned</c>
///         answers "which work items have nobody as X?" for <i>any</i> role, not only the ones in
///         the assignee-role set. That combination is the whole point of the filter: the role you
///         most want to chase is the one nobody has been put on.</item>
///   <item><b>The queue's dropdown contents.</b> <c>roles</c> is the configured vocabulary (not
///         derived from the data, or a role with nobody in it could never be picked), and
///         <c>unknownRoles</c> reports the roles the queue's items actually carry that aren't
///         configured.</item>
///   <item><b>Manual assignment.</b> Naming a person requires a configured role; ingest stays
///         permissive, and clearing a slot works whatever its role, or an ingested typo would be
///         unremovable.</item>
/// </list>
/// </summary>
public class WorkItemRoleVocabularyTests
    : IClassFixture<WorkItemRoleVocabularyTests.RoleVocabularyFactory>, IDisposable
{
    private readonly RoleVocabularyFactory _factory;
    private readonly HttpClient _apiKeyClient;
    private readonly HttpClient _adminClient;

    public WorkItemRoleVocabularyTests(RoleVocabularyFactory factory)
    {
        _factory = factory;
        _apiKeyClient = factory.CreateClient();
        _apiKeyClient.DefaultRequestHeaders.Add("X-Api-Key", RoleVocabularyFactory.TestApiKey);
        _adminClient = CreateAuthenticatedClient("admin@localhost", "admin123");
    }

    public void Dispose()
    {
        _apiKeyClient.Dispose();
        _adminClient.Dispose();
    }

    // ── The role dropdown's contents ────────────────────────────────────────

    [Fact]
    public async Task Queue_Roles_IsTheBuiltInVocabulary()
    {
        var queue = await GetPendingAsync();

        // The built-in defaults cover every role the deploy-ingest and Jira paths actually emit, so a
        // producer-sent role isn't flagged as unconfigured on a fresh install.
        Assert.Equal(
            new[] { "triggered-by", "author", "reviewer", "qa", "qa-owner", "assignee", "reporter" },
            queue.Roles);
    }

    [Fact]
    public async Task Queue_Roles_TracksTheConfiguredVocabulary()
    {
        // The dropdown follows the settings row, not the roles present in the data.
        await SaveRoleVocabularyAsync(("qa-owner", "QA owner"), ("author", "Author"));
        try
        {
            var queue = await GetPendingAsync();
            Assert.Equal(new[] { "qa-owner", "author" }, queue.Roles);
        }
        finally
        {
            await ResetRoleVocabularyAsync();
        }
    }

    [Fact]
    public async Task Queue_UnknownRoles_ReportsRolesTheItemsCarryThatArentConfigured()
    {
        var product = NewProduct();
        await SeedPolicyAsync(product);

        // Ingest takes the producer at its word, so a role nobody configured lands on the item.
        // "release-captain" is deliberately not in the built-in vocabulary.
        await CreateWorkItemPromotionAsync(product, "svc-unknown-role", "UNK-1", new[]
        {
            new { role = "release-captain", displayName = "Ola", email = "ola@example.com" },
        });

        var queue = await GetPendingAsync();
        Assert.Contains("release-captain", queue.UnknownRoles);
        // It is reported as unrecognised, not smuggled into the configured list.
        Assert.DoesNotContain("release-captain", queue.Roles);
        // Configured roles that happen to be in use are not "unknown".
        Assert.DoesNotContain("qa", queue.UnknownRoles);
    }

    // ── The "nobody is in this role" filter ─────────────────────────────────

    [Fact]
    public async Task RoleFilter_AnyConfiguredRole_MatchesTheItemsOwnParticipants()
    {
        // Regression: the role filter used to match only participants whose role was in a privileged
        // "assignee role" subset. Filtering on any other configured role — "author" here — therefore
        // matched nothing, which made every item look like it was missing that role and the
        // "role only" view come back empty.
        var product = NewProduct();
        await SeedPolicyAsync(product);

        await CreateWorkItemPromotionAsync(product, "svc-has-author", "AUTH-1", new[]
        {
            new { role = "author", displayName = "Ada", email = "ada@example.com" },
        });
        await CreateWorkItemPromotionAsync(product, "svc-no-author", "AUTH-2", new[]
        {
            new { role = "qa", displayName = "Quinn", email = "quinn@example.com" },
        });

        var withAuthor = await GetPendingAsync(role: "author");
        Assert.Contains(withAuthor.Tickets, t => t == "AUTH-1");
        Assert.DoesNotContain(withAuthor.Tickets, t => t == "AUTH-2");

        var missingAuthor = await GetPendingAsync(role: "author", assignee: "unassigned");
        Assert.Contains(missingAuthor.Tickets, t => t == "AUTH-2");
        Assert.DoesNotContain(missingAuthor.Tickets, t => t == "AUTH-1");
    }

    [Fact]
    public async Task RoleFilter_UnconfiguredRole_IsStillFilterable()
    {
        // The roles reported in `unknownRoles` are offered as filter choices, so they have to work.
        var product = NewProduct();
        await SeedPolicyAsync(product);

        await CreateWorkItemPromotionAsync(product, "svc-owner-set", "OWN-1", new[]
        {
            new { role = "release-captain", displayName = "Ola", email = "ola@example.com" },
        });
        await CreateWorkItemPromotionAsync(product, "svc-owner-unset", "OWN-2", new[]
        {
            new { role = "qa", displayName = "Quinn", email = "quinn@example.com" },
        });

        var withOwner = await GetPendingAsync(role: "release-captain");
        Assert.Contains(withOwner.Tickets, t => t == "OWN-1");
        Assert.DoesNotContain(withOwner.Tickets, t => t == "OWN-2");

        var missingOwner = await GetPendingAsync(role: "release-captain", assignee: "unassigned");
        Assert.Contains(missingOwner.Tickets, t => t == "OWN-2");
        Assert.DoesNotContain(missingOwner.Tickets, t => t == "OWN-1");
    }

    [Fact]
    public async Task RoleFilter_ParticipantWithoutEmail_CountsAsUnassigned()
    {
        // A role with a name but nobody behind it is an empty slot, not an assignment — otherwise
        // "who has no QA?" would skip the items whose QA is a label with no address.
        var product = NewProduct();
        await SeedPolicyAsync(product);

        await CreateWorkItemPromotionAsync(product, "svc-nameless-qa", "NOEMAIL-1", new[]
        {
            new { role = "qa", displayName = "Unknown QA", email = (string?)null },
        });

        var missingQa = await GetPendingAsync(role: "qa", assignee: "unassigned");
        Assert.Contains(missingQa.Tickets, t => t == "NOEMAIL-1");
    }

    // ── Manual assignment is limited to the configured vocabulary ───────────

    [Fact]
    public async Task Assign_ConfiguredRole_Succeeds()
    {
        var product = NewProduct();
        await SeedPolicyAsync(product);
        var candidateId = await CreateWorkItemPromotionAsync(product, "svc-assign-ok", "ASSIGN-1", null);

        var resp = await AssignReferenceParticipantAsync(
            candidateId, "ASSIGN-1", role: "qa",
            assignee: new { email = "quinn@example.com", displayName = "Quinn" });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await Deserialize(resp);
        Assert.Contains(body.GetProperty("participants").EnumerateArray(),
            p => p.GetProperty("role").GetString() == "qa"
                 && p.GetProperty("email").GetString() == "quinn@example.com");
    }

    [Fact]
    public async Task Assign_UnconfiguredRole_IsRefusedAndSaysWhy()
    {
        var product = NewProduct();
        await SeedPolicyAsync(product);
        var candidateId = await CreateWorkItemPromotionAsync(product, "svc-assign-bad", "ASSIGN-2", null);

        var resp = await AssignReferenceParticipantAsync(
            candidateId, "ASSIGN-2", role: "release-captain",
            assignee: new { email = "ola@example.com", displayName = "Ola" });

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var error = (await Deserialize(resp)).GetProperty("error").GetString() ?? "";
        Assert.Contains("release-captain", error);
        Assert.Contains("Participant Roles", error);

        // And nothing was written to the candidate's work-item reference.
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var candidate = await db.PromotionCandidates.AsNoTracking()
            .SingleAsync(c => c.Id == Guid.Parse(candidateId));
        var reference = candidate.References.Single(r => r.Key == "ASSIGN-2");
        Assert.DoesNotContain(reference.Participants ?? [], p => p.Role == "qa-owner");
    }

    [Fact]
    public async Task Assign_UnconfiguredRole_BecomesAssignableOnceConfigured()
    {
        var product = NewProduct();
        await SeedPolicyAsync(product);
        var candidateId = await CreateWorkItemPromotionAsync(product, "svc-assign-later", "ASSIGN-3", null);

        await SaveRoleVocabularyAsync(
            ("triggered-by", "Triggered by"),
            ("author", "Author"),
            ("reviewer", "Reviewer"),
            ("qa", "QA"),
            ("qa-owner", "QA owner"));
        try
        {
            var resp = await AssignReferenceParticipantAsync(
                candidateId, "ASSIGN-3", role: "qa-owner",
                assignee: new { email = "ola@example.com", displayName = "Ola" });
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        }
        finally
        {
            await ResetRoleVocabularyAsync();
        }
    }

    [Fact]
    public async Task Clear_UnconfiguredRole_IsAllowed()
    {
        // An ingested payload can put someone on a role nobody configured. Refusing the clear too
        // would leave that assignment permanently stuck — removal is the remedy, so it stays open.
        var product = NewProduct();
        await SeedPolicyAsync(product);
        var candidateId = await CreateWorkItemPromotionAsync(product, "svc-clear-bad", "CLEAR-1", new[]
        {
            new { role = "qa-owner", displayName = "Ola", email = "ola@example.com" },
        });

        var resp = await AssignReferenceParticipantAsync(
            candidateId, "CLEAR-1", role: "qa-owner", assignee: null);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await Deserialize(resp);
        Assert.DoesNotContain(body.GetProperty("participants").EnumerateArray(),
            p => p.GetProperty("role").GetString() == "qa-owner");
    }

    [Fact]
    public async Task PromotionLevelAssign_UnconfiguredRole_IsRefused()
    {
        // Promotion-level participants fall through onto every work item on the candidate, so the
        // same rule applies to that write path.
        var product = NewProduct();
        await SeedPolicyAsync(product);
        var candidateId = await CreateWorkItemPromotionAsync(product, "svc-promo-level", "PROMO-1", null);

        var bad = await _adminClient.PostAsJsonAsync(
            $"/api/promotions/{candidateId}/participants",
            new { role = "release-captain", displayName = "Ola", email = "ola@example.com" });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        var good = await _adminClient.PostAsJsonAsync(
            $"/api/promotions/{candidateId}/participants",
            new { role = "reviewer", displayName = "Rita", email = "rita@example.com" });
        Assert.Equal(HttpStatusCode.OK, good.StatusCode);
    }

    [Fact]
    public async Task Ingest_UnconfiguredRole_IsStillAccepted()
    {
        // The vocabulary gates operators, not producers: a payload is a record of what happened.
        var product = NewProduct();
        await SeedPolicyAsync(product);

        var candidateId = await CreateWorkItemPromotionAsync(product, "svc-ingest-any", "INGEST-1", new[]
        {
            new { role = "release-shepherd", displayName = "Sam", email = "sam@example.com" },
        });

        Assert.False(string.IsNullOrEmpty(candidateId));
        var queue = await GetPendingAsync();
        Assert.Contains("release-shepherd", queue.UnknownRoles);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static string NewProduct() => $"vocab-{Guid.NewGuid():N}"[..18];

    private record QueueResponse(List<string> Tickets, List<string> Roles, List<string> UnknownRoles);

    private async Task<QueueResponse> GetPendingAsync(string? role = null, string? assignee = null)
    {
        var query = new List<string>();
        if (!string.IsNullOrEmpty(role)) query.Add($"role={Uri.EscapeDataString(role)}");
        if (!string.IsNullOrEmpty(assignee)) query.Add($"assignee={Uri.EscapeDataString(assignee)}");
        var url = query.Count == 0
            ? "/api/work-items/me/pending"
            : $"/api/work-items/me/pending?{string.Join("&", query)}";

        var resp = await _adminClient.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await Deserialize(resp);

        return new QueueResponse(
            body.GetProperty("tickets").EnumerateArray()
                .Select(t => t.GetProperty("workItemKey").GetString()!).ToList(),
            ReadStringArray(body, "roles"),
            ReadStringArray(body, "unknownRoles"));
    }

    private static List<string> ReadStringArray(JsonElement body, string property)
        => body.TryGetProperty(property, out var el) && el.ValueKind == JsonValueKind.Array
            ? el.EnumerateArray().Select(r => r.GetString()!).ToList()
            : new List<string>();

    private Task<HttpResponseMessage> AssignReferenceParticipantAsync(
        string candidateId, string referenceKey, string role, object? assignee)
        => _adminClient.PatchAsJsonAsync(
            $"/api/promotions/{candidateId}/references/{Uri.EscapeDataString(referenceKey)}/participants",
            new { role, assignee });

    /// <summary>
    /// Creates a Pending staging→prod candidate carrying one work-item reference, optionally with
    /// nested participants. Returns the candidate id.
    /// </summary>
    private async Task<string> CreateWorkItemPromotionAsync(
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
            ? new { type = "work-item", provider = "jira", key = referenceKey, title = "Role vocabulary test" }
            : new
            {
                type = "work-item",
                provider = "jira",
                key = referenceKey,
                title = "Role vocabulary test",
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
            // Not in the default assignee-role set, so it never reads as "assigned".
            participants = new[]
            {
                new { role = "triggered-by", displayName = "Bob", email = "bob@example.com" },
            },
        });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        return (await Deserialize(create)).GetProperty("id").GetString()!;
    }

    private async Task SeedPolicyAsync(string product)
    {
        await _adminClient.PutAsJsonAsync("/api/features/features.promotions", new { enabled = true });
        await _adminClient.PostAsJsonAsync("/api/promotions/admin/policies", new
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
            escalationGroup = (string?)null,
        });
    }

    /// <summary>
    /// Replaces the configured participant roles via the admin settings endpoint. Environments and
    /// the activity template are sent as-read so this only moves the roles.
    /// </summary>
    private async Task SaveRoleVocabularyAsync(params (string Key, string DisplayName)[] roles)
    {
        var current = await Deserialize(await _adminClient.GetAsync("/api/settings"));
        var payload = new
        {
            environments = current.GetProperty("environments").EnumerateArray()
                .Select(e => new
                {
                    key = e.GetProperty("key").GetString(),
                    displayName = e.GetProperty("displayName").GetString(),
                    color = e.TryGetProperty("color", out var c) ? c.GetString() : null,
                }).ToArray(),
            roles = roles.Select(r => new { key = r.Key, displayName = r.DisplayName }).ToArray(),
            activityTemplate = current.GetProperty("activityTemplate").EnumerateArray()
                .Select(l => new
                {
                    template = l.GetProperty("template").GetString(),
                    style = l.GetProperty("style").GetString(),
                }).ToArray(),
        };

        var put = await _adminClient.PutAsJsonAsync("/api/settings", payload);
        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);
    }

    /// <summary>Drops the saved settings row so the built-in defaults apply again — the fixture is
    /// shared, and sibling tests assert against the default vocabulary.</summary>
    private async Task ResetRoleVocabularyAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var existing = await db.PlatformSettings
            .FirstOrDefaultAsync(s => s.Key == Platform.Api.Features.Settings.AppSettingsService.SettingsKey);
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

    // ── Factory ─────────────────────────────────────────────────────────────

    public class RoleVocabularyFactory : WebApplicationFactory<Program>
    {
        public const string TestApiKey = "role-vocabulary-test-api-key-13579";

        private readonly SqliteConnection _connection;

        public RoleVocabularyFactory()
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

            builder.UseSetting("Deployments:ApiKeys:0:Name", "role-vocabulary-integration-test");
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
