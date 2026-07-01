using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Data.Common;
using Platform.Api.Features.Promotions.Models;
using Platform.Api.Infrastructure.Persistence;

namespace Platform.Integration.Tests;

/// <summary>
/// End-to-end tests for the rollback feature: feature-flag + per-product gating, the two selection
/// modes (manual + align-all-except), the "version must have run here" safety rule, auto-approve +
/// completion via deploy event, and the stale-promotion invariant (drift block + reactivation).
/// </summary>
public class RollbackIntegrationTests : IClassFixture<RollbackIntegrationTests.RollbackFactory>, IDisposable
{
    private readonly RollbackFactory _factory;
    private readonly HttpClient _admin;
    private readonly HttpClient _apiKey;

    private const string Product = "acme";

    public RollbackIntegrationTests(RollbackFactory factory)
    {
        _factory = factory;
        _admin = factory.CreateAdminClient();
        _apiKey = factory.CreateClient();
        _apiKey.DefaultRequestHeaders.Add("X-Api-Key", RollbackFactory.TestApiKey);
    }

    public void Dispose()
    {
        _admin.Dispose();
        _apiKey.Dispose();
    }

    // ── Tests ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Create_WhenProductNotEnrolled_Returns400()
    {
        await EnableRollbacksAsync();
        // Deliberately do NOT enroll a fresh product.
        var product = $"unenrolled-{Guid.NewGuid():N}";
        await IngestAsync(product, "api", "staging", "1.0", Hours(-2));

        var resp = await _admin.PostAsJsonAsync("/api/rollbacks", new
        {
            product,
            targetEnv = "staging",
            mode = "manual",
            items = new[] { new { service = "api", toVersion = "1.0" } },
        });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task ManualRollback_AutoApproves_AndCompletesOnDeployEvent()
    {
        var product = await FreshEnrolledProductAsync();
        // History: staging ran 1.0 then 1.1 (current). No promotion policy → rollback auto-approves.
        await IngestAsync(product, "api", "staging", "1.0", Hours(-2));
        await IngestAsync(product, "api", "staging", "1.1", Hours(-1));

        var create = await _admin.PostAsJsonAsync("/api/rollbacks", new
        {
            product,
            targetEnv = "staging",
            mode = "manual",
            reason = "rolling api back to 1.0",
            items = new[] { new { service = "api", toVersion = "1.0" } },
        });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var created = await Body(create);
        var id = created.GetProperty("id").GetString()!;
        Assert.Equal("Approved", created.GetProperty("status").GetString()); // no approver group → auto
        var item = created.GetProperty("items").EnumerateArray().Single();
        Assert.Equal("api", item.GetProperty("service").GetString());
        Assert.Equal("1.1", item.GetProperty("fromVersion").GetString());
        Assert.Equal("1.0", item.GetProperty("toVersion").GetString());

        // The operator performs the rollback → a deploy event for 1.0 lands in staging.
        await IngestAsync(product, "api", "staging", "1.0", Hours(1), isRollback: true);

        var detail = await Body(await _admin.GetAsync($"/api/rollbacks/{id}"));
        Assert.Equal("RolledBack", detail.GetProperty("status").GetString());
        Assert.Equal("RolledBack", detail.GetProperty("items").EnumerateArray().Single().GetProperty("status").GetString());
    }

    [Fact]
    public async Task Cancel_OnApprovedRequest_Rejected()
    {
        var product = await FreshEnrolledProductAsync();
        await IngestAsync(product, "api", "staging", "1.0", Hours(-2));
        await IngestAsync(product, "api", "staging", "1.1", Hours(-1));

        var created = await Body(await _admin.PostAsJsonAsync("/api/rollbacks", new
        {
            product, targetEnv = "staging", mode = "manual",
            items = new[] { new { service = "api", toVersion = "1.0" } },
        }));
        Assert.Equal("Approved", created.GetProperty("status").GetString()); // auto-approved, webhook fired
        var id = created.GetProperty("id").GetString();

        // Cancelling an approved (already-dispatched) rollback must be rejected.
        var cancel = await _admin.PostAsJsonAsync($"/api/rollbacks/{id}/cancel", new { });
        Assert.Equal(HttpStatusCode.BadRequest, cancel.StatusCode);
    }

    [Fact]
    public async Task SafetyRule_RejectsVersionThatNeverRanInEnv()
    {
        var product = await FreshEnrolledProductAsync();
        await IngestAsync(product, "api", "staging", "2.0", Hours(-1)); // only 2.0 ever ran

        var resp = await _admin.PostAsJsonAsync("/api/rollbacks", new
        {
            product,
            targetEnv = "staging",
            mode = "manual",
            items = new[] { new { service = "api", toVersion = "1.0" } }, // 1.0 never ran here
        });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task AlignAllExcept_PreviewAndCreate()
    {
        var product = await FreshEnrolledProductAsync();
        // staging history: svc-a 1→2, svc-b 1→2, svc-c 2. prod (reference): a=1, b=1, c=2.
        await IngestAsync(product, "svc-a", "staging", "1.0", Hours(-3));
        await IngestAsync(product, "svc-a", "staging", "2.0", Hours(-2));
        await IngestAsync(product, "svc-b", "staging", "1.0", Hours(-3));
        await IngestAsync(product, "svc-b", "staging", "2.0", Hours(-2));
        await IngestAsync(product, "svc-c", "staging", "2.0", Hours(-2));
        await IngestAsync(product, "svc-a", "prod", "1.0", Hours(-1));
        await IngestAsync(product, "svc-b", "prod", "1.0", Hours(-1));
        await IngestAsync(product, "svc-c", "prod", "2.0", Hours(-1));

        var body = new
        {
            product,
            targetEnv = "staging",
            mode = "align",
            referenceEnv = "prod",
            exclude = new[] { "svc-b" },
        };

        var preview = await Body(await _admin.PostAsJsonAsync("/api/rollbacks/preview", body));
        var items = preview.GetProperty("items").EnumerateArray().ToList();
        var a = items.Single(i => i.GetProperty("service").GetString() == "svc-a");
        var b = items.Single(i => i.GetProperty("service").GetString() == "svc-b");
        var c = items.Single(i => i.GetProperty("service").GetString() == "svc-c");
        Assert.True(a.GetProperty("eligible").GetBoolean());      // 2.0 → 1.0, 1.0 ran in staging
        Assert.Equal("1.0", a.GetProperty("toVersion").GetString());
        Assert.False(b.GetProperty("eligible").GetBoolean());     // excluded
        Assert.Equal("excluded", b.GetProperty("skipReason").GetString());
        Assert.False(c.GetProperty("eligible").GetBoolean());     // already matching (2.0 == 2.0)

        var create = await _admin.PostAsJsonAsync("/api/rollbacks", body);
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var created = await Body(create);
        var created_items = created.GetProperty("items").EnumerateArray().ToList();
        Assert.Single(created_items); // only svc-a
        Assert.Equal("svc-a", created_items[0].GetProperty("service").GetString());
    }

    [Fact]
    public async Task StalePromotion_BlockedAfterRollback_ThenReactivatedOnRedeploy()
    {
        // The source-drift invariant (PromotionService.EvaluateGateAsync) and idempotent
        // reactivation (re-create on the same natural key → ReevaluateAsync) survive the refactor.
        // What changed: the staging→prod candidate is created via POST /api/promotions (D19), not
        // derived from a staging deploy ingest, and "redeploy" is modelled as re-POSTing the create.
        var product = $"promo-{Guid.NewGuid():N}";
        await SeedPromotionTopologyAndPolicyAsync(product);
        await EnableRollbacksAsync();

        // prod already runs 1.0 so rolling staging back to 1.0 matches prod.
        await IngestAsync(product, "api", "prod", "1.0", Hours(-4));
        // Staging is on 1.1; an external create opens a Pending staging→prod candidate for 1.1.
        await IngestAsync(product, "api", "staging", "1.0", Hours(-3));
        await IngestAsync(product, "api", "staging", "1.1", Hours(-2));
        var create = await CreatePromotionAsync(product, "api", "staging", "prod", "1.1");
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var candidateId = await GetPendingCandidateIdAsync(product, "prod");
        Assert.NotNull(candidateId);

        // Roll staging back to 1.0 (flagged rollback).
        await IngestAsync(product, "api", "staging", "1.0", Hours(-1), isRollback: true);

        // Approving now must NOT promote: the source env no longer runs 1.1 (drifted).
        var approve = await _admin.PostAsJsonAsync($"/api/promotions/{candidateId}/approve", new { });
        Assert.Equal(HttpStatusCode.OK, approve.StatusCode);
        var afterApprove = await Body(await _admin.GetAsync($"/api/promotions/{candidateId}"));
        Assert.Equal("Pending", afterApprove.GetProperty("candidate").GetProperty("status").GetString()); // blocked by drift

        // Redeploy 1.1 to staging, then the external system re-POSTs the create for the same
        // natural key → the existing Pending candidate is reactivated and re-evaluated. Drift is
        // gone and the prior approval already satisfies the gate → Approved.
        await IngestAsync(product, "api", "staging", "1.1", Hours(1));
        var recreate = await CreatePromotionAsync(product, "api", "staging", "prod", "1.1");
        Assert.Equal(HttpStatusCode.Created, recreate.StatusCode);
        var afterRedeploy = await Body(await _admin.GetAsync($"/api/promotions/{candidateId}"));
        Assert.Equal("Approved", afterRedeploy.GetProperty("candidate").GetProperty("status").GetString());
    }

    [Fact]
    public async Task RollbackToVersionAheadOfProd_PromotionCandidateCanBeCreatedForRolledBackVersion()
    {
        // Was RollbackToVersionAheadOfProd_RecreatesPromotionCandidate. Candidates are no longer
        // auto-recreated when a rollback lands (D19 — ingest never generates candidates). The
        // equivalent new behaviour: after rolling staging back to 2.0 (which still differs from
        // prod 1.5), the external system opens a fresh staging→prod candidate for 2.0 via the
        // create API, and it lands Pending under the gated policy.
        var product = $"promo-{Guid.NewGuid():N}";
        await SeedPromotionTopologyAndPolicyAsync(product);
        await EnableRollbacksAsync();
        await EnrollAsync(product);

        await IngestAsync(product, "api", "prod", "1.5", Hours(-5));
        await IngestAsync(product, "api", "staging", "2.0", Hours(-4));
        await IngestAsync(product, "api", "staging", "2.1", Hours(-3));

        // Rollback staging 2.1 → 2.0.
        var created = await Body(await _admin.PostAsJsonAsync("/api/rollbacks", new
        {
            product, targetEnv = "staging", mode = "manual",
            items = new[] { new { service = "api", toVersion = "2.0" } },
        }));
        var rbId = created.GetProperty("id").GetString();
        if (created.GetProperty("status").GetString() == "Pending")
            await _admin.PostAsJsonAsync($"/api/rollbacks/{rbId}/approve", new { });
        await IngestAsync(product, "api", "staging", "2.0", Hours(-1), isRollback: true); // rollback lands

        // The external system opens a staging→prod candidate for the rolled-back 2.0.
        var create = await CreatePromotionAsync(product, "api", "staging", "prod", "2.0");
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);

        // A staging→prod candidate for 2.0 must now be Pending (source still runs 2.0, no drift).
        var promos = await Body(await _admin.GetAsync($"/api/promotions/?product={product}&targetEnv=prod&status=Pending"));
        var cands = promos.GetProperty("candidates").EnumerateArray()
            .Select(c => c.GetProperty("version").GetString()).ToList();
        Assert.Contains("2.0", cands);
    }

    [Fact]
    public async Task PromotionCandidate_CarriesExternallyComputedWorkItemDiff()
    {
        // Was RollbackBundle_ReflectsVersionDiff_NotRevertedStories. The candidate no longer derives
        // its work-item bundle from deploy-event history (self-contained, D19): the external creator
        // computes the authoritative net change set vs the target and POSTs it as work-item
        // references, which populate the candidate's PromotionWorkItem index. This asserts that the
        // candidate carries exactly the work items it was created with — the 1.3 promotion carries
        // only FOO-3 (FOO-4 reverted), while the roll-forward 1.4 carries FOO-3 + FOO-4.
        var product = $"promo-{Guid.NewGuid():N}";
        await SeedPromotionTopologyAndPolicyAsync(product);
        await EnableRollbacksAsync();
        await EnrollAsync(product);

        // Establish history: prod=1.1; staging shipped 1.3 then 1.4 (so both versions are valid
        // rollback targets in staging).
        await IngestAsync(product, "api", "prod", "1.1", Hours(-6));
        await IngestAsync(product, "api", "staging", "1.3", Hours(-5));
        await IngestAsync(product, "api", "staging", "1.4", Hours(-4));

        // Roll staging back to 1.3 and land it. The external system computes the net change set vs
        // prod (1.1 → 1.3 carries only FOO-3; 1.4/FOO-4 was reverted) and POSTs the candidate.
        await RollbackAndLand(product, "api", "1.3", Hours(-1));
        await CreatePromotionAsync(product, "api", "staging", "prod", "1.3", workItemKeys: new[] { "FOO-3" });
        var keys13 = await CandidateWorkItemKeysAsync(product, "prod", "1.3");
        Assert.Contains("FOO-3", keys13);
        Assert.DoesNotContain("FOO-4", keys13); // reverted — must not leak onto the 1.3 promotion

        // Roll forward to 1.4; the net change set vs prod now carries FOO-3 + FOO-4.
        await RollbackAndLand(product, "api", "1.4", Hours(1));
        await CreatePromotionAsync(product, "api", "staging", "prod", "1.4", workItemKeys: new[] { "FOO-3", "FOO-4" });
        var keys14 = await CandidateWorkItemKeysAsync(product, "prod", "1.4");
        Assert.Contains("FOO-3", keys14);
        Assert.Contains("FOO-4", keys14); // roll-forward carries the full diff vs prod
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    // Create a promotion candidate via the external create API (API-key auth). Optionally carries
    // work-item references, which populate the candidate's self-contained PromotionWorkItem index.
    private async Task<HttpResponseMessage> CreatePromotionAsync(
        string product, string service, string sourceEnv, string targetEnv, string version,
        string[]? workItemKeys = null)
    {
        var references = (workItemKeys ?? Array.Empty<string>())
            .Select(k => new { type = "work-item", key = k })
            .ToArray();
        return await _apiKey.PostAsJsonAsync("/api/promotions", new
        {
            product, service, sourceEnv, targetEnv, version,
            references,
        });
    }

    private async Task RollbackAndLand(string product, string service, string toVersion, DateTimeOffset landAt)
    {
        var created = await Body(await _admin.PostAsJsonAsync("/api/rollbacks", new
        {
            product, targetEnv = "staging", mode = "manual",
            items = new[] { new { service, toVersion } },
        }));
        var id = created.GetProperty("id").GetString();
        if (created.GetProperty("status").GetString() == "Pending")
            await _admin.PostAsJsonAsync($"/api/rollbacks/{id}/approve", new { });
        await IngestAsync(product, service, "staging", toVersion, landAt, isRollback: true);
    }

    private async Task<List<string>> CandidateWorkItemKeysAsync(string product, string targetEnv, string version)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var cand = await db.PromotionCandidates
            .Where(c => c.Product == product && c.TargetEnv == targetEnv && c.Version == version)
            .OrderByDescending(c => c.CreatedAt)
            .FirstAsync();
        // The candidate is self-contained: its work items live in the PromotionWorkItem index
        // keyed on CandidateId (no more DeployEvent bundle to aggregate).
        return await db.PromotionWorkItems
            .Where(w => w.CandidateId == cand.Id)
            .Select(w => w.WorkItemKey)
            .ToListAsync();
    }

    private async Task EnrollAsync(string product)
    {
        var enabled = await GetEnabledProductsAsync();
        enabled.Add(product);
        (await _admin.PutAsJsonAsync("/api/rollbacks/admin/enabled-products", new { products = enabled }))
            .EnsureSuccessStatusCode();
    }

    private async Task EnableRollbacksAsync()
    {
        (await _admin.PutAsJsonAsync("/api/features/features.rollbacks", new { enabled = true })).EnsureSuccessStatusCode();
        (await _admin.PutAsJsonAsync("/api/features/features.promotions", new { enabled = true })).EnsureSuccessStatusCode();
    }

    private async Task<string> FreshEnrolledProductAsync()
    {
        await EnableRollbacksAsync();
        var product = $"acme-{Guid.NewGuid():N}";
        var enabled = await GetEnabledProductsAsync();
        enabled.Add(product);
        (await _admin.PutAsJsonAsync("/api/rollbacks/admin/enabled-products", new { products = enabled }))
            .EnsureSuccessStatusCode();
        return product;
    }

    private async Task<List<string>> GetEnabledProductsAsync()
    {
        var body = await Body(await _admin.GetAsync("/api/rollbacks/admin/enabled-products"));
        return body.GetProperty("products").EnumerateArray().Select(e => e.GetString()!).ToList();
    }

    private async Task IngestAsync(string product, string service, string env, string version,
        DateTimeOffset deployedAt, bool isRollback = false)
    {
        var resp = await _apiKey.PostAsJsonAsync("/api/deployments/events", new
        {
            product, service, environment = env, version,
            source = "rollback-test", deployedAt, status = "succeeded", isRollback,
        });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
    }

    private async Task<string?> GetPendingCandidateIdAsync(string product, string targetEnv)
    {
        var body = await Body(await _admin.GetAsync(
            $"/api/promotions/?product={product}&targetEnv={targetEnv}&status=Pending"));
        var arr = body.GetProperty("candidates").EnumerateArray().ToList();
        return arr.Count == 0 ? null : arr[0].GetProperty("id").GetString();
    }

    // Seeds a staging→prod topology and a product-default promotion policy (with an approver group
    // so candidates are born Pending) directly via the DbContext.
    private async Task SeedPromotionTopologyAndPolicyAsync(string product)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        if (!await db.PlatformSettings.AnyAsync(s => s.Key == "promotions.topology"))
        {
            db.PlatformSettings.Add(new Platform.Api.Infrastructure.Features.PlatformSetting
            {
                Key = "promotions.topology",
                Value = "{\"environments\":[\"staging\",\"prod\"],\"edges\":[{\"from\":\"staging\",\"to\":\"prod\"}]}",
                UpdatedAt = DateTimeOffset.UtcNow,
                UpdatedBy = "test",
            });
        }
        db.PromotionPolicies.Add(new PromotionPolicy
        {
            Id = Guid.NewGuid(),
            Product = product,
            Service = null,
            SourceEnv = "staging",
            TargetEnv = "prod",
            // §8 rule tree: a single "any one member of release-managers" requirement, so candidates
            // resolved against this policy are born Pending (a human gate exists).
            ApprovalSteps = new()
            {
                new ApprovalStep("Approval", new()
                {
                    new ApproverRequirement("Release Managers", new() { new GroupRef("release-managers", "release-managers") }, new(), 1),
                }),
            },
        });
        await db.SaveChangesAsync();
    }

    private static DateTimeOffset Hours(double h) => DateTimeOffset.UtcNow.AddHours(h);

    private static async Task<JsonElement> Body(HttpResponseMessage resp)
    {
        var stream = await resp.Content.ReadAsStreamAsync();
        return (await JsonDocument.ParseAsync(stream)).RootElement;
    }

    // ── Factory ───────────────────────────────────────────────────────────

    public class RollbackFactory : TestFactory
    {
        public const string TestApiKey = "rollback-test-api-key-13579";

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("Deployments:ApiKeys:0:Name", "rollback-integration-test");
            builder.UseSetting("Deployments:ApiKeys:0:Key", TestApiKey);
            builder.UseSetting("Deployments:ApiKeys:0:Roles:0", "InfraPortal.Admin");
        }
    }
}
