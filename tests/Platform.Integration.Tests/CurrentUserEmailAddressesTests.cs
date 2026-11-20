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
using Platform.Api.Infrastructure.Identity;
using Platform.Api.Infrastructure.Persistence;

namespace Platform.Integration.Tests;

/// <summary>
/// Someone whose sign-in name is not their mail. Work items name their people by mail, so before the
/// directory's addresses were consulted, "Assigned to me" (<c>assignee=&lt;sign-in name&gt;</c>) came
/// back empty for such a person while anyone filtering the queue by their mail saw their items.
///
/// <para>The directory here is the local users table plus one extra mailbox for the seeded admin
/// (<see cref="MailboxIdentityService"/>), standing in for an Entra account whose mail differs from its
/// sign-in name.</para>
/// </summary>
public class CurrentUserEmailAddressesTests
    : IClassFixture<CurrentUserEmailAddressesTests.MailboxFactory>, IDisposable
{
    private const string SignInName = "admin@localhost";
    private const string Mailbox = "Admin.Mailbox@example.com";

    private readonly MailboxFactory _factory;
    private readonly HttpClient _apiKeyClient;
    private readonly HttpClient _adminClient;

    public CurrentUserEmailAddressesTests(MailboxFactory factory)
    {
        _factory = factory;
        _apiKeyClient = factory.CreateClient();
        _apiKeyClient.DefaultRequestHeaders.Add("X-Api-Key", MailboxFactory.TestApiKey);
        _adminClient = CreateAuthenticatedClient(SignInName, "admin123");
    }

    public void Dispose()
    {
        _apiKeyClient.Dispose();
        _adminClient.Dispose();
    }

    [Fact]
    public async Task Assigned_to_me_includes_work_items_that_name_me_by_my_mail()
    {
        var product = NewProduct();
        await SeedPolicyAsync(product, requiredRoles: ["qa"]);
        await CreatePromotionAsync(product, "svc-mailbox", "MAILBOX-1",
            new { role = "qa", displayName = "Admin", email = Mailbox.ToLowerInvariant() });
        await CreatePromotionAsync(product, "svc-signin", "SIGNIN-1",
            new { role = "qa", displayName = "Admin", email = SignInName });
        await CreatePromotionAsync(product, "svc-other", "OTHER-1",
            new { role = "qa", displayName = "Other", email = "other@example.com" });

        // What the "Assigned to me" tab and the My tasks badge send: the sign-in name.
        var mine = await GetAssignedAsync(SignInName);

        Assert.Contains("MAILBOX-1", mine);
        Assert.Contains("SIGNIN-1", mine);
        Assert.DoesNotContain("OTHER-1", mine);
    }

    [Fact]
    public async Task Filtering_by_someone_else_is_not_widened()
    {
        var product = NewProduct();
        await SeedPolicyAsync(product, requiredRoles: ["qa"]);
        await CreatePromotionAsync(product, "svc-mailbox-2", "MAILBOX-2",
            new { role = "qa", displayName = "Admin", email = Mailbox });
        await CreatePromotionAsync(product, "svc-other-2", "OTHER-2",
            new { role = "qa", displayName = "Other", email = "other@example.com" });

        var theirs = await GetAssignedAsync("other@example.com");

        Assert.Contains("OTHER-2", theirs);
        Assert.DoesNotContain("MAILBOX-2", theirs);
    }

    [Fact]
    public async Task My_emails_are_the_sign_in_name_then_the_directory_addresses()
    {
        var resp = await _adminClient.GetAsync("/api/me/emails");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await Deserialize(resp);
        Assert.Equal(SignInName, body.GetProperty("email").GetString());
        Assert.Equal(
            [SignInName, Mailbox],
            body.GetProperty("emails").EnumerateArray().Select(e => e.GetString()!).ToArray());
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static string NewProduct() => $"mbox-{Guid.NewGuid():N}"[..18];

    private async Task<List<string>> GetAssignedAsync(string assignee)
    {
        var resp = await _adminClient.GetAsync(
            $"/api/work-items/me/pending?assignee={Uri.EscapeDataString(assignee)}&roleRequirement=assigned");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await Deserialize(resp);
        return body.GetProperty("tickets").EnumerateArray()
            .Select(t => t.GetProperty("workItemKey").GetString()!)
            .ToList();
    }

    // A Pending staging→prod promotion carrying one work item with one person on it.
    private async Task CreatePromotionAsync(string product, string service, string key, object participant)
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

        var create = await _apiKeyClient.PostAsJsonAsync("/api/promotions", new
        {
            product,
            service,
            sourceEnv = "staging",
            targetEnv = "prod",
            version,
            references = new[]
            {
                new
                {
                    type = "work-item",
                    provider = "jira",
                    key,
                    title = "Mailbox test",
                    participants = new[] { participant },
                },
            },
        });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
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
            requiredWorkItemRoles = requiredRoles,
            escalationGroup = (string?)null,
        });
        Assert.True(resp.IsSuccessStatusCode, $"Seeding the policy failed: {resp.StatusCode}");
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

    /// <summary>
    /// The local directory, except that the seeded admin also owns <see cref="Mailbox"/> — an account
    /// whose mail is not its sign-in name.
    /// </summary>
    public sealed class MailboxIdentityService : IIdentityService
    {
        private readonly StubIdentityService _local;

        public MailboxIdentityService(PlatformDbContext db) { _local = new StubIdentityService(db); }

        public async Task<IReadOnlyList<string>?> GetEmailAddresses(string userId, CancellationToken ct = default)
        {
            var local = await _local.GetEmailAddresses(userId, ct);
            if (local is null) return null;
            return local.Contains(SignInName, StringComparer.OrdinalIgnoreCase) ? [.. local, Mailbox] : local;
        }

        public Task<IReadOnlyList<UserInfo>> GetGroupMembers(string groupId, CancellationToken ct = default)
            => _local.GetGroupMembers(groupId, ct);

        public Task<UserInfo?> GetUser(string userId, CancellationToken ct = default) => _local.GetUser(userId, ct);

        public Task<AccountStatus> GetAccountStatus(string userId, CancellationToken ct = default)
            => _local.GetAccountStatus(userId, ct);

        public Task<IReadOnlyList<UserInfo>> SearchUsers(string query, CancellationToken ct = default)
            => _local.SearchUsers(query, ct);

        public Task<IReadOnlyList<GroupInfo>> SearchGroups(string query, CancellationToken ct = default)
            => _local.SearchGroups(query, ct);

        public Task<IReadOnlyList<UserInfo>> SearchUsersInGroups(
            string query, IReadOnlyList<GroupInfo> groups, CancellationToken ct = default)
            => _local.SearchUsersInGroups(query, groups, ct);
    }

    public class MailboxFactory : WebApplicationFactory<Program>
    {
        public const string TestApiKey = "mailbox-test-api-key-13579";

        private readonly SqliteConnection _connection;

        public MailboxFactory()
        {
            _connection = TestDatabase.Open();
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");

            builder.UseSetting("Deployments:ApiKeys:0:Name", "mailbox-integration-test");
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
                RemoveService<IIdentityService>(services);

                services.AddSingleton<DbConnection>(_connection);
                services.AddDbContext<PlatformDbContext, SqliteTestDbContext>((sp, options) =>
                    options.UseSqlite(sp.GetRequiredService<DbConnection>()));
                services.AddScoped<IIdentityService, MailboxIdentityService>();

                TestHostIsolation.Apply(services);
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
