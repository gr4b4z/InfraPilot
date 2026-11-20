using System.Globalization;
using System.Net;
using Platform.Api.Features.ServiceInventory.Http;

namespace Platform.Api.Features.ServiceInventory.Sources;

/// <summary>
/// A JSON file at a plain HTTPS URL: blob storage with a SAS token (a <c>query</c> credential), a
/// release asset, an internal API. Change detection uses <c>ETag</c> / <c>If-None-Match</c>, then
/// <c>Last-Modified</c> / <c>If-Modified-Since</c>, then the body's hash.
/// </summary>
public class HttpsArtifactSource : IArtifactSource
{
    private const string ETagPrefix = "etag:";
    private const string LastModifiedPrefix = "last-modified:";

    private readonly InventoryHttpClient _http;
    private readonly Uri _url;
    private readonly string? _monthTemplate;
    private readonly InventoryCredentialOptions? _credential;

    public HttpsArtifactSource(InventoryHttpClient http, Uri url, string? monthTemplate, InventoryCredentialOptions? credential)
    {
        _http = http;
        _url = url;
        _monthTemplate = monthTemplate;
        _credential = credential;
    }

    public string Kind => InventorySourceKinds.Https;

    /// <summary>Scheme, host, port and path: never a query string or user information.</summary>
    public string Location => new UriBuilder(_url) { UserName = "", Password = "", Query = "", Fragment = "" }.Uri.GetLeftPart(UriPartial.Path);

    public string Identity => Location;

    public async Task<FetchResult> FetchIfChangedAsync(string? lastVersion, CancellationToken ct)
    {
        var headers = new Dictionary<string, string>();
        if (lastVersion?.StartsWith(ETagPrefix, StringComparison.Ordinal) == true)
            headers["If-None-Match"] = lastVersion[ETagPrefix.Length..];
        else if (lastVersion?.StartsWith(LastModifiedPrefix, StringComparison.Ordinal) == true)
            headers["If-Modified-Since"] = lastVersion[LastModifiedPrefix.Length..];

        var response = await _http.SendAsync(new InventoryRequest(HttpMethod.Get, _url)
        {
            Credential = RequestCredential.From(_credential, HostAuthority.Of(_url)),
            Headers = headers,
            ExpectJson = true,
        }, ct);

        if (response.Status == HttpStatusCode.NotModified) return FetchResult.Unchanged(lastVersion);
        if (response.Status == HttpStatusCode.Unauthorized)
            throw new InventoryFetchException($"{InventoryHttpClient.Describe(_url)} refused the credentials (401).", response.Status);

        var version = response.Headers.ETag is { } etag
            ? ETagPrefix + etag
            : response.ContentHeaders?.LastModified is { } modified
                ? LastModifiedPrefix + modified.ToString("R", CultureInfo.InvariantCulture)
                : ContentHash.Sha256(response.Body);

        // A server without validators always answers 200: the body's hash says whether it changed.
        if (version == lastVersion) return FetchResult.Unchanged(version);
        return FetchResult.Downloaded(new ArtifactFile(response.Body, version, Location, Kind));
    }

    public Task<IReadOnlyList<string>> ListClosedMonthsAsync(string from, ISet<string> stored, DateTimeOffset now, CancellationToken ct)
    {
        // Nothing to list over plain HTTPS: every month from `from` up to last month that is not
        // stored yet is a candidate, and fetching it says whether it exists (404 = not yet).
        IReadOnlyList<string> months = _monthTemplate is null
            ? []
            : Months(from, now).Where(m => !stored.Contains(m)).ToList();
        return Task.FromResult(months);
    }

    public IArtifactSource ForMonth(string period)
    {
        if (_monthTemplate is null) throw new InvalidOperationException("This source has no closed months.");
        return new HttpsArtifactSource(_http, new Uri(_monthTemplate.Replace("{month}", period, StringComparison.Ordinal)), null, _credential);
    }

    /// <summary>YYYY-MM from <paramref name="from"/> up to the month before <paramref name="now"/>.</summary>
    public static IEnumerable<string> Months(string from, DateTimeOffset now)
    {
        var cursor = DateTime.ParseExact(from + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var current = new DateTime(now.UtcDateTime.Year, now.UtcDateTime.Month, 1);
        for (; cursor < current; cursor = cursor.AddMonths(1))
            yield return cursor.ToString("yyyy-MM", CultureInfo.InvariantCulture);
    }
}
