using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Platform.Api.Infrastructure.Identity;
using Platform.Api.Infrastructure.Persistence;

namespace Platform.Integration.Tests;

/// <summary>
/// Administrator oversight of personal access tokens (<c>/api/tokens/admin</c>, Settings → Agent
/// Tokens), and the directory check that stops a token whose owner was disabled or deleted:
/// <list type="bullet">
///   <item>An admin sees everyone's tokens with their owners, never a secret; nobody else can.</item>
///   <item>An admin revokes any one token, or all of one person's, and each revoke is audited.</item>
///   <item>A token stops working when the directory reports its owner disabled or missing, and
///         keeps working when the directory cannot answer.</item>
/// </list>
/// Each test builds its own host: the owner check caches per owner, and revoking or deactivating a
/// seeded user would otherwise leak into the next test.
/// </summary>
public class PersonalAccessTokenAdminTests
{
    private static async Task<(string Id, string Secret)> CreateTokenAsync(HttpClient owner, string name = "laptop")
    {
        var response = await owner.PostAsJsonAsync("/api/me/tokens", new { name, expiresInDays = 30 });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return (body.GetProperty("id").GetString()!, body.GetProperty("token").GetString()!);
    }

    private static HttpClient TokenClient(TestFactory factory, string secret)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", secret);
        return client;
    }

    private static async Task<HttpStatusCode> ReadWith(TestFactory factory, string secret)
    {
        using var client = TokenClient(factory, secret);
        return (await client.GetAsync("/api/deployments/products")).StatusCode;
    }

    private static async Task<JsonElement[]> AdminListAsync(HttpClient admin, bool includeInactive = false)
    {
        var body = await admin.GetFromJsonAsync<JsonElement>($"/api/tokens/admin?includeInactive={includeInactive}");
        return body.GetProperty("tokens").EnumerateArray().ToArray();
    }

    // ── Listing ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Admin_lists_everyones_tokens_with_their_owners_and_no_secret()
    {
        await using var factory = new TestFactory();
        using var admin = factory.CreateAdminClient();
        using var qa = factory.CreateAuthenticatedClient("qa@localhost", "qa123");
        using var user = factory.CreateAuthenticatedClient("user@localhost", "user123");
        var (qaId, qaSecret) = await CreateTokenAsync(qa, "qa laptop");
        var (userId, userSecret) = await CreateTokenAsync(user, "user ci");

        var body = await admin.GetAsync("/api/tokens/admin");
        Assert.Equal(HttpStatusCode.OK, body.StatusCode);
        var raw = await body.Content.ReadAsStringAsync();
        Assert.DoesNotContain(qaSecret, raw);
        Assert.DoesNotContain(userSecret, raw);

        var rows = await AdminListAsync(admin);
        var qaRow = rows.Single(t => t.GetProperty("id").GetString() == qaId);
        Assert.Equal("qa@localhost", qaRow.GetProperty("ownerEmail").GetString());
        Assert.Equal("qa laptop", qaRow.GetProperty("name").GetString());
        Assert.StartsWith(qaRow.GetProperty("prefix").GetString()!, qaSecret);
        Assert.False(qaRow.TryGetProperty("tokenHash", out _));
        Assert.Equal("user@localhost", rows.Single(t => t.GetProperty("id").GetString() == userId)
            .GetProperty("ownerEmail").GetString());
    }

    [Fact]
    public async Task Only_admins_in_a_session_reach_the_admin_routes()
    {
        await using var factory = new TestFactory();
        using var admin = factory.CreateAdminClient();
        using var qa = factory.CreateAuthenticatedClient("qa@localhost", "qa123");
        var (qaTokenId, _) = await CreateTokenAsync(qa);
        // An admin's own token carries no Admin role, so it is refused like anyone's.
        var (_, adminSecret) = await CreateTokenAsync(admin);
        using var adminToken = TokenClient(factory, adminSecret);

        foreach (var client in new[] { qa, adminToken })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/tokens/admin")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden,
                (await client.PostAsJsonAsync($"/api/tokens/admin/{qaTokenId}/revoke", new { })).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden,
                (await client.PostAsJsonAsync("/api/tokens/admin/revoke-owner", new { email = "qa@localhost" })).StatusCode);
        }
        using var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/tokens/admin")).StatusCode);
    }

    [Fact]
    public async Task Ended_tokens_are_listed_on_request_for_ninety_days()
    {
        await using var factory = new TestFactory();
        using var admin = factory.CreateAdminClient();
        using var qa = factory.CreateAuthenticatedClient("qa@localhost", "qa123");
        var (activeId, _) = await CreateTokenAsync(qa, "active");
        var (revokedId, _) = await CreateTokenAsync(qa, "revoked");
        var (oldId, _) = await CreateTokenAsync(qa, "long gone");
        Assert.Equal(HttpStatusCode.NoContent, (await qa.DeleteAsync($"/api/me/tokens/{revokedId}")).StatusCode);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            var old = await db.PersonalAccessTokens.SingleAsync(t => t.Id == Guid.Parse(oldId));
            old.CreatedAt = DateTimeOffset.UtcNow.AddDays(-130);
            old.ExpiresAt = DateTimeOffset.UtcNow.AddDays(-100);
            await db.SaveChangesAsync();
        }

        var activeOnly = (await AdminListAsync(admin)).Select(t => t.GetProperty("id").GetString()).ToList();
        Assert.Equal([activeId], activeOnly);

        var withHistory = (await AdminListAsync(admin, includeInactive: true)).Select(t => t.GetProperty("id").GetString()).ToList();
        Assert.Contains(activeId, withHistory);
        Assert.Contains(revokedId, withHistory);
        Assert.DoesNotContain(oldId, withHistory);
    }

    // ── Revoking ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Admin_revokes_anyones_token_and_it_stops_working_with_an_audit_entry()
    {
        await using var factory = new TestFactory();
        using var admin = factory.CreateAdminClient();
        using var qa = factory.CreateAuthenticatedClient("qa@localhost", "qa123");
        var (id, secret) = await CreateTokenAsync(qa, "leaked one");
        Assert.Equal(HttpStatusCode.OK, await ReadWith(factory, secret));

        var revoke = await admin.PostAsJsonAsync($"/api/tokens/admin/{id}/revoke", new { reason = "posted in a public channel" });
        Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, await ReadWith(factory, secret));

        // The owner sees it ended on their own page.
        var mine = (await qa.GetFromJsonAsync<JsonElement>("/api/me/tokens")).GetProperty("tokens").EnumerateArray()
            .Single(t => t.GetProperty("id").GetString() == id);
        Assert.NotEqual(JsonValueKind.Null, mine.GetProperty("revokedAt").ValueKind);

        // Idempotent; an unknown id is a 404.
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsJsonAsync($"/api/tokens/admin/{id}/revoke", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await admin.PostAsJsonAsync($"/api/tokens/admin/{Guid.NewGuid()}/revoke", new { })).StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var entry = await db.AuditLog.AsNoTracking()
            .SingleAsync(a => a.Action == "personal_token.revoked" && a.EntityId == Guid.Parse(id));
        Assert.Equal("auth", entry.Module);
        Assert.Equal("Admin User", entry.ActorName);
        Assert.Contains("qa@localhost", entry.AfterState);
        Assert.Contains("posted in a public channel", entry.Metadata);
    }

    [Fact]
    public async Task Admin_revokes_all_of_one_persons_active_tokens_and_no_one_elses()
    {
        await using var factory = new TestFactory();
        using var admin = factory.CreateAdminClient();
        using var user = factory.CreateAuthenticatedClient("user@localhost", "user123");
        using var qa = factory.CreateAuthenticatedClient("qa@localhost", "qa123");
        var (firstId, first) = await CreateTokenAsync(user, "laptop");
        var (secondId, second) = await CreateTokenAsync(user, "ci");
        var (_, qaSecret) = await CreateTokenAsync(qa, "qa's");

        // The email is matched the way owners are stored: trimmed, ignoring case.
        var response = await admin.PostAsJsonAsync("/api/tokens/admin/revoke-owner", new { email = " User@Localhost ", reason = "left the team" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("revoked").GetInt32());

        Assert.Equal(HttpStatusCode.Unauthorized, await ReadWith(factory, first));
        Assert.Equal(HttpStatusCode.Unauthorized, await ReadWith(factory, second));
        Assert.Equal(HttpStatusCode.OK, await ReadWith(factory, qaSecret));

        // Nothing left to revoke the second time.
        var again = await admin.PostAsJsonAsync("/api/tokens/admin/revoke-owner", new { email = "user@localhost" });
        Assert.Equal(0, (await again.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("revoked").GetInt32());

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var audited = await db.AuditLog.AsNoTracking()
            .Where(a => a.Action == "personal_token.revoked")
            .Select(a => a.EntityId)
            .ToListAsync();
        Assert.Equal(new[] { Guid.Parse(firstId), Guid.Parse(secondId) }.Order(), audited.Select(g => g!.Value).Order());
    }

    [Fact]
    public async Task Revoke_owner_needs_an_email_and_a_reason_of_bounded_length()
    {
        await using var factory = new TestFactory();
        using var admin = factory.CreateAdminClient();

        Assert.Equal(HttpStatusCode.BadRequest,
            (await admin.PostAsJsonAsync("/api/tokens/admin/revoke-owner", new { email = "  " })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await admin.PostAsJsonAsync("/api/tokens/admin/revoke-owner", new { email = "qa@localhost", reason = new string('x', 501) })).StatusCode);
    }

    // ── The owner's account ──────────────────────────────────────────────────

    [Fact]
    public async Task Local_mode_a_token_stops_working_when_its_owner_is_deactivated()
    {
        await using var factory = new TestFactory();
        using var user = factory.CreateAuthenticatedClient("user@localhost", "user123");
        using var qa = factory.CreateAuthenticatedClient("qa@localhost", "qa123");
        var (_, userSecret) = await CreateTokenAsync(user);
        var (_, qaSecret) = await CreateTokenAsync(qa);

        // Deactivated before the token's first use, so no earlier answer is cached for the owner.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            var row = await db.LocalUsers.SingleAsync(u => u.Email == "user@localhost");
            row.IsActive = false;
            await db.SaveChangesAsync();
        }

        Assert.Equal(HttpStatusCode.Unauthorized, await ReadWith(factory, userSecret));
        Assert.Equal(HttpStatusCode.OK, await ReadWith(factory, qaSecret));
    }

    [Fact]
    public async Task Directory_refuses_disabled_and_missing_owners_and_lets_unknown_through_reading_each_owner_once()
    {
        await using var factory = new DirectoryFactory();
        using var admin = factory.CreateAdminClient();
        using var qa = factory.CreateAuthenticatedClient("qa@localhost", "qa123");
        using var user = factory.CreateAuthenticatedClient("user@localhost", "user123");
        var (_, adminSecret) = await CreateTokenAsync(admin);
        var (_, qaSecret) = await CreateTokenAsync(qa);
        var (_, userSecret) = await CreateTokenAsync(user);

        using (var scope = factory.Services.CreateScope())
        {
            var ids = await scope.ServiceProvider.GetRequiredService<PlatformDbContext>().LocalUsers
                .ToDictionaryAsync(u => u.Email, u => u.Id.ToString());
            factory.Directory.Statuses[ids["admin@localhost"]] = AccountStatus.Disabled;
            factory.Directory.Statuses[ids["user@localhost"]] = AccountStatus.Missing;
            // qa: not set — the directory cannot say (Graph throttled or down).
        }

        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, await ReadWith(factory, adminSecret));
            Assert.Equal(HttpStatusCode.Unauthorized, await ReadWith(factory, userSecret));
            Assert.Equal(HttpStatusCode.OK, await ReadWith(factory, qaSecret));
        }

        // One read per owner, however many requests: the answers are cached.
        Assert.Equal(3, factory.Directory.Lookups.Count);
        Assert.All(factory.Directory.Lookups.Values, n => Assert.Equal(1, n));
    }

    /// <summary>A <see cref="TestFactory"/> whose directory answers account status from a table.</summary>
    private sealed class DirectoryFactory : TestFactory
    {
        public ScriptedDirectory Directory { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(services =>
            {
                RemoveService<IIdentityService>(services);
                services.AddSingleton<IIdentityService>(Directory);
            });
        }
    }

    private sealed class ScriptedDirectory : WorkItemApprovalTests.EmptyIdentityService, IIdentityService
    {
        public ConcurrentDictionary<string, AccountStatus> Statuses { get; } = new();
        public ConcurrentDictionary<string, int> Lookups { get; } = new();

        Task<AccountStatus> IIdentityService.GetAccountStatus(string userId, CancellationToken ct)
        {
            Lookups.AddOrUpdate(userId, 1, (_, n) => n + 1);
            return Task.FromResult(Statuses.GetValueOrDefault(userId, AccountStatus.Unknown));
        }
    }
}
