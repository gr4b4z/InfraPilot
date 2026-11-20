using System.Net;
using Platform.Api.Features.ServiceInventory;
using Platform.Api.Features.ServiceInventory.Http;

namespace Platform.Api.Tests.Features.ServiceInventory;

/// <summary>The outbound rules every inventory request goes through (ADR-0004).</summary>
public class InventoryHttpClientTests
{
    private static readonly RequestCredential Credential = new("registry.example.com", "Authorization", "Bearer secret-value", null);

    private static InventoryRequest Get(string url, RequestCredential? credential = null, bool json = true) =>
        new(HttpMethod.Get, new Uri(url)) { Credential = credential, ExpectJson = json };

    [Fact]
    public async Task A_host_not_in_the_allowlist_is_refused_before_connecting()
    {
        var handler = new RecordingHandler(_ => Http.Json("{}"));
        var client = Http.Client(handler, Http.Options("registry.example.com"));

        var ex = await Assert.ThrowsAsync<InventoryFetchException>(() =>
            client.SendAsync(Get("https://elsewhere.example.net/file.json"), default));

        Assert.Contains("not in AllowedHosts", ex.Message);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Plain_http_is_refused()
    {
        var handler = new RecordingHandler(_ => Http.Json("{}"));
        var client = Http.Client(handler, Http.Options("registry.example.com"));

        await Assert.ThrowsAsync<InventoryFetchException>(() =>
            client.SendAsync(Get("http://registry.example.com/file.json"), default));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task A_redirect_off_the_allowlist_is_refused()
    {
        var handler = new RecordingHandler(_ => Http.Redirect("https://evil.example.net/file.json"));
        var client = Http.Client(handler, Http.Options("registry.example.com"));

        var ex = await Assert.ThrowsAsync<InventoryFetchException>(() =>
            client.SendAsync(Get("https://registry.example.com/file.json", Credential), default));

        Assert.Contains("not in AllowedHosts", ex.Message);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task A_redirect_within_the_allowlist_is_followed_and_the_credential_stays_on_its_own_host()
    {
        var handler = new RecordingHandler(r => r.RequestUri!.Host == "registry.example.com"
            ? Http.Redirect("https://files.example.com/file.json")
            : Http.Json("{}"));
        var client = Http.Client(handler, Http.Options("registry.example.com", "files.example.com"));

        var response = await client.SendAsync(Get("https://registry.example.com/file.json", Credential), default);

        Assert.Equal(HttpStatusCode.OK, response.Status);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("Bearer secret-value", handler.Requests[0].Header("Authorization"));
        Assert.Null(handler.Requests[1].Header("Authorization"));
    }

    [Fact]
    public async Task The_blob_redirect_may_leave_the_allowlist_once_and_never_carries_the_credential()
    {
        var handler = new RecordingHandler(r => r.RequestUri!.Host == "registry.example.com"
            ? Http.Redirect("https://blobstore.example.org/blob?sig=storage-signature")
            : Http.Json("{}", "application/octet-stream"));
        var client = Http.Client(handler, Http.Options("registry.example.com"));

        await client.SendAsync(new InventoryRequest(HttpMethod.Get, new Uri("https://registry.example.com/v2/x/blobs/sha256:1"))
        {
            Credential = Credential,
            ExpectJson = true,
            AllowOctetStream = true,
            AllowCredentiallessRedirectOffAllowlist = true,
        }, default);

        Assert.Equal("blobstore.example.org", handler.Requests[1].Uri.Host);
        Assert.Null(handler.Requests[1].Header("Authorization"));
    }

    [Fact]
    public async Task A_second_hop_off_the_allowlist_is_refused()
    {
        var handler = new RecordingHandler(r => r.RequestUri!.Host switch
        {
            "registry.example.com" => Http.Redirect("https://blobstore.example.org/blob"),
            _ => Http.Redirect("https://other.example.org/blob"),
        });
        var client = Http.Client(handler, Http.Options("registry.example.com"));

        await Assert.ThrowsAsync<InventoryFetchException>(() => client.SendAsync(
            new InventoryRequest(HttpMethod.Get, new Uri("https://registry.example.com/v2/x/blobs/sha256:1"))
            { AllowCredentiallessRedirectOffAllowlist = true }, default));
    }

    [Fact]
    public async Task A_body_declared_larger_than_the_limit_is_refused()
    {
        var handler = new RecordingHandler(_ => Http.Json(new byte[2048]));
        var options = Http.Options("files.example.com");
        options.MaxResponseBytes = 1024;
        var client = Http.Client(handler, options);

        var ex = await Assert.ThrowsAsync<InventoryFetchException>(() =>
            client.SendAsync(Get("https://files.example.com/big.json"), default));
        Assert.Contains("more than the 1024", ex.Message);
    }

    [Fact]
    public async Task A_body_without_a_length_that_grows_past_the_limit_is_refused_while_streaming()
    {
        var handler = new RecordingHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new MemoryStream(new byte[4096])) };
            response.Content.Headers.ContentType = new("application/json");
            response.Content.Headers.ContentLength = null;
            return response;
        });
        var options = Http.Options("files.example.com");
        options.MaxResponseBytes = 1024;
        var client = Http.Client(handler, options);

        await Assert.ThrowsAsync<InventoryFetchException>(() => client.SendAsync(Get("https://files.example.com/big.json"), default));
    }

    [Fact]
    public async Task A_request_that_does_not_answer_in_time_fails_with_a_timeout()
    {
        var options = Http.Options("files.example.com");
        options.TimeoutSeconds = 1;
        var client = Http.Client(new HangingHandler(), options);

        var ex = await Assert.ThrowsAsync<InventoryFetchException>(() => client.SendAsync(Get("https://files.example.com/slow.json"), default));
        Assert.Contains("did not answer within 1 s", ex.Message);
    }

    [Fact]
    public async Task A_document_that_is_not_json_is_refused()
    {
        var handler = new RecordingHandler(_ => Http.Json("<html></html>", "text/html"));
        var client = Http.Client(handler, Http.Options("files.example.com"));

        var ex = await Assert.ThrowsAsync<InventoryFetchException>(() => client.SendAsync(Get("https://files.example.com/x.json"), default));
        Assert.Contains("not JSON", ex.Message);
    }

    [Fact]
    public async Task A_query_credential_is_sent_but_never_appears_in_an_error()
    {
        var handler = new RecordingHandler(_ => Http.Status(HttpStatusCode.Forbidden));
        var client = Http.Client(handler, Http.Options("files.example.com"));
        var sas = RequestCredential.From(new InventoryCredentialOptions { Kind = "query", Value = "sv=1&sig=SECRETSIGNATURE" }, "files.example.com");

        var ex = await Assert.ThrowsAsync<InventoryFetchException>(() =>
            client.SendAsync(Get("https://files.example.com/cost.json", sas), default));

        Assert.Contains("sig=SECRETSIGNATURE", handler.Requests[0].Uri.Query);
        Assert.DoesNotContain("SECRETSIGNATURE", ex.Message);
        Assert.Contains("files.example.com/cost.json", ex.Message);
    }
}
