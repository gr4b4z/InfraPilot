using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Platform.Api.Features.Deployments.Models;
using Platform.Api.Features.Promotions.Models;
using Platform.Api.Infrastructure.Persistence;

namespace Platform.Integration.Tests;

/// <summary>
/// Integration tests for <c>/api/analytics</c>. Deploy events go in through the real ingest
/// endpoint (API key) so the work-item projection is exercised end-to-end; promotion candidates
/// are seeded directly via EF — analytics only reads their columns, the promotion pipeline
/// around them is covered elsewhere.
/// </summary>
public class AnalyticsEndpointsTests : IClassFixture<AnalyticsEndpointsTests.AnalyticsFactory>, IDisposable
{
    private const string TestApiKey = "test-analytics-key-12345";

    private readonly AnalyticsFactory _factory;
    private readonly HttpClient _apiKeyClient;
    private readonly HttpClient _adminClient;

    public AnalyticsEndpointsTests(AnalyticsFactory factory)
    {
        _factory = factory;
        _apiKeyClient = factory.CreateClient();
        _apiKeyClient.DefaultRequestHeaders.Add("X-Api-Key", TestApiKey);
        _adminClient = factory.CreateAdminClient();
    }

    public void Dispose()
    {
        _apiKeyClient.Dispose();
        _adminClient.Dispose();
    }

    // ── Frequency ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Frequency_CountsSucceededAndExcludesRollbacksAndRedeploys()
    {
        var product = Unique("freq");
        await IngestAsync(product, "api", "dev", "v1", "2026-08-01T10:00:00Z");
        await IngestAsync(product, "api", "dev", "v2", "2026-08-02T10:00:00Z");
        await IngestAsync(product, "api", "dev", "v3", "2026-08-03T10:00:00Z", status: "failed");
        // Redeploy: same version as previous — excluded by default.
        await IngestAsync(product, "api", "dev", "v2", "2026-08-04T10:00:00Z", previousVersion: "v2");
        // Rollback — reported apart, not counted.
        await IngestAsync(product, "api", "dev", "v1", "2026-08-05T10:00:00Z", isRollback: true);

        var body = await GetJson(
            $"/api/analytics/deployments/frequency?product={product}&from=2026-08-01T00:00:00Z&to=2026-08-08T00:00:00Z");

        var series = Assert.Single(body.GetProperty("series").EnumerateArray());
        var summary = series.GetProperty("summary");
        Assert.Equal(2, summary.GetProperty("total").GetInt32());
        Assert.Equal(2, SumBucket(series, "count"));
        Assert.Equal(1, SumBucket(series, "failed"));
        Assert.Equal(1, SumBucket(series, "rollbacks"));
        // CFR = (1 failed + 1 rollback) / (2 succeeded + 1 failed)
        Assert.Equal(0.667, summary.GetProperty("changeFailureRate").GetDouble(), precision: 3);
        // The response describes its own definition.
        Assert.Equal("day", body.GetProperty("definition").GetProperty("bucket").GetString());
        Assert.False(body.GetProperty("definition").GetProperty("includeRollbacks").GetBoolean());
    }

    [Fact]
    public async Task Frequency_PreviousPeriodTotal_ComparesEqualSpanBefore()
    {
        var product = Unique("freqprev");
        await IngestAsync(product, "api", "dev", "v1", "2026-07-26T10:00:00Z"); // previous window
        await IngestAsync(product, "api", "dev", "v2", "2026-08-02T10:00:00Z"); // current window
        await IngestAsync(product, "api", "dev", "v3", "2026-08-03T10:00:00Z");

        var body = await GetJson(
            $"/api/analytics/deployments/frequency?product={product}&from=2026-08-01T00:00:00Z&to=2026-08-08T00:00:00Z");

        var summary = Assert.Single(body.GetProperty("series").EnumerateArray()).GetProperty("summary");
        Assert.Equal(2, summary.GetProperty("total").GetInt32());
        Assert.Equal(1, summary.GetProperty("previousPeriodTotal").GetInt32());
    }

    [Fact]
    public async Task Frequency_GroupByService_SplitsSeries()
    {
        var product = Unique("freqgrp");
        await IngestAsync(product, "api", "dev", "v1", "2026-08-02T10:00:00Z");
        await IngestAsync(product, "web", "dev", "v1", "2026-08-02T11:00:00Z");

        var body = await GetJson(
            $"/api/analytics/deployments/frequency?product={product}&groupBy=service&from=2026-08-01T00:00:00Z&to=2026-08-08T00:00:00Z");

        var keys = body.GetProperty("series").EnumerateArray()
            .Select(s => s.GetProperty("key").GetProperty("serviceName").GetString())
            .OrderBy(k => k).ToList();
        Assert.Equal(["api", "web"], keys);
    }

    [Fact]
    public async Task Frequency_GroupByService_EmitsZeroSeriesForStaleServices()
    {
        var product = Unique("freqstale");
        // "worker" deployed long before the window — must still appear, as a zero series
        // with its true last deploy, so stale services can't fall out of the report.
        await IngestAsync(product, "worker", "dev", "v1", "2026-05-01T10:00:00Z");
        await IngestAsync(product, "api", "dev", "v2", "2026-08-02T10:00:00Z");

        var body = await GetJson(
            $"/api/analytics/deployments/frequency?product={product}&groupBy=service&from=2026-08-01T00:00:00Z&to=2026-08-08T00:00:00Z");

        var series = body.GetProperty("series").EnumerateArray().ToList();
        Assert.Equal(2, series.Count);
        var worker = series.Single(s => s.GetProperty("key").GetProperty("serviceName").GetString() == "worker");
        Assert.Equal(0, worker.GetProperty("summary").GetProperty("total").GetInt32());
        Assert.StartsWith("2026-05-01", worker.GetProperty("summary").GetProperty("lastDeployedAt").GetString());
    }

    [Fact]
    public async Task Frequency_SummaryOnly_OmitsBuckets()
    {
        var product = Unique("freqsum");
        await IngestAsync(product, "api", "dev", "v1", "2026-08-02T10:00:00Z");

        var body = await GetJson(
            $"/api/analytics/deployments/frequency?product={product}&summaryOnly=true&from=2026-08-01T00:00:00Z&to=2026-08-08T00:00:00Z");

        var series = Assert.Single(body.GetProperty("series").EnumerateArray());
        Assert.Equal(0, series.GetProperty("buckets").GetArrayLength());
        Assert.Equal(1, series.GetProperty("summary").GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task Frequency_InvalidBucket_Returns400()
    {
        var response = await _adminClient.GetAsync("/api/analytics/deployments/frequency?bucket=month");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ── Matrix ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Matrix_RequiresProduct()
    {
        var response = await _adminClient.GetAsync("/api/analytics/work-items/matrix");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Matrix_ShowsDeployedAndAwaitingStates()
    {
        await EnablePromotionsAsync();
        var product = Unique("matrix");
        await IngestAsync(product, "api", "dev", "v1", "2026-08-02T10:00:00Z",
            workItems: ["MTX-1"]);
        await IngestAsync(product, "api", "test", "v1", "2026-08-03T10:00:00Z",
            workItems: ["MTX-1"]);
        await SeedCandidateAsync(product, "api", "test", "prod", "v1",
            PromotionStatus.Approved, workItems: ["MTX-1"],
            createdAt: DateTimeOffset.Parse("2026-08-03T12:00:00Z"),
            approvedAt: DateTimeOffset.Parse("2026-08-04T09:00:00Z"));

        var body = await GetJson(
            $"/api/analytics/work-items/matrix?product={product}&from=2026-08-01T00:00:00Z&to=2026-08-08T00:00:00Z");

        var item = Assert.Single(body.GetProperty("items").EnumerateArray());
        Assert.Equal("MTX-1", item.GetProperty("key").GetString());
        var envs = item.GetProperty("envs");
        Assert.Equal("deployed", envs.GetProperty("dev").GetProperty("state").GetString());
        Assert.Equal("deployed", envs.GetProperty("test").GetProperty("state").GetString());
        Assert.Equal("approved-awaiting-deploy", envs.GetProperty("prod").GetProperty("state").GetString());
        Assert.Equal("test", item.GetProperty("furthestEnv").GetString());
        // prod appears in environments because the open candidate targets it.
        var envList = body.GetProperty("environments").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains("prod", envList);
    }

    [Fact]
    public async Task Matrix_CoverageCountsDeploysWithoutWorkItems()
    {
        await EnablePromotionsAsync();
        var product = Unique("matrixcov");
        await IngestAsync(product, "api", "dev", "v1", "2026-08-02T10:00:00Z", workItems: ["COV-1"]);
        await IngestAsync(product, "api", "dev", "v2", "2026-08-03T10:00:00Z"); // no ticket
        await IngestAsync(product, "api", "dev", "v3", "2026-08-04T10:00:00Z"); // no ticket

        var body = await GetJson(
            $"/api/analytics/work-items/matrix?product={product}&from=2026-08-01T00:00:00Z&to=2026-08-08T00:00:00Z");

        var coverage = body.GetProperty("coverage");
        Assert.Equal(3, coverage.GetProperty("deployments").GetInt32());
        Assert.Equal(2, coverage.GetProperty("withoutWorkItem").GetInt32());
        Assert.Equal(0.333, coverage.GetProperty("ratio").GetDouble(), precision: 3);
    }

    [Fact]
    public async Task Matrix_WindowSelectsStories_ButCellsShowFullState()
    {
        await EnablePromotionsAsync();
        var product = Unique("matrixwin");
        // Deployed to dev long before the window, to test inside it: the story is selected
        // by the test deploy, and the dev checkmark must still show.
        await IngestAsync(product, "api", "dev", "v1", "2026-06-01T10:00:00Z", workItems: ["WIN-1"]);
        await IngestAsync(product, "api", "test", "v1", "2026-08-03T10:00:00Z", workItems: ["WIN-1"]);
        // Only old activity — outside the window, no open candidate: not selected.
        await IngestAsync(product, "api", "dev", "v0", "2026-06-01T09:00:00Z", workItems: ["WIN-OLD"]);

        var body = await GetJson(
            $"/api/analytics/work-items/matrix?product={product}&from=2026-08-01T00:00:00Z&to=2026-08-08T00:00:00Z");

        var item = Assert.Single(body.GetProperty("items").EnumerateArray());
        Assert.Equal("WIN-1", item.GetProperty("key").GetString());
        Assert.Equal("deployed", item.GetProperty("envs").GetProperty("dev").GetProperty("state").GetString());
    }

    [Fact]
    public async Task Matrix_ReachedEnvFilter_SelectsFirstDeployInWindow()
    {
        await EnablePromotionsAsync();
        var product = Unique("matrixship");
        // SHIP-1 first reached test inside the window.
        await IngestAsync(product, "api", "test", "v1", "2026-08-03T10:00:00Z", workItems: ["SHIP-1"]);
        // SHIP-2 first reached test before the window (re-deployed inside it — still not "shipped this period").
        await IngestAsync(product, "api", "test", "v1", "2026-07-01T10:00:00Z", workItems: ["SHIP-2"]);
        await IngestAsync(product, "api", "test", "v2", "2026-08-04T10:00:00Z", workItems: ["SHIP-2"]);

        var body = await GetJson(
            $"/api/analytics/work-items/matrix?product={product}&reachedEnv=test&from=2026-08-01T00:00:00Z&to=2026-08-08T00:00:00Z");

        var keys = body.GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("key").GetString()).ToList();
        Assert.Equal(["SHIP-1"], keys);
    }

    [Fact]
    public async Task Matrix_ReachedEnvs_AllSemantics_RequiresEveryEnvironment()
    {
        await EnablePromotionsAsync();
        var product = Unique("matrixall");
        // BOTH-1 landed on both prods; the SECOND landing (prod-eu, 08-05) dates it.
        await IngestAsync(product, "api", "prod-us", "v1", "2026-08-02T10:00:00Z", workItems: ["BOTH-1"]);
        await IngestAsync(product, "api", "prod-eu", "v1", "2026-08-05T10:00:00Z", workItems: ["BOTH-1"]);
        // HALF-1 reached only one of the two — not shipped.
        await IngestAsync(product, "api", "prod-us", "v2", "2026-08-03T10:00:00Z", workItems: ["HALF-1"]);

        var body = await GetJson(
            $"/api/analytics/work-items/matrix?product={product}&reachedEnv=prod-us,prod-eu&from=2026-08-01T00:00:00Z&to=2026-08-08T00:00:00Z");

        var item = Assert.Single(body.GetProperty("items").EnumerateArray());
        Assert.Equal("BOTH-1", item.GetProperty("key").GetString());
    }

    [Fact]
    public async Task Matrix_ReachedEnvs_WindowMatchesCompletionOfTheSet()
    {
        await EnablePromotionsAsync();
        var product = Unique("matrixcomp");
        // First prod long before the window, second inside it → completion is in-window: shipped.
        await IngestAsync(product, "api", "prod-us", "v1", "2026-06-01T10:00:00Z", workItems: ["LATE-1"]);
        await IngestAsync(product, "api", "prod-eu", "v1", "2026-08-03T10:00:00Z", workItems: ["LATE-1"]);

        var body = await GetJson(
            $"/api/analytics/work-items/matrix?product={product}&reachedEnv=prod-us,prod-eu&from=2026-08-01T00:00:00Z&to=2026-08-08T00:00:00Z");

        var item = Assert.Single(body.GetProperty("items").EnumerateArray());
        Assert.Equal("LATE-1", item.GetProperty("key").GetString());
    }

    // ── Queue ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Queue_ReportsEdgesAndLatencies()
    {
        var product = Unique("queue");
        var created = DateTimeOffset.Parse("2026-08-02T10:00:00Z");
        await SeedCandidateAsync(product, "api", "dev", "test", "v1", PromotionStatus.Pending,
            createdAt: created);
        await SeedCandidateAsync(product, "api", "test", "prod", "v1", PromotionStatus.Approved,
            createdAt: created, approvedAt: created.AddHours(10));
        await SeedCandidateAsync(product, "api", "test", "prod", "v0", PromotionStatus.Deployed,
            createdAt: created, approvedAt: created.AddHours(2), deployedAt: created.AddHours(6));

        var body = await GetJson(
            $"/api/analytics/promotions/queue?product={product}&from=2026-08-01T00:00:00Z&to=2026-08-08T00:00:00Z");

        var edges = body.GetProperty("edges").EnumerateArray().ToList();
        Assert.Equal(2, edges.Count);
        var prodEdge = edges.Single(e => e.GetProperty("targetEnv").GetString() == "prod");
        Assert.Equal(0, prodEdge.GetProperty("pending").GetInt32());
        Assert.Equal(1, prodEdge.GetProperty("awaitingDeploy").GetInt32());
        var testEdge = edges.Single(e => e.GetProperty("targetEnv").GetString() == "test");
        Assert.Equal(1, testEdge.GetProperty("pending").GetInt32());

        // Two approvals in window: 10h and 2h → p50 = 6h. One deploy: 4h.
        var approval = body.GetProperty("approvalLatency");
        Assert.Equal(2, approval.GetProperty("n").GetInt32());
        Assert.Equal(6.0, approval.GetProperty("p50Hours").GetDouble(), precision: 1);
        var deploy = body.GetProperty("deployLatency");
        Assert.Equal(1, deploy.GetProperty("n").GetInt32());
        Assert.Equal(4.0, deploy.GetProperty("p50Hours").GetDouble(), precision: 1);
    }

    // ── Lead time ───────────────────────────────────────────────────────────

    [Fact]
    public async Task LeadTime_ComputesFromPullRequestOccurredAt()
    {
        await EnablePromotionsAsync();
        var product = Unique("lead");
        // PR merged 2026-08-01T10:00, deployed to dev two days later → 48h.
        await IngestAsync(product, "api", "dev", "v1", "2026-08-03T10:00:00Z",
            workItems: ["LT-1"], prOccurredAt: "2026-08-01T10:00:00Z");

        var body = await GetJson(
            $"/api/analytics/lead-time?product={product}&from=2026-08-01T00:00:00Z&to=2026-08-08T00:00:00Z");

        var coverage = body.GetProperty("coverage");
        Assert.Equal(1, coverage.GetProperty("workItems").GetInt32());
        Assert.Equal(1, coverage.GetProperty("withClockStart").GetInt32());

        var env = Assert.Single(body.GetProperty("byEnvironment").EnumerateArray());
        Assert.Equal("dev", env.GetProperty("environment").GetString());
        Assert.Equal(48.0, env.GetProperty("p50Hours").GetDouble(), precision: 1);

        var slowest = Assert.Single(body.GetProperty("slowest").EnumerateArray());
        Assert.Equal("LT-1", slowest.GetProperty("workItemKey").GetString());
    }

    [Fact]
    public async Task LeadTime_NoOccurredAt_ReportsZeroCoverageNot404()
    {
        await EnablePromotionsAsync();
        var product = Unique("leadempty");
        await IngestAsync(product, "api", "dev", "v1", "2026-08-03T10:00:00Z", workItems: ["LT-2"]);

        var body = await GetJson(
            $"/api/analytics/lead-time?product={product}&from=2026-08-01T00:00:00Z&to=2026-08-08T00:00:00Z");

        var coverage = body.GetProperty("coverage");
        Assert.Equal(1, coverage.GetProperty("workItems").GetInt32());
        Assert.Equal(0, coverage.GetProperty("withClockStart").GetInt32());
        Assert.Equal(0.0, coverage.GetProperty("ratio").GetDouble());
        var env = Assert.Single(body.GetProperty("byEnvironment").EnumerateArray());
        Assert.Equal(0, env.GetProperty("n").GetInt32());
        Assert.Equal(JsonValueKind.Null, env.GetProperty("p50Hours").ValueKind);
    }

    [Fact]
    public async Task LeadTime_FirstDeployPerEnvironmentIsTheGrain()
    {
        await EnablePromotionsAsync();
        var product = Unique("leadfirst");
        // Same ticket deployed twice to dev; the grain must use the FIRST deploy (24h), not the retry.
        await IngestAsync(product, "api", "dev", "v1", "2026-08-02T10:00:00Z",
            workItems: ["LT-3"], prOccurredAt: "2026-08-01T10:00:00Z");
        await IngestAsync(product, "api", "dev", "v2", "2026-08-05T10:00:00Z",
            workItems: ["LT-3"], prOccurredAt: "2026-08-01T10:00:00Z");

        var body = await GetJson(
            $"/api/analytics/lead-time?product={product}&from=2026-08-01T00:00:00Z&to=2026-08-08T00:00:00Z");

        var env = Assert.Single(body.GetProperty("byEnvironment").EnumerateArray());
        Assert.Equal(1, env.GetProperty("n").GetInt32());
        Assert.Equal(24.0, env.GetProperty("p50Hours").GetDouble(), precision: 1);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    /// <summary>Unique product per test so the shared fixture's data never bleeds between tests.</summary>
    private static string Unique(string prefix) => $"{prefix}-{Guid.NewGuid():N}"[..20];

    /// <summary>
    /// Work-item projection (DeployEventWorkItem) is written by the promotion ingest hook, which
    /// is gated on the promotions feature flag — off by default. Tests that assert on tickets
    /// turn it on first.
    /// </summary>
    private async Task EnablePromotionsAsync()
    {
        var response = await _adminClient.PutAsJsonAsync(
            "/api/features/features.promotions", new { enabled = true });
        response.EnsureSuccessStatusCode();
    }

    private async Task IngestAsync(
        string product, string service, string environment, string version, string deployedAt,
        string status = "succeeded", bool isRollback = false, string? previousVersion = null,
        IReadOnlyList<string>? workItems = null, string? prOccurredAt = null)
    {
        var references = new List<object>();
        foreach (var key in workItems ?? [])
            references.Add(new { type = "work-item", key });
        if (prOccurredAt is not null)
            references.Add(new { type = "pull-request", key = "99", occurredAt = prOccurredAt });

        var response = await _apiKeyClient.PostAsJsonAsync("/api/deployments/events", new
        {
            product,
            service,
            environment,
            version,
            source = "ci",
            deployedAt,
            status,
            isRollback,
            previousVersion,
            references,
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    private async Task SeedCandidateAsync(
        string product, string service, string sourceEnv, string targetEnv, string version,
        PromotionStatus status, IReadOnlyList<string>? workItems = null,
        DateTimeOffset? createdAt = null, DateTimeOffset? approvedAt = null,
        DateTimeOffset? deployedAt = null)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();

        var candidate = new PromotionCandidate
        {
            Id = Guid.NewGuid(),
            Product = product,
            Service = service,
            SourceEnv = sourceEnv,
            TargetEnv = targetEnv,
            Version = version,
            Status = status,
            CreatedAt = createdAt ?? DateTimeOffset.UtcNow,
            ApprovedAt = approvedAt,
            DeployedAt = deployedAt,
            ParticipantsJson = "[]",
        };
        candidate.References = (workItems ?? [])
            .Select(k => new ReferenceDto("work-item", Key: k))
            .ToList();
        db.PromotionCandidates.Add(candidate);

        foreach (var key in workItems ?? [])
        {
            db.PromotionWorkItems.Add(new PromotionWorkItem
            {
                Id = Guid.NewGuid(),
                CandidateId = candidate.Id,
                WorkItemKey = key,
                Product = product,
                TargetEnv = targetEnv,
                CreatedAt = candidate.CreatedAt,
            });
        }

        await db.SaveChangesAsync();
    }

    private async Task<JsonElement> GetJson(string url)
    {
        var response = await _adminClient.GetAsync(url);
        response.EnsureSuccessStatusCode();
        var stream = await response.Content.ReadAsStreamAsync();
        var doc = await JsonDocument.ParseAsync(stream);
        return doc.RootElement.Clone();
    }

    private static int SumBucket(JsonElement series, string field)
        => series.GetProperty("buckets").EnumerateArray().Sum(b => b.GetProperty(field).GetInt32());

    // ── Test factory ────────────────────────────────────────────────────────

    public class AnalyticsFactory : TestFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);

            builder.UseSetting("Deployments:ApiKeys:0:Name", "test-key");
            builder.UseSetting("Deployments:ApiKeys:0:Key", TestApiKey);
        }
    }
}
