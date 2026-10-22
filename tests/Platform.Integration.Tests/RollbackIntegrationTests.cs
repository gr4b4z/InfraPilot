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
using Platform.Api.Infrastructure.Identity;
using Platform.Api.Infrastructure.Persistence;

namespace Platform.Integration.Tests;

/// <summary>
/// End-to-end tests for the rollback feature: per-product creator and approver permissions
/// (<c>RollbackPolicy</c>), the admin approval-gate override, the two selection modes (manual +
/// align-all-except), the "version must have run here" safety rule, completion via deploy event, and
/// the stale-promotion invariant (drift block + reactivation).
/// </summary>
public class RollbackIntegrationTests : IClassFixture<RollbackIntegrationTests.RollbackFactory>, IDisposable
{
    private readonly RollbackFactory _factory;
    private readonly HttpClient _admin;
    private readonly HttpClient _apiKey;
    // Two non-admin identities, so creator/approver membership can be tested from both sides.
    private readonly HttpClient _user;   // user@localhost   — QA + User
    private readonly HttpClient _viewer; // viewer@localhost — no roles at all

    private const string Product = "acme";

    public RollbackIntegrationTests(RollbackFactory factory)
    {
        _factory = factory;
        _admin = factory.CreateAdminClient();
        _apiKey = factory.CreateClient();
        _apiKey.DefaultRequestHeaders.Add("X-Api-Key", RollbackFactory.TestApiKey);
        _user = factory.CreateAuthenticatedClient("user@localhost", "user123");
        _viewer = factory.CreateAuthenticatedClient("viewer@localhost", "viewer123");
    }

    public void Dispose()
    {
        _admin.Dispose();
        _apiKey.Dispose();
        _user.Dispose();
        _viewer.Dispose();
    }

    private static object RollbackBody(string product, string toVersion, string targetEnv = "staging") => new
    {
        product,
        targetEnv,
        mode = "manual",
        items = new[] { new { service = "api", toVersion } },
    };

    // ── Tests ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Create_ForUnconfiguredProduct_ByNonAdmin_Returns403()
    {
        await EnableRollbacksAsync();
        // Deliberately do NOT configure a policy for this product.
        var product = $"unconfigured-{Guid.NewGuid():N}";
        await IngestAsync(product, "api", "staging", "1.0", Hours(-2));
        await IngestAsync(product, "api", "staging", "1.1", Hours(-1));

        var resp = await _viewer.PostAsJsonAsync("/api/rollbacks", RollbackBody(product, "1.0"));
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task Create_ForUnconfiguredProduct_ByAdmin_LandsPending_AndNeverAutoApproves()
    {
        // The security fix: an unconfigured environment used to project to "auto-approve" and revert
        // with no human gate at all. It must now land Pending, approvable only by an explicit override.
        await EnableRollbacksAsync();
        var product = $"unconfigured-{Guid.NewGuid():N}";
        await IngestAsync(product, "api", "staging", "1.0", Hours(-2));
        await IngestAsync(product, "api", "staging", "1.1", Hours(-1));

        var created = await Body(await _admin.PostAsJsonAsync("/api/rollbacks", RollbackBody(product, "1.0")));
        Assert.Equal("Pending", created.GetProperty("status").GetString());
        var id = created.GetProperty("id").GetString();

        var detail = await Body(await _admin.GetAsync($"/api/rollbacks/{id}"));
        Assert.True(detail.GetProperty("unconfigured").GetBoolean());
        Assert.False(detail.GetProperty("canApprove").GetBoolean());  // nobody is authorized
        Assert.True(detail.GetProperty("canOverride").GetBoolean());  // only route forward

        // A normal approval must refuse, and say why.
        var approve = await _admin.PostAsJsonAsync($"/api/rollbacks/{id}/approve", new { });
        Assert.Equal(HttpStatusCode.BadRequest, approve.StatusCode);
        Assert.Contains("override", (await Body(approve)).GetProperty("error").GetString()!);
    }

    [Fact]
    public async Task Create_ByNonCreator_Returns403_AndByListedCreator_Succeeds()
    {
        await EnableRollbacksAsync();
        var product = $"creators-{Guid.NewGuid():N}";
        await IngestAsync(product, "api", "staging", "1.0", Hours(-2));
        await IngestAsync(product, "api", "staging", "1.1", Hours(-1));

        // Only user@localhost may create; viewer@localhost may not.
        await UpsertPolicyAsync(product, "staging", creatorUsers: new[] { "user@localhost" }, requirementGroup: null);

        var denied = await _viewer.PostAsJsonAsync("/api/rollbacks", RollbackBody(product, "1.0"));
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);

        var allowed = await _user.PostAsJsonAsync("/api/rollbacks", RollbackBody(product, "1.0"));
        Assert.Equal(HttpStatusCode.Created, allowed.StatusCode);
    }

    [Fact]
    public async Task Preview_ByNonCreator_Returns403()
    {
        // The dry run reports current and candidate versions across environments, so it is gated by the
        // same create permission rather than left open as a "read-only" endpoint.
        await EnableRollbacksAsync();
        var product = $"preview-{Guid.NewGuid():N}";
        await IngestAsync(product, "api", "staging", "1.0", Hours(-2));
        await IngestAsync(product, "api", "staging", "1.1", Hours(-1));
        await UpsertPolicyAsync(product, "staging", creatorUsers: new[] { "user@localhost" }, requirementGroup: null);

        var resp = await _viewer.PostAsJsonAsync("/api/rollbacks/preview", RollbackBody(product, "1.0"));
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task Approve_ByAdminNotNamedInGate_Returns403()
    {
        // Admins are NOT implicit members of every approver group for rollbacks (unlike promotions):
        // an admin clearing a gate has to do it through the reason-carrying override, so a bypass never
        // looks like an ordinary approval in the history.
        await EnableRollbacksAsync();
        var product = $"gated-{Guid.NewGuid():N}";
        await IngestAsync(product, "api", "staging", "1.0", Hours(-2));
        await IngestAsync(product, "api", "staging", "1.1", Hours(-1));
        await UpsertPolicyAsync(product, "staging",
            creatorUsers: new[] { "admin@localhost" }, requirementGroup: RollbackFactory.ApproverGroup);

        var created = await Body(await _admin.PostAsJsonAsync("/api/rollbacks", RollbackBody(product, "1.0")));
        Assert.Equal("Pending", created.GetProperty("status").GetString());
        var id = created.GetProperty("id").GetString();

        // admin@localhost is in no group in the strict test directory, so the only thing that could let
        // this through is the implicit admin shortcut — which rollbacks switch off.
        var approve = await _admin.PostAsJsonAsync($"/api/rollbacks/{id}/approve", new { });
        Assert.Equal(HttpStatusCode.Forbidden, approve.StatusCode);

        // Still Pending, and the gate reports 0 of 1 satisfied.
        var detail = await Body(await _admin.GetAsync($"/api/rollbacks/{id}"));
        Assert.Equal("Pending", detail.GetProperty("status").GetString());
        var req = detail.GetProperty("gate").EnumerateArray().Single();
        Assert.Equal(0, req.GetProperty("matched").GetInt32());
        Assert.Equal(1, req.GetProperty("required").GetInt32());
        Assert.False(req.GetProperty("satisfied").GetBoolean());

        // A genuine member of the group can approve, confirming the gate is refusing the admin
        // specifically rather than being unsatisfiable.
        var byMember = await _viewer.PostAsJsonAsync($"/api/rollbacks/{id}/approve", new { });
        Assert.Equal(HttpStatusCode.OK, byMember.StatusCode);
        Assert.Equal("Approved", (await Body(byMember)).GetProperty("status").GetString());
    }

    [Fact]
    public async Task Override_RequiresAdminAndReason_ThenApprovesAndIsFlagged()
    {
        await EnableRollbacksAsync();
        var product = $"override-{Guid.NewGuid():N}";
        await IngestAsync(product, "api", "staging", "1.0", Hours(-2));
        await IngestAsync(product, "api", "staging", "1.1", Hours(-1));
        await UpsertPolicyAsync(product, "staging",
            creatorUsers: new[] { "admin@localhost", "viewer@localhost" }, requirementGroup: RollbackFactory.ApproverGroup);

        var created = await Body(await _admin.PostAsJsonAsync("/api/rollbacks", RollbackBody(product, "1.0")));
        var id = created.GetProperty("id").GetString();
        Assert.Equal("Pending", created.GetProperty("status").GetString());

        // Non-admin cannot override even though they may create here.
        var byViewer = await _viewer.PostAsJsonAsync($"/api/rollbacks/{id}/override-approval",
            new { reason = "let me through" });
        Assert.Equal(HttpStatusCode.Forbidden, byViewer.StatusCode);

        // A reason is mandatory.
        var noReason = await _admin.PostAsJsonAsync($"/api/rollbacks/{id}/override-approval", new { reason = "  " });
        Assert.Equal(HttpStatusCode.BadRequest, noReason.StatusCode);

        var ok = await _admin.PostAsJsonAsync($"/api/rollbacks/{id}/override-approval",
            new { reason = "SEV1 INC-42, approvers unreachable" });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);

        var detail = await Body(await _admin.GetAsync($"/api/rollbacks/{id}"));
        Assert.Equal("Approved", detail.GetProperty("status").GetString());
        Assert.True(detail.GetProperty("approvalOverridden").GetBoolean());
        var approval = detail.GetProperty("approvals").EnumerateArray().Single();
        Assert.True(approval.GetProperty("isOverride").GetBoolean());
        Assert.Equal("SEV1 INC-42, approvers unreachable", approval.GetProperty("comment").GetString());
        Assert.Equal("admin@localhost", approval.GetProperty("approverEmail").GetString());

        // Overriding twice is refused — the request is no longer Pending.
        var again = await _admin.PostAsJsonAsync($"/api/rollbacks/{id}/override-approval", new { reason = "again" });
        Assert.Equal(HttpStatusCode.BadRequest, again.StatusCode);
    }

    [Fact]
    public async Task Approve_ByNamedUser_SatisfiesGate()
    {
        // The positive path for the gate: a requirement naming a user explicitly is satisfied by them.
        await EnableRollbacksAsync();
        var product = $"approve-{Guid.NewGuid():N}";
        await IngestAsync(product, "api", "staging", "1.0", Hours(-2));
        await IngestAsync(product, "api", "staging", "1.1", Hours(-1));

        var resp = await _admin.PostAsJsonAsync("/api/rollbacks/admin/policies", new
        {
            product,
            targetEnv = "staging",
            creators = new { groups = Array.Empty<object>(), users = new[] { "admin@localhost" } },
            steps = new[]
            {
                new
                {
                    name = "Approval",
                    requirements = new[]
                    {
                        new
                        {
                            name = "Named approver",
                            groups = Array.Empty<object>(),
                            users = new[] { "viewer@localhost" },
                            minApprovers = 1,
                        },
                    },
                },
            },
        });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);

        var created = await Body(await _admin.PostAsJsonAsync("/api/rollbacks", RollbackBody(product, "1.0")));
        var id = created.GetProperty("id").GetString();
        Assert.Equal("Pending", created.GetProperty("status").GetString());

        var approve = await _viewer.PostAsJsonAsync($"/api/rollbacks/{id}/approve", new { comment = "ok" });
        Assert.Equal(HttpStatusCode.OK, approve.StatusCode);
        Assert.Equal("Approved", (await Body(approve)).GetProperty("status").GetString());

        var detail = await Body(await _admin.GetAsync($"/api/rollbacks/{id}"));
        Assert.False(detail.GetProperty("approvalOverridden").GetBoolean()); // a real approval, not a bypass
    }

    [Fact]
    public async Task EnvSpecificPolicy_WinsOverProductDefault()
    {
        await EnableRollbacksAsync();
        var product = $"scoped-{Guid.NewGuid():N}";
        await IngestAsync(product, "api", "staging", "1.0", Hours(-2));
        await IngestAsync(product, "api", "staging", "1.1", Hours(-1));
        await IngestAsync(product, "api", "prod", "1.0", Hours(-2));
        await IngestAsync(product, "api", "prod", "1.1", Hours(-1));

        // Product default: ungated. Prod: gated. Same product, different answers per environment.
        await UpsertPolicyAsync(product, targetEnv: null,
            creatorUsers: new[] { "viewer@localhost" }, requirementGroup: null);
        await UpsertPolicyAsync(product, targetEnv: "prod",
            creatorUsers: new[] { "viewer@localhost" }, requirementGroup: RollbackFactory.ApproverGroup);

        var staging = await Body(await _viewer.PostAsJsonAsync("/api/rollbacks", RollbackBody(product, "1.0")));
        Assert.Equal("Approved", staging.GetProperty("status").GetString()); // default policy, no gate

        var prod = await Body(await _viewer.PostAsJsonAsync("/api/rollbacks",
            RollbackBody(product, "1.0", targetEnv: "prod")));
        Assert.Equal("Pending", prod.GetProperty("status").GetString());     // env-specific gate applies
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
        Assert.True(a.GetProperty("eligible").GetBoolean());      // 2.0 → 1.0, 1.0 ran in staging
        Assert.Equal("1.0", a.GetProperty("toVersion").GetString());
        Assert.False(b.GetProperty("eligible").GetBoolean());     // excluded
        Assert.Equal("excluded", b.GetProperty("skipReason").GetString());
        // svc-c already matches the reference (2.0 == 2.0), so it isn't in the diff and isn't listed.
        Assert.DoesNotContain(items, i => i.GetProperty("service").GetString() == "svc-c");

        var create = await _admin.PostAsJsonAsync("/api/rollbacks", body);
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var created = await Body(create);
        var created_items = created.GetProperty("items").EnumerateArray().ToList();
        Assert.Single(created_items); // only svc-a
        Assert.Equal("svc-a", created_items[0].GetProperty("service").GetString());
    }

    // ── Staleness: a rollback closes once it no longer describes the environment ──

    [Fact]
    public async Task Align_IgnoresFailedDeploys_AndCreatesNothingForServicesAlreadyAtReference()
    {
        // The 2026-09-21 shape: a release whose deploys all failed, so the cluster never left the old
        // version, and an align to prod raised on top of it.
        var product = await FreshEnrolledProductAsync();
        await IngestAsync(product, "svc-a", "staging", "5.0.350", Hours(-10));
        await IngestAsync(product, "svc-a", "staging", "5.9.0", Hours(-3), status: "failed");
        await IngestAsync(product, "svc-a", "staging", "5.9.0", Hours(-2), status: "failed");
        await IngestAsync(product, "svc-a", "prod", "5.0.350", Hours(-5));
        // svc-b really did move to 5.9.0, so it is the one genuine difference.
        await IngestAsync(product, "svc-b", "staging", "5.0.1", Hours(-10));
        await IngestAsync(product, "svc-b", "staging", "5.9.0", Hours(-2));
        await IngestAsync(product, "svc-b", "prod", "5.0.1", Hours(-5));
        // svc-c only ever failed in staging: nothing runs there, so it is not a service to align.
        await IngestAsync(product, "svc-c", "staging", "5.9.0", Hours(-2), status: "failed");
        await IngestAsync(product, "svc-c", "prod", "5.0.7", Hours(-5));

        var body = new { product, targetEnv = "staging", mode = "align", referenceEnv = "prod" };

        var preview = await Body(await _admin.PostAsJsonAsync("/api/rollbacks/preview", body));
        var a = ItemFor(preview, "svc-a");
        Assert.False(a.GetProperty("eligible").GetBoolean());
        Assert.Equal("5.0.350", a.GetProperty("fromVersion").GetString()); // the last succeeded deploy
        Assert.Equal("already running this version", a.GetProperty("skipReason").GetString());
        // Still reported: deploy history shows 5.9.0, and the preview has to say why it disagrees.
        var note = a.GetProperty("note").GetString()!;
        Assert.Contains("5.9.0", note);
        Assert.Contains("failed", note);
        Assert.Contains("5.0.350 is still running", note);
        Assert.True(ItemFor(preview, "svc-b").GetProperty("eligible").GetBoolean());
        Assert.DoesNotContain(preview.GetProperty("items").EnumerateArray(),
            i => i.GetProperty("service").GetString() == "svc-c");

        var created = await Body(await _admin.PostAsJsonAsync("/api/rollbacks", body));
        var item = Assert.Single(created.GetProperty("items").EnumerateArray());
        Assert.Equal("svc-b", item.GetProperty("service").GetString());
        Assert.Equal("5.9.0", item.GetProperty("fromVersion").GetString());
    }

    [Fact]
    public async Task Manual_AfterAFailedDeploy_StartsFromTheFailedVersion_AndNeverTargetsIt()
    {
        var product = await FreshEnrolledProductAsync();
        await IngestAsync(product, "api", "staging", "1.0", Hours(-3));
        await IngestAsync(product, "api", "staging", "1.1", Hours(-2));
        await IngestAsync(product, "api", "staging", "2.0", Hours(-1), status: "failed");

        // 2.0 is the version whose deploy failed: rolling "back" to it would change nothing.
        var preview = await Body(await _admin.PostAsJsonAsync("/api/rollbacks/preview", RollbackBody(product, "2.0")));
        var target = ItemFor(preview, "api");
        Assert.False(target.GetProperty("eligible").GetBoolean());
        Assert.Equal("this is the version whose deploy failed", target.GetProperty("skipReason").GetString());

        // The release pipeline declares 2.0 now, so the rollback is written against it, and the last
        // succeeded version is a real target: putting it back means deploying it again.
        var restore = await Body(await _admin.PostAsJsonAsync("/api/rollbacks/preview", RollbackBody(product, "1.1")));
        var item = ItemFor(restore, "api");
        Assert.True(item.GetProperty("eligible").GetBoolean());
        Assert.Equal("2.0", item.GetProperty("fromVersion").GetString());
        Assert.Contains("1.1 is the last version that deployed successfully", item.GetProperty("note").GetString()!);

        var created = await Body(await _admin.PostAsJsonAsync("/api/rollbacks", RollbackBody(product, "1.0")));
        Assert.Equal("2.0", ItemFor(created, "api").GetProperty("fromVersion").GetString());
    }

    [Fact]
    public async Task LastKnownGood_RestoresTheLastSucceededVersion_AndClosesWhenItLands()
    {
        var product = await FreshEnrolledProductAsync();
        await IngestAsync(product, "api", "staging", "1.0", Hours(-3));
        var good = await IngestAsync(product, "api", "staging", "1.1", Hours(-2));
        var failed = await IngestAsync(product, "api", "staging", "1.2", Hours(-1), status: "failed");

        var offer = await Body(await _admin.GetAsync($"/api/rollbacks/last-known-good/{failed}"));
        Assert.True(offer.GetProperty("available").GetBoolean());
        Assert.Equal("1.2", offer.GetProperty("failedVersion").GetString());
        Assert.Equal("1.1", offer.GetProperty("lastGoodVersion").GetString());
        Assert.Equal(good, offer.GetProperty("lastGoodEventId").GetString());
        Assert.Equal("auto", offer.GetProperty("approval").GetString());
        Assert.True(offer.GetProperty("canCreate").GetBoolean());
        Assert.Equal(JsonValueKind.Null, offer.GetProperty("openRollback").ValueKind);
        // Nothing in this test host subscribes to rollback.approved, and the page has to say so.
        Assert.False(offer.GetProperty("executorConfigured").GetBoolean());

        var resp = await _admin.PostAsJsonAsync($"/api/rollbacks/last-known-good/{failed}", new { });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var created = await Body(resp);
        Assert.Equal("Approved", created.GetProperty("status").GetString());
        Assert.Contains("1.2", created.GetProperty("reason").GetString()!);
        var item = ItemFor(created, "api");
        Assert.Equal("1.2", item.GetProperty("fromVersion").GetString());
        Assert.Equal("1.1", item.GetProperty("toVersion").GetString());
        var id = created.GetProperty("id").GetString()!;

        // 1.1 is what InfraPortal counts as running, but the item is not settled on approval: the
        // redeploy is the point, and the pipeline still has to carry it.
        var detail = await Body(await _admin.GetAsync($"/api/rollbacks/{id}"));
        Assert.Equal("Pending", ItemFor(detail, "api").GetProperty("status").GetString());
        Assert.Equal(new[] { "api@1.1" }, await OpenItemsSeenByPipelineAsync(product, "staging"));

        // A second click points at the open request instead of raising another.
        var again = await _admin.GetAsync($"/api/rollbacks/last-known-good/{failed}");
        Assert.Equal(id, (await Body(again)).GetProperty("openRollback").GetProperty("id").GetString());
        var duplicate = await _admin.PostAsJsonAsync($"/api/rollbacks/last-known-good/{failed}", new { });
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Equal(id, (await Body(duplicate)).GetProperty("rollbackId").GetString());

        // The executor redeploys 1.1: the item and the request close, and the failure is history.
        var landed = await IngestAsync(product, "api", "staging", "1.1", Hours(1));
        var done = await Body(await _admin.GetAsync($"/api/rollbacks/{id}"));
        Assert.Equal("RolledBack", done.GetProperty("status").GetString());
        Assert.Equal(landed, ItemFor(done, "api").GetProperty("completedDeployEventId").GetString());
        var after = await Body(await _admin.GetAsync($"/api/rollbacks/last-known-good/{failed}"));
        Assert.False(after.GetProperty("available").GetBoolean());
        Assert.Contains("after this failure", after.GetProperty("reason").GetString()!);
    }

    [Fact]
    public async Task LastKnownGood_IsOnlyOfferedForAFailureThatIsStillCurrent()
    {
        var product = await FreshEnrolledProductAsync();
        var succeeded = await IngestAsync(product, "api", "staging", "1.0", Hours(-3));
        var onlyFailed = await IngestAsync(product, "web", "staging", "1.0", Hours(-3), status: "failed");
        await IngestAsync(product, "worker", "staging", "3.0", Hours(-3));
        var redeployFailed = await IngestAsync(product, "worker", "staging", "3.0", Hours(-2), status: "failed");
        // The 2026-10-22 shape after the rollback: 2.1 failed, 2.0 was deployed again and failed too.
        await IngestAsync(product, "cache", "staging", "2.0", Hours(-3));
        var failedBeforeRestore = await IngestAsync(product, "cache", "staging", "2.1", Hours(-2), status: "failed");
        await IngestAsync(product, "cache", "staging", "2.0", Hours(-1), status: "failed");
        await IngestAsync(product, "queue", "staging", "4.0", Hours(-3));
        var failedBeforeInProgress = await IngestAsync(product, "queue", "staging", "4.1", Hours(-2), status: "failed");
        await IngestAsync(product, "queue", "staging", "4.2", Hours(-1), status: "in_progress");

        async Task<string> ReasonFor(string eventId)
        {
            var offer = await Body(await _admin.GetAsync($"/api/rollbacks/last-known-good/{eventId}"));
            Assert.False(offer.GetProperty("available").GetBoolean());
            return offer.GetProperty("reason").GetString()!;
        }

        Assert.Equal("This deployment did not fail.", await ReasonFor(succeeded));
        Assert.Contains("No version of web has deployed successfully", await ReasonFor(onlyFailed));
        // A failed redeploy of the running version has no earlier good version to go back to.
        Assert.Contains("3.0 is also the last version that deployed successfully", await ReasonFor(redeployFailed));
        // Going back already failed, so the reason says so instead of suggesting something is still running.
        var restoreFailed = await ReasonFor(failedBeforeRestore);
        Assert.Contains("2.0, the last version that deployed successfully in staging, failed too", restoreFailed);
        Assert.DoesNotContain("not finished", restoreFailed);
        Assert.Contains("has not finished yet", await ReasonFor(failedBeforeInProgress));
        Assert.Equal(HttpStatusCode.BadRequest,
            (await _admin.PostAsJsonAsync($"/api/rollbacks/last-known-good/{succeeded}", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await _admin.GetAsync($"/api/rollbacks/last-known-good/{Guid.NewGuid()}")).StatusCode);
    }

    [Fact]
    public async Task LastKnownGood_ByNonCreator_IsOfferedButNotPermitted()
    {
        var product = await FreshEnrolledProductAsync();
        await IngestAsync(product, "api", "staging", "1.0", Hours(-2));
        var failed = await IngestAsync(product, "api", "staging", "1.1", Hours(-1), status: "failed");

        var offer = await Body(await _user.GetAsync($"/api/rollbacks/last-known-good/{failed}"));
        Assert.True(offer.GetProperty("available").GetBoolean());
        Assert.False(offer.GetProperty("canCreate").GetBoolean());
        Assert.Contains("not allowed", offer.GetProperty("createBlockedReason").GetString()!);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await _user.PostAsJsonAsync($"/api/rollbacks/last-known-good/{failed}", new { })).StatusCode);
    }

    [Fact]
    public async Task GatedRestore_AfterAFailedDeploy_StaysOpenWhenApproved()
    {
        await EnableRollbacksAsync();
        var product = $"restore-{Guid.NewGuid():N}";
        await IngestAsync(product, "api", "staging", "1.0", Hours(-3));
        var failed = await IngestAsync(product, "api", "staging", "1.1", Hours(-2), status: "failed");
        await UpsertPolicyAsync(product, "staging",
            creatorUsers: new[] { "viewer@localhost" }, requirementGroup: RollbackFactory.ApproverGroup);

        var offer = await Body(await _viewer.GetAsync($"/api/rollbacks/last-known-good/{failed}"));
        Assert.Equal("required", offer.GetProperty("approval").GetString());
        Assert.Contains("Approval", offer.GetProperty("approvers").EnumerateArray().Select(a => a.GetString()));

        var created = await Body(await _viewer.PostAsJsonAsync($"/api/rollbacks/last-known-good/{failed}", new { reason = "INC-7" }));
        Assert.Equal("Pending", created.GetProperty("status").GetString());
        Assert.Equal("INC-7", created.GetProperty("reason").GetString());
        var id = created.GetProperty("id").GetString()!;

        Assert.Equal(HttpStatusCode.OK, (await _viewer.PostAsJsonAsync($"/api/rollbacks/{id}/approve", new { })).StatusCode);

        // Unlike an item whose target was reached some other way, nothing has redeployed 1.0 yet.
        var detail = await Body(await _admin.GetAsync($"/api/rollbacks/{id}"));
        Assert.Equal("Approved", detail.GetProperty("status").GetString());
        Assert.Equal("Pending", ItemFor(detail, "api").GetProperty("status").GetString());
    }

    [Fact]
    public async Task ApprovedRollback_ReopensThePromotionThatApprovedTheFailedVersion()
    {
        var product = $"promo-{Guid.NewGuid():N}";
        await SeedPromotionTopologyAndPolicyAsync(product);
        await EnableRollbacksAsync();
        await EnrollAsync(product, "prod");

        await IngestAsync(product, "api", "prod", "1.0", Hours(-6));
        await IngestAsync(product, "api", "staging", "1.1", Hours(-5));
        Assert.Equal(HttpStatusCode.Created, (await CreatePromotionAsync(product, "api", "staging", "prod", "1.1")).StatusCode);
        var candidateId = await GetPendingCandidateIdAsync(product, "prod");
        Assert.Equal(HttpStatusCode.OK, (await _admin.PostAsJsonAsync($"/api/promotions/{candidateId}/approve", new { })).StatusCode);
        Assert.Equal("Approved", (await PromotionAsync(candidateId!)).GetProperty("candidate").GetProperty("status").GetString());

        // The approved promotion's deploy fails, which leaves it Approved: nothing ever closes it.
        var failed = await IngestAsync(product, "api", "prod", "1.1", Hours(-1), status: "failed");

        var offer = await Body(await _admin.GetAsync($"/api/rollbacks/last-known-good/{failed}"));
        var listed = Assert.Single(offer.GetProperty("promotionsToReopen").EnumerateArray());
        Assert.Equal(candidateId, listed.GetProperty("id").GetString());

        var created = await Body(await _admin.PostAsJsonAsync($"/api/rollbacks/last-known-good/{failed}", new { }));
        Assert.Equal("Approved", created.GetProperty("status").GetString());
        var rollbackId = created.GetProperty("id").GetString()!;

        // Approving the rollback reopened it: back to Pending, the sign-off gone, and the thread says why.
        var promotion = await PromotionAsync(candidateId!);
        Assert.Equal("Pending", promotion.GetProperty("candidate").GetProperty("status").GetString());
        Assert.DoesNotContain(promotion.GetProperty("approvals").EnumerateArray(),
            a => a.GetProperty("decision").GetString() == "Approved");
        var note = promotion.GetProperty("comments").EnumerateArray()
            .Select(c => c.GetProperty("body").GetString()!)
            .Last(b => b.Contains("Rollback"));
        Assert.Contains(rollbackId[..8], note);
        Assert.Contains("1.1 → 1.0", note);
        Assert.Contains("needs a new approval", note);
        Assert.Contains("rollback.promotions.reopened", await AuditActionsAsync(rollbackId));
        Assert.Contains("promotion.approval.cancelled", await AuditActionsAsync(candidateId!));

        // The rollback landing does not touch it again: it waits for a new decision.
        await IngestAsync(product, "api", "prod", "1.0", Hours(1));
        Assert.Equal("Pending", (await PromotionAsync(candidateId!)).GetProperty("candidate").GetProperty("status").GetString());
    }

    [Fact]
    public async Task ApprovedRollback_LeavesAPromotionItsPolicyWouldReapproveAtOnce()
    {
        var product = $"promo-{Guid.NewGuid():N}";
        await SeedPromotionTopologyAndPolicyAsync(product, gated: false);
        await EnableRollbacksAsync();
        await EnrollAsync(product, "prod");

        await IngestAsync(product, "api", "prod", "1.0", Hours(-6));
        await IngestAsync(product, "api", "staging", "1.1", Hours(-5));
        var createResp = await CreatePromotionAsync(product, "api", "staging", "prod", "1.1");
        var candidateId = (await Body(createResp)).GetProperty("id").GetString()!;
        Assert.Equal("Approved", (await PromotionAsync(candidateId)).GetProperty("candidate").GetProperty("status").GetString());
        var failed = await IngestAsync(product, "api", "prod", "1.1", Hours(-1), status: "failed");

        Assert.Equal(HttpStatusCode.Created,
            (await _admin.PostAsJsonAsync($"/api/rollbacks/last-known-good/{failed}", new { })).StatusCode);

        // Reopening it would only let the gate approve it again and re-dispatch 1.1, so it stays, and
        // the thread says what to do instead.
        var promotion = await PromotionAsync(candidateId);
        Assert.Equal("Approved", promotion.GetProperty("candidate").GetProperty("status").GetString());
        Assert.Contains(promotion.GetProperty("comments").EnumerateArray(),
            c => c.GetProperty("body").GetString()!.Contains("would approve it again at once"));
    }

    [Fact]
    public async Task ApprovedRollback_IsSupersededByALaterDeployOfAnotherVersion()
    {
        var product = await FreshEnrolledProductAsync();
        await IngestAsync(product, "api", "staging", "1.0", Hours(-3));
        await IngestAsync(product, "api", "staging", "1.1", Hours(-2));
        var created = await Body(await _admin.PostAsJsonAsync("/api/rollbacks", RollbackBody(product, "1.0")));
        Assert.Equal("Approved", created.GetProperty("status").GetString());
        var id = created.GetProperty("id").GetString()!;

        // Staging moves forward to 1.2 instead of back to 1.0: the rollback is stale.
        var landedId = await IngestAsync(product, "api", "staging", "1.2", Hours(1));

        var detail = await Body(await _admin.GetAsync($"/api/rollbacks/{id}"));
        var item = ItemFor(detail, "api");
        Assert.Equal("Superseded", item.GetProperty("status").GetString());
        Assert.Equal(landedId, item.GetProperty("completedDeployEventId").GetString());
        Assert.NotEqual(JsonValueKind.Null, item.GetProperty("completedAt").ValueKind);
        Assert.Contains("1.2", item.GetProperty("note").GetString()!);
        // Its only item is terminal, so the request is settled and leaves the pipeline's view.
        Assert.Equal("RolledBack", detail.GetProperty("status").GetString());
        Assert.Empty(await OpenItemsSeenByPipelineAsync(product, "staging"));
    }

    [Fact]
    public async Task ApprovedRollback_StaysOpenThroughARedeployOfItsFromVersionAndFailedDeploys()
    {
        var product = await FreshEnrolledProductAsync();
        await IngestAsync(product, "api", "staging", "1.0", Hours(-3));
        await IngestAsync(product, "api", "staging", "1.1", Hours(-2));
        var created = await Body(await _admin.PostAsJsonAsync("/api/rollbacks", RollbackBody(product, "1.0")));
        var id = created.GetProperty("id").GetString()!;

        // 1.1 lands again (a retry, or an overlapping run finishing late): the version the rollback
        // exists to remove, so the rollback still describes the environment.
        await IngestAsync(product, "api", "staging", "1.1", Hours(1));
        // A failed deploy of another version moved nothing.
        await IngestAsync(product, "api", "staging", "1.2", Hours(2), status: "failed");
        // A failed deploy of the target itself did not put it live.
        var failedId = await IngestAsync(product, "api", "staging", "1.0", Hours(3), status: "failed");

        var detail = await Body(await _admin.GetAsync($"/api/rollbacks/{id}"));
        var item = ItemFor(detail, "api");
        Assert.Equal("Pending", item.GetProperty("status").GetString());
        Assert.Equal("Approved", detail.GetProperty("status").GetString());
        // The failed attempt is reported, so an open item that was already tried says so.
        Assert.Equal(failedId, item.GetProperty("lastFailedAttempt").GetProperty("deployEventId").GetString());
        Assert.Equal(new[] { "api@1.0" }, await OpenItemsSeenByPipelineAsync(product, "staging"));

        // Only a succeeded deploy of the target completes it.
        await IngestAsync(product, "api", "staging", "1.0", Hours(4));
        var done = await Body(await _admin.GetAsync($"/api/rollbacks/{id}"));
        Assert.Equal("RolledBack", ItemFor(done, "api").GetProperty("status").GetString());
        Assert.Equal("RolledBack", done.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Override_ClosesItemsAlreadyAtTheirTarget_AndSupersedesOnesTheEnvironmentMovedPast()
    {
        // Unconfigured product: the request lands Pending, so the environment can move before the
        // gate clears.
        await EnableRollbacksAsync();
        var product = $"unconfigured-{Guid.NewGuid():N}";
        foreach (var (svc, from, to) in new[] { ("svc-a", "1.1", "1.0"), ("svc-b", "2.1", "2.0"), ("svc-c", "3.1", "3.0") })
        {
            await IngestAsync(product, svc, "staging", to, Hours(-5));
            await IngestAsync(product, svc, "staging", from, Hours(-4));
        }
        var created = await Body(await _admin.PostAsJsonAsync("/api/rollbacks", new
        {
            product, targetEnv = "staging", mode = "manual",
            items = new[]
            {
                new { service = "svc-a", toVersion = "1.0" },
                new { service = "svc-b", toVersion = "2.0" },
                new { service = "svc-c", toVersion = "3.0" },
            },
        }));
        Assert.Equal("Pending", created.GetProperty("status").GetString());
        var id = created.GetProperty("id").GetString()!;

        // While it waits: svc-a reaches its target some other way, and svc-b moves on to a third version.
        var aLanded = await IngestAsync(product, "svc-a", "staging", "1.0", Hours(0.1));
        var bLanded = await IngestAsync(product, "svc-b", "staging", "2.2", Hours(0.2));

        var resp = await _admin.PostAsJsonAsync($"/api/rollbacks/{id}/override-approval", new { reason = "INC-1" });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var detail = await Body(await _admin.GetAsync($"/api/rollbacks/{id}"));
        var a = ItemFor(detail, "svc-a");
        Assert.Equal("RolledBack", a.GetProperty("status").GetString());
        Assert.Equal(aLanded, a.GetProperty("completedDeployEventId").GetString());
        var b = ItemFor(detail, "svc-b");
        Assert.Equal("Superseded", b.GetProperty("status").GetString());
        Assert.Equal(bLanded, b.GetProperty("completedDeployEventId").GetString());
        Assert.Equal("Pending", ItemFor(detail, "svc-c").GetProperty("status").GetString());
        Assert.Equal("RollingBack", detail.GetProperty("status").GetString());
        Assert.Equal(new[] { "svc-c@3.0" }, await OpenItemsSeenByPipelineAsync(product, "staging"));
    }

    [Fact]
    public async Task Approve_WhenEveryItemIsAlreadyAtItsTarget_SettlesTheRequestAtOnce()
    {
        await EnableRollbacksAsync();
        var product = $"satisfied-{Guid.NewGuid():N}";
        await IngestAsync(product, "api", "staging", "1.0", Hours(-3));
        await IngestAsync(product, "api", "staging", "1.1", Hours(-2));
        await UpsertPolicyAsync(product, "staging",
            creatorUsers: new[] { "viewer@localhost" }, requirementGroup: RollbackFactory.ApproverGroup);

        var created = await Body(await _viewer.PostAsJsonAsync("/api/rollbacks", RollbackBody(product, "1.0")));
        Assert.Equal("Pending", created.GetProperty("status").GetString());
        var id = created.GetProperty("id").GetString()!;
        var landed = await IngestAsync(product, "api", "staging", "1.0", Hours(0.1)); // reached by other means

        var approve = await _viewer.PostAsJsonAsync($"/api/rollbacks/{id}/approve", new { });
        Assert.Equal(HttpStatusCode.OK, approve.StatusCode);

        var detail = await Body(await _admin.GetAsync($"/api/rollbacks/{id}"));
        Assert.Equal("RolledBack", detail.GetProperty("status").GetString());
        var item = ItemFor(detail, "api");
        Assert.Equal("RolledBack", item.GetProperty("status").GetString());
        Assert.Equal(landed, item.GetProperty("completedDeployEventId").GetString());
        Assert.Contains("Already running 1.0", item.GetProperty("note").GetString()!);
    }

    [Fact]
    public async Task Cancel_ApprovedRequest_NeedsAnAdminAndAReason_AndSkipsTheOpenItems()
    {
        var product = await FreshEnrolledProductAsync();
        foreach (var svc in new[] { "svc-a", "svc-b" })
        {
            await IngestAsync(product, svc, "staging", "1.0", Hours(-3));
            await IngestAsync(product, svc, "staging", "1.1", Hours(-2));
        }
        var created = await Body(await _admin.PostAsJsonAsync("/api/rollbacks", new
        {
            product, targetEnv = "staging", mode = "manual",
            items = new[] { new { service = "svc-a", toVersion = "1.0" }, new { service = "svc-b", toVersion = "1.0" } },
        }));
        var id = created.GetProperty("id").GetString()!;
        await IngestAsync(product, "svc-a", "staging", "1.0", Hours(1)); // svc-a lands → RollingBack

        var nonAdmin = await _user.PostAsJsonAsync($"/api/rollbacks/{id}/cancel", new { reason = "stuck" });
        Assert.Equal(HttpStatusCode.Forbidden, nonAdmin.StatusCode);
        var noReason = await _admin.PostAsJsonAsync($"/api/rollbacks/{id}/cancel", new { reason = "  " });
        Assert.Equal(HttpStatusCode.BadRequest, noReason.StatusCode);

        var cancel = await _admin.PostAsJsonAsync($"/api/rollbacks/{id}/cancel", new { reason = "svc-b stays on 1.1" });
        Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);

        var detail = await Body(await _admin.GetAsync($"/api/rollbacks/{id}"));
        Assert.Equal("Cancelled", detail.GetProperty("status").GetString());
        Assert.Equal("svc-b stays on 1.1", detail.GetProperty("cancelReason").GetString());
        Assert.False(string.IsNullOrEmpty(detail.GetProperty("cancelledByName").GetString()));
        Assert.Equal("RolledBack", ItemFor(detail, "svc-a").GetProperty("status").GetString()); // landed, untouched
        var b = ItemFor(detail, "svc-b");
        Assert.Equal("Skipped", b.GetProperty("status").GetString());
        Assert.Contains("cancelled", b.GetProperty("note").GetString()!);
        Assert.Empty(await OpenItemsSeenByPipelineAsync(product, "staging"));
        Assert.Contains("rollback.cancelled", await AuditActionsAsync(id));
    }

    [Fact]
    public async Task SkipItem_IsAdminOnly_ClosesOneItem_AndSettlesTheRequestWithTheLast()
    {
        var product = await FreshEnrolledProductAsync();
        foreach (var svc in new[] { "svc-a", "svc-b" })
        {
            await IngestAsync(product, svc, "staging", "1.0", Hours(-3));
            await IngestAsync(product, svc, "staging", "1.1", Hours(-2));
        }
        var created = await Body(await _admin.PostAsJsonAsync("/api/rollbacks", new
        {
            product, targetEnv = "staging", mode = "manual",
            items = new[] { new { service = "svc-a", toVersion = "1.0" }, new { service = "svc-b", toVersion = "1.0" } },
        }));
        var id = created.GetProperty("id").GetString()!;
        var aId = ItemFor(created, "svc-a").GetProperty("id").GetString();
        var bId = ItemFor(created, "svc-b").GetProperty("id").GetString();

        Assert.Equal(HttpStatusCode.Forbidden,
            (await _user.PostAsJsonAsync($"/api/rollbacks/{id}/items/{aId}/skip", new { reason = "no" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await _admin.PostAsJsonAsync($"/api/rollbacks/{id}/items/{aId}/skip", new { })).StatusCode);

        Assert.Equal(HttpStatusCode.OK,
            (await _admin.PostAsJsonAsync($"/api/rollbacks/{id}/items/{aId}/skip", new { reason = "keep 1.1" })).StatusCode);
        var afterOne = await Body(await _admin.GetAsync($"/api/rollbacks/{id}"));
        Assert.Equal("Skipped", ItemFor(afterOne, "svc-a").GetProperty("status").GetString());
        Assert.Contains("keep 1.1", ItemFor(afterOne, "svc-a").GetProperty("note").GetString()!);
        Assert.Equal("RollingBack", afterOne.GetProperty("status").GetString());
        Assert.Equal(new[] { "svc-b@1.0" }, await OpenItemsSeenByPipelineAsync(product, "staging"));

        // A closed item cannot be skipped again.
        Assert.Equal(HttpStatusCode.BadRequest,
            (await _admin.PostAsJsonAsync($"/api/rollbacks/{id}/items/{aId}/skip", new { reason = "again" })).StatusCode);

        Assert.Equal(HttpStatusCode.OK,
            (await _admin.PostAsJsonAsync($"/api/rollbacks/{id}/items/{bId}/skip", new { reason = "keep 1.1" })).StatusCode);
        var afterBoth = await Body(await _admin.GetAsync($"/api/rollbacks/{id}"));
        Assert.Equal("RolledBack", afterBoth.GetProperty("status").GetString());
        Assert.Contains("rollback.item.skipped", await AuditActionsAsync(id));
    }

    [Fact]
    public async Task IncidentReplay_StaleRollbackIsNotReappliedOnceTheNewerPromotionDeploys()
    {
        // The 2026-09-29 regression on a staging→prod edge. A rollback is approved but never lands;
        // a newer promotion is approved and deployed. The release pipeline rebuilds its manifest from
        // what is still open, so the moment the promotion leaves Approved, a still-open rollback item
        // would revert the version that promotion just shipped.
        var product = $"promo-{Guid.NewGuid():N}";
        await SeedPromotionTopologyAndPolicyAsync(product);
        await EnableRollbacksAsync();
        await EnrollAsync(product, "prod");

        await IngestAsync(product, "api", "prod", "1.0", Hours(-6));
        await IngestAsync(product, "api", "prod", "1.1", Hours(-5));
        var rollback = await Body(await _admin.PostAsJsonAsync("/api/rollbacks",
            RollbackBody(product, "1.0", targetEnv: "prod")));
        Assert.Equal("Approved", rollback.GetProperty("status").GetString());
        var rollbackId = rollback.GetProperty("id").GetString()!;
        Assert.Equal(new[] { "api@1.0" }, await OpenItemsSeenByPipelineAsync(product, "prod"));

        // A newer version is promoted and approved after the rollback.
        await IngestAsync(product, "api", "staging", "1.2", Hours(-1));
        Assert.Equal(HttpStatusCode.Created, (await CreatePromotionAsync(product, "api", "staging", "prod", "1.2")).StatusCode);
        var candidateId = await GetPendingCandidateIdAsync(product, "prod");
        Assert.Equal(HttpStatusCode.OK, (await _admin.PostAsJsonAsync($"/api/promotions/{candidateId}/approve", new { })).StatusCode);

        // The promotion deploys, and with that leaves Approved.
        await IngestAsync(product, "api", "prod", "1.2", Hours(1));
        var promotion = await Body(await _admin.GetAsync($"/api/promotions/{candidateId}"));
        Assert.Equal("Deployed", promotion.GetProperty("candidate").GetProperty("status").GetString());

        // The rollback must not come back: it is closed, and the pipeline's read returns nothing.
        Assert.Empty(await OpenItemsSeenByPipelineAsync(product, "prod"));
        var detail = await Body(await _admin.GetAsync($"/api/rollbacks/{rollbackId}"));
        Assert.Equal("Superseded", ItemFor(detail, "api").GetProperty("status").GetString());
        Assert.Equal("RolledBack", detail.GetProperty("status").GetString());
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

    // Enrollment is now the existence of a rollback policy. An "enrolled" product in these tests gets
    // an ungated (empty-steps) policy, which reproduces what the pre-policy code did for these
    // scenarios: the rollback target is staging, the seeded promotion policy targets prod, so
    // target-only resolution found nothing and every rollback auto-approved.
    private async Task EnrollAsync(string product, string targetEnv = "staging")
        => await UpsertPolicyAsync(product, targetEnv, creatorUsers: null, requirementGroup: null);

    /// <summary>
    /// Creates a rollback policy. <paramref name="creatorUsers"/> null ⇒ no creator set (admins only);
    /// <paramref name="requirementGroup"/> null ⇒ no approval requirements (auto-approve).
    /// </summary>
    private async Task<string> UpsertPolicyAsync(
        string product, string? targetEnv, string[]? creatorUsers, string? requirementGroup,
        int minApprovers = 1)
    {
        object? steps = requirementGroup is null
            ? Array.Empty<object>()
            : new[]
            {
                new
                {
                    name = "Approval",
                    requirements = new[]
                    {
                        new
                        {
                            name = "Approvers",
                            groups = new[] { new { id = requirementGroup, name = requirementGroup } },
                            users = Array.Empty<string>(),
                            minApprovers,
                        },
                    },
                },
            };

        var resp = await _admin.PostAsJsonAsync("/api/rollbacks/admin/policies", new
        {
            product,
            targetEnv,
            creators = new { groups = Array.Empty<object>(), users = creatorUsers ?? Array.Empty<string>() },
            steps,
        });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        return (await Body(resp)).GetProperty("id").GetString()!;
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
        await EnrollAsync(product);
        return product;
    }

    /// <summary>Posts a deploy event and returns its id.</summary>
    private async Task<string> IngestAsync(string product, string service, string env, string version,
        DateTimeOffset deployedAt, bool isRollback = false, string status = "succeeded")
    {
        var resp = await _apiKey.PostAsJsonAsync("/api/deployments/events", new
        {
            product, service, environment = env, version,
            source = "rollback-test", deployedAt, status, isRollback,
        });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        return (await Body(resp)).GetProperty("id").GetString()!;
    }

    private static JsonElement ItemFor(JsonElement request, string service)
        => request.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("service").GetString() == service);

    /// <summary>
    /// The items the release pipeline would still apply: open items of Approved and RollingBack
    /// requests, the same two reads get-approved-rollbacks.ps1 makes.
    /// </summary>
    private async Task<List<string>> OpenItemsSeenByPipelineAsync(string product, string targetEnv)
    {
        var open = new List<string>();
        foreach (var status in new[] { "Approved", "RollingBack" })
        {
            var body = await Body(await _apiKey.GetAsync(
                $"/api/rollbacks?product={product}&targetEnv={targetEnv}&status={status}"));
            foreach (var request in body.GetProperty("requests").EnumerateArray())
            foreach (var item in request.GetProperty("items").EnumerateArray())
            {
                var itemStatus = item.GetProperty("status").GetString();
                if (itemStatus is "Pending" or "RollingBack")
                    open.Add($"{item.GetProperty("service").GetString()}@{item.GetProperty("toVersion").GetString()}");
            }
        }
        return open;
    }

    private async Task<List<string>> AuditActionsAsync(string entityId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var id = Guid.Parse(entityId);
        return await db.AuditLog.Where(a => a.EntityId == id).Select(a => a.Action).ToListAsync();
    }

    private async Task<string?> GetPendingCandidateIdAsync(string product, string targetEnv)
    {
        var body = await Body(await _admin.GetAsync(
            $"/api/promotions/?product={product}&targetEnv={targetEnv}&status=Pending"));
        var arr = body.GetProperty("candidates").EnumerateArray().ToList();
        return arr.Count == 0 ? null : arr[0].GetProperty("id").GetString();
    }

    private async Task<JsonElement> PromotionAsync(string candidateId)
        => await Body(await _admin.GetAsync($"/api/promotions/{candidateId}"));

    // Seeds a staging→prod topology and a product-default promotion policy directly via the
    // DbContext: with an approver group so candidates are born Pending, or (gated: false) with no
    // requirements so they are approved on creation.
    private async Task SeedPromotionTopologyAndPolicyAsync(string product, bool gated = true)
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
            ApprovalSteps = gated
                ? new()
                {
                    new ApprovalStep("Approval", new()
                    {
                        new ApproverRequirement("Release Managers", new() { new GroupRef("release-managers", "release-managers") }, new(), 1),
                    }),
                }
                : new(),
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

        /// <summary>The one group with real membership in these tests: viewer@localhost only.</summary>
        public const string ApproverGroup = "release-managers";

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("Deployments:ApiKeys:0:Name", "rollback-integration-test");
            builder.UseSetting("Deployments:ApiKeys:0:Key", TestApiKey);
            builder.UseSetting("Deployments:ApiKeys:0:Roles:0", "InfraPortal.Admin");
            // Every test here builds its deploy history through ingest on this one key, and the class
            // as a whole posts more than the production limit of 120 a minute; the limit is not what
            // these tests are about.
            builder.UseSetting("Deployments:IngestionRateLimit:AuthenticatedPermitLimit", "10000");

            // The default StubIdentityService answers GetGroupMembers with *every* active local user
            // regardless of the group asked for, so under local auth everyone is in every group. That
            // makes group-based approver requirements untestable — and in particular it cannot show
            // whether an admin cleared a gate by genuine membership or by the implicit admin shortcut,
            // which is exactly the distinction rollbacks now depend on. Swap in a strict directory.
            builder.ConfigureServices(services =>
            {
                RemoveService<IIdentityService>(services);
                services.AddScoped<IIdentityService, StrictIdentityService>();
            });
        }
    }

    /// <summary>
    /// Test directory with explicit membership: <see cref="RollbackFactory.ApproverGroup"/> contains
    /// viewer@localhost and nobody else, and every other group is empty. Notably admin@localhost is in
    /// no group at all, so any gate an admin clears must have been cleared by an override.
    /// </summary>
    private sealed class StrictIdentityService : IIdentityService
    {
        private static readonly Dictionary<string, UserInfo[]> Members = new(StringComparer.OrdinalIgnoreCase)
        {
            [RollbackFactory.ApproverGroup] = [new UserInfo("viewer-1", "Viewer", "viewer@localhost")],
        };

        public Task<IReadOnlyList<UserInfo>> GetGroupMembers(string groupId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<UserInfo>>(
                Members.TryGetValue(groupId, out var m) ? m : Array.Empty<UserInfo>());

        public Task<UserInfo?> GetUser(string userId, CancellationToken ct = default)
            => Task.FromResult<UserInfo?>(null);

        public Task<AccountStatus> GetAccountStatus(string userId, CancellationToken ct = default)
            => Task.FromResult(AccountStatus.Unknown);

        public Task<IReadOnlyList<UserInfo>> SearchUsers(string query, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<UserInfo>>(Array.Empty<UserInfo>());

        public Task<IReadOnlyList<GroupInfo>> SearchGroups(string query, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<GroupInfo>>(
                [new GroupInfo(RollbackFactory.ApproverGroup, RollbackFactory.ApproverGroup)]);

        public Task<IReadOnlyList<UserInfo>> SearchUsersInGroups(
            string query, IReadOnlyList<GroupInfo> groups, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<UserInfo>>(Array.Empty<UserInfo>());
    }
}
