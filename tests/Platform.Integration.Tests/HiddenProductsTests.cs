using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;

namespace Platform.Integration.Tests;

/// <summary>
/// The global product filter: a product a user hides must disappear from every list they see,
/// across features, and must follow them rather than their browser.
///
/// <para>The point of exercising it end to end is that enforcement is server-side. A test that
/// asserted a page component filtered its own array would prove nothing about the endpoints the
/// rest of the app calls — the reason the filter lives in the API is precisely that no page can
/// then forget to apply it.</para>
/// </summary>
public class HiddenProductsTests : IClassFixture<HiddenProductsTests.HiddenFactory>, IDisposable
{
    private const string TestApiKey = "test-hidden-key-12345";

    private readonly HiddenFactory _factory;
    private readonly HttpClient _apiKeyClient;
    private readonly HttpClient _adminClient;

    public HiddenProductsTests(HiddenFactory factory)
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

    private async Task IngestAsync(string product, string service = "api", string env = "staging",
        string version = "v1.0.0")
    {
        var res = await _apiKeyClient.PostAsJsonAsync("/api/deployments/events", new
        {
            product,
            service,
            environment = env,
            version,
            source = "ci",
            deployedAt = DateTimeOffset.UtcNow,
            status = "succeeded",
        });
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
    }

    private async Task HideAsync(params string[] products)
    {
        var res = await _adminClient.PutAsJsonAsync("/api/me/preferences/hidden-products",
            new { products });
        res.EnsureSuccessStatusCode();
    }

    /// <summary>Product names from the overview matrix. The endpoint returns a bare array.</summary>
    private static async Task<List<string>> ProductNamesAsync(HttpClient client)
    {
        var body = await client.GetFromJsonAsync<JsonElement>("/api/deployments/products");
        return body.EnumerateArray().Select(p => p.GetProperty("product").GetString()!).ToList();
    }

    private Task<List<string>> ProductNamesAsync() => ProductNamesAsync(_adminClient);

    // ── Tests ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task HiddenProduct_DisappearsFromTheProductMatrix_AndComesBack()
    {
        var keep = $"keep-{Guid.NewGuid():N}"[..14];
        var hide = $"hide-{Guid.NewGuid():N}"[..14];
        await IngestAsync(keep);
        await IngestAsync(hide);

        Assert.Contains(hide, await ProductNamesAsync());

        await HideAsync(hide);
        var afterHide = await ProductNamesAsync();
        Assert.DoesNotContain(hide, afterHide);
        Assert.Contains(keep, afterHide);

        await HideAsync();
        Assert.Contains(hide, await ProductNamesAsync());
    }

    /// <summary>
    /// The preference is per-person, so hiding something must not change what anybody else sees.
    /// This is the check that would fail if the filter were ever stored globally by mistake.
    /// </summary>
    [Fact]
    public async Task HiddenProduct_IsHiddenOnlyForTheUserWhoHidIt()
    {
        var hide = $"mine-{Guid.NewGuid():N}"[..14];
        await IngestAsync(hide);

        await HideAsync(hide);
        Assert.DoesNotContain(hide, await ProductNamesAsync());

        using var other = _factory.CreateAuthenticatedClient("qa@localhost", "qa123");
        Assert.Contains(hide, await ProductNamesAsync(other));

        await HideAsync();
    }

    /// <summary>
    /// The control that manages the filter is the one surface that has to keep seeing hidden
    /// products — otherwise there is no way to un-hide one, and the setting becomes a trap.
    /// </summary>
    [Fact]
    public async Task PreferencesProductList_StillShowsHiddenProducts()
    {
        var hide = $"trap-{Guid.NewGuid():N}"[..14];
        await IngestAsync(hide);
        await HideAsync(hide);

        var body = await _adminClient.GetFromJsonAsync<JsonElement>("/api/me/preferences/products");
        var all = body.GetProperty("products").EnumerateArray().Select(p => p.GetString()!).ToList();
        var hidden = body.GetProperty("hiddenProducts").EnumerateArray().Select(p => p.GetString()!).ToList();

        Assert.Contains(hide, all);
        Assert.Contains(hide, hidden);

        await HideAsync();
    }

    /// <summary>
    /// Ingest authenticates with an API key and has no human behind it. A display preference must
    /// never reach that path — if it did, hiding a product would start silently dropping its
    /// deployments instead of merely not showing them to one person.
    /// </summary>
    [Fact]
    public async Task ApiKeyCallers_AreUnaffectedByAnyUsersPreference()
    {
        var hide = $"ingest-{Guid.NewGuid():N}"[..14];
        await IngestAsync(hide);
        await HideAsync(hide);

        // Still ingestable, and still there for anyone who hasn't hidden it.
        await IngestAsync(hide, version: "v2.0.0");

        using var other = _factory.CreateAuthenticatedClient("qa@localhost", "qa123");
        Assert.Contains(hide, await ProductNamesAsync(other));

        await HideAsync();
    }

    [Fact]
    public async Task HiddenProduct_DisappearsFromPromotionsAndReleaseNotes()
    {
        var hide = $"multi-{Guid.NewGuid():N}"[..14];

        // A promotion needs a policy on the edge and a succeeded source deploy.
        await _adminClient.PutAsJsonAsync("/api/features/features.promotions", new { enabled = true });
        await _adminClient.PostAsJsonAsync("/api/promotions/admin/policies", new
        {
            product = hide,
            service = (string?)null,
            sourceEnv = "staging",
            targetEnv = "prod",
            steps = Array.Empty<object>(),
            escalationGroup = (string?)null,
        });
        await IngestAsync(hide, env: "staging", version: "v3.0.0");

        var created = await _apiKeyClient.PostAsJsonAsync("/api/promotions", new
        {
            product = hide,
            service = "api",
            sourceEnv = "staging",
            targetEnv = "prod",
            version = "v3.0.0",
            references = Array.Empty<object>(),
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        List<string> PromotionProducts(JsonElement body) =>
            body.GetProperty("candidates").EnumerateArray()
                .Select(c => c.GetProperty("product").GetString()!)
                .ToList();

        var before = await _adminClient.GetFromJsonAsync<JsonElement>("/api/promotions/");
        Assert.Contains(hide, PromotionProducts(before));

        await HideAsync(hide);

        // Unfiltered "all statuses" query — the case called out explicitly: hidden means hidden even
        // when the user asks for everything.
        var after = await _adminClient.GetFromJsonAsync<JsonElement>("/api/promotions/");
        Assert.DoesNotContain(hide, PromotionProducts(after));

        var notes = await _adminClient.GetFromJsonAsync<JsonElement>("/api/release-notes/");
        var noteProducts = notes.GetProperty("items").EnumerateArray()
            .Select(n => n.GetProperty("product").GetString()!)
            .ToList();
        Assert.DoesNotContain(hide, noteProducts);

        await HideAsync();
    }

    // ── Factory ─────────────────────────────────────────────────────────────

    public class HiddenFactory : TestFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("Deployments:ApiKeys:0:Name", "test-key");
            builder.UseSetting("Deployments:ApiKeys:0:Key", TestApiKey);
        }
    }
}
