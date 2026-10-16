using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Platform.Api.Features.Promotions.Models;
using Platform.Api.Infrastructure.Persistence;

namespace Platform.Integration.Tests;

/// <summary>
/// HTTP surface of the Promotion Freeze: admin create / list / lift under
/// <c>/api/promotions/admin/freezes</c> and the non-admin read under <c>/api/promotions/freezes</c>.
/// The gate behaviour itself is covered by the unit tests (PromotionFreezeTests). Each test uses its
/// own product so tests sharing the fixture's database do not interfere.
/// </summary>
public class PromotionFreezeEndpointTests : IClassFixture<TestFactory>
{
    private readonly TestFactory _factory;

    public PromotionFreezeEndpointTests(TestFactory factory) => _factory = factory;

    [Fact]
    public async Task Create_WithoutReason_Returns400()
    {
        SeedPolicy("frz-400");
        var admin = _factory.CreateAdminClient();

        var response = await admin.PostAsJsonAsync("/api/promotions/admin/freezes",
            new { product = "frz-400", targetEnv = "prod", reason = " " });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Create_AlreadyFrozenPair_Returns409()
    {
        SeedPolicy("frz-409");
        var admin = _factory.CreateAdminClient();
        var first = await admin.PostAsJsonAsync("/api/promotions/admin/freezes",
            new { product = "frz-409", targetEnv = "prod", reason = "first" });
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        var second = await admin.PostAsJsonAsync("/api/promotions/admin/freezes",
            new { product = "frz-409", targetEnv = "prod", reason = "second" });

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task Create_PairWithoutApprovalPolicy_Returns422()
    {
        var admin = _factory.CreateAdminClient();

        var response = await admin.PostAsJsonAsync("/api/promotions/admin/freezes",
            new { product = "frz-no-policy", targetEnv = "prod", reason = "why" });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task CreateAndLift_NonAdmin_Returns403()
    {
        SeedPolicy("frz-403");
        var user = CreateUserClient();

        var create = await user.PostAsJsonAsync("/api/promotions/admin/freezes",
            new { product = "frz-403", targetEnv = "prod", reason = "why" });
        var lift = await user.DeleteAsync("/api/promotions/admin/freezes/frz-403/prod");

        Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, lift.StatusCode);
    }

    [Fact]
    public async Task Freeze_IsListedWithHeldCount_ForAdminAndUser_ThenLifted()
    {
        SeedPolicy("frz-flow");
        SeedPendingCandidate("frz-flow");
        var admin = _factory.CreateAdminClient();
        var user = CreateUserClient();

        var created = await admin.PostAsJsonAsync("/api/promotions/admin/freezes",
            new { product = "frz-flow", targetEnv = "prod", reason = "Release window" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        foreach (var (client, url) in new[] { (admin, "/api/promotions/admin/freezes"), (user, "/api/promotions/freezes") })
        {
            var list = await client.GetAsync(url);
            Assert.Equal(HttpStatusCode.OK, list.StatusCode);
            var freeze = (await Json(list)).GetProperty("freezes").EnumerateArray()
                .Single(f => f.GetProperty("product").GetString() == "frz-flow");
            Assert.Equal("prod", freeze.GetProperty("targetEnv").GetString());
            Assert.Equal("Release window", freeze.GetProperty("reason").GetString());
            Assert.Equal("Admin User", freeze.GetProperty("frozenBy").GetString());
            Assert.Equal(JsonValueKind.String, freeze.GetProperty("frozenAt").ValueKind);
            Assert.Equal(1, freeze.GetProperty("heldCandidates").GetInt32());
        }

        var lift = await admin.DeleteAsync("/api/promotions/admin/freezes/frz-flow/prod");
        Assert.Equal(HttpStatusCode.NoContent, lift.StatusCode);

        var after = await Json(await user.GetAsync("/api/promotions/freezes"));
        Assert.DoesNotContain(after.GetProperty("freezes").EnumerateArray(),
            f => f.GetProperty("product").GetString() == "frz-flow");
        var liftAgain = await admin.DeleteAsync("/api/promotions/admin/freezes/frz-flow/prod");
        Assert.Equal(HttpStatusCode.NotFound, liftAgain.StatusCode);
    }

    [Fact]
    public async Task Detail_FrozenPendingCandidate_ReturnsFreeze_UnfrozenReturnsNull()
    {
        SeedPolicy("frz-detail");
        var frozenId = SeedPendingCandidate("frz-detail");
        var unfrozenId = SeedPendingCandidate("frz-detail-free");
        var admin = _factory.CreateAdminClient();
        var user = CreateUserClient();
        var created = await admin.PostAsJsonAsync("/api/promotions/admin/freezes",
            new { product = "frz-detail", targetEnv = "prod", reason = "Release window" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var frozen = await user.GetAsync($"/api/promotions/{frozenId}");
        Assert.Equal(HttpStatusCode.OK, frozen.StatusCode);
        var body = await Json(frozen);
        var freeze = body.GetProperty("freeze");
        Assert.Equal("frz-detail", freeze.GetProperty("product").GetString());
        Assert.Equal("prod", freeze.GetProperty("targetEnv").GetString());
        Assert.Equal("Release window", freeze.GetProperty("reason").GetString());
        Assert.Equal("Admin User", freeze.GetProperty("frozenBy").GetString());
        Assert.Equal(JsonValueKind.String, freeze.GetProperty("frozenAt").ValueKind);
        var progress = body.GetProperty("approvalProgress");
        Assert.False(progress.GetProperty("allSatisfied").GetBoolean());
        Assert.True(progress.GetProperty("frozen").GetBoolean());
        Assert.Equal("Release window", progress.GetProperty("blockedReason").GetString());

        var free = await Json(await user.GetAsync($"/api/promotions/{unfrozenId}"));
        Assert.Equal(JsonValueKind.Null, free.GetProperty("freeze").ValueKind);
        Assert.False(free.GetProperty("approvalProgress").GetProperty("frozen").GetBoolean());

        var lift = await admin.DeleteAsync("/api/promotions/admin/freezes/frz-detail/prod");
        Assert.Equal(HttpStatusCode.NoContent, lift.StatusCode);
        var afterLift = await Json(await user.GetAsync($"/api/promotions/{frozenId}"));
        Assert.Equal(JsonValueKind.Null, afterLift.GetProperty("freeze").ValueKind);
    }

    [Fact]
    public async Task List_MarksPendingCandidatesOfAFrozenPairAsFrozen_ForNonAdmin()
    {
        SeedPolicy("frz-list");
        SeedPolicy("frz-list-open");
        SeedPendingCandidate("frz-list");
        SeedPendingCandidate("frz-list-open");
        var admin = _factory.CreateAdminClient();
        var created = await admin.PostAsJsonAsync("/api/promotions/admin/freezes",
            new { product = "frz-list", targetEnv = "prod", reason = "Release window" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var user = CreateUserClient();

        bool FrozenOf(JsonElement body) =>
            body.GetProperty("candidates").EnumerateArray().Single().GetProperty("frozen").GetBoolean();

        Assert.True(FrozenOf(await Json(await user.GetAsync("/api/promotions?product=frz-list"))));
        Assert.False(FrozenOf(await Json(await user.GetAsync("/api/promotions?product=frz-list-open"))));
    }

    [Fact]
    public async Task CreateAndLift_UnderAnEnvironmentAlias_UseTheCanonicalKey()
    {
        // Own factory: saving environments rewrites the shared settings row.
        using var f = new TestFactory();
        using var admin = f.CreateAdminClient();
        var settings = await admin.PutAsJsonAsync("/api/settings", new
        {
            environments = new[] { new { key = "production", displayName = "Production", aliases = new[] { "prod" } } },
            roles = Array.Empty<object>(),
            activityTemplate = Array.Empty<object>(),
        });
        Assert.Equal(HttpStatusCode.NoContent, settings.StatusCode);
        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            db.PromotionPolicies.Add(new PromotionPolicy
            {
                Id = Guid.NewGuid(), Product = "frz-alias", SourceEnv = "staging", TargetEnv = "production",
            });
            db.SaveChanges();
        }

        var created = await admin.PostAsJsonAsync("/api/promotions/admin/freezes",
            new { product = "frz-alias", targetEnv = "prod", reason = "Release window" });

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal("production", (await Json(created)).GetProperty("targetEnv").GetString());
        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            Assert.Equal("production", db.PromotionFreezes.Single(x => x.Product == "frz-alias").TargetEnv);
        }

        var lift = await admin.DeleteAsync("/api/promotions/admin/freezes/frz-alias/prod");
        Assert.Equal(HttpStatusCode.NoContent, lift.StatusCode);
        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            Assert.False(db.PromotionFreezes.Any(x => x.Product == "frz-alias"));
        }
    }

    [Fact]
    public async Task Lift_FreezeStoredBeforeItsEnvBecameAnAlias_StillLifts()
    {
        using var f = new TestFactory();
        using var admin = f.CreateAdminClient();
        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            db.PromotionFreezes.Add(new PromotionFreeze
            {
                Id = Guid.NewGuid(), Product = "frz-legacy", TargetEnv = "prod", Reason = "old",
                FrozenBy = "Admin User", FrozenByEmail = "admin@localhost", FrozenAt = DateTimeOffset.UtcNow,
            });
            db.SaveChanges();
        }
        var settings = await admin.PutAsJsonAsync("/api/settings", new
        {
            environments = new[] { new { key = "production", displayName = "Production", aliases = new[] { "prod" } } },
            roles = Array.Empty<object>(),
            activityTemplate = Array.Empty<object>(),
        });
        Assert.Equal(HttpStatusCode.NoContent, settings.StatusCode);

        var lift = await admin.DeleteAsync("/api/promotions/admin/freezes/frz-legacy/prod");

        Assert.Equal(HttpStatusCode.NoContent, lift.StatusCode);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private void SeedPolicy(string product)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        db.PromotionPolicies.Add(new PromotionPolicy
        {
            Id = Guid.NewGuid(),
            Product = product,
            SourceEnv = "staging",
            TargetEnv = "prod",
        });
        db.SaveChanges();
    }

    private Guid SeedPendingCandidate(string product)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var id = Guid.NewGuid();
        db.PromotionCandidates.Add(new PromotionCandidate
        {
            Id = id,
            Product = product,
            Service = "api",
            SourceEnv = "staging",
            TargetEnv = "prod",
            Version = "v1",
            Status = PromotionStatus.Pending,
            ResolvedPolicyJson = "{}",
            CreatedAt = DateTimeOffset.UtcNow,
        });
        db.SaveChanges();
        return id;
    }

    private HttpClient CreateUserClient()
    {
        var client = _factory.CreateClient();
        var login = client.PostAsJsonAsync("/api/auth/login", new { email = "user@localhost", password = "user123" })
            .GetAwaiter().GetResult();
        login.EnsureSuccessStatusCode();
        var token = Json(login).GetAwaiter().GetResult().GetProperty("token").GetString()!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response)
    {
        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        return doc.RootElement.Clone();
    }
}
