using System.Net;
using System.Net.Http.Headers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Platform.Api.Features.ServiceInventory;
using Platform.Api.Features.ServiceInventory.Http;
using Platform.Api.Infrastructure.Persistence;

namespace Platform.Api.Tests.Features.ServiceInventory;

/// <summary>The fictional fixtures under Fixtures/, copied to the test output.</summary>
internal static class Fixture
{
    public static byte[] Bytes(string name) =>
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Features", "ServiceInventory", "Fixtures", name));
}

/// <summary>A request as the handler saw it, headers included, for asserting what was sent where.</summary>
internal sealed record SentRequest(HttpMethod Method, Uri Uri, IReadOnlyDictionary<string, string> Headers)
{
    public string? Header(string name) => Headers.TryGetValue(name, out var v) ? v : null;
}

/// <summary>Answers requests from a function and records every one of them.</summary>
internal sealed class RecordingHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;
    public List<SentRequest> Requests { get; } = [];

    public RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Requests.Add(new SentRequest(request.Method, request.RequestUri!,
            request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase)));
        return Task.FromResult(_respond(request));
    }
}

/// <summary>A handler that never answers before the request is cancelled.</summary>
internal sealed class HangingHandler : HttpMessageHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.Infinite, cancellationToken);
        throw new InvalidOperationException("unreachable");
    }
}

internal sealed class SingleClientFactory : IHttpClientFactory
{
    private readonly HttpMessageHandler _handler;
    public SingleClientFactory(HttpMessageHandler handler) => _handler = handler;
    public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
}

internal sealed class StaticOptions<T> : IOptionsMonitor<T>
{
    public StaticOptions(T value) => CurrentValue = value;
    public T CurrentValue { get; set; }
    public T Get(string? name) => CurrentValue;
    public IDisposable? OnChange(Action<T, string?> listener) => null;
}

internal sealed class ManualClock : TimeProvider
{
    public ManualClock(DateTimeOffset now) => Now = now;
    public DateTimeOffset Now { get; set; }
    public override DateTimeOffset GetUtcNow() => Now;
}

internal static class Http
{
    public static HttpResponseMessage Json(byte[] body, string contentType = "application/json", HttpStatusCode status = HttpStatusCode.OK)
    {
        var response = new HttpResponseMessage(status) { Content = new ByteArrayContent(body) };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        return response;
    }

    public static HttpResponseMessage Json(string body, string contentType = "application/json") =>
        Json(System.Text.Encoding.UTF8.GetBytes(body), contentType);

    public static HttpResponseMessage Status(HttpStatusCode status) => new(status) { Content = new ByteArrayContent([]) };

    public static HttpResponseMessage Redirect(string location, HttpStatusCode status = HttpStatusCode.TemporaryRedirect)
    {
        var response = Status(status);
        response.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute);
        return response;
    }

    public static ServiceInventoryOptions Options(params string[] hosts) => new()
    {
        AllowedHosts = hosts.ToList(),
        MaxResponseBytes = 1024 * 1024,
        TimeoutSeconds = 5,
    };

    public static InventoryHttpClient Client(HttpMessageHandler handler, ServiceInventoryOptions options) =>
        new(new SingleClientFactory(handler), new StaticOptions<ServiceInventoryOptions>(options));
}

internal static class Db
{
    public static PlatformDbContext InMemory() =>
        new(new DbContextOptionsBuilder<PlatformDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options);
}
