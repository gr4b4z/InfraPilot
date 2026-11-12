using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Platform.Api.Features.Deployments.Models;
using Platform.Api.Features.Migrations.Models;
using Platform.Api.Features.Promotions.Models;
using Platform.Api.Infrastructure.Persistence;

namespace Platform.Integration.Tests;

/// <summary>
/// <c>/api/releases</c> end to end: who may open and change a release, how its checklist moves
/// (including recording a release after the fact with backdated steps), and the progress the portal
/// works out from deploy events, promotions and migration runs on the version line.
/// </summary>
public class ReleaseEndpointsTests : IClassFixture<ReleaseEndpointsTests.ReleasesFactory>, IDisposable
{
    private const string ManagerEmail = "user@localhost";

    private readonly ReleasesFactory _factory;
    private readonly HttpClient _rm;
    private readonly HttpClient _qa;
    private readonly HttpClient _viewer;
    private readonly HttpClient _admin;

    public ReleaseEndpointsTests(ReleasesFactory factory)
    {
        _factory = factory;
        _rm = factory.CreateAuthenticatedClient(ManagerEmail, "user123");
        _qa = factory.CreateAuthenticatedClient("qa@localhost", "qa123");
        _viewer = factory.CreateAuthenticatedClient("viewer@localhost", "viewer123");
        _admin = factory.CreateAdminClient();
    }

    public void Dispose()
    {
        _rm.Dispose();
        _qa.Dispose();
        _viewer.Dispose();
        _admin.Dispose();
    }

    [Fact]
    public async Task ReleaseManagers_AreTheApproversOfTheProductionGate()
    {
        var product = Unique("gate");
        await SeedGate(product, ("QA Review", "QA Manager", "qa@localhost"), ("Release Approval", "Release Manager", ManagerEmail));

        // Everyone reads who they are: the "Release Manager" requirement of the production gate.
        var asViewer = await Json(await _viewer.GetAsync($"/api/releases/managers?product={product}"));
        Assert.Equal("Release Manager", asViewer.GetProperty("requirement").GetString());
        var gate = Assert.Single(asViewer.GetProperty("gates").EnumerateArray());
        Assert.Equal("Release Approval", gate.GetProperty("step").GetString());
        Assert.Equal("prod", gate.GetProperty("targetEnv").GetString());
        Assert.Equal(ManagerEmail, gate.GetProperty("users")[0].GetString());
        Assert.Contains(asViewer.GetProperty("knownRequirements").EnumerateArray(), r => r.GetString() == "QA Manager");
        Assert.False(asViewer.GetProperty("canEdit").GetBoolean());
        Assert.False(asViewer.GetProperty("isReleaseManager").GetBoolean());
        Assert.True((await Json(await _rm.GetAsync($"/api/releases/managers?product={product}"))).GetProperty("isReleaseManager").GetBoolean());

        // Signing another requirement of the gate (QA) doesn't make anyone a release manager.
        Assert.True((await Json(await _rm.GetAsync("/api/releases?limit=1"))).GetProperty("canCreate").GetBoolean());
        Assert.False((await Json(await _qa.GetAsync("/api/releases?limit=1"))).GetProperty("canCreate").GetBoolean());
        Assert.True((await Json(await _admin.GetAsync("/api/releases?limit=1"))).GetProperty("canCreate").GetBoolean());
        Assert.Equal(HttpStatusCode.Forbidden,
            (await _qa.PostAsJsonAsync("/api/releases", new { version = "7.0", products = new[] { product } })).StatusCode);

        // A release manager of one product can't open a release of a product whose gate doesn't name them.
        Assert.Equal(HttpStatusCode.Forbidden,
            (await _rm.PostAsJsonAsync("/api/releases", new { version = "7.1", products = new[] { Unique("nogate") } })).StatusCode);

        // Only admins choose which requirement it is.
        Assert.Equal(HttpStatusCode.Forbidden,
            (await _rm.PutAsJsonAsync("/api/releases/managers", new { requirement = "QA Manager" })).StatusCode);
        try
        {
            await Json(await _admin.PutAsJsonAsync("/api/releases/managers", new { requirement = "QA Manager" }));
            Assert.False((await Json(await _rm.GetAsync($"/api/releases/managers?product={product}"))).GetProperty("isReleaseManager").GetBoolean());
            Assert.Equal(HttpStatusCode.Created,
                (await _qa.PostAsJsonAsync("/api/releases", new { version = "7.2", products = new[] { product } })).StatusCode);
        }
        finally
        {
            await _admin.PutAsJsonAsync("/api/releases/managers", new { requirement = "Release Manager" });
        }
    }

    [Fact]
    public async Task Create_StartsFromTheTemplate_AndOnlyManagersChangeIt()
    {
        var product = Unique("rel");
        await SeedGate(product, ("Release Approval", "Release Manager", ManagerEmail));
        var created = await _rm.PostAsJsonAsync("/api/releases", new
        {
            version = "v5.10", products = new[] { product },
            links = new[] { new { label = "Issue tracker version", url = "https://tracker.example.com/versions/510" } },
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var release = await Json(created);
        var id = release.GetProperty("id").GetGuid();

        Assert.Equal("5.10", release.GetProperty("version").GetString());
        Assert.Equal("Release 5.10", release.GetProperty("title").GetString());
        Assert.Equal("planned", release.GetProperty("status").GetString());
        // A release has no release manager of its own: its products' production gates say who runs it.
        Assert.False(release.TryGetProperty("releaseManager", out _));
        Assert.Equal("staging", release.GetProperty("stagingEnvironment").GetString());
        Assert.Equal("prod", release.GetProperty("productionEnvironment").GetString());
        Assert.True(release.GetProperty("canManage").GetBoolean());
        Assert.Equal("Issue tracker version", release.GetProperty("links")[0].GetProperty("label").GetString());
        var steps = release.GetProperty("steps").EnumerateArray().ToList();
        Assert.Equal(12, steps.Count);
        Assert.Equal("plan", steps[0].GetProperty("key").GetString());
        Assert.Equal("Start the 5.10 release line", steps[1].GetProperty("title").GetString());
        Assert.Equal("production-sanity", steps[^1].GetProperty("key").GetString());
        Assert.Equal(5, steps.Count(s => s.GetProperty("check").ValueKind == JsonValueKind.String));
        // The maintenance page wraps the production deploy and migrations, for a release that needs downtime.
        var keys = steps.Select(s => s.GetProperty("key").GetString()).ToList();
        Assert.Equal(keys.IndexOf("production-deploys") - 1, keys.IndexOf("maintenance-on"));
        Assert.Equal(keys.IndexOf("production-migrations") + 1, keys.IndexOf("maintenance-off"));
        Assert.Equal(["maintenance-on", "maintenance-off"],
            steps.Where(s => s.GetProperty("optional").GetBoolean()).Select(s => s.GetProperty("key").GetString()).ToArray());

        // Anyone signed in reads it; only admins and release managers change it. QA is no exception.
        var stepId = steps[0].GetProperty("id").GetGuid();
        foreach (var reader in new[] { _viewer, _qa })
        {
            var seen = await Json(await reader.GetAsync($"/api/releases/{id}"));
            Assert.False(seen.GetProperty("canManage").GetBoolean());
            Assert.Equal(HttpStatusCode.Forbidden,
                (await reader.PatchAsJsonAsync($"/api/releases/{id}/steps/{stepId}", new { status = "done" })).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden,
                (await reader.PutAsJsonAsync($"/api/releases/{id}", new { version = "5.10", products = new[] { product } })).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden,
                (await reader.PostAsJsonAsync($"/api/releases/{id}/complete", new { })).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden,
                (await reader.PostAsJsonAsync("/api/releases", new { version = "5.11", products = new[] { product } })).StatusCode);
        }
        Assert.Equal(HttpStatusCode.Forbidden, (await _rm.DeleteAsync($"/api/releases/{id}")).StatusCode);
        // Nor can a release manager move a release to products whose gates don't name them.
        Assert.Equal(HttpStatusCode.Forbidden,
            (await _rm.PutAsJsonAsync($"/api/releases/{id}", new { version = "5.10", products = new[] { Unique("elsewhere") } })).StatusCode);

        // One 5.10 per product.
        var duplicate = await _admin.PostAsJsonAsync("/api/releases", new { version = "5.10", products = new[] { "other", product } });
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Equal(id, (await Json(duplicate)).GetProperty("existingId").GetGuid());

        Assert.Equal(HttpStatusCode.BadRequest,
            (await _rm.PostAsJsonAsync("/api/releases", new { version = "5.10.1", products = new[] { product } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await _rm.PostAsJsonAsync("/api/releases", new { version = "5.12", products = Array.Empty<string>() })).StatusCode);

        var list = await Json(await _viewer.GetAsync($"/api/releases?product={product}"));
        Assert.Equal(1, list.GetProperty("total").GetInt32());
        Assert.False(list.GetProperty("canCreate").GetBoolean());
        var summary = list.GetProperty("items")[0];
        // Optional steps nobody has started don't count; once skipped (or started) they do.
        Assert.Equal(0, summary.GetProperty("stepsClosed").GetInt32());
        Assert.Equal(10, summary.GetProperty("stepsTotal").GetInt32());
        Assert.Equal("Plan the scope and release date", summary.GetProperty("currentStep").GetString());
        var maintenanceOn = steps.Single(s => s.GetProperty("key").GetString() == "maintenance-on").GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK,
            (await _rm.PatchAsJsonAsync($"/api/releases/{id}/steps/{maintenanceOn}", new { status = "skipped" })).StatusCode);
        summary = (await Json(await _viewer.GetAsync($"/api/releases?product={product}"))).GetProperty("items")[0];
        Assert.Equal(1, summary.GetProperty("stepsClosed").GetInt32());
        Assert.Equal(11, summary.GetProperty("stepsTotal").GetInt32());

        Assert.Equal(HttpStatusCode.NoContent, (await _admin.DeleteAsync($"/api/releases/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _admin.GetAsync($"/api/releases/{id}")).StatusCode);
    }

    [Fact]
    public async Task Steps_MoveThroughTheirStates_AndCanBeBackdated()
    {
        var product = Unique("steps");
        await SeedGate(product, ("Release Approval", "Release Manager", ManagerEmail));
        var release = await Json(await _rm.PostAsJsonAsync("/api/releases", new { version = "6.0", products = new[] { product } }));
        var id = release.GetProperty("id").GetGuid();
        var steps = release.GetProperty("steps").EnumerateArray().ToList();
        var plan = steps[0].GetProperty("id").GetGuid();
        var cut = steps[1].GetProperty("id").GetGuid();

        // Recorded after the fact: done in one go, at a time in the past.
        var done = await Patch(id, plan, new
        {
            status = "done", completedAt = "2026-10-12T10:00:00Z", note = "Scope agreed",
            links = new object[]
            {
                new { label = "Release plan", url = "https://wiki.example.com/release-6.0" },
                new { url = "https://tracker.example.com/versions/60" },
                new { url = "https://tracker.example.com/versions/60" },
            },
        });
        Assert.Equal(HttpStatusCode.OK, done.StatusCode);
        var afterPlan = await Json(done);
        var planStep = StepById(afterPlan, plan);
        Assert.Equal("done", planStep.GetProperty("status").GetString());
        Assert.Equal(DateTimeOffset.Parse("2026-10-12T10:00:00Z"), planStep.GetProperty("startedAt").GetDateTimeOffset());
        Assert.Equal("user@localhost", planStep.GetProperty("completedBy").GetString());
        Assert.Equal("Scope agreed", planStep.GetProperty("note").GetString());
        // Links keep their order, lose duplicates, and keep a label only when one was given.
        var links = planStep.GetProperty("links").EnumerateArray().ToList();
        Assert.Equal(2, links.Count);
        Assert.Equal("Release plan", links[0].GetProperty("label").GetString());
        Assert.Equal(JsonValueKind.Null, links[1].GetProperty("label").ValueKind);
        // The first step that gets going starts the release, when the step says it did.
        Assert.Equal("in_progress", afterPlan.GetProperty("status").GetString());
        Assert.Equal(DateTimeOffset.Parse("2026-10-12T10:00:00Z"), afterPlan.GetProperty("startedAt").GetDateTimeOffset());

        var started = await Json(await Patch(id, cut, new { status = "in_progress" }));
        Assert.Equal("in_progress", StepById(started, cut).GetProperty("status").GetString());
        Assert.NotEqual(JsonValueKind.Null, StepById(started, cut).GetProperty("startedAt").ValueKind);

        Assert.Equal(HttpStatusCode.BadRequest, (await Patch(id, cut, new { status = "done", completedAt = DateTimeOffset.UtcNow.AddDays(2) })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await Patch(id, cut, new { status = "done", startedAt = "2026-10-20T10:00:00Z", completedAt = "2026-10-19T10:00:00Z" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Patch(id, cut, new { status = "finished" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Patch(id, cut, new { links = new[] { new { url = "not a link" } } })).StatusCode);
        // An empty list clears them; leaving the field out keeps them.
        var kept = await Json(await Patch(id, plan, new { note = "Scope agreed in the meeting" }));
        Assert.Equal(2, StepById(kept, plan).GetProperty("links").GetArrayLength());
        var cleared = await Json(await Patch(id, plan, new { links = Array.Empty<object>() }));
        Assert.Equal(0, StepById(cleared, plan).GetProperty("links").GetArrayLength());

        var reset = await Json(await Patch(id, cut, new { status = "pending" }));
        Assert.Equal(JsonValueKind.Null, StepById(reset, cut).GetProperty("startedAt").ValueKind);

        // Custom steps go where they are put, and only they can be removed.
        var goNoGo = steps.First(s => s.GetProperty("key").GetString() == "go-no-go").GetProperty("id").GetGuid();
        var added = await Json(await _rm.PostAsJsonAsync($"/api/releases/{id}/steps", new { title = "Export account team data", owner = "DevOps", afterStepId = goNoGo }));
        var ordered = added.GetProperty("steps").EnumerateArray().Select(s => s.GetProperty("title").GetString()).ToList();
        Assert.Equal(13, ordered.Count);
        Assert.Equal("Export account team data", ordered[ordered.IndexOf("Go / no-go") + 1]);
        var custom = added.GetProperty("steps").EnumerateArray().First(s => s.GetProperty("custom").GetBoolean()).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.BadRequest, (await _rm.DeleteAsync($"/api/releases/{id}/steps/{plan}")).StatusCode);
        var removed = await Json(await _rm.DeleteAsync($"/api/releases/{id}/steps/{custom}"));
        Assert.Equal(12, removed.GetProperty("steps").GetArrayLength());

        // Closing and reopening the release.
        var completed = await Json(await _rm.PostAsJsonAsync($"/api/releases/{id}/complete", new { completedAt = "2026-11-12T12:00:00Z" }));
        Assert.Equal("completed", completed.GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.BadRequest,
            (await _rm.PostAsJsonAsync($"/api/releases/{id}/cancel", new { reason = "changed our mind" })).StatusCode);
        var reopened = await Json(await _rm.PostAsync($"/api/releases/{id}/reopen", null));
        Assert.Equal("in_progress", reopened.GetProperty("status").GetString());
        var cancelled = await Json(await _rm.PostAsJsonAsync($"/api/releases/{id}/cancel", new { reason = "merged into 6.1" }));
        Assert.Equal("cancelled", cancelled.GetProperty("status").GetString());

        // A cancelled release frees its line for the product.
        Assert.Equal(HttpStatusCode.Created,
            (await _rm.PostAsJsonAsync("/api/releases", new { version = "6.0", products = new[] { product } })).StatusCode);
    }

    [Fact]
    public async Task Progress_ReadsTheLineFromDeploysPromotionsAndMigrations()
    {
        var product = Unique("prog");
        await SeedGate(product, ("Release Approval", "Release Manager", ManagerEmail));
        var t0 = DateTimeOffset.UtcNow.AddDays(-20);

        // On the line and shipped: staged, promoted, deployed, migrated.
        const string sdp = """{"provider":"azure-devops","runId":"8971132","runNumber":"Marketplace 5.10.129 (Diff)","workflowName":"marketplace-sdp-prod","runUrl":"https://dev.azure.example.com/MPT/_build/results?buildId=8971132"}""";
        const string reconcile = """{"provider":"github-actions","runId":"431","runNumber":"75","workflowName":"Reconcile Prod","runUrl":"https://github.com/acme/release/actions/runs/431","jobUrl":"https://github.com/acme/release/actions/runs/431/job/9"}""";
        await SeedDeploy(product, "svc-a", "staging", "5.10.1-gaaaa", "succeeded", t0.AddDays(1));
        var aProd = await SeedDeploy(product, "svc-a", "prod", "5.10.1-gaaaa", "succeeded", t0.AddDays(10), sdp);
        await SeedCandidate(product, "svc-a", "5.10.1-gaaaa", PromotionStatus.Deployed, t0.AddDays(2), t0.AddDays(10).AddMinutes(-9), t0.AddDays(10));
        await SeedMigration(product, "svc-a", "5.10.1-gaaaa", MigrationKinds.Schema, MigrationRunStatus.Succeeded, aProd, t0.AddDays(10), applied: 3);
        await SeedMigration(product, "svc-a", "5.10.1-gaaaa", MigrationKinds.Data, MigrationRunStatus.Succeeded, null, t0.AddDays(10).AddMinutes(5), applied: 1);

        // Approved, but every production attempt failed.
        await SeedDeploy(product, "svc-b", "staging", "5.10.3-gbbbb", "succeeded", t0.AddDays(3));
        await SeedCandidate(product, "svc-b", "5.10.2-gbbbb", PromotionStatus.Superseded, t0.AddDays(2), null, null);
        await SeedCandidate(product, "svc-b", "5.10.3-gbbbb", PromotionStatus.Approved, t0.AddDays(3), t0.AddDays(10).AddMinutes(-9), null);
        await SeedDeploy(product, "svc-b", "prod", "5.10.3-gbbbb", "failed", t0.AddDays(10), sdp);
        await SeedDeploy(product, "svc-b", "prod", "5.10.3-gbbbb", "failed", t0.AddDays(10).AddMinutes(20), reconcile);

        // Still waiting for its approval.
        await SeedDeploy(product, "svc-c", "staging", "5.10.0", "succeeded", t0.AddDays(4));
        await SeedCandidate(product, "svc-c", "5.10.0", PromotionStatus.Pending, t0.AddDays(4), null, null);

        // Its own version scheme: no 5.10 build, but approved during the release.
        await SeedDeploy(product, "svc-x", "staging", "1.2.3", "succeeded", t0.AddDays(5));
        await SeedCandidate(product, "svc-x", "1.2.3", PromotionStatus.Approved, t0.AddDays(5), t0.AddDays(10).AddMinutes(-9), null);

        // Services the release manager will leave out: the previous line, a line that only starts the same, and one not shipping.
        await SeedDeploy(product, "svc-old", "staging", "5.9.4-gcccc", "succeeded", t0.AddDays(1));
        await SeedDeploy(product, "svc-d", "staging", "5.100.1", "succeeded", t0.AddDays(1));
        await SeedDeploy(product, "svc-skip", "staging", "5.10.1", "succeeded", t0.AddDays(1));
        await SeedCandidate(product, "svc-skip", "5.10.1", PromotionStatus.Pending, t0.AddDays(1), null, null);

        var release = await Json(await _rm.PostAsJsonAsync("/api/releases", new { version = "5.10", products = new[] { product }, startedAt = t0 }));
        var id = release.GetProperty("id").GetGuid();

        // Every service of the product is in the release until the release manager leaves it out.
        var first = await Json(await _viewer.GetAsync($"/api/releases/{id}/progress"));
        var everyService = first.GetProperty("services").EnumerateArray().ToDictionary(s => s.GetProperty("service").GetString()!);
        Assert.Equal(["svc-a", "svc-b", "svc-c", "svc-d", "svc-old", "svc-skip", "svc-x"], everyService.Keys.Order().ToArray());
        Assert.All(everyService.Values, s => Assert.False(s.GetProperty("excluded").GetBoolean()));
        Assert.Equal(7, first.GetProperty("checks").EnumerateArray()
            .First(c => c.GetProperty("key").GetString() == "production-promotions").GetProperty("total").GetInt32());
        // A service on its own version scheme is read from what it did during the release.
        Assert.False(everyService["svc-x"].GetProperty("onLine").GetBoolean());
        Assert.True(everyService["svc-a"].GetProperty("onLine").GetBoolean());
        Assert.Equal("1.2.3", everyService["svc-x"].GetProperty("promotion").GetProperty("version").GetString());

        // Left out from the Services list: still listed, no longer counted. Only release managers choose.
        var leftOut = new[] { $"{product}/svc-skip", $"{product}/svc-old", $"{product}/svc-d" };
        Assert.Equal(HttpStatusCode.Forbidden, (await _qa.PutAsJsonAsync($"/api/releases/{id}/services", new { excluded = leftOut })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await _rm.PutAsJsonAsync($"/api/releases/{id}/services", new { excluded = new[] { "another-product/svc-a" } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await _rm.PutAsJsonAsync($"/api/releases/{id}/services", new { excluded = new[] { "svc-a" } })).StatusCode);
        var scoped = await Json(await _rm.PutAsJsonAsync($"/api/releases/{id}/services", new { excluded = leftOut }));
        Assert.Equal(3, scoped.GetProperty("excludedServices").GetArrayLength());

        var progress = await Json(await _viewer.GetAsync($"/api/releases/{id}/progress"));
        var services = progress.GetProperty("services").EnumerateArray().ToDictionary(s => s.GetProperty("service").GetString()!);
        Assert.Equal(7, services.Count);
        Assert.True(services["svc-skip"].GetProperty("excluded").GetBoolean());
        Assert.False(services["svc-x"].GetProperty("excluded").GetBoolean());
        Assert.Equal("5.10.3-gbbbb", services["svc-b"].GetProperty("promotion").GetProperty("version").GetString());
        Assert.Equal(2, services["svc-b"].GetProperty("productionFailures").GetInt32());
        Assert.Equal("failed", services["svc-b"].GetProperty("production").GetProperty("status").GetString());
        Assert.Equal(2, services["svc-a"].GetProperty("migrations").GetArrayLength());

        var checks = progress.GetProperty("checks").EnumerateArray().ToDictionary(c => c.GetProperty("key").GetString()!);

        var staging = checks["staging-builds"];
        Assert.Equal("complete", staging.GetProperty("state").GetString());
        Assert.Equal(4, staging.GetProperty("done").GetInt32());

        var promotions = checks["production-promotions"];
        Assert.Equal("in_progress", promotions.GetProperty("state").GetString());
        Assert.Equal(3, promotions.GetProperty("done").GetInt32());
        Assert.Equal(4, promotions.GetProperty("total").GetInt32());
        Assert.Contains(promotions.GetProperty("issues").EnumerateArray(),
            i => i.GetProperty("service").GetString() == "svc-c" && i.GetProperty("problem").GetString()!.Contains("waiting for approval"));

        var deploys = checks["production-deploys"];
        Assert.Equal("attention", deploys.GetProperty("state").GetString());
        Assert.Equal(1, deploys.GetProperty("done").GetInt32());
        Assert.Contains("2 failed attempts", deploys.GetProperty("summary").GetString());
        // The pipeline runs behind the deploys, grouped by run and named the way people quote them.
        var runs = deploys.GetProperty("runs").EnumerateArray().ToList();
        Assert.Equal(2, runs.Count);
        Assert.Equal("marketplace-sdp-prod 8971132", runs[0].GetProperty("label").GetString());
        Assert.Equal(2, runs[0].GetProperty("services").GetInt32());
        Assert.Equal(1, runs[0].GetProperty("succeeded").GetInt32());
        Assert.Equal(1, runs[0].GetProperty("failed").GetInt32());
        Assert.Equal("Reconcile Prod #75", runs[1].GetProperty("label").GetString());
        // A service row links its own job when the producer sent one.
        Assert.Equal("https://github.com/acme/release/actions/runs/431/job/9",
            services["svc-b"].GetProperty("production").GetProperty("run").GetProperty("url").GetString());
        Assert.Contains(deploys.GetProperty("issues").EnumerateArray(),
            i => i.GetProperty("service").GetString() == "svc-b" && i.GetProperty("problem").GetString()!.Contains("failed"));

        var migrations = checks["production-migrations"];
        // svc-a's migrations finished, but three services are not in production yet.
        Assert.Equal("in_progress", migrations.GetProperty("state").GetString());
        Assert.Equal(2, migrations.GetProperty("done").GetInt32());
        Assert.Equal(2, migrations.GetProperty("total").GetInt32());
        Assert.Contains("4 migrations applied", migrations.GetProperty("summary").GetString());

        // Once the approval round is closed, a promotion raised after it is the service's next change.
        var approvalStep = release.GetProperty("steps").EnumerateArray()
            .First(s => s.GetProperty("key").GetString() == "production-promotions").GetProperty("id").GetGuid();
        await Json(await Patch(id, approvalStep, new { status = "done", completedAt = t0.AddDays(10) }));
        await SeedCandidate(product, "svc-x", "1.2.4", PromotionStatus.Pending, t0.AddDays(10).AddHours(2), null, null);
        var afterRound = await Json(await _viewer.GetAsync($"/api/releases/{id}/progress"));
        var x = afterRound.GetProperty("services").EnumerateArray().First(s => s.GetProperty("service").GetString() == "svc-x");
        Assert.Equal("1.2.3", x.GetProperty("promotion").GetProperty("version").GetString());

        // Completed releases keep describing the day they shipped: later records are outside the window.
        await Json(await Patch(id, approvalStep, new { status = "in_progress" }));
        await _rm.PostAsJsonAsync($"/api/releases/{id}/complete", new { completedAt = t0.AddDays(11) });
        await SeedCandidate(product, "svc-a", "5.10.2-gdddd", PromotionStatus.Pending, t0.AddDays(12), null, null);
        var later = await Json(await _viewer.GetAsync($"/api/releases/{id}/progress"));
        var laterA = later.GetProperty("services").EnumerateArray().First(s => s.GetProperty("service").GetString() == "svc-a");
        Assert.Equal("5.10.1-gaaaa", laterA.GetProperty("promotion").GetProperty("version").GetString());
    }

    [Fact]
    public async Task Migrations_HeldBackByAFailedDeploy_WarnUntilTheNextRunRunsThem()
    {
        var product = Unique("held");
        await SeedGate(product, ("Release Approval", "Release Manager", ManagerEmail));
        var t0 = DateTimeOffset.UtcNow.AddDays(-2);
        const string failedRun = """{"provider":"azure-devops","runId":"8971132","workflowName":"marketplace-sdp-prod","runUrl":"https://dev.azure.example.com/MPT/_build/results?buildId=8971132&view=logs"}""";
        const string hotfixRun = """{"provider":"azure-devops","runId":"8971293","workflowName":"marketplace-sdp-prod","runUrl":"https://dev.azure.example.com/MPT/_build/results?buildId=8971293&view=logs"}""";
        const string otherRun = """{"provider":"azure-devops","runId":"8971400","workflowName":"marketplace-sdp-prod","runUrl":"https://dev.azure.example.com/MPT/_build/results?buildId=8971400&view=logs"}""";

        // swo-platform failed in the release's run; the others deployed and queued their data
        // migrations, which the run then skipped.
        await SeedDeploy(product, "swo-platform", "prod", "5.10.23", "failed", t0.AddHours(1), failedRun);
        await SeedDeploy(product, "mpt-helpdesk", "prod", "5.10.133", "succeeded", t0.AddHours(1), failedRun);
        await SeedDeploy(product, "mpt-notifications", "prod", "5.10.8", "succeeded", t0.AddHours(1), failedRun);
        await SeedMigration(product, "mpt-helpdesk", "5.10.133", MigrationKinds.Data, MigrationRunStatus.Pending, null, t0.AddHours(1), 0, failedRun);
        await SeedMigration(product, "mpt-notifications", "5.10.8", MigrationKinds.Data, MigrationRunStatus.Pending, null, t0.AddHours(1), 0, failedRun);
        // Pending in a run where nothing failed: that run simply hasn't got to it.
        await SeedDeploy(product, "mpt-procurement", "prod", "5.10.1", "succeeded", t0.AddHours(1), otherRun);
        await SeedMigration(product, "mpt-procurement", "5.10.1", MigrationKinds.Data, MigrationRunStatus.Pending, null, t0.AddHours(1), 0, otherRun);

        var release = await Json(await _rm.PostAsJsonAsync("/api/releases", new { version = "5.10", products = new[] { product }, startedAt = t0 }));
        var id = release.GetProperty("id").GetGuid();

        var check = (await Json(await _viewer.GetAsync($"/api/releases/{id}/progress"))).GetProperty("checks").EnumerateArray()
            .First(c => c.GetProperty("key").GetString() == "production-migrations");
        Assert.Equal("in_progress", check.GetProperty("state").GetString());
        var warning = Assert.Single(check.GetProperty("warnings").EnumerateArray()).GetString();
        Assert.Equal(
            "swo-platform failed to deploy in marketplace-sdp-prod 8971132, so that run skipped the data migrations of "
            + "mpt-helpdesk and mpt-notifications. They run with the next deploy run to prod.",
            warning);
        var problems = check.GetProperty("issues").EnumerateArray()
            .ToDictionary(i => i.GetProperty("service").GetString()!, i => i.GetProperty("problem").GetString()!);
        Assert.Equal("Data migration of 5.10.133 waits for the next run: swo-platform failed to deploy in marketplace-sdp-prod 8971132",
            problems["mpt-helpdesk"]);
        Assert.Equal("Data migration of 5.10.1 is pending", problems["mpt-procurement"]);

        // The hotfix run deploys swo-platform and runs the waiting data migrations with it.
        await SeedDeploy(product, "swo-platform", "prod", "5.10.27", "succeeded", t0.AddHours(2), hotfixRun);
        foreach (var (service, version) in new[] { ("mpt-helpdesk", "5.10.133"), ("mpt-notifications", "5.10.8"), ("mpt-procurement", "5.10.1") })
            await MarkDataMigrationRun(product, service, version, MigrationRunStatus.Succeeded, hotfixRun, t0.AddHours(2).AddMinutes(10));

        var after = (await Json(await _viewer.GetAsync($"/api/releases/{id}/progress"))).GetProperty("checks").EnumerateArray()
            .First(c => c.GetProperty("key").GetString() == "production-migrations");
        Assert.Equal("complete", after.GetProperty("state").GetString());
        Assert.Empty(after.GetProperty("warnings").EnumerateArray());
        Assert.Empty(after.GetProperty("issues").EnumerateArray());
    }

    [Fact]
    public async Task StepTables_CarryBuildsTheQaGateAndWorkItems()
    {
        var product = Unique("qa");
        await SeedGate(product, ("Release Approval", "Release Manager", ManagerEmail));
        var t0 = DateTimeOffset.UtcNow.AddDays(-5);

        // Built, staged, QA signed (one work item open), waiting for release approval.
        await SeedBuild(product, "svc-q", "5.12.4-gqqqq", t0.AddDays(1));
        await SeedDeploy(product, "svc-q", "staging", "5.12.4-gqqqq", "succeeded", t0.AddDays(1).AddHours(1));
        var q = await SeedCandidate(product, "svc-q", "5.12.4-gqqqq", PromotionStatus.Pending, t0.AddDays(1).AddHours(2), null, null, withGates: true);
        await SeedApproval(q, "QA Review", t0.AddDays(2));
        await SeedWorkItems(product, "svc-q", q, ("MPT-1", WorkItemDecision.Approved), ("MPT-2", null));

        // Staged, QA not signed yet, both work items approved.
        await SeedDeploy(product, "svc-w", "staging", "5.12.1", "succeeded", t0.AddDays(1));
        var w = await SeedCandidate(product, "svc-w", "5.12.1", PromotionStatus.Pending, t0.AddDays(1).AddHours(1), null, null, withGates: true);
        await SeedWorkItems(product, "svc-w", w, ("MPT-3", WorkItemDecision.Approved), ("MPT-4", WorkItemDecision.Approved));

        // Approved outright: past every gate, QA included.
        await SeedDeploy(product, "svc-a", "staging", "5.12.2", "succeeded", t0.AddDays(1));
        await SeedCandidate(product, "svc-a", "5.12.2", PromotionStatus.Approved, t0.AddDays(1).AddHours(1), t0.AddDays(3), null, withGates: true);

        var release = await Json(await _rm.PostAsJsonAsync("/api/releases", new { version = "5.12", products = new[] { product }, startedAt = t0 }));
        var steps = release.GetProperty("steps").EnumerateArray().ToDictionary(x => x.GetProperty("key").GetString()!);
        Assert.Equal("Build and Staging", steps["staging-builds"].GetProperty("title").GetString());
        Assert.Equal("staging-qa", steps["staging-testing"].GetProperty("check").GetString());

        var progress = await Json(await _viewer.GetAsync($"/api/releases/{release.GetProperty("id").GetGuid()}/progress"));
        Assert.Equal("QA Review", progress.GetProperty("qaStep").GetString());
        var services = progress.GetProperty("services").EnumerateArray().ToDictionary(x => x.GetProperty("service").GetString()!);

        Assert.Equal("5.12.4-gqqqq", services["svc-q"].GetProperty("build").GetProperty("version").GetString());
        Assert.Equal(JsonValueKind.Null, services["svc-w"].GetProperty("build").ValueKind);

        var pq = services["svc-q"].GetProperty("promotion");
        Assert.Equal("approved", pq.GetProperty("qaReview").GetString());
        Assert.Equal(["Release Approval"], pq.GetProperty("outstandingSteps").EnumerateArray().Select(x => x.GetString()).ToArray());
        Assert.Equal(2, pq.GetProperty("workItems").GetProperty("total").GetInt32());
        Assert.Equal(1, pq.GetProperty("workItems").GetProperty("approved").GetInt32());
        var pw = services["svc-w"].GetProperty("promotion");
        Assert.Equal("pending", pw.GetProperty("qaReview").GetString());
        Assert.Equal(2, pw.GetProperty("workItems").GetProperty("approved").GetInt32());
        Assert.Equal("approved", services["svc-a"].GetProperty("promotion").GetProperty("qaReview").GetString());

        var qa = progress.GetProperty("checks").EnumerateArray().First(c => c.GetProperty("key").GetString() == "staging-qa");
        Assert.Equal("in_progress", qa.GetProperty("state").GetString());
        Assert.Equal(2, qa.GetProperty("done").GetInt32());
        Assert.Equal(3, qa.GetProperty("total").GetInt32());
        var issue = Assert.Single(qa.GetProperty("issues").EnumerateArray());
        Assert.Equal("svc-w", issue.GetProperty("service").GetString());
        Assert.Contains("2 of 2 work items", issue.GetProperty("problem").GetString());

        // Only the approved one is through the approval round.
        var approvals = progress.GetProperty("checks").EnumerateArray().First(c => c.GetProperty("key").GetString() == "production-promotions");
        Assert.Equal(1, approvals.GetProperty("done").GetInt32());
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static string Unique(string prefix) => $"{prefix}-{Guid.NewGuid():N}"[..20];

    private Task<HttpResponseMessage> Patch(Guid id, Guid stepId, object body) =>
        _rm.PatchAsJsonAsync($"/api/releases/{id}/steps/{stepId}", body);

    private static JsonElement StepById(JsonElement release, Guid stepId) =>
        release.GetProperty("steps").EnumerateArray().First(s => s.GetProperty("id").GetGuid() == stepId);

    private static async Task<JsonElement> Json(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.Conflict,
            $"{(int)response.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private async Task<Guid> SeedDeploy(
        string product, string service, string env, string version, string status, DateTimeOffset at, string? runJson = null)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var e = new DeployEvent
        {
            Id = Guid.NewGuid(), Product = product, Service = service, Environment = env, Version = version,
            Status = status, Source = "helm-deploy", DeployedAt = at, CreatedAt = at, RunJson = runJson,
        };
        db.DeployEvents.Add(e);
        await db.SaveChangesAsync();
        return e.Id;
    }

    /// <summary>
    /// A staging → prod promotion policy for the product with the given approval requirements, each
    /// naming one approver by email — group membership can't be tested here, the test identity stub
    /// puts everybody in every group.
    /// </summary>
    private async Task SeedGate(string product, params (string Step, string Requirement, string User)[] requirements)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var policy = new PromotionPolicy { Id = Guid.NewGuid(), Product = product, SourceEnv = "staging", TargetEnv = "prod" };
        policy.ApprovalSteps = requirements
            .GroupBy(r => r.Step)
            .Select(g => new ApprovalStep(g.Key, g.Select(r => new ApproverRequirement(r.Requirement, [], [r.User])).ToList()))
            .ToList();
        db.PromotionPolicies.Add(policy);
        await db.SaveChangesAsync();
    }

    private async Task<Guid> SeedCandidate(
        string product, string service, string version, PromotionStatus status,
        DateTimeOffset createdAt, DateTimeOffset? approvedAt, DateTimeOffset? deployedAt, bool withGates = false)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var id = Guid.NewGuid();
        // The gate a real promotion carries: QA Review, then Release Approval.
        var snapshot = withGates
            ? JsonSerializer.Serialize(new ResolvedPolicySnapshot(null, null)
            {
                ApprovalSteps =
                [
                    new ApprovalStep("QA Review", [new ApproverRequirement("QA Manager", [], ["qa@localhost"])]),
                    new ApprovalStep("Release Approval", [new ApproverRequirement("Release Manager", [], [ManagerEmail])]),
                ],
            }, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            : null;
        db.PromotionCandidates.Add(new PromotionCandidate
        {
            Id = id, Product = product, Service = service, SourceEnv = "staging", TargetEnv = "prod",
            Version = version, Status = status, CreatedAt = createdAt, ApprovedAt = approvedAt, DeployedAt = deployedAt,
            ParticipantsJson = "[]", ResolvedPolicyJson = snapshot,
        });
        await db.SaveChangesAsync();
        return id;
    }

    private async Task SeedApproval(Guid candidateId, string step, DateTimeOffset at)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        db.PromotionApprovals.Add(new PromotionApproval
        {
            Id = Guid.NewGuid(), CandidateId = candidateId, ApproverEmail = "qa@localhost", ApproverName = "QA Engineer",
            Decision = PromotionDecision.Approved, StepName = step, RequirementName = "QA Manager", CreatedAt = at,
        });
        await db.SaveChangesAsync();
    }

    private async Task SeedWorkItems(string product, string service, Guid candidateId, params (string Key, WorkItemDecision? Decision)[] items)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        foreach (var (key, decision) in items)
        {
            db.PromotionWorkItems.Add(new PromotionWorkItem
            {
                Id = Guid.NewGuid(), CandidateId = candidateId, WorkItemKey = key, Product = product, Service = service, TargetEnv = "prod",
            });
            if (decision is { } d)
            {
                db.WorkItemApprovals.Add(new WorkItemApproval
                {
                    Id = Guid.NewGuid(), WorkItemKey = key, Product = product, Service = service, TargetEnv = "prod",
                    ApproverEmail = "qa@localhost", ApproverName = "QA Engineer", Decision = d,
                });
            }
        }
        await db.SaveChangesAsync();
    }

    private async Task SeedBuild(string product, string service, string version, DateTimeOffset at)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        db.Builds.Add(new Platform.Api.Features.Builds.Models.Build
        {
            Id = Guid.NewGuid(), Product = product, Service = service, Version = version, Branch = "release/5",
            BuildId = "1234", BuildUrl = "https://ci.example.com/builds/1234", CreatedAt = at,
        });
        await db.SaveChangesAsync();
    }

    private async Task SeedMigration(
        string product, string service, string version, string kind, string status, Guid? deployEventId,
        DateTimeOffset queuedAt, int applied, string? runJson = null)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        db.MigrationRuns.Add(new MigrationRun
        {
            Id = Guid.NewGuid(), Kind = kind, Product = product, Service = service, Environment = "prod", Version = version,
            RunKey = deployEventId is null ? "" : $"deploy:{deployEventId}", Status = status, Source = "test",
            DeployEventId = deployEventId, QueuedAt = queuedAt,
            CompletedAt = status == MigrationRunStatus.Pending ? null : queuedAt.AddMinutes(2),
            AppliedCount = applied, TotalCount = applied, RunJson = runJson,
        });
        await db.SaveChangesAsync();
    }

    /// <summary>A later run ran the data migration: what the pipeline's reports leave on the row.</summary>
    private async Task MarkDataMigrationRun(string product, string service, string version, string status, string runJson, DateTimeOffset at)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var run = await db.MigrationRuns.SingleAsync(m =>
            m.Kind == MigrationKinds.Data && m.Product == product && m.Service == service && m.Version == version);
        run.Status = status;
        run.RunJson = runJson;
        run.StartedAt = at;
        run.CompletedAt = at.AddMinutes(5);
        run.Attempts++;
        await db.SaveChangesAsync();
    }

    public class ReleasesFactory : TestFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
        }
    }
}
