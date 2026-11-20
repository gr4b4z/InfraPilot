using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Options;
using Platform.Api.Infrastructure;

namespace Platform.Api.Features.ServiceInventory.Http;

/// <summary>
/// Credentials for one host. Applied to a request only when the request goes to exactly that host,
/// so a redirect can never carry them elsewhere.
/// </summary>
public sealed record RequestCredential(string Host, string? HeaderName, string? HeaderValue, string? Query)
{
    public static RequestCredential? From(InventoryCredentialOptions? options, string host)
    {
        if (options is null) return null;
        return options.Kind?.Trim().ToLowerInvariant() switch
        {
            InventoryCredentialKinds.Bearer => new(host, "Authorization", $"Bearer {options.Token}", null),
            InventoryCredentialKinds.Basic => new(host, "Authorization",
                "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{options.Username}:{options.Password}")), null),
            InventoryCredentialKinds.Header => new(host, options.HeaderName!.Trim(), options.Value ?? "", null),
            InventoryCredentialKinds.Query => new(host, null, null, (options.Value ?? "").TrimStart('?')),
            _ => null,
        };
    }

    public static RequestCredential Bearer(string host, string token) => new(host, "Authorization", $"Bearer {token}", null);

    public bool AppliesTo(Uri uri) => string.Equals(uri.Host, Host, StringComparison.OrdinalIgnoreCase);
}

public sealed class InventoryRequest
{
    public InventoryRequest(HttpMethod method, Uri uri) { Method = method; Uri = uri; }

    public HttpMethod Method { get; }
    public Uri Uri { get; }
    public RequestCredential? Credential { get; init; }
    public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>();

    /// <summary>Refuse a body whose content type is not JSON.</summary>
    public bool ExpectJson { get; init; }

    /// <summary>Also accept <c>application/octet-stream</c> (an OCI blob's type comes from its manifest).</summary>
    public bool AllowOctetStream { get; init; }

    /// <summary>
    /// The OCI blob case: one redirect to a host outside the allowlist is followed, HTTPS only,
    /// without any credential. The caller verifies the body against its digest.
    /// </summary>
    public bool AllowCredentiallessRedirectOffAllowlist { get; init; }
}

public sealed record InventoryResponse(
    HttpStatusCode Status,
    HttpResponseHeaders Headers,
    HttpContentHeaders? ContentHeaders,
    byte[] Body)
{
    public string? Header(string name) =>
        Headers.TryGetValues(name, out var v) ? v.FirstOrDefault()
        : ContentHeaders is not null && ContentHeaders.TryGetValues(name, out var c) ? c.FirstOrDefault()
        : null;
}

/// <summary>
/// A request the inventory refused or could not complete. The message names the status, host and
/// path only (never a query string or a header) and has been through <see cref="SecretRedactor"/>.
/// </summary>
public class InventoryFetchException : Exception
{
    public InventoryFetchException(string message, HttpStatusCode? status = null)
        : base(SecretRedactor.Redact(message)) => Status = status;

    public HttpStatusCode? Status { get; }
}

/// <summary>
/// The only way the service inventory talks to the outside (ADR-0004): HTTPS only, hosts on the
/// allowlist only, redirects followed here (never by the handler) and re-checked, credentials
/// attached per request to their own host only, a response size limit, a timeout and a JSON-only
/// rule for documents.
/// </summary>
public class InventoryHttpClient
{
    public const string ClientName = "service-inventory";
    private const int MaxRedirects = 3;

    private readonly IHttpClientFactory _factory;
    private readonly IOptionsMonitor<ServiceInventoryOptions> _options;

    public InventoryHttpClient(IHttpClientFactory factory, IOptionsMonitor<ServiceInventoryOptions> options)
    {
        _factory = factory;
        _options = options;
    }

    public async Task<InventoryResponse> SendAsync(InventoryRequest request, CancellationToken ct)
    {
        var options = _options.CurrentValue;
        var uri = request.Uri;
        var credential = request.Credential;
        var offAllowlistUsed = false;

        for (var hop = 0; ; hop++)
        {
            if (uri.Scheme != Uri.UriSchemeHttps)
                throw new InventoryFetchException($"Refused {Describe(uri)}: only https is allowed.");
            var allowed = options.IsHostAllowed(uri.Host);
            if (!allowed && !(offAllowlistUsed && credential is null))
                throw new InventoryFetchException($"Refused {Describe(uri)}: the host is not in AllowedHosts.");

            using var message = new HttpRequestMessage(request.Method, WithCredentialQuery(uri, credential));
            foreach (var (name, value) in request.Headers)
                message.Headers.TryAddWithoutValidation(name, value);
            if (credential is not null && credential.AppliesTo(uri) && credential.HeaderName is not null)
                message.Headers.TryAddWithoutValidation(credential.HeaderName, credential.HeaderValue);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));

            HttpResponseMessage response;
            try
            {
                response = await _factory.CreateClient(ClientName)
                    .SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new InventoryFetchException($"{Describe(uri)} did not answer within {options.TimeoutSeconds} s.");
            }
            catch (HttpRequestException ex)
            {
                throw new InventoryFetchException($"Could not reach {Describe(uri)}: {ex.HttpRequestError}.");
            }

            using (response)
            {
                if (IsRedirect(response.StatusCode) && response.Headers.Location is { } location)
                {
                    if (hop >= MaxRedirects)
                        throw new InventoryFetchException($"{Describe(uri)} redirected more than {MaxRedirects} times.");
                    var next = location.IsAbsoluteUri ? location : new Uri(uri, location);
                    if (next.Scheme != Uri.UriSchemeHttps)
                        throw new InventoryFetchException($"Refused a redirect from {Describe(uri)}: only https is allowed.");
                    if (!options.IsHostAllowed(next.Host))
                    {
                        if (!request.AllowCredentiallessRedirectOffAllowlist || offAllowlistUsed)
                            throw new InventoryFetchException(
                                $"Refused a redirect from {Describe(uri)} to {next.Host}: the host is not in AllowedHosts.");
                        offAllowlistUsed = true;
                    }
                    // A credential belongs to one host; another host never receives it.
                    if (credential is not null && !credential.AppliesTo(next)) credential = null;
                    uri = next;
                    continue;
                }

                if (response.StatusCode == HttpStatusCode.NotModified)
                    return new InventoryResponse(response.StatusCode, response.Headers, response.Content?.Headers, []);

                if (!response.IsSuccessStatusCode)
                {
                    // 401 is returned, not thrown: the OCI source answers the registry's challenge.
                    if (response.StatusCode == HttpStatusCode.Unauthorized)
                        return new InventoryResponse(response.StatusCode, response.Headers, response.Content?.Headers, []);
                    throw new InventoryFetchException(
                        $"{Describe(uri)} answered {(int)response.StatusCode} {response.ReasonPhrase}.", response.StatusCode);
                }

                if (request.Method == HttpMethod.Head)
                    return new InventoryResponse(response.StatusCode, response.Headers, response.Content?.Headers, []);

                if (request.ExpectJson) CheckContentType(response, uri, request.AllowOctetStream);
                var body = await ReadLimitedAsync(response, uri, options.MaxResponseBytes, timeout.Token, ct, options.TimeoutSeconds);
                return new InventoryResponse(response.StatusCode, response.Headers, response.Content.Headers, body);
            }
        }
    }

    /// <summary>Host and path only. Never the query string, which may hold a SAS token.</summary>
    public static string Describe(Uri uri) => $"{uri.Host}{uri.AbsolutePath}";

    private static Uri WithCredentialQuery(Uri uri, RequestCredential? credential)
    {
        if (credential?.Query is not { Length: > 0 } query || !credential.AppliesTo(uri)) return uri;
        var builder = new UriBuilder(uri);
        builder.Query = string.IsNullOrEmpty(builder.Query) ? query : builder.Query.TrimStart('?') + "&" + query;
        return builder.Uri;
    }

    private static bool IsRedirect(HttpStatusCode code) =>
        code is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther
            or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

    private static void CheckContentType(HttpResponseMessage response, Uri uri, bool allowOctetStream)
    {
        var mediaType = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant() ?? "";
        var json = mediaType == "application/json" || mediaType.EndsWith("+json", StringComparison.Ordinal);
        var octet = allowOctetStream && mediaType == "application/octet-stream";
        if (!json && !octet)
            throw new InventoryFetchException(
                $"{Describe(uri)} answered with content type '{(mediaType.Length == 0 ? "none" : mediaType)}', not JSON.");
    }

    private static async Task<byte[]> ReadLimitedAsync(
        HttpResponseMessage response, Uri uri, long max, CancellationToken readToken, CancellationToken ct, int timeoutSeconds)
    {
        if (response.Content.Headers.ContentLength is long declared && declared > max)
            throw new InventoryFetchException($"{Describe(uri)} is {declared} bytes, more than the {max} allowed.");

        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(readToken);
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(chunk, readToken)) > 0)
            {
                if (buffer.Length + read > max)
                    throw new InventoryFetchException($"{Describe(uri)} is more than the {max} bytes allowed.");
                buffer.Write(chunk, 0, read);
            }
            return buffer.ToArray();
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new InventoryFetchException($"{Describe(uri)} did not finish within {timeoutSeconds} s.");
        }
    }
}
