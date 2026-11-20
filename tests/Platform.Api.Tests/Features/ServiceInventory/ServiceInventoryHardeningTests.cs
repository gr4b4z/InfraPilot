using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.Api.Features.ServiceInventory;
using Platform.Api.Features.ServiceInventory.Documents;
using Platform.Api.Features.ServiceInventory.Http;
using Platform.Api.Features.ServiceInventory.Models;
using Platform.Api.Features.ServiceInventory.Sources;

namespace Platform.Api.Tests.Features.ServiceInventory;

/// <summary>Malformed files are rejected whole instead of being stored and failing later.</summary>
public class DocumentHardeningTests
{
    private static byte[] Edit(string fixture, Action<JsonNode> change)
    {
        var node = JsonNode.Parse(Fixture.Bytes(fixture))!;
        change(node);
        return Encoding.UTF8.GetBytes(node.ToJsonString());
    }

    [Theory]
    [InlineData("services")]
    [InlineData("teams")]
    public void A_null_list_in_the_catalog_is_rejected(string field)
    {
        var result = DocumentReader.Read("catalog", Edit("catalog.json", n => n[field] = null));
        Assert.False(result.Ok);
    }

    [Fact]
    public void A_null_entry_in_a_list_is_rejected()
    {
        Assert.False(DocumentReader.Read("catalog", Edit("catalog.json", n => n["services"]!.AsArray().Add(null))).Ok);
        Assert.False(DocumentReader.Read("status", Edit("status.json", n => n["services"]!.AsArray().Add(null))).Ok);
        Assert.False(DocumentReader.Read("cost", Edit("cost-2026-11.json", n => n["services"]!.AsArray().Add(null))).Ok);
    }

    [Theory]
    [InlineData("catalog", "catalog.json")]
    [InlineData("status", "status.json")]
    public void A_file_without_services_is_rejected_but_an_empty_list_is_accepted(string type, string fixture)
    {
        var missing = DocumentReader.Read(type, Edit(fixture, n => n.AsObject().Remove("services")));
        var empty = DocumentReader.Read(type, Edit(fixture, n => n["services"] = new JsonArray()));

        Assert.False(missing.Ok);
        Assert.Contains("no services list", missing.Error);
        Assert.True(empty.Ok, empty.Error);
    }

    [Fact]
    public void A_null_service_id_is_rejected()
    {
        Assert.False(DocumentReader.Read("status", Edit("status.json", n => n["services"]![0]!["id"] = null)).Ok);
    }

    [Fact]
    public void A_database_binding_that_does_not_match_the_format_rejects_the_catalog()
    {
        var bad = Edit("catalog.json", n =>
            n["services"]![1]!["bindings"]!["postgres"]![0]!["owner"] = "true");

        var result = DocumentReader.Read("catalog", bad);

        Assert.False(result.Ok);
        Assert.Contains("does not match the format", result.Error);
    }

    [Theory]
    [InlineData("\"1\"")]
    [InlineData("null")]
    [InlineData("1.5")]
    public void A_schema_version_that_is_not_an_integer_is_rejected(string value)
    {
        var json = Encoding.UTF8.GetString(Fixture.Bytes("catalog.json"))
            .Replace("\"schemaVersion\": 1,", $"\"schemaVersion\": {value},");

        var result = DocumentReader.Read("catalog", Encoding.UTF8.GetBytes(json));

        Assert.False(result.Ok);
        Assert.Contains("schemaVersion", result.Error);
    }
}

public class OutboundHardeningTests
{
    [Fact]
    public void A_url_with_a_user_name_or_password_is_rejected_and_never_shown()
    {
        var options = new ServiceInventoryOptions
        {
            AllowedHosts = ["files.example.com"],
            Feeds = [new() { Name = "catalog", Type = "catalog", Source = new() { Kind = "https", Url = "https://reader:password@files.example.com/feed.json" } }],
        };
        Assert.Contains(ServiceInventoryOptionsValidator.Validate(options), e => e.Contains("user name or password"));

        var source = new HttpsArtifactSource(Http.Client(new RecordingHandler(_ => Http.Json("{}")), Http.Options("files.example.com")),
            new Uri("https://reader:password@files.example.com/feed.json"), null, null);
        Assert.Equal("https://files.example.com/feed.json", source.Location);
    }

    [Fact]
    public async Task A_connection_lost_while_reading_the_body_is_a_recorded_fetch_error()
    {
        var handler = new RecordingHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new BrokenStream()) };
            response.Content.Headers.ContentType = new("application/json");
            return response;
        });
        var client = Http.Client(handler, Http.Options("files.example.com"));

        var ex = await Assert.ThrowsAsync<InventoryFetchException>(() =>
            client.SendAsync(new InventoryRequest(HttpMethod.Get, new Uri("https://files.example.com/x.json")) { ExpectJson = true }, default));
        Assert.Contains("interrupted", ex.Message);
    }

    private sealed class BrokenStream : MemoryStream
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            throw new IOException("connection reset by peer");
        public override int Read(byte[] buffer, int offset, int count) => throw new IOException("connection reset by peer");
    }

    private const string Registry = "registry.example.com";

    private static RecordingHandler ChallengeFrom(string realmHost) => new(r =>
    {
        if (r.RequestUri!.AbsolutePath == "/token") return Http.Json("""{"access_token":"t","expires_in":300}""");
        if (r.Headers.Authorization?.Parameter == "t")
        {
            var ok = Http.Status(HttpStatusCode.OK);
            ok.Headers.Add("Docker-Content-Digest", "sha256:abc");
            return ok;
        }
        var challenge = Http.Status(HttpStatusCode.Unauthorized);
        challenge.Headers.WwwAuthenticate.ParseAdd($"Bearer realm=\"https://{realmHost}/token\",service=\"{Registry}\"");
        return challenge;
    });

    private static OciArtifactSource Oci(RecordingHandler handler, string? authHost) =>
        new(Http.Client(handler, Http.Options(Registry, "files.example.com", "auth.example.com")), new OciTokenCache(),
            TimeProvider.System, Registry, "service-catalog/catalog", "latest", "catalog.json",
            new InventoryCredentialOptions { Kind = "basic", Username = "reader", Password = "registry-password" }, authHost);

    [Fact]
    public async Task Registry_credentials_are_never_sent_to_another_allowed_host_named_by_the_challenge()
    {
        // files.example.com is on the allowlist for another feed; a challenge naming it gets nothing.
        var handler = ChallengeFrom("files.example.com");

        var ex = await Assert.ThrowsAsync<InventoryFetchException>(() => Oci(handler, authHost: null).FetchIfChangedAsync("sha256:abc", default));

        Assert.Contains("set AuthHost", ex.Message);
        Assert.DoesNotContain(handler.Requests, r => r.Uri.Host == "files.example.com");
    }

    [Fact]
    public async Task A_token_service_on_its_own_host_is_used_when_configured_as_the_auth_host()
    {
        var handler = ChallengeFrom("auth.example.com");

        var result = await Oci(handler, authHost: "auth.example.com").FetchIfChangedAsync("sha256:abc", default);

        Assert.False(result.Changed);
        Assert.StartsWith("Basic ", handler.Requests.Single(r => r.Uri.Host == "auth.example.com").Header("Authorization"));
    }

    [Fact]
    public async Task The_stored_digest_is_the_downloaded_manifests_even_if_the_tag_moved_after_the_head()
    {
        var catalog = Fixture.Bytes("catalog.json");
        var handler = new RecordingHandler(r =>
        {
            var path = r.RequestUri!.AbsolutePath;
            if (path.Contains("/manifests/"))
            {
                var response = r.Method == HttpMethod.Head ? Http.Status(HttpStatusCode.OK)
                    : Http.Json(Fixture.Bytes("oci-manifest-catalog.json"), "application/vnd.oci.image.manifest.v1+json");
                response.Headers.Add("Docker-Content-Digest", r.Method == HttpMethod.Head ? "sha256:head" : "sha256:get");
                return response;
            }
            return Http.Json(catalog, "application/octet-stream");
        });
        var source = new OciArtifactSource(Http.Client(handler, Http.Options(Registry)), new OciTokenCache(), TimeProvider.System,
            Registry, "service-catalog/catalog", "latest", "catalog.json", null);

        var result = await source.FetchIfChangedAsync(null, default);

        Assert.Equal("sha256:get", result.Version);
    }

    private static OciArtifactSource Plain(RecordingHandler handler, string repository = "service-catalog/catalog", string file = "catalog.json") =>
        new(Http.Client(handler, Http.Options(Registry, "auth.example.com")), new OciTokenCache(), TimeProvider.System,
            Registry, repository, "latest", file, new InventoryCredentialOptions { Kind = "basic", Username = "u", Password = "p" },
            "auth.example.com");

    private static HttpResponseMessage Challenge()
    {
        var challenge = Http.Status(HttpStatusCode.Unauthorized);
        challenge.Headers.WwwAuthenticate.ParseAdd($"Bearer realm=\"https://auth.example.com/token\",service=\"{Registry}\"");
        return challenge;
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[1, 2]")]
    [InlineData("{\"tags\": 5}")]
    public async Task A_malformed_tag_list_is_a_fetch_error(string body)
    {
        var handler = new RecordingHandler(_ => Http.Json(body));

        await Assert.ThrowsAsync<InventoryFetchException>(() => Plain(handler, "service-cost/allocation", "cost.json")
            .ListClosedMonthsAsync("2026-01", new HashSet<string>(), new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero), default));
    }

    [Theory]
    [InlineData("{\"layers\":[{\"digest\":\"sha256:x\",\"annotations\":{\"org.opencontainers.image.title\":7}}]}")]
    [InlineData("{\"layers\":[7]}")]
    [InlineData("[]")]
    public async Task A_manifest_of_the_wrong_shape_is_a_fetch_error(string manifest)
    {
        var handler = new RecordingHandler(r =>
        {
            var response = r.Method == HttpMethod.Head ? Http.Status(HttpStatusCode.OK) : Http.Json(manifest, "application/vnd.oci.image.manifest.v1+json");
            response.Headers.Add("Docker-Content-Digest", "sha256:new");
            return response;
        });

        await Assert.ThrowsAsync<InventoryFetchException>(() => Plain(handler).FetchIfChangedAsync(null, default));
    }

    [Theory]
    [InlineData("{\"access_token\":123}")]
    [InlineData("[\"token\"]")]
    public async Task A_token_response_of_the_wrong_shape_is_a_fetch_error(string tokenBody)
    {
        var handler = new RecordingHandler(r => r.RequestUri!.Host == "auth.example.com" ? Http.Json(tokenBody) : Challenge());

        var ex = await Assert.ThrowsAsync<InventoryFetchException>(() => Plain(handler).FetchIfChangedAsync(null, default));
        Assert.Contains("returned no token", ex.Message);
    }

    [Fact]
    public async Task A_string_expiry_falls_back_to_a_default_lifetime_instead_of_failing()
    {
        var handler = new RecordingHandler(r =>
        {
            if (r.RequestUri!.Host == "auth.example.com") return Http.Json("{\"access_token\":\"t\",\"expires_in\":\"soon\"}");
            if (r.Headers.Authorization?.Parameter != "t") return Challenge();
            var ok = Http.Status(HttpStatusCode.OK);
            ok.Headers.Add("Docker-Content-Digest", "sha256:same");
            return ok;
        });

        var result = await Plain(handler).FetchIfChangedAsync("sha256:same", default);
        Assert.False(result.Changed);
    }

    [Fact]
    public async Task A_relative_next_link_is_resolved_against_the_page_just_read()
    {
        var handler = new RecordingHandler(r =>
        {
            if (r.RequestUri!.Query.Contains("last="))
                return Http.Json("""{"tags":["2026-08"]}""");
            var page = Http.Json("""{"tags":["2026-09"]}""");
            page.Headers.Add("Link", "<?n=1000&last=2026-09>; rel=\"next\"");
            return page;
        });
        var source = new OciArtifactSource(Http.Client(handler, Http.Options(Registry)), new OciTokenCache(), TimeProvider.System,
            Registry, "service-cost/allocation", "latest", "cost.json", null);

        var months = await source.ListClosedMonthsAsync("2026-01", new HashSet<string>(), new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero), default);

        Assert.Equal(["2026-08", "2026-09"], months);
        Assert.Equal("/v2/service-cost/allocation/tags/list", handler.Requests[1].Uri.AbsolutePath);
    }
}

public class RefreshHardeningTests
{
    private readonly Platform.Api.Infrastructure.Persistence.PlatformDbContext _db = Db.InMemory();
    private readonly ScriptedSource _source = new();
    private readonly ManualClock _clock = new(new DateTimeOffset(2026, 11, 20, 16, 0, 0, TimeSpan.Zero));

    private InventoryRefresher Refresher() => new(_db, new ScriptedFactory(_source), _clock, NullLogger<InventoryRefresher>.Instance);

    private static InventoryFeedOptions Feed(string type, ClosedMonthsOptions? closed = null) => new()
    {
        Name = type, Type = type, Source = new() { Kind = "https", Url = "https://test.example.com/feed", ClosedMonths = closed },
    };

    [Fact]
    public async Task A_feed_moved_to_another_source_never_sends_the_old_version()
    {
        _source.Next = _ => ScriptedSource.File(Fixture.Bytes("catalog.json"), "etag:\"v1\"");
        await Refresher().RefreshAsync(Feed("catalog"), default);

        _source.Identity = "https://other.example.com/catalog.json";
        _source.Next = v => v is null ? ScriptedSource.File(Fixture.Bytes("catalog.json"), "etag:\"v1\"") : FetchResult.Unchanged(v);
        await Refresher().RefreshAsync(Feed("catalog"), default);

        Assert.Equal([null, null], _source.AskedWith);
    }

    [Fact]
    public async Task The_feed_follows_the_latest_file_back_to_a_month_already_stored()
    {
        var october = Fixture.Bytes("cost-2026-10.json");
        _source.Next = _ => ScriptedSource.File(october, "a");
        await Refresher().RefreshAsync(Feed("cost"), default);
        _source.Next = _ => ScriptedSource.File(Fixture.Bytes("cost-2026-11.json"), "b");
        await Refresher().RefreshAsync(Feed("cost"), default);
        var octoberSnapshot = _db.InventoryFeeds.AsNoTracking().Single(f => f.FeedKey == "cost:2026-10").CurrentSnapshotId;

        _source.Next = _ => ScriptedSource.File(october, "c");
        await Refresher().RefreshAsync(Feed("cost"), default);

        Assert.Equal(octoberSnapshot, _db.InventoryFeeds.AsNoTracking().Single(f => f.FeedKey == "cost").CurrentSnapshotId);
    }

    [Fact]
    public async Task Unpublished_months_do_not_starve_later_published_ones()
    {
        // 14 candidate months; only the two oldest are unpublished-forever, the rest exist. With 12
        // per cycle, every month is tried within two cycles.
        var months = Enumerable.Range(0, 14).Select(i => new DateTime(2025, 9, 1).AddMonths(i).ToString("yyyy-MM")).ToList();
        _source.Next = _ => FetchResult.Unchanged(null);
        _source.ListedMonths = months;
        var tried = new List<string>();
        foreach (var month in months)
        {
            var m = month;
            _source.Months[m] = () =>
            {
                tried.Add(m);
                throw new InventoryFetchException("not published", HttpStatusCode.NotFound);
            };
        }
        var feed = Feed("cost", new ClosedMonthsOptions { From = months[0] });

        await Refresher().RefreshAsync(feed, default);
        _clock.Now = _clock.Now.AddHours(1);
        await Refresher().RefreshAsync(feed, default);

        Assert.Equal(months.Order(), tried.Distinct().Order());
    }
}

public class ReadServiceHardeningTests
{
    [Fact]
    public async Task Reads_go_through_the_configured_feed_not_an_old_row_of_the_same_type()
    {
        var db = Db.InMemory();
        db.InventoryFeeds.AddRange(
            new InventoryFeed { FeedKey = "catalog-old", FeedName = "catalog-old", FeedType = "catalog", LastSuccessAt = DateTimeOffset.UtcNow },
            new InventoryFeed { FeedKey = "catalog", FeedName = "catalog", FeedType = "catalog", LastSuccessAt = DateTimeOffset.UtcNow.AddDays(-1) });
        await db.SaveChangesAsync();
        var options = new ServiceInventoryOptions { Feeds = [new() { Name = "catalog", Type = "catalog" }] };

        var feed = await new InventoryReadService(db, new MemoryCache(new MemoryCacheOptions()), new StaticOptions<ServiceInventoryOptions>(options))
            .FeedAsync("catalog");

        Assert.Equal("catalog", feed!.FeedName);
    }
}
