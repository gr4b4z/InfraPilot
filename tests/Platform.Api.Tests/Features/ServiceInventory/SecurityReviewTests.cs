using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Platform.Api.Features.ServiceInventory;
using Platform.Api.Features.ServiceInventory.Documents;
using Platform.Api.Features.ServiceInventory.Http;
using Platform.Api.Features.ServiceInventory.Sources;

namespace Platform.Api.Tests.Features.ServiceInventory;

/// <summary>Findings of the security pass over the outbound side, each pinned by a test.</summary>
public class OutboundSecurityTests
{
    private static InventoryRequest Blob(RequestCredential? credential = null) =>
        new(HttpMethod.Get, new Uri("https://registry.example.com/v2/x/blobs/sha256:1"))
        {
            Credential = credential,
            ExpectJson = true,
            AllowOctetStream = true,
            AllowCredentiallessRedirectOffAllowlist = true,
        };

    [Fact]
    public async Task Another_port_on_an_allowed_host_is_not_allowed()
    {
        var handler = new RecordingHandler(_ => Http.Redirect("https://registry.example.com:8443/elsewhere"));
        var client = Http.Client(handler, Http.Options("registry.example.com"));

        var ex = await Assert.ThrowsAsync<InventoryFetchException>(() => client.SendAsync(
            new InventoryRequest(HttpMethod.Get, new Uri("https://registry.example.com/file.json")) { ExpectJson = true }, default));

        Assert.Contains("registry.example.com:8443", ex.Message);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task A_credential_is_not_sent_to_another_port_of_its_host_even_when_both_are_allowed()
    {
        var handler = new RecordingHandler(r => r.RequestUri!.Port == 443
            ? Http.Redirect("https://registry.example.com:8443/file.json")
            : Http.Json("{}"));
        var client = Http.Client(handler, Http.Options("registry.example.com", "registry.example.com:8443"));
        var credential = RequestCredential.Bearer("registry.example.com", "secret");

        await client.SendAsync(new InventoryRequest(HttpMethod.Get, new Uri("https://registry.example.com/file.json"))
            { Credential = credential, ExpectJson = true }, default);

        Assert.Equal("Bearer secret", handler.Requests[0].Header("Authorization"));
        Assert.Null(handler.Requests[1].Header("Authorization"));
    }

    [Fact]
    public async Task A_registry_on_its_own_port_works_when_listed_with_that_port()
    {
        var handler = new RecordingHandler(_ => Http.Json("{}"));
        var client = Http.Client(handler, Http.Options("Registry.Example.com:5000"));
        var credential = RequestCredential.Bearer("registry.example.com:5000", "secret");

        await client.SendAsync(new InventoryRequest(HttpMethod.Get, new Uri("https://registry.example.com:5000/v2/"))
            { Credential = credential, ExpectJson = true }, default);

        Assert.Equal("Bearer secret", handler.Requests[0].Header("Authorization"));
    }

    [Theory]
    [InlineData("https://10.0.0.5/blob")]
    [InlineData("https://169.254.169.254/latest/meta-data")]
    [InlineData("https://127.0.0.1:8443/admin")]
    [InlineData("https://[::1]/admin")]
    [InlineData("https://localhost/admin")]
    [InlineData("https://intranet/admin")]
    public async Task The_blob_redirect_cannot_point_at_an_internal_address(string target)
    {
        var handler = new RecordingHandler(_ => Http.Redirect(target));
        var client = Http.Client(handler, Http.Options("registry.example.com"));

        var ex = await Assert.ThrowsAsync<InventoryFetchException>(() => client.SendAsync(Blob(), default));

        Assert.Contains("internal address", ex.Message);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task The_blob_redirect_cannot_point_at_a_name_that_resolves_to_a_private_address()
    {
        var handler = new RecordingHandler(_ => Http.Redirect("https://storage.attacker.example/blob"));
        var client = Http.Client(handler, Http.Options("registry.example.com"), _ => [IPAddress.Parse("192.168.1.20")]);

        await Assert.ThrowsAsync<InventoryFetchException>(() => client.SendAsync(Blob(), default));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Private_storage_is_reached_by_listing_its_host()
    {
        var handler = new RecordingHandler(r => r.RequestUri!.Host == "registry.example.com"
            ? Http.Redirect("https://data.registry.example.com/blob")
            : Http.Json("{}", "application/octet-stream"));
        var client = Http.Client(handler, Http.Options("registry.example.com", "data.registry.example.com"),
            _ => [IPAddress.Parse("10.1.2.3")]);

        var response = await client.SendAsync(Blob(), default);

        Assert.Equal(HttpStatusCode.OK, response.Status);
    }

    [Theory]
    [InlineData("10.0.0.1", true)]
    [InlineData("172.16.0.1", true)]
    [InlineData("172.32.0.1", false)]
    [InlineData("192.168.0.1", true)]
    [InlineData("169.254.169.254", true)]
    [InlineData("100.64.0.1", true)]
    [InlineData("127.0.0.1", true)]
    [InlineData("0.0.0.0", true)]
    [InlineData("::1", true)]
    [InlineData("fe80::1", true)]
    [InlineData("fd00::1", true)]
    [InlineData("::ffff:10.0.0.1", true)]
    [InlineData("203.0.113.10", false)]
    [InlineData("2001:4860:4860::8888", false)]
    public void Internal_addresses_are_recognised(string address, bool expected)
    {
        Assert.Equal(expected, InventoryHttpClient.IsInternal(IPAddress.Parse(address)));
    }

    [Fact]
    public async Task An_error_never_repeats_the_reason_phrase_the_remote_side_chose()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.BadGateway)
            { ReasonPhrase = "text chosen by the remote side", Content = new ByteArrayContent([]) });
        var client = Http.Client(handler, Http.Options("files.example.com"));

        var ex = await Assert.ThrowsAsync<InventoryFetchException>(() => client.SendAsync(
            new InventoryRequest(HttpMethod.Get, new Uri("https://files.example.com/x.json")), default));

        Assert.DoesNotContain("chosen by the remote side", ex.Message);
        Assert.Contains("502", ex.Message);
    }
}

public class OciSecurityTests
{
    private const string Registry = "registry.example.com";

    private static OciArtifactSource Source(RecordingHandler handler, string repository = "service-catalog/catalog") =>
        new(Http.Client(handler, Http.Options(Registry, "files.example.com")), new OciTokenCache(), TimeProvider.System,
            Registry, repository, "latest", "catalog.json", null);

    [Theory]
    [InlineData("sha256:../../../other/blobs/sha256:abc")]
    [InlineData("sha256:abc?x=1")]
    [InlineData("sha256:0123")]
    [InlineData("sha256:ABCDEFABCDEFABCDEFABCDEFABCDEFABCDEFABCDEFABCDEFABCDEFABCDEFABCD")]
    public async Task A_layer_digest_that_is_not_exactly_a_sha256_is_never_put_into_a_url(string digest)
    {
        var manifest = new JsonObject
        {
            ["layers"] = new JsonArray(new JsonObject
            {
                ["digest"] = digest,
                ["annotations"] = new JsonObject { ["org.opencontainers.image.title"] = "catalog.json" },
            }),
        }.ToJsonString();
        var handler = new RecordingHandler(r =>
        {
            var response = r.Method == HttpMethod.Head ? Http.Status(HttpStatusCode.OK) : Http.Json(manifest, "application/vnd.oci.image.manifest.v1+json");
            response.Headers.Add("Docker-Content-Digest", "sha256:new");
            return response;
        });

        var ex = await Assert.ThrowsAsync<InventoryFetchException>(() => Source(handler).FetchIfChangedAsync(null, default));

        Assert.Contains("no layer titled", ex.Message);
        Assert.DoesNotContain(handler.Requests, r => r.Uri.AbsolutePath.Contains("/blobs/"));
    }

    [Fact]
    public async Task The_tag_list_cannot_continue_on_another_host()
    {
        var handler = new RecordingHandler(_ =>
        {
            var page = Http.Json("{\"tags\":[\"2026-09\"]}");
            page.Headers.Add("Link", "<https://files.example.com/v2/x/tags/list?last=1>; rel=\"next\"");
            return page;
        });

        var ex = await Assert.ThrowsAsync<InventoryFetchException>(() => Source(handler, "service-cost/allocation")
            .ListClosedMonthsAsync("2026-01", new HashSet<string>(), new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero), default));

        Assert.Contains("another host", ex.Message);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task A_tag_with_a_trailing_newline_or_foreign_digits_is_not_a_month()
    {
        var handler = new RecordingHandler(_ => Http.Json("{\"tags\":[\"2026-09\\n\",\"٢٠٢٦-10\",\"2026-08\"]}"));

        var months = await Source(handler, "service-cost/allocation")
            .ListClosedMonthsAsync("2026-01", new HashSet<string>(), new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero), default);

        Assert.Equal(["2026-08"], months);
    }
}

public class DocumentSecurityTests
{
    private static byte[] Edit(string fixture, Action<JsonNode> change)
    {
        var node = JsonNode.Parse(Fixture.Bytes(fixture))!;
        change(node);
        return Encoding.UTF8.GetBytes(node.ToJsonString());
    }

    [Fact]
    public void A_period_or_currency_with_a_trailing_newline_is_rejected()
    {
        Assert.False(DocumentReader.Read("cost", Edit("cost-2026-11.json", n => n["period"] = "2026-11\n")).Ok);
        Assert.False(DocumentReader.Read("cost", Edit("cost-2026-11.json", n => n["totals"]!["total"] = new JsonObject { ["USD\n"] = 1 })).Ok);
    }
}
