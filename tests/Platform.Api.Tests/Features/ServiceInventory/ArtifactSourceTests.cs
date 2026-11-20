using System.Net;
using System.Net.Http.Headers;
using Platform.Api.Features.ServiceInventory;
using Platform.Api.Features.ServiceInventory.Http;
using Platform.Api.Features.ServiceInventory.Sources;

namespace Platform.Api.Tests.Features.ServiceInventory;

public class HttpsArtifactSourceTests
{
    private static readonly Uri Url = new("https://files.example.com/cost/latest.json");

    private static HttpsArtifactSource Source(RecordingHandler handler, InventoryCredentialOptions? credential = null,
        string? template = "https://files.example.com/cost/{month}.json") =>
        new(Http.Client(handler, Http.Options("files.example.com")), Url, template, credential);

    [Fact]
    public async Task An_etag_is_sent_back_and_304_means_unchanged()
    {
        var handler = new RecordingHandler(r =>
        {
            if (r.Headers.IfNoneMatch.Any(t => t.Tag == "\"v1\"")) return Http.Status(HttpStatusCode.NotModified);
            var response = Http.Json(Fixture.Bytes("cost-2026-11.json"));
            response.Headers.ETag = new EntityTagHeaderValue("\"v1\"");
            return response;
        });
        var source = Source(handler);

        var first = await source.FetchIfChangedAsync(null, default);
        var second = await source.FetchIfChangedAsync(first.Version, default);

        Assert.True(first.Changed);
        Assert.Equal("etag:\"v1\"", first.Version);
        Assert.False(second.Changed);
        Assert.Equal("\"v1\"", handler.Requests[1].Header("If-None-Match"));
    }

    [Fact]
    public async Task Without_an_etag_last_modified_is_used()
    {
        var modified = new DateTimeOffset(2026, 11, 20, 15, 0, 0, TimeSpan.Zero);
        var handler = new RecordingHandler(r =>
        {
            if (r.Headers.IfModifiedSince == modified) return Http.Status(HttpStatusCode.NotModified);
            var response = Http.Json(Fixture.Bytes("cost-2026-11.json"));
            response.Content.Headers.LastModified = modified;
            return response;
        });
        var source = Source(handler);

        var first = await source.FetchIfChangedAsync(null, default);
        var second = await source.FetchIfChangedAsync(first.Version, default);

        Assert.StartsWith("last-modified:", first.Version);
        Assert.False(second.Changed);
    }

    [Fact]
    public async Task Without_validators_the_body_hash_decides_and_unchanged_content_is_unchanged()
    {
        var handler = new RecordingHandler(_ => Http.Json(Fixture.Bytes("cost-2026-11.json")));
        var source = Source(handler);

        var first = await source.FetchIfChangedAsync(null, default);
        var second = await source.FetchIfChangedAsync(first.Version, default);

        Assert.StartsWith("sha256:", first.Version);
        Assert.True(first.Changed);
        Assert.False(second.Changed);
    }

    [Fact]
    public async Task A_query_credential_is_appended_to_the_request_and_kept_out_of_the_location()
    {
        var handler = new RecordingHandler(_ => Http.Json(Fixture.Bytes("cost-2026-11.json")));
        var source = Source(handler, new InventoryCredentialOptions { Kind = "query", Value = "sv=1&sig=SECRETSIGNATURE" });

        var result = await source.FetchIfChangedAsync(null, default);

        Assert.Contains("sig=SECRETSIGNATURE", handler.Requests[0].Uri.Query);
        Assert.DoesNotContain("SECRET", result.File!.Location);
        Assert.Equal("https://files.example.com/cost/latest.json", result.File.Location);
    }

    [Fact]
    public async Task Closed_months_are_every_unstored_month_from_the_start_up_to_last_month()
    {
        var source = Source(new RecordingHandler(_ => Http.Json("{}")));
        var stored = new HashSet<string> { "2026-09" };

        var months = await source.ListClosedMonthsAsync("2026-08", stored, new DateTimeOffset(2026, 11, 20, 0, 0, 0, TimeSpan.Zero), default);

        Assert.Equal(["2026-08", "2026-10"], months);
        Assert.Equal(new Uri("https://files.example.com/cost/2026-10.json"), new Uri(((HttpsArtifactSource)source.ForMonth("2026-10")).Location));
    }
}

public class OciArtifactSourceTests
{
    private const string Registry = "registry.example.com";
    private static readonly byte[] Catalog = Fixture.Bytes("catalog.json");
    private static readonly string LayerDigest = ContentHash.Sha256(Catalog);
    private const string ManifestDigest = "sha256:1111111111111111111111111111111111111111111111111111111111111111";

    private static readonly InventoryCredentialOptions Credential = new() { Kind = "basic", Username = "reader", Password = "pw" };

    /// <summary>
    /// A registry that answers like the real protocol: 401 with a Bearer challenge without a token,
    /// a token for Basic credentials at its realm, the manifest and a blob that redirects to storage.
    /// </summary>
    private static RecordingHandler Registry_(string manifestDigest = ManifestDigest, byte[]? blob = null, bool blobRedirect = true)
    {
        return new RecordingHandler(r =>
        {
            var uri = r.RequestUri!;
            if (uri.Host == "blobstore.example.org") return Http.Json(blob ?? Catalog, "application/octet-stream");
            if (uri.AbsolutePath == "/oauth2/token")
                return r.Headers.Authorization?.Scheme == "Basic" ? Http.Json(Fixture.Bytes("oci-token.json")) : Http.Status(HttpStatusCode.Unauthorized);
            if (r.Headers.Authorization?.Parameter != "fixture-access-token-not-a-secret")
            {
                var challenge = Http.Status(HttpStatusCode.Unauthorized);
                challenge.Headers.WwwAuthenticate.ParseAdd($"Bearer realm=\"https://{Registry}/oauth2/token\",service=\"{Registry}\"");
                return challenge;
            }
            if (uri.AbsolutePath.Contains("/manifests/"))
            {
                var response = r.Method == HttpMethod.Head ? Http.Status(HttpStatusCode.OK)
                    : Http.Json(Fixture.Bytes("oci-manifest-catalog.json"), "application/vnd.oci.image.manifest.v1+json");
                response.Headers.Add("Docker-Content-Digest", manifestDigest);
                return response;
            }
            if (uri.AbsolutePath.Contains("/blobs/"))
                return blobRedirect ? Http.Redirect("https://blobstore.example.org/data?sig=storage-signature")
                    : Http.Json(blob ?? Catalog, "application/octet-stream");
            if (uri.AbsolutePath.EndsWith("/tags/list"))
            {
                if (uri.Query.Contains("last=")) return Http.Json(Fixture.Bytes("oci-tags-page2.json"));
                var page = Http.Json(Fixture.Bytes("oci-tags-page1.json"));
                page.Headers.Add("Link", "</v2/service-cost/allocation/tags/list?n=1000&last=2026-11>; rel=\"next\"");
                return page;
            }
            return Http.Status(HttpStatusCode.NotFound);
        });
    }

    private static OciArtifactSource Source(RecordingHandler handler, OciTokenCache? tokens = null, string repository = "service-catalog/catalog") =>
        new(Http.Client(handler, Http.Options(Registry)), tokens ?? new OciTokenCache(),
            new ManualClock(new DateTimeOffset(2026, 11, 20, 16, 0, 0, TimeSpan.Zero)),
            Registry, repository, "latest", "catalog.json", Credential);

    [Fact]
    public async Task Challenge_token_manifest_and_blob_give_the_file()
    {
        var handler = Registry_();

        var result = await Source(handler).FetchIfChangedAsync(null, default);

        Assert.True(result.Changed);
        Assert.Equal(Catalog, result.File!.Content);
        Assert.Equal(ManifestDigest, result.Version);
        Assert.Equal("registry.example.com/service-catalog/catalog:latest", result.File.Location);
        var token = handler.Requests.Single(r => r.Uri.AbsolutePath == "/oauth2/token");
        Assert.Contains("scope=repository%3Aservice-catalog%2Fcatalog%3Apull", token.Uri.Query);
        Assert.StartsWith("Basic ", token.Header("Authorization"));
    }

    [Fact]
    public async Task An_unchanged_manifest_digest_downloads_nothing()
    {
        var handler = Registry_();

        var result = await Source(handler).FetchIfChangedAsync(ManifestDigest, default);

        Assert.False(result.Changed);
        Assert.DoesNotContain(handler.Requests, r => r.Method == HttpMethod.Get && r.Uri.AbsolutePath.Contains("/manifests/"));
        Assert.DoesNotContain(handler.Requests, r => r.Uri.AbsolutePath.Contains("/blobs/"));
    }

    [Fact]
    public async Task The_blob_redirect_is_followed_without_any_credential()
    {
        var handler = Registry_();

        await Source(handler).FetchIfChangedAsync(null, default);

        var storage = handler.Requests.Single(r => r.Uri.Host == "blobstore.example.org");
        Assert.Null(storage.Header("Authorization"));
    }

    [Fact]
    public async Task A_blob_that_does_not_match_its_digest_is_rejected()
    {
        var handler = Registry_(blob: "{\"tampered\":true}"u8.ToArray());

        var ex = await Assert.ThrowsAsync<InventoryFetchException>(() => Source(handler).FetchIfChangedAsync(null, default));
        Assert.Contains("does not match its digest", ex.Message);
    }

    [Fact]
    public async Task A_manifest_without_the_named_layer_is_an_error()
    {
        var handler = Registry_();
        var source = new OciArtifactSource(Http.Client(handler, Http.Options(Registry)), new OciTokenCache(),
            TimeProvider.System, Registry, "service-catalog/catalog", "latest", "status.json", Credential);

        var ex = await Assert.ThrowsAsync<InventoryFetchException>(() => source.FetchIfChangedAsync(null, default));
        Assert.Contains("no layer titled status.json", ex.Message);
    }

    [Fact]
    public async Task A_token_realm_off_the_allowlist_is_refused()
    {
        var handler = new RecordingHandler(_ =>
        {
            var challenge = Http.Status(HttpStatusCode.Unauthorized);
            challenge.Headers.WwwAuthenticate.ParseAdd("Bearer realm=\"https://auth.example.net/token\",service=\"x\"");
            return challenge;
        });

        var ex = await Assert.ThrowsAsync<InventoryFetchException>(() => Source(handler).FetchIfChangedAsync(null, default));
        Assert.Contains("auth.example.net", ex.Message);
        Assert.DoesNotContain(handler.Requests, r => r.Uri.Host == "auth.example.net");
    }

    [Fact]
    public async Task The_token_is_reused_while_it_is_valid()
    {
        var tokens = new OciTokenCache();
        var handler = Registry_();

        await Source(handler, tokens).FetchIfChangedAsync(null, default);
        await Source(handler, tokens).FetchIfChangedAsync(ManifestDigest, default);

        Assert.Single(handler.Requests, r => r.Uri.AbsolutePath == "/oauth2/token");
    }

    [Fact]
    public async Task Closed_months_come_from_the_tag_list_across_pages_filtered_and_without_stored_months()
    {
        var handler = Registry_();
        var source = Source(handler, repository: "service-cost/allocation");

        var months = await source.ListClosedMonthsAsync("2026-08", new HashSet<string> { "2026-09" },
            new DateTimeOffset(2026, 11, 20, 0, 0, 0, TimeSpan.Zero), default);

        // latest, not-a-month, before From (2025-12), stored (2026-09) and the open month (2026-11) are out.
        Assert.Equal(["2026-08", "2026-10"], months);
    }
}
