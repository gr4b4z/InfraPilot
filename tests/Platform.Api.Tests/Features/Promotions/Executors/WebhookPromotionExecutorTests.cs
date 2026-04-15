using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Platform.Api.Features.Promotions.Executors;
using Platform.Api.Features.Promotions.Models;

namespace Platform.Api.Tests.Features.Promotions.Executors;

public class WebhookPromotionExecutorTests
{
    private static PromotionCandidate Candidate() => new()
    {
        Id = Guid.Parse("11111111-1111-1111-1111-111111111111"),
        Product = "acme",
        Service = "api",
        SourceEnv = "staging",
        TargetEnv = "prod",
        Version = "v1",
        SourceDeployEventId = Guid.Parse("22222222-2222-2222-2222-222222222222"),
        ApprovedAt = DateTimeOffset.UtcNow,
    };

    private static WebhookPromotionExecutor BuildSut(FakeHandler handler)
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient("promotion-webhook").Returns(new HttpClient(handler));
        return new WebhookPromotionExecutor(factory, Substitute.For<ILogger<WebhookPromotionExecutor>>());
    }

    [Fact]
    public async Task MissingConfig_Fails()
    {
        var sut = BuildSut(new FakeHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        var result = await sut.DispatchAsync(Candidate(), null, CancellationToken.None);
        Assert.False(result.Success);
        Assert.Contains("config JSON blob", result.Error);
    }

    [Fact]
    public async Task MalformedConfig_Fails()
    {
        var sut = BuildSut(new FakeHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        var result = await sut.DispatchAsync(Candidate(), "{not valid json", CancellationToken.None);
        Assert.False(result.Success);
        Assert.Contains("Invalid webhook config", result.Error);
    }

    [Fact]
    public async Task MissingUrl_Fails()
    {
        var sut = BuildSut(new FakeHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        var result = await sut.DispatchAsync(Candidate(), "{}", CancellationToken.None);
        Assert.False(result.Success);
        Assert.Contains("missing 'url'", result.Error);
    }

    [Fact]
    public async Task Success_PostsPayloadAndReturnsRenderedRunUrl()
    {
        HttpMethod? capturedMethod = null;
        Uri? capturedUri = null;
        string? capturedBody = null;
        IEnumerable<string>? capturedToken = null;

        // Capture during handler invocation — the HttpRequestMessage is disposed after SendAsync
        // returns, so we can't read its Content later.
        var handler = new FakeHandler((req, _) =>
        {
            capturedMethod = req.Method;
            capturedUri = req.RequestUri;
            capturedBody = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            req.Headers.TryGetValues("X-Token", out capturedToken);
            return new HttpResponseMessage(HttpStatusCode.Accepted);
        });
        var sut = BuildSut(handler);

        var config = JsonSerializer.Serialize(new
        {
            url = "https://ci.example/hooks/promote",
            headers = new Dictionary<string, string> { ["X-Token"] = "secret" },
            runUrlTemplate = "https://ci.example/runs/{candidateId}",
        });

        var result = await sut.DispatchAsync(Candidate(), config, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("https://ci.example/runs/11111111-1111-1111-1111-111111111111", result.ExternalRunUrl);

        Assert.Equal(HttpMethod.Post, capturedMethod);
        Assert.Equal("https://ci.example/hooks/promote", capturedUri!.ToString());
        Assert.Equal("secret", capturedToken!.Single());

        var body = JsonSerializer.Deserialize<PromotionWebhookPayload>(
            capturedBody!, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.NotNull(body);
        Assert.Equal("acme", body!.Product);
        Assert.Equal("api", body.Service);
        Assert.Equal("prod", body.TargetEnv);
        Assert.Equal("v1", body.Version);
    }

    [Fact]
    public async Task Non2xxResponse_Fails()
    {
        var handler = new FakeHandler((_, _) =>
            new HttpResponseMessage(HttpStatusCode.InternalServerError) { ReasonPhrase = "Boom" });
        var sut = BuildSut(handler);

        var result = await sut.DispatchAsync(
            Candidate(),
            JsonSerializer.Serialize(new { url = "https://ci.example" }),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("500", result.Error);
    }

    [Fact]
    public async Task TransportError_Fails()
    {
        var handler = new FakeHandler((_, _) => throw new HttpRequestException("dns fail"));
        var sut = BuildSut(handler);

        var result = await sut.DispatchAsync(
            Candidate(),
            JsonSerializer.Serialize(new { url = "https://ci.example" }),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("Transport error", result.Error);
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> _send;
        public FakeHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> send) => _send = send;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(_send(request, ct));
    }
}
