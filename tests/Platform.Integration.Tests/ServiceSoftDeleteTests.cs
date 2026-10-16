using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;

namespace Platform.Integration.Tests;

/// <summary>
/// Retiring an obsolete service, end to end.
///
/// <para>Exercised over HTTP for the reason the hidden-products suite spells out: the filter lives in
/// the API precisely so no page can forget it. Asserting that the deployment matrix endpoint and the
/// promotions endpoint both stop returning the service is the only check that proves the feature —
/// a component test over a local array would prove nothing about either.</para>
///
/// <para>The other half is the gate. Retiring a service changes what a whole team sees, so the
/// non-admin case is as much a requirement as the admin one.</para>
/// </summary>
public class ServiceSoftDeleteTests : IClassFixture<ServiceSoftDeleteTests.SoftDeleteFactory>, IDisposable
{
    private const string TestApiKey = "test-soft-delete-key-12345";

    private readonly SoftDeleteFactory _factory;
    private readonly HttpClient _apiKeyClient;
    private readonly HttpClient _adminClient;

    public ServiceSoftDeleteTests(SoftDeleteFactory factory)
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

    // ── Helpers ─────────────────────────────────────────────────────────────

    private async Task IngestAsync(
        string product, string service, string env = "staging", string version = "v1.0.0",
        DateTimeOffset? deployedAt = null)
    {
        var res = await _apiKeyClient.PostAsJsonAsync("/api/deployments/events", new
        {
            product,
            service,
            environment = env,
            version,
            source = "ci",
            deployedAt = deployedAt ?? DateTimeOffset.UtcNow,
            status = "succeeded",
        });
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
    }

    private async Task<List<string>> MatrixServicesAsync(string product)
    {
        var body = await _adminClient.GetFromJsonAsync<JsonElement>($"/api/deployments/state?product={product}");
        return body.EnumerateArray().Select(s => s.GetProperty("service").GetString()!).ToList();
    }

    private Task<HttpResponseMessage> RemoveAsync(string product, string service, string? reason = null) =>
        _adminClient.PostAsJsonAsync("/api/deployments/admin/deleted-services",
            new { product, service, reason });

    private Task<HttpResponseMessage> RestoreAsync(string product, string service) =>
        _adminClient.DeleteAsync(
            $"/api/deployments/admin/deleted-services?product={product}&serviceName={service}");

    // ── Tests ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task RemovedService_DisappearsFromTheMatrix_AndCanBeRestored()
    {
        var product = $"sd-{Guid.NewGuid():N}"[..12];
        await IngestAsync(product, "legacy-api");
        await IngestAsync(product, "billing-api");

        Assert.Contains("legacy-api", await MatrixServicesAsync(product));

        var removed = await RemoveAsync(product, "legacy-api", "migrated to billing-api");
        removed.EnsureSuccessStatusCode();
        var result = await removed.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, result.GetProperty("hiddenDeployments").GetInt32());

        var after = await MatrixServicesAsync(product);
        Assert.DoesNotContain("legacy-api", after);
        Assert.Contains("billing-api", after);

        // It is visible on the restore list — the only place it can be, since it is hidden everywhere else.
        var listed = await _adminClient.GetFromJsonAsync<JsonElement>(
            $"/api/deployments/admin/deleted-services?product={product}");
        var entry = Assert.Single(listed.EnumerateArray());
        Assert.Equal("legacy-api", entry.GetProperty("service").GetString());
        Assert.Equal("migrated to billing-api", entry.GetProperty("reason").GetString());

        (await RestoreAsync(product, "legacy-api")).EnsureSuccessStatusCode();
        Assert.Contains("legacy-api", await MatrixServicesAsync(product));
    }

    /// <summary>
    /// "…until a new deployment is sent with it." The pipeline is the authority on whether a service
    /// is still alive, so a deploy that arrives after the retirement undoes it without anybody having
    /// to notice or intervene.
    /// </summary>
    [Fact]
    public async Task ANewDeployment_BringsTheServiceBackByItself()
    {
        var product = $"sd-{Guid.NewGuid():N}"[..12];
        await IngestAsync(product, "legacy-api", deployedAt: DateTimeOffset.UtcNow.AddHours(-1));

        (await RemoveAsync(product, "legacy-api")).EnsureSuccessStatusCode();
        Assert.DoesNotContain("legacy-api", await MatrixServicesAsync(product));

        await IngestAsync(product, "legacy-api", version: "v2.0.0", deployedAt: DateTimeOffset.UtcNow.AddMinutes(5));

        Assert.Contains("legacy-api", await MatrixServicesAsync(product));
        var listed = await _adminClient.GetFromJsonAsync<JsonElement>(
            $"/api/deployments/admin/deleted-services?product={product}");
        Assert.Empty(listed.EnumerateArray());
    }

    [Fact]
    public async Task RemovedService_DisappearsFromPromotions()
    {
        var product = $"sd-{Guid.NewGuid():N}"[..12];

        await _adminClient.PutAsJsonAsync("/api/features/features.promotions", new { enabled = true });
        await _adminClient.PostAsJsonAsync("/api/promotions/admin/policies", new
        {
            product,
            service = (string?)null,
            sourceEnv = "staging",
            targetEnv = "prod",
            steps = Array.Empty<object>(),
            escalationGroup = (string?)null,
        });
        await IngestAsync(product, "legacy-api", env: "staging", version: "v3.0.0");

        var created = await _apiKeyClient.PostAsJsonAsync("/api/promotions", new
        {
            product,
            service = "legacy-api",
            sourceEnv = "staging",
            targetEnv = "prod",
            version = "v3.0.0",
            references = Array.Empty<object>(),
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        async Task<List<string>> PromotionServicesAsync()
        {
            var body = await _adminClient.GetFromJsonAsync<JsonElement>($"/api/promotions/?product={product}");
            return body.GetProperty("candidates").EnumerateArray()
                .Select(c => c.GetProperty("service").GetString()!)
                .ToList();
        }

        Assert.Contains("legacy-api", await PromotionServicesAsync());

        var removed = await RemoveAsync(product, "legacy-api");
        removed.EnsureSuccessStatusCode();
        // The open promotion is part of what the admin is told they just hid.
        var result = await removed.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, result.GetProperty("hiddenOpenPromotions").GetInt32());

        Assert.DoesNotContain("legacy-api", await PromotionServicesAsync());

        // Restoring brings the promotion back too — the candidate was never touched.
        (await RestoreAsync(product, "legacy-api")).EnsureSuccessStatusCode();
        Assert.Contains("legacy-api", await PromotionServicesAsync());
    }

    /// <summary>
    /// Retiring a service changes what an entire team sees. A signed-in non-admin — an approver, the
    /// role most likely to be looking at these pages — must not be able to do it.
    /// </summary>
    [Fact]
    public async Task NonAdmins_CannotRemoveOrRestoreAService()
    {
        var product = $"sd-{Guid.NewGuid():N}"[..12];
        await IngestAsync(product, "legacy-api");

        using var qa = _factory.CreateAuthenticatedClient("qa@localhost", "qa123");

        var attempt = await qa.PostAsJsonAsync("/api/deployments/admin/deleted-services",
            new { product, service = "legacy-api", reason = (string?)null });
        Assert.Equal(HttpStatusCode.Forbidden, attempt.StatusCode);

        var read = await qa.GetAsync($"/api/deployments/admin/deleted-services?product={product}");
        Assert.Equal(HttpStatusCode.Forbidden, read.StatusCode);

        // And it really wasn't removed.
        Assert.Contains("legacy-api", await MatrixServicesAsync(product));

        (await RemoveAsync(product, "legacy-api")).EnsureSuccessStatusCode();
        var undo = await qa.DeleteAsync(
            $"/api/deployments/admin/deleted-services?product={product}&serviceName=legacy-api");
        Assert.Equal(HttpStatusCode.Forbidden, undo.StatusCode);

        (await RestoreAsync(product, "legacy-api")).EnsureSuccessStatusCode();
    }

    /// <summary>
    /// A name that was never deployed is a typo. Storing it would hide nothing while reporting
    /// success, and the admin would go looking for a service that is still sitting in the matrix.
    /// </summary>
    [Fact]
    public async Task RemovingAServiceThatNeverDeployed_Is404()
    {
        var product = $"sd-{Guid.NewGuid():N}"[..12];
        await IngestAsync(product, "billing-api");

        var res = await RemoveAsync(product, "biling-api");
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task RestoringAServiceThatWasNotRemoved_Is404()
    {
        var product = $"sd-{Guid.NewGuid():N}"[..12];
        await IngestAsync(product, "billing-api");

        var res = await RestoreAsync(product, "billing-api");
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    // ── Factory ─────────────────────────────────────────────────────────────

    public class SoftDeleteFactory : TestFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("Deployments:ApiKeys:0:Name", "test-key");
            builder.UseSetting("Deployments:ApiKeys:0:Key", TestApiKey);
        }
    }
}
