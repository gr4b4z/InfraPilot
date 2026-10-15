using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;

namespace Platform.Integration.Tests;

/// <summary>
/// Retiring an environment, end to end over HTTP against SQLite: the current-state endpoints drop
/// it — which also proves the name-list filter translates on a relational provider, inside the
/// matrix's correlated per-cell lookup included — while history keeps it.
///
/// <para>Each test owns a factory: the retired flag lives in the one global settings row, so tests
/// sharing a database would retire environments under each other.</para>
/// </summary>
public class RetiredEnvironmentReadPathTests
{
    private const string TestApiKey = "test-retired-env-key-12345";
    private const string Product = "marketplace";

    // ── Helpers ─────────────────────────────────────────────────────────────

    private sealed class Harness : IDisposable
    {
        public Factory Factory { get; } = new();
        public HttpClient Ingest { get; }
        public HttpClient Admin { get; }

        public Harness()
        {
            Ingest = Factory.CreateClient();
            Ingest.DefaultRequestHeaders.Add("X-Api-Key", TestApiKey);
            Admin = Factory.CreateAdminClient();
        }

        public void Dispose()
        {
            Ingest.Dispose();
            Admin.Dispose();
            Factory.Dispose();
        }
    }

    private static async Task IngestAsync(
        Harness h, string service, string environment, string version, string deployedAt,
        params string[] workItems)
    {
        var references = new List<object>();
        foreach (var key in workItems) references.Add(new { type = "work-item", key });
        if (workItems.Length > 0)
            references.Add(new { type = "pull-request", key = "1", occurredAt = "2026-08-01T00:00:00Z" });

        var response = await h.Ingest.PostAsJsonAsync("/api/deployments/events", new
        {
            product = Product,
            service,
            environment,
            version,
            source = "ci",
            deployedAt,
            status = "succeeded",
            references,
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    /// <summary>api reached dev, stable and prod; legacy only ever stable.</summary>
    private static async Task SeedAsync(Harness h)
    {
        // The work-item projection analytics reads is written by the promotion ingest hook, which
        // is gated on the promotions flag — off by default.
        (await h.Admin.PutAsJsonAsync("/api/features/features.promotions", new { enabled = true }))
            .EnsureSuccessStatusCode();

        await IngestAsync(h, "api", "stable", "v0.9.0", "2026-08-02T08:00:00Z", "RET-0");
        await IngestAsync(h, "api", "dev", "v1.0.0", "2026-08-02T09:00:00Z", "RET-1");
        await IngestAsync(h, "api", "stable", "v1.0.0", "2026-08-02T10:00:00Z", "RET-1");
        await IngestAsync(h, "api", "prod", "v1.0.0", "2026-08-02T11:00:00Z", "RET-1");
        await IngestAsync(h, "legacy", "stable", "v3.0.0", "2026-08-02T12:00:00Z");
    }

    private static async Task ConfigureAsync(Harness h, bool retireStable)
    {
        var put = await h.Admin.PutAsJsonAsync("/api/settings", new
        {
            environments = new object[]
            {
                new { key = "dev", displayName = "Development" },
                new { key = "stable", displayName = "Staging Candidate", retired = retireStable },
                new { key = "prod", displayName = "Production", isProduction = true },
            },
            roles = new[] { new { key = "triggered-by", displayName = "Triggered by" } },
            activityTemplate = Array.Empty<object>(),
        });
        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);
    }

    private static async Task<JsonElement> GetJson(Harness h, string url)
    {
        var response = await h.Admin.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync())).RootElement;
    }

    private static List<string> Cells(JsonElement state)
        => state.EnumerateArray()
            .Select(s => $"{s.GetProperty("service").GetString()}/{s.GetProperty("environment").GetString()}")
            .ToList();

    private static List<string> Strings(JsonElement array, string? property = null)
        => array.EnumerateArray()
            .Select(e => (property is null ? e : e.GetProperty(property)).GetString()!)
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();

    // ── Deployments ─────────────────────────────────────────────────────────

    [Fact]
    public async Task CurrentStateEndpoints_DropTheEnvironmentOnceRetired()
    {
        using var h = new Harness();
        await SeedAsync(h);

        // Not retired yet: everything shows it, exactly as before the flag existed.
        await ConfigureAsync(h, retireStable: false);
        Assert.Equal(
            ["api/dev", "api/prod", "api/stable", "legacy/stable"],
            Cells(await GetJson(h, $"/api/deployments/state?product={Product}")));
        Assert.Contains(
            "stable", Strings((await GetJson(h, "/api/webhooks/filter-options")).GetProperty("environments")));

        await ConfigureAsync(h, retireStable: true);

        Assert.Equal(["api/dev", "api/prod"], Cells(await GetJson(h, $"/api/deployments/state?product={Product}")));
        Assert.Equal(["api/dev", "api/prod"], Cells(await GetJson(h, $"/api/deployments/state?product={Product}&view=summary")));
        Assert.Empty((await GetJson(h, "/api/deployments/state?environment=stable")).EnumerateArray());

        var card = Assert.Single((await GetJson(h, "/api/deployments/products")).EnumerateArray());
        Assert.Equal(
            ["dev", "prod"],
            card.GetProperty("environments").EnumerateObject().Select(p => p.Name).OrderBy(n => n).ToList());

        var hits = (await GetJson(h, "/api/deployments/services/search?q=a")).GetProperty("results");
        var api = Assert.Single(hits.EnumerateArray());
        Assert.Equal("api", api.GetProperty("service").GetString());
        Assert.Equal(["dev", "prod"], Strings(api.GetProperty("environments"), "environment"));

        var detail = await GetJson(h, $"/api/deployments/services/{Product}/api");
        Assert.Equal(["dev", "prod"], Strings(detail.GetProperty("environments"), "environment"));
        var versions = detail.GetProperty("recentVersions").EnumerateArray().ToList();
        Assert.Equal(["v1.0.0"], versions.Select(v => v.GetProperty("version").GetString()));
        Assert.Equal(["dev", "prod"], Strings(versions[0].GetProperty("environments"), "environment"));

        Assert.Equal(
            HttpStatusCode.NotFound,
            (await h.Admin.GetAsync($"/api/deployments/services/{Product}/legacy")).StatusCode);

        Assert.DoesNotContain(
            "stable", Strings((await GetJson(h, "/api/webhooks/filter-options")).GetProperty("environments")));
    }

    [Fact]
    public async Task History_KeepsTheRetiredEnvironment()
    {
        using var h = new Harness();
        await SeedAsync(h);
        await ConfigureAsync(h, retireStable: true);

        var history = await GetJson(h, $"/api/deployments/history/{Product}/api");
        Assert.Equal(2, history.EnumerateArray().Count(e => e.GetProperty("environment").GetString() == "stable"));
        Assert.Single((await GetJson(h, $"/api/deployments/history/{Product}/legacy")).EnumerateArray());
        // The rollback picker reads history too: every version that ever ran there.
        Assert.Equal(
            3,
            (await GetJson(h, $"/api/deployments/versions?product={Product}&environment=stable"))
                .GetProperty("versions").EnumerateArray().Count());
    }

    // ── Analytics ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Analytics_DropTheRetiredEnvironmentFromPerEnvironmentOutputs()
    {
        using var h = new Harness();
        await SeedAsync(h);
        await ConfigureAsync(h, retireStable: true);

        const string window = "from=2026-08-01T00:00:00Z&to=2026-08-08T00:00:00Z";

        var frequency = await GetJson(h, $"/api/analytics/deployments/frequency?product={Product}&groupBy=environment&{window}");
        Assert.Equal(
            ["dev", "prod"],
            frequency.GetProperty("series").EnumerateArray()
                .Select(s => s.GetProperty("key").GetProperty("environment").GetString()!)
                .OrderBy(e => e).ToList());

        var matrix = await GetJson(h, $"/api/analytics/work-items/matrix?product={Product}&{window}");
        Assert.Equal(["dev", "prod"], Strings(matrix.GetProperty("environments")));
        // RET-0 only ever reached stable.
        var item = Assert.Single(matrix.GetProperty("items").EnumerateArray());
        Assert.Equal("RET-1", item.GetProperty("key").GetString());

        var lead = await GetJson(h, $"/api/analytics/lead-time?product={Product}&{window}");
        Assert.Equal(["dev", "prod"], Strings(lead.GetProperty("byEnvironment"), "environment"));
    }

    // ── Factory ─────────────────────────────────────────────────────────────

    public class Factory : TestFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);

            builder.UseSetting("Deployments:ApiKeys:0:Name", "test-key");
            builder.UseSetting("Deployments:ApiKeys:0:Key", TestApiKey);
        }
    }
}
