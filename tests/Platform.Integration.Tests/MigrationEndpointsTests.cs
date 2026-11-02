using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;

namespace Platform.Integration.Tests;

/// <summary>
/// The migrations surface end to end: pipelines report data migrations through the API, schema
/// migrations are read out of the hook output deploy events already carry, and the portal answers
/// what is pending or failed, what a deployment's migrations were, and where a migration has run.
/// </summary>
public class MigrationEndpointsTests : IClassFixture<MigrationEndpointsTests.MigrationsFactory>, IDisposable
{
    private const string TestApiKey = "test-migrations-key-12345";

    private readonly HttpClient _apiKeyClient;
    private readonly HttpClient _adminClient;

    public MigrationEndpointsTests(MigrationsFactory factory)
    {
        _apiKeyClient = factory.CreateClient();
        _apiKeyClient.DefaultRequestHeaders.Add("X-Api-Key", TestApiKey);
        _adminClient = factory.CreateAdminClient();
    }

    public void Dispose()
    {
        _apiKeyClient.Dispose();
        _adminClient.Dispose();
    }

    private const string FailedSchemaOutput = """
        [.] 4640 AddOrderIndex         1m                   -
        [.] 4650 AlterAccountName      1m                   -
        [✓] 4640 AddOrderIndex         1m 2026-11-02T09:14:03
        [<] 4640 AddOrderIndex         1m 2026-11-02T09:14:04               1s
        [x] 4650 AlterAccountName      1m 2026-11-02T09:14:04               0s Invalid column name 'DisplayName'.
        The following migrations have failed:
        - AlterAccountName: Invalid column name 'DisplayName'.
        - AddOrderIndex: Migration rolled back
        """;

    private const string AppliedSchemaOutput = """
        [.] 4650 AlterAccountName      1m                   -
        [✓] 4650 AlterAccountName      1m 2026-11-01T08:00:05               2s
        Schema migrations completed.
        """;

    private async Task<string> IngestDeployAsync(
        string service, string environment, string version, string deployedAt,
        string status = "succeeded", object[]? logs = null, string? failureReason = null)
    {
        var response = await _apiKeyClient.PostAsJsonAsync("/api/deployments/events", new
        {
            product = "acme",
            service,
            environment,
            version,
            source = "helm-deploy",
            deployedAt,
            status,
            run = failureReason is null ? null : new { provider = "github-actions", failureReason },
            logs,
        });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await Json(response)).GetProperty("id").GetString()!;
    }

    private async Task<JsonElement> ReportAsync(object body, HttpStatusCode? expect = null)
    {
        var response = await _apiKeyClient.PostAsJsonAsync("/api/migrations/runs", body);
        if (expect is not null) Assert.Equal(expect, response.StatusCode);
        else Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return await Json(response);
    }

    [Fact]
    public async Task DataMigration_NewerVersionSupersedesThePendingOne_AndOnlyItIsFlagged()
    {
        await IngestDeployAsync("orders", "staging", "1.0.0", "2026-10-01T10:00:00Z");
        await IngestDeployAsync("orders", "staging", "1.1.0", "2026-10-02T10:00:00Z");

        var older = await ReportAsync(new
        {
            kind = "data", product = "acme", service = "orders", environment = "staging", version = "1.0.0",
            status = "pending", source = "mpt-release", queuedAt = "2026-10-01T10:05:00Z",
        });
        var newer = await ReportAsync(new
        {
            kind = "data", product = "acme", service = "orders", environment = "staging", version = "1.1.0",
            status = "pending", source = "mpt-release", queuedAt = "2026-10-02T10:05:00Z",
        });

        var olderDetail = await Json(await _adminClient.GetAsync($"/api/migrations/runs/{older.GetProperty("id").GetString()}"));
        Assert.Equal("superseded", olderDetail.GetProperty("run").GetProperty("status").GetString());
        Assert.Equal(newer.GetProperty("id").GetString(), olderDetail.GetProperty("supersededBy").GetProperty("id").GetString());

        var flags = (await Json(await _adminClient.GetAsync("/api/migrations/flags?product=acme&service=orders"))).EnumerateArray().ToList();
        var flag = Assert.Single(flags);
        Assert.Equal("1.1.0", flag.GetProperty("version").GetString());
        Assert.Equal("pending", flag.GetProperty("status").GetString());
        // Linked to the deploy that queued it.
        Assert.False(string.IsNullOrEmpty(flag.GetProperty("deployEventId").GetString()));
    }

    [Fact]
    public async Task DataMigration_ReportedOutOfOrder_TheOlderOneIsSupersededOnArrival()
    {
        await ReportAsync(new
        {
            kind = "data", product = "acme", service = "late", environment = "prod", version = "2.0.0",
            status = "pending", source = "backfill", queuedAt = "2026-10-05T00:00:00Z",
        });
        var older = await ReportAsync(new
        {
            kind = "data", product = "acme", service = "late", environment = "prod", version = "1.9.0",
            status = "failed", source = "backfill", queuedAt = "2026-10-04T00:00:00Z",
        });

        Assert.Equal("superseded", older.GetProperty("status").GetString());
    }

    [Fact]
    public async Task DataMigration_AReplayedPendingNeverReopensOneThatRan()
    {
        var body = new
        {
            kind = "data", product = "acme", service = "replay", environment = "dev", version = "3.0.0",
            source = "mpt-release", queuedAt = "2026-10-06T00:00:00Z",
        };
        await ReportAsync(new { body.kind, body.product, body.service, body.environment, body.version, body.source, body.queuedAt, status = "pending" });
        await ReportAsync(new
        {
            body.kind, body.product, body.service, body.environment, body.version, body.source, status = "succeeded",
            startedAt = "2026-10-06T01:00:00Z", completedAt = "2026-10-06T01:05:00Z",
            logs = new[] { new { name = "data migration job output", content = "[✓] 9001 BackfillCodes 1m 2026-10-06T01:04:00 4m\nData migrations completed." } },
        });
        var again = await ReportAsync(new { body.kind, body.product, body.service, body.environment, body.version, body.source, body.queuedAt, status = "pending" });

        Assert.Equal("succeeded", again.GetProperty("status").GetString());
        Assert.Equal(1, again.GetProperty("appliedCount").GetInt32());
    }

    [Fact]
    public async Task SchemaMigration_IsReadFromTheDeploysHookLogs_AndShownOnTheDeployment()
    {
        var hookLogs =
            "=======================Logs from hook pod/billing-migrations-x1 (job billing-migrations)=======================\n" +
            FailedSchemaOutput + "\n" +
            "=======================End logs from hook pod/billing-migrations-x1=======================\n";
        var eventId = await IngestDeployAsync("billing", "staging", "5.0.0", "2026-10-07T10:00:00Z", "failed",
            [
                new { name = "helm upgrade output", source = "helm", content = "Error: UPGRADE FAILED: pre-upgrade hooks failed: 1 error occurred:\n\t* job billing-migrations failed: BackoffLimitExceeded\n" },
                new { name = "helm hook logs", source = "helm", content = hookLogs },
            ],
            failureReason: "Error: UPGRADE FAILED: pre-upgrade hooks failed: 1 error occurred:");

        var detail = await Json(await _adminClient.GetAsync($"/api/deployments/events/{eventId}"));
        var migration = Assert.Single(detail.GetProperty("migrations").EnumerateArray());
        Assert.Equal("schema", migration.GetProperty("kind").GetString());
        Assert.Equal("failed", migration.GetProperty("status").GetString());
        Assert.Equal("AlterAccountName: Invalid column name 'DisplayName'.", migration.GetProperty("failureReason").GetString());

        var run = await Json(await _adminClient.GetAsync($"/api/migrations/runs/{migration.GetProperty("id").GetString()}"));
        var items = run.GetProperty("migrations").EnumerateArray()
            .ToDictionary(i => i.GetProperty("name").GetString()!, i => i.GetProperty("status").GetString());
        Assert.Equal("rolled-back", items["AddOrderIndex"]);
        Assert.Equal("failed", items["AlterAccountName"]);
        // Only the migration's own output is kept with it.
        var log = Assert.Single(run.GetProperty("logs").EnumerateArray());
        var content = await Json(await _adminClient.GetAsync(
            $"/api/migrations/runs/{migration.GetProperty("id").GetString()}/logs/{log.GetProperty("id").GetString()}"));
        Assert.Contains("billing-migrations-x1", content.GetProperty("content").GetString());

        // Replaying the same event doesn't add a second one.
        await IngestDeployAsync("billing", "staging", "5.0.0", "2026-10-07T10:00:00Z", "failed",
            [new { name = "helm hook logs", source = "helm", content = hookLogs }],
            failureReason: "Error: UPGRADE FAILED: pre-upgrade hooks failed: 1 error occurred:");
        var list = await Json(await _adminClient.GetAsync("/api/migrations/runs?product=acme&service=billing&kind=schema"));
        Assert.Equal(1, list.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task Search_FindsAMigrationByAPartialMisspeltName_PerEnvironment()
    {
        await ReportAsync(new
        {
            kind = "schema", product = "acme", service = "accounts", environment = "test", version = "7.0.0",
            status = "succeeded", source = "sdp", runKey = "run-1",
            logs = new[] { new { name = "schema migration output", content = AppliedSchemaOutput } },
        });
        await ReportAsync(new
        {
            kind = "schema", product = "acme", service = "accounts", environment = "staging", version = "7.0.0",
            status = "failed", source = "sdp", runKey = "run-2",
            logs = new[] { new { name = "schema migration output", content = FailedSchemaOutput } },
        });

        var result = await Json(await _adminClient.GetAsync("/api/migrations/search?q=acount%20nmae&product=acme"));
        var match = result.GetProperty("matches").EnumerateArray().First();
        Assert.Equal("AlterAccountName", match.GetProperty("name").GetString());
        var byEnv = match.GetProperty("hits").EnumerateArray()
            .Where(h => h.GetProperty("service").GetString() == "accounts")
            .ToDictionary(h => h.GetProperty("environment").GetString()!, h => h.GetProperty("status").GetString());
        Assert.Equal("applied", byEnv["test"]);
        Assert.Equal("failed", byEnv["staging"]);

        // By the script's order number too.
        var byOrder = await Json(await _adminClient.GetAsync("/api/migrations/search?q=4650&product=acme"));
        Assert.Contains("AlterAccountName", byOrder.GetProperty("matches").EnumerateArray().Select(m => m.GetProperty("name").GetString()));
    }

    [Fact]
    public async Task Report_ValidatesKindStatusAndReservedRunKeys()
    {
        var bad = await ReportAsync(new
        {
            kind = "seed", product = "acme", service = "x", environment = "dev", version = "1",
            status = "superseded", source = "s", runKey = "deploy:abc",
        }, HttpStatusCode.BadRequest);
        var errors = bad.GetProperty("errors").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains(errors, e => e!.StartsWith("'kind'"));
        Assert.Contains(errors, e => e!.StartsWith("'status'"));
        Assert.Contains(errors, e => e!.StartsWith("'runKey'"));
    }

    [Fact]
    public async Task Report_NeedsAnApiKey()
    {
        var response = await _adminClient.PostAsJsonAsync("/api/migrations/runs", new
        {
            kind = "data", product = "acme", service = "x", environment = "dev", version = "1", status = "pending", source = "s",
        });
        Assert.Contains(response.StatusCode, new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden });
    }

    [Fact]
    public async Task AdminDerive_ReadsDeploysRecordedBeforeTheFeature()
    {
        var hookLogs =
            "=======================Logs from hook pod/legacy-migrations-q (job legacy-migrations)=======================\n" +
            "No migrations to run.\nSchema migrations completed.\n" +
            "=======================End logs from hook pod/legacy-migrations-q=======================\n";
        await IngestDeployAsync("legacy", "dev", "0.9.0", "2026-10-08T00:00:00Z",
            logs: [new { name = "helm hook logs", source = "helm", content = hookLogs }]);

        var response = await _adminClient.PostAsync("/api/migrations/admin/derive?since=2026-10-07T00:00:00Z&product=acme", null);
        response.EnsureSuccessStatusCode();
        var result = await Json(response);
        // Already derived at ingest, so a re-read finds it unchanged.
        Assert.True(result.GetProperty("examined").GetInt32() >= 1);
        Assert.Equal(0, result.GetProperty("created").GetInt32());
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response)
    {
        var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        return doc.RootElement.Clone();
    }

    public class MigrationsFactory : TestFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("Deployments:ApiKeys:0:Name", "test-key");
            builder.UseSetting("Deployments:ApiKeys:0:Key", TestApiKey);
        }
    }
}
