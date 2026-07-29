using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;

namespace Platform.Integration.Tests;

/// <summary>
/// End-to-end coverage for the deployment detail surface: a pipeline posts an event carrying its CI
/// run and its captured output, and the portal serves both back — the run link and highlighted error
/// on the detail response, the log text through a separate per-block call.
/// </summary>
public class DeploymentDetailIntegrationTests
    : IClassFixture<DeploymentDetailIntegrationTests.DeployDetailFactory>, IDisposable
{
    private const string TestApiKey = "test-detail-key-12345";

    private const string JobUrl =
        "https://github.com/softwareone-platform/mpt-release/actions/runs/30464088990/job/90617803378";

    private readonly HttpClient _apiKeyClient;
    private readonly HttpClient _adminClient;

    public DeploymentDetailIntegrationTests(DeployDetailFactory factory)
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

    private const string HelmOutput = "Release \"api\" has been upgraded.\nSTATUS: failed\n";
    private const string Diagnostics = "=== Pods Status ===\n##[error]Helm deployment failed for api\n";

    private async Task<string> IngestFailedDeployAsync(string version, string deployedAt)
    {
        var response = await _apiKeyClient.PostAsJsonAsync("/api/deployments/events", new
        {
            product = "acme",
            service = "api",
            environment = "staging",
            version,
            source = "helm-deploy",
            deployedAt,
            status = "failed",
            references = new object[]
            {
                new
                {
                    type = "build-manifest",
                    url = "https://github.com/softwareone-platform/mpt-release/blob/abc123/staging/api/build-metadata.yaml",
                    provider = "github",
                    key = "api/build-metadata.yaml",
                },
            },
            run = new
            {
                provider = "github-actions",
                runId = "30464088990",
                runNumber = "294",
                attempt = 1,
                workflowName = "Reconcile staging",
                jobName = "Deploy Helm (api)",
                runUrl = "https://github.com/softwareone-platform/mpt-release/actions/runs/30464088990",
                jobUrl = JobUrl,
                triggeredBy = "plebann",
                failureReason = "pod api-x2klm keeps crash-looping (restartCount=4)",
            },
            logs = new object[]
            {
                new { name = "helm upgrade output", source = "helm", content = HelmOutput },
                new { name = "failure diagnostics", source = "kubectl", content = Diagnostics },
            },
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await Deserialize(response);
        return created.GetProperty("id").GetString()!;
    }

    [Fact]
    public async Task Detail_CarriesRunLinkFailureReasonAndLogSummaries()
    {
        var id = await IngestFailedDeployAsync("v1.0.0", "2026-04-16T10:00:00Z");

        var response = await _adminClient.GetAsync($"/api/deployments/events/{id}");
        response.EnsureSuccessStatusCode();
        var body = await Deserialize(response);

        var run = body.GetProperty("event").GetProperty("run");
        Assert.Equal(JobUrl, run.GetProperty("jobUrl").GetString());
        Assert.Equal("294", run.GetProperty("runNumber").GetString());
        Assert.Equal(
            "pod api-x2klm keeps crash-looping (restartCount=4)",
            run.GetProperty("failureReason").GetString());

        // The version's link to the release repository's manifest rides in as a reference.
        var manifest = body.GetProperty("event").GetProperty("references").EnumerateArray()
            .Single(r => r.GetProperty("type").GetString() == "build-manifest");
        Assert.Contains("build-metadata.yaml", manifest.GetProperty("url").GetString());

        var logs = body.GetProperty("logs").EnumerateArray().ToList();
        Assert.Equal(2, logs.Count);
        Assert.Equal("helm upgrade output", logs[0].GetProperty("name").GetString());
        Assert.Equal("failure diagnostics", logs[1].GetProperty("name").GetString());
        // Summaries are sized but carry no text — that's the whole point of the split.
        Assert.True(logs[0].GetProperty("byteCount").GetInt32() > 0);
        Assert.False(logs[0].TryGetProperty("content", out _));
    }

    [Fact]
    public async Task LogContent_IsFetchedPerBlock()
    {
        var id = await IngestFailedDeployAsync("v1.1.0", "2026-04-16T11:00:00Z");

        var detail = await Deserialize(await _adminClient.GetAsync($"/api/deployments/events/{id}"));
        var diagnosticsId = detail.GetProperty("logs").EnumerateArray()
            .Single(l => l.GetProperty("name").GetString() == "failure diagnostics")
            .GetProperty("id").GetString();

        var response = await _adminClient.GetAsync($"/api/deployments/events/{id}/logs/{diagnosticsId}");
        response.EnsureSuccessStatusCode();
        var body = await Deserialize(response);

        Assert.Equal(Diagnostics, body.GetProperty("content").GetString());
        Assert.False(body.GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public async Task Detail_UnknownEvent_Returns404()
    {
        var response = await _adminClient.GetAsync($"/api/deployments/events/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task LogContent_UnknownBlock_Returns404()
    {
        var id = await IngestFailedDeployAsync("v1.2.0", "2026-04-16T12:00:00Z");

        var response = await _adminClient.GetAsync($"/api/deployments/events/{id}/logs/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Ingest_RejectsAnUnnamedLogBlock()
    {
        var response = await _apiKeyClient.PostAsJsonAsync("/api/deployments/events", new
        {
            product = "acme",
            service = "api",
            environment = "staging",
            version = "v1.3.0",
            source = "helm-deploy",
            deployedAt = "2026-04-16T13:00:00Z",
            logs = new object[] { new { source = "helm", content = "orphaned output" } },
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await Deserialize(response);
        Assert.Contains(
            "'logs[0].name' is required",
            body.GetProperty("errors").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public async Task StateMatrix_CarriesTheEventIdSoTheUiCanLinkToDetail()
    {
        var id = await IngestFailedDeployAsync("v1.4.0", "2026-04-16T14:00:00Z");

        var body = await Deserialize(await _adminClient.GetAsync("/api/deployments/state?product=acme&serviceName=api"));
        var entry = body.EnumerateArray().Single(e => e.GetProperty("environment").GetString() == "staging");

        Assert.Equal(id, entry.GetProperty("id").GetString());
    }

    private static async Task<JsonElement> Deserialize(HttpResponseMessage response)
    {
        var stream = await response.Content.ReadAsStreamAsync();
        var doc = await JsonDocument.ParseAsync(stream);
        return doc.RootElement;
    }

    public class DeployDetailFactory : TestFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);

            builder.UseSetting("Deployments:ApiKeys:0:Name", "test-key");
            builder.UseSetting("Deployments:ApiKeys:0:Key", TestApiKey);
        }
    }
}
