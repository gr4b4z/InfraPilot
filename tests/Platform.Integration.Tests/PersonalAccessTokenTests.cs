using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Platform.Api.Infrastructure.Persistence;

namespace Platform.Integration.Tests;

/// <summary>
/// Personal access tokens (<c>/api/me/tokens</c>) — what a person mints on the Agent access page:
/// <list type="bullet">
///   <item>The secret is returned once; lists show only its prefix, and only the owner's tokens.</item>
///   <item>It works wherever an <c>X-Api-Key</c> does for reads, as its owner.</item>
///   <item>A standard one (the default) cannot do what only a pipeline or an administrator does,
///         cannot make approval decisions, and cannot manage tokens. QA and Admin tokens are in
///         <see cref="PersonalAccessTokenAccessTests"/>.</item>
///   <item>Revoked and expired tokens stop working.</item>
/// </list>
/// </summary>
public class PersonalAccessTokenTests : IClassFixture<TestFactory>
{
    private readonly TestFactory _factory;

    public PersonalAccessTokenTests(TestFactory factory) => _factory = factory;

    private async Task<(string Id, string Secret)> CreateTokenAsync(HttpClient owner, string name = "laptop", int days = 30)
    {
        var response = await owner.PostAsJsonAsync("/api/me/tokens", new { name, expiresInDays = days });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return (body.GetProperty("id").GetString()!, body.GetProperty("token").GetString()!);
    }

    private HttpClient TokenClient(string secret)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", secret);
        return client;
    }

    [Fact]
    public async Task Created_token_is_shown_once_and_listed_by_prefix_to_its_owner_only()
    {
        using var qa = _factory.CreateAuthenticatedClient("qa@localhost", "qa123");
        using var admin = _factory.CreateAdminClient();

        var (id, secret) = await CreateTokenAsync(qa, "qa laptop");
        Assert.StartsWith("ipt_", secret);

        var list = await qa.GetFromJsonAsync<JsonElement>("/api/me/tokens");
        var row = list.GetProperty("tokens").EnumerateArray().Single(t => t.GetProperty("id").GetString() == id);
        Assert.Equal("qa laptop", row.GetProperty("name").GetString());
        Assert.StartsWith(row.GetProperty("prefix").GetString()!, secret);
        Assert.False(row.TryGetProperty("token", out _));
        Assert.DoesNotContain(secret, list.GetRawText());

        var adminList = await admin.GetFromJsonAsync<JsonElement>("/api/me/tokens");
        Assert.DoesNotContain(adminList.GetProperty("tokens").EnumerateArray(), t => t.GetProperty("id").GetString() == id);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.DeleteAsync($"/api/me/tokens/{id}")).StatusCode);
    }

    [Fact]
    public async Task Standard_token_reads_as_its_owner_but_never_carries_admin()
    {
        using var admin = _factory.CreateAdminClient();
        var (_, secret) = await CreateTokenAsync(admin);
        using var client = TokenClient(secret);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/deployments/products")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/promotions")).StatusCode);
        // Admin-only groups refuse it even though its owner is an admin.
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/audit")).StatusCode);
    }

    [Fact]
    public async Task Standard_token_cannot_do_pipeline_work_make_decisions_or_manage_tokens()
    {
        using var qa = _factory.CreateAuthenticatedClient("qa@localhost", "qa123");
        var (_, secret) = await CreateTokenAsync(qa);
        using var client = TokenClient(secret);

        var ingest = await client.PostAsJsonAsync("/api/deployments/events", new
        {
            product = "pat-shop", service = "api", environment = "staging", version = "1.0.0", source = "test", deployedAt = DateTimeOffset.UtcNow,
        });
        Assert.Equal(HttpStatusCode.Forbidden, ingest.StatusCode);

        var register = await client.PostAsJsonAsync("/api/builds", new { product = "pat-shop", service = "api", version = "1.0.0", branch = "refs/heads/main" });
        Assert.Equal(HttpStatusCode.Forbidden, register.StatusCode);

        var approve = await client.PostAsJsonAsync($"/api/promotions/{Guid.NewGuid()}/approve", new { comment = "lgtm" });
        Assert.Equal(HttpStatusCode.Forbidden, approve.StatusCode);
        Assert.Contains("portal", await approve.Content.ReadAsStringAsync());

        var signOff = await client.PostAsJsonAsync("/api/work-items/MPT-1/approvals", new { product = "pat-shop", service = "api", targetEnv = "production" });
        Assert.Equal(HttpStatusCode.Forbidden, signOff.StatusCode);

        // A token cannot see, mint or revoke tokens — only a signed-in session can.
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/me/tokens")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.PostAsJsonAsync("/api/me/tokens", new { name = "child", expiresInDays = 7 })).StatusCode);
    }

    [Fact]
    public async Task Revoked_and_expired_tokens_stop_working()
    {
        using var qa = _factory.CreateAuthenticatedClient("qa@localhost", "qa123");
        var (revokedId, revoked) = await CreateTokenAsync(qa, "to revoke");
        var (expiredId, expired) = await CreateTokenAsync(qa, "to expire");

        Assert.Equal(HttpStatusCode.NoContent, (await qa.DeleteAsync($"/api/me/tokens/{revokedId}")).StatusCode);
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            var row = await db.PersonalAccessTokens.SingleAsync(t => t.Id == Guid.Parse(expiredId));
            row.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
            await db.SaveChangesAsync();
        }

        using var revokedClient = TokenClient(revoked);
        using var expiredClient = TokenClient(expired);
        Assert.Equal(HttpStatusCode.Unauthorized, (await revokedClient.GetAsync("/api/deployments/products")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await expiredClient.GetAsync("/api/deployments/products")).StatusCode);

        var list = await qa.GetFromJsonAsync<JsonElement>("/api/me/tokens");
        var revokedRow = list.GetProperty("tokens").EnumerateArray().Single(t => t.GetProperty("id").GetString() == revokedId);
        Assert.NotEqual(JsonValueKind.Null, revokedRow.GetProperty("revokedAt").ValueKind);
    }

    [Fact]
    public async Task Use_is_recorded_on_the_token()
    {
        using var qa = _factory.CreateAuthenticatedClient("qa@localhost", "qa123");
        var (id, secret) = await CreateTokenAsync(qa, "used");
        using var client = TokenClient(secret);
        (await client.GetAsync("/api/deployments/products")).EnsureSuccessStatusCode();

        var list = await qa.GetFromJsonAsync<JsonElement>("/api/me/tokens");
        var row = list.GetProperty("tokens").EnumerateArray().Single(t => t.GetProperty("id").GetString() == id);
        Assert.NotEqual(JsonValueKind.Null, row.GetProperty("lastUsedAt").ValueKind);
    }

    [Fact]
    public async Task A_token_can_live_up_to_a_year()
    {
        using var qa = _factory.CreateAuthenticatedClient("qa@localhost", "qa123");
        var response = await qa.PostAsJsonAsync("/api/me/tokens", new { name = "long-running agent", expiresInDays = 365 });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var lifetime = body.GetProperty("expiresAt").GetDateTimeOffset() - body.GetProperty("createdAt").GetDateTimeOffset();
        Assert.Equal(TimeSpan.FromDays(365), lifetime);
    }

    [Theory]
    [InlineData("", 30)]
    [InlineData("ok name", 366)]
    [InlineData("ok name", 0)]
    public async Task Create_rejects_a_missing_name_or_an_unoffered_lifetime(string name, int days)
    {
        using var qa = _factory.CreateAuthenticatedClient("qa@localhost", "qa123");
        var response = await qa.PostAsJsonAsync("/api/me/tokens", new { name, expiresInDays = days });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(string.IsNullOrWhiteSpace((await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString()));
    }

    [Fact]
    public async Task A_person_holds_at_most_ten_active_tokens()
    {
        using var user = _factory.CreateAuthenticatedClient("user@localhost", "user123");
        var existing = (await user.GetFromJsonAsync<JsonElement>("/api/me/tokens")).GetProperty("tokens")
            .EnumerateArray().Count(t => t.GetProperty("revokedAt").ValueKind == JsonValueKind.Null);
        for (var i = existing; i < 10; i++) await CreateTokenAsync(user, $"t{i}", 7);

        var eleventh = await user.PostAsJsonAsync("/api/me/tokens", new { name = "one too many", expiresInDays = 7 });
        Assert.Equal(HttpStatusCode.BadRequest, eleventh.StatusCode);
    }
}
