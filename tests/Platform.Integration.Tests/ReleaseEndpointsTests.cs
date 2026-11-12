using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
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
    private readonly ReleasesFactory _factory;
    private readonly HttpClient _qa;
    private readonly HttpClient _viewer;
    private readonly HttpClient _admin;

    public ReleaseEndpointsTests(ReleasesFactory factory)
    {
        _factory = factory;
        _qa = factory.CreateAuthenticatedClient("qa@localhost", "qa123");
        _viewer = factory.CreateAuthenticatedClient("viewer@localhost", "viewer123");
        _admin = factory.CreateAdminClient();
    }

    public void Dispose()
    {
        _qa.Dispose();
        _viewer.Dispose();
        _admin.Dispose();
    }

    [Fact]
    public async Task Create_StartsFromTheTemplate_AndOnlyManagersChangeIt()
    {
        var product = Unique("rel");
        var created = await _qa.PostAsJsonAsync("/api/releases", new
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
        Assert.Equal("qa@localhost", release.GetProperty("releaseManager").GetString());
        Assert.Equal("staging", release.GetProperty("stagingEnvironment").GetString());
        Assert.Equal("prod", release.GetProperty("productionEnvironment").GetString());
        Assert.True(release.GetProperty("canManage").GetBoolean());
        Assert.Equal("Issue tracker version", release.GetProperty("links")[0].GetProperty("label").GetString());
        var steps = release.GetProperty("steps").EnumerateArray().ToList();
        Assert.Equal(10, steps.Count);
        Assert.Equal("plan", steps[0].GetProperty("key").GetString());
        Assert.Equal("Start the 5.10 release line", steps[1].GetProperty("title").GetString());
        Assert.Equal("production-sanity", steps[^1].GetProperty("key").GetString());
        Assert.Equal(4, steps.Count(s => s.GetProperty("check").ValueKind == JsonValueKind.String));

        // Anyone signed in reads it; only admins, QA and its release manager change it.
        var asViewer = await Json(await _viewer.GetAsync($"/api/releases/{id}"));
        Assert.False(asViewer.GetProperty("canManage").GetBoolean());
        var stepId = steps[0].GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.Forbidden,
            (await _viewer.PatchAsJsonAsync($"/api/releases/{id}/steps/{stepId}", new { status = "done" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await _viewer.PostAsJsonAsync("/api/releases", new { version = "5.11", products = new[] { product } })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _qa.DeleteAsync($"/api/releases/{id}")).StatusCode);

        // One 5.10 per product.
        var duplicate = await _admin.PostAsJsonAsync("/api/releases", new { version = "5.10", products = new[] { "other", product } });
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Equal(id, (await Json(duplicate)).GetProperty("existingId").GetGuid());

        Assert.Equal(HttpStatusCode.BadRequest,
            (await _qa.PostAsJsonAsync("/api/releases", new { version = "5.10.1", products = new[] { product } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await _qa.PostAsJsonAsync("/api/releases", new { version = "5.12", products = Array.Empty<string>() })).StatusCode);

        var list = await Json(await _viewer.GetAsync($"/api/releases?product={product}"));
        Assert.Equal(1, list.GetProperty("total").GetInt32());
        var summary = list.GetProperty("items")[0];
        Assert.Equal(0, summary.GetProperty("stepsClosed").GetInt32());
        Assert.Equal(10, summary.GetProperty("stepsTotal").GetInt32());
        Assert.Equal("Plan the scope and release date", summary.GetProperty("currentStep").GetString());

        Assert.Equal(HttpStatusCode.NoContent, (await _admin.DeleteAsync($"/api/releases/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _admin.GetAsync($"/api/releases/{id}")).StatusCode);
    }

    [Fact]
    public async Task Steps_MoveThroughTheirStates_AndCanBeBackdated()
    {
        var product = Unique("steps");
        var release = await Json(await _qa.PostAsJsonAsync("/api/releases", new { version = "6.0", products = new[] { product } }));
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
        Assert.Equal("qa@localhost", planStep.GetProperty("completedBy").GetString());
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
        var added = await Json(await _qa.PostAsJsonAsync($"/api/releases/{id}/steps", new { title = "Export account team data", owner = "DevOps", afterStepId = goNoGo }));
        var ordered = added.GetProperty("steps").EnumerateArray().Select(s => s.GetProperty("title").GetString()).ToList();
        Assert.Equal(11, ordered.Count);
        Assert.Equal("Export account team data", ordered[ordered.IndexOf("Go / no-go") + 1]);
        var custom = added.GetProperty("steps").EnumerateArray().First(s => s.GetProperty("custom").GetBoolean()).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.BadRequest, (await _qa.DeleteAsync($"/api/releases/{id}/steps/{plan}")).StatusCode);
        var removed = await Json(await _qa.DeleteAsync($"/api/releases/{id}/steps/{custom}"));
        Assert.Equal(10, removed.GetProperty("steps").GetArrayLength());

        // Closing and reopening the release.
        var completed = await Json(await _qa.PostAsJsonAsync($"/api/releases/{id}/complete", new { completedAt = "2026-11-12T12:00:00Z" }));
        Assert.Equal("completed", completed.GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.BadRequest,
            (await _qa.PostAsJsonAsync($"/api/releases/{id}/cancel", new { reason = "changed our mind" })).StatusCode);
        var reopened = await Json(await _qa.PostAsync($"/api/releases/{id}/reopen", null));
        Assert.Equal("in_progress", reopened.GetProperty("status").GetString());
        var cancelled = await Json(await _qa.PostAsJsonAsync($"/api/releases/{id}/cancel", new { reason = "merged into 6.1" }));
        Assert.Equal("cancelled", cancelled.GetProperty("status").GetString());

        // A cancelled release frees its line for the product.
        Assert.Equal(HttpStatusCode.Created,
            (await _qa.PostAsJsonAsync("/api/releases", new { version = "6.0", products = new[] { product } })).StatusCode);
    }

    [Fact]
    public async Task Progress_ReadsTheLineFromDeploysPromotionsAndMigrations()
    {
        var product = Unique("prog");
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

        // Its own version scheme, shipping with the train because it is named.
        await SeedDeploy(product, "svc-x", "staging", "1.2.3", "succeeded", t0.AddDays(5));
        await SeedCandidate(product, "svc-x", "1.2.3", PromotionStatus.Approved, t0.AddDays(5), t0.AddDays(10).AddMinutes(-9), null);

        // Not in the release: the previous line, a line that only starts the same, and an excluded service.
        await SeedDeploy(product, "svc-old", "staging", "5.9.4-gcccc", "succeeded", t0.AddDays(1));
        await SeedDeploy(product, "svc-d", "staging", "5.100.1", "succeeded", t0.AddDays(1));
        await SeedDeploy(product, "svc-skip", "staging", "5.10.1", "succeeded", t0.AddDays(1));
        await SeedCandidate(product, "svc-skip", "5.10.1", PromotionStatus.Pending, t0.AddDays(1), null, null);

        var release = await Json(await _qa.PostAsJsonAsync("/api/releases", new
        {
            version = "5.10",
            products = new[] { product },
            startedAt = t0,
            includedServices = new[] { "svc-x" },
            excludedServices = new[] { $"{product}/svc-skip" },
        }));
        var id = release.GetProperty("id").GetGuid();

        var progress = await Json(await _viewer.GetAsync($"/api/releases/{id}/progress"));
        var services = progress.GetProperty("services").EnumerateArray().ToDictionary(s => s.GetProperty("service").GetString()!);
        Assert.Equal(["svc-a", "svc-b", "svc-c", "svc-x"], services.Keys.Order().ToArray());
        Assert.True(services["svc-x"].GetProperty("included").GetBoolean());
        Assert.False(services["svc-a"].GetProperty("included").GetBoolean());
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
        await _qa.PostAsJsonAsync($"/api/releases/{id}/complete", new { completedAt = t0.AddDays(11) });
        await SeedCandidate(product, "svc-a", "5.10.2-gdddd", PromotionStatus.Pending, t0.AddDays(12), null, null);
        var later = await Json(await _viewer.GetAsync($"/api/releases/{id}/progress"));
        var laterA = later.GetProperty("services").EnumerateArray().First(s => s.GetProperty("service").GetString() == "svc-a");
        Assert.Equal("5.10.1-gaaaa", laterA.GetProperty("promotion").GetProperty("version").GetString());
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static string Unique(string prefix) => $"{prefix}-{Guid.NewGuid():N}"[..20];

    private Task<HttpResponseMessage> Patch(Guid id, Guid stepId, object body) =>
        _qa.PatchAsJsonAsync($"/api/releases/{id}/steps/{stepId}", body);

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

    private async Task SeedCandidate(
        string product, string service, string version, PromotionStatus status,
        DateTimeOffset createdAt, DateTimeOffset? approvedAt, DateTimeOffset? deployedAt)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        db.PromotionCandidates.Add(new PromotionCandidate
        {
            Id = Guid.NewGuid(), Product = product, Service = service, SourceEnv = "staging", TargetEnv = "prod",
            Version = version, Status = status, CreatedAt = createdAt, ApprovedAt = approvedAt, DeployedAt = deployedAt,
            ParticipantsJson = "[]",
        });
        await db.SaveChangesAsync();
    }

    private async Task SeedMigration(
        string product, string service, string version, string kind, string status, Guid? deployEventId,
        DateTimeOffset queuedAt, int applied)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        db.MigrationRuns.Add(new MigrationRun
        {
            Id = Guid.NewGuid(), Kind = kind, Product = product, Service = service, Environment = "prod", Version = version,
            RunKey = deployEventId is null ? "" : $"deploy:{deployEventId}", Status = status, Source = "test",
            DeployEventId = deployEventId, QueuedAt = queuedAt, CompletedAt = queuedAt.AddMinutes(2),
            AppliedCount = applied, TotalCount = applied,
        });
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
