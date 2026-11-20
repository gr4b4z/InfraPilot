using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Platform.Api.Features.ServiceInventory.Http;

namespace Platform.Api.Features.ServiceInventory.Sources;

/// <summary>
/// Bearer tokens handed out by registries, shared by every source so a token is fetched once per
/// repository and reused until shortly before it expires. Singleton.
/// </summary>
public class OciTokenCache
{
    private readonly ConcurrentDictionary<string, (string Token, DateTimeOffset ExpiresAt)> _tokens = new();

    public string? Get(string key, DateTimeOffset now) =>
        _tokens.TryGetValue(key, out var t) && t.ExpiresAt > now ? t.Token : null;

    public void Set(string key, string token, DateTimeOffset expiresAt) => _tokens[key] = (token, expiresAt);

    public void Remove(string key) => _tokens.TryRemove(key, out _);
}

/// <summary>A parsed <c>WWW-Authenticate</c> challenge.</summary>
public sealed partial record OciChallenge(string Scheme, string? Realm, string? Service)
{
    [GeneratedRegex("""(\w+)="([^"]*)"|(\w+)=([^,\s]+)""")]
    private static partial Regex Parameter();

    public static OciChallenge? Parse(string? header)
    {
        if (string.IsNullOrWhiteSpace(header)) return null;
        var space = header.IndexOf(' ');
        var scheme = space < 0 ? header.Trim() : header[..space].Trim();
        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (space > 0)
            foreach (Match m in Parameter().Matches(header[(space + 1)..]))
                parameters[m.Groups[1].Success ? m.Groups[1].Value : m.Groups[3].Value] =
                    m.Groups[2].Success ? m.Groups[2].Value : m.Groups[4].Value;
        return new OciChallenge(scheme,
            parameters.GetValueOrDefault("realm"), parameters.GetValueOrDefault("service"));
    }
}

/// <summary>
/// One file in an OCI artifact: <c>{registry}/{repository}:{tag}</c>, the layer titled
/// <c>{file}</c>. Plain distribution protocol over HTTPS:
/// <list type="number">
/// <item>The manifest digest (<c>HEAD</c>, <c>Docker-Content-Digest</c>) is the version; equal to the
/// stored one means unchanged and nothing is downloaded.</item>
/// <item>The registry's <c>Bearer</c> challenge is answered with the feed's credential at the realm,
/// and the token is reused until it expires.</item>
/// <item>The blob may redirect to storage: followed once without credentials, and its content must
/// match the layer digest.</item>
/// </list>
/// </summary>
public partial class OciArtifactSource : IArtifactSource
{
    private const string ManifestTypes =
        "application/vnd.oci.image.manifest.v1+json, application/vnd.docker.distribution.manifest.v2+json";

    [GeneratedRegex("""<([^>]+)>\s*;\s*rel="?next"?""")]
    private static partial Regex NextLink();

    private readonly InventoryHttpClient _http;
    private readonly OciTokenCache _tokens;
    private readonly TimeProvider _clock;
    private readonly string _registry;
    private readonly string _repository;
    private readonly string _tag;
    private readonly string _file;
    private readonly InventoryCredentialOptions? _credential;
    private readonly string? _authHost;

    public OciArtifactSource(
        InventoryHttpClient http, OciTokenCache tokens, TimeProvider clock,
        string registry, string repository, string tag, string file, InventoryCredentialOptions? credential,
        string? authHost = null)
    {
        _authHost = string.IsNullOrWhiteSpace(authHost) ? null : authHost.Trim();
        _http = http;
        _tokens = tokens;
        _clock = clock;
        _registry = registry;
        _repository = repository;
        _tag = tag;
        _file = file;
        _credential = credential;
    }

    public string Kind => InventorySourceKinds.Oci;

    public string Location => $"{_registry}/{_repository}:{_tag}";

    public string Identity => $"oci://{Location}#{_file}";

    private Uri V2(string path) => new($"https://{_registry}/v2/{_repository}/{path}");

    public async Task<FetchResult> FetchIfChangedAsync(string? lastVersion, CancellationToken ct)
    {
        var manifestUri = V2($"manifests/{_tag}");
        var head = await SendAsync(HttpMethod.Head, manifestUri, accept: ManifestTypes, ct: ct);
        var digest = head.Header("Docker-Content-Digest");
        if (digest is not null && digest == lastVersion) return FetchResult.Unchanged(digest);

        // The tag may have moved since the HEAD: the version stored is the digest of the manifest
        // actually downloaded, never the HEAD's.
        var manifestResponse = await SendAsync(HttpMethod.Get, manifestUri, accept: ManifestTypes, expectJson: true, ct: ct);
        digest = manifestResponse.Header("Docker-Content-Digest") ?? ContentHash.Sha256(manifestResponse.Body);
        if (digest == lastVersion) return FetchResult.Unchanged(digest);

        var layerDigest = FindLayer(manifestResponse.Body);
        var blob = await SendAsync(HttpMethod.Get, V2($"blobs/{layerDigest}"),
            expectJson: true, allowOctetStream: true, blobRedirect: true, ct: ct);
        if (!string.Equals(ContentHash.Sha256(blob.Body), layerDigest, StringComparison.OrdinalIgnoreCase))
            throw new InventoryFetchException($"{Location}: the content of {_file} does not match its digest in the manifest.");

        return FetchResult.Downloaded(new ArtifactFile(blob.Body, digest, Location, Kind));
    }

    public async Task<IReadOnlyList<string>> ListClosedMonthsAsync(string from, ISet<string> stored, DateTimeOffset now, CancellationToken ct)
    {
        var tags = new List<string>();
        Uri? next = V2("tags/list?n=1000");
        for (var page = 0; next is not null && page < 50; page++)
        {
            var current = next;
            var response = await SendAsync(HttpMethod.Get, current, expectJson: true, ct: ct);
            try
            {
                using var doc = JsonDocument.Parse(response.Body);
                if (doc.RootElement.ValueKind != JsonValueKind.Object)
                    throw new InventoryFetchException($"{Location}: the tag list is not a JSON object.");
                // "tags" may be null for a repository without tags; anything else that isn't a list of
                // strings is not a tag list.
                if (doc.RootElement.TryGetProperty("tags", out var list) && list.ValueKind != JsonValueKind.Null)
                {
                    if (list.ValueKind != JsonValueKind.Array)
                        throw new InventoryFetchException($"{Location}: the tag list has no list of tags.");
                    tags.AddRange(list.EnumerateArray().Select(Str).OfType<string>());
                }
            }
            catch (JsonException)
            {
                throw new InventoryFetchException($"{Location}: the tag list is not valid JSON.");
            }
            var link = response.Header("Link");
            var match = link is null ? null : NextLink().Match(link);
            // A relative link is relative to the page just read (RFC 8288), not to the registry root.
            next = match is { Success: true } ? new Uri(current, match.Groups[1].Value) : null;
        }

        var thisMonth = now.UtcDateTime.ToString("yyyy-MM");
        return tags
            .Where(t => ServiceInventoryOptionsValidator.PeriodPattern().IsMatch(t))
            .Where(t => string.CompareOrdinal(t, from) >= 0 && string.CompareOrdinal(t, thisMonth) < 0)
            .Where(t => !stored.Contains(t))
            .Distinct()
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    public IArtifactSource ForMonth(string period) =>
        new OciArtifactSource(_http, _tokens, _clock, _registry, _repository, period, _file, _credential, _authHost);

    private string FindLayer(byte[] manifest)
    {
        try
        {
            using var doc = JsonDocument.Parse(manifest);
            // Every value's kind is checked before it is read: a manifest of the wrong shape is a failed
            // fetch, recorded like any other, never an exception the refresher does not expect.
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("layers", out var layers) && layers.ValueKind == JsonValueKind.Array)
            {
                foreach (var layer in layers.EnumerateArray())
                {
                    if (layer.ValueKind == JsonValueKind.Object
                        && layer.TryGetProperty("annotations", out var annotations)
                        && annotations.ValueKind == JsonValueKind.Object
                        && annotations.TryGetProperty("org.opencontainers.image.title", out var title)
                        && Str(title) == _file
                        && layer.TryGetProperty("digest", out var digest)
                        && Str(digest) is { } value
                        && value.StartsWith("sha256:", StringComparison.Ordinal))
                        return value;
                }
            }
        }
        catch (JsonException)
        {
            throw new InventoryFetchException($"{Location}: the manifest is not valid JSON.");
        }
        throw new InventoryFetchException($"{Location}: the manifest has no layer titled {_file}.");
    }

    private string TokenKey => $"{_registry}|{_repository}";

    /// <summary>The string, or null for any other kind of value (GetString would throw).</summary>
    private static string? Str(JsonElement e) => e.ValueKind == JsonValueKind.String ? e.GetString() : null;

    private async Task<InventoryResponse> SendAsync(
        HttpMethod method, Uri uri, string? accept = null, bool expectJson = false,
        bool allowOctetStream = false, bool blobRedirect = false, CancellationToken ct = default)
    {
        InventoryRequest Build(RequestCredential? credential) => new(method, uri)
        {
            Credential = credential,
            Headers = accept is null ? new Dictionary<string, string>() : new Dictionary<string, string> { ["Accept"] = accept },
            ExpectJson = expectJson,
            AllowOctetStream = allowOctetStream,
            AllowCredentiallessRedirectOffAllowlist = blobRedirect,
        };

        var cached = _tokens.Get(TokenKey, _clock.GetUtcNow());
        var response = await _http.SendAsync(Build(cached is null ? null : RequestCredential.Bearer(_registry, cached)), ct);
        if (response.Status != HttpStatusCode.Unauthorized) return response;

        _tokens.Remove(TokenKey);
        var challenge = OciChallenge.Parse(response.Headers.WwwAuthenticate.ToString());
        RequestCredential? retryCredential;
        if (challenge is { Scheme: var scheme, Realm: { } realm } && scheme.Equals("Bearer", StringComparison.OrdinalIgnoreCase))
            retryCredential = RequestCredential.Bearer(_registry, await GetTokenAsync(realm, challenge.Service, ct));
        else if (challenge?.Scheme.Equals("Basic", StringComparison.OrdinalIgnoreCase) == true)
            retryCredential = RequestCredential.From(_credential, _registry);
        else
            throw new InventoryFetchException($"{InventoryHttpClient.Describe(uri)} asked for authentication the inventory does not support.");

        response = await _http.SendAsync(Build(retryCredential), ct);
        if (response.Status == HttpStatusCode.Unauthorized)
            throw new InventoryFetchException($"The registry refused the credentials (401) for {InventoryHttpClient.Describe(uri)}.", response.Status);
        return response;
    }

    private async Task<string> GetTokenAsync(string realm, string? service, CancellationToken ct)
    {
        if (!Uri.TryCreate(realm, UriKind.Absolute, out var realmUri))
            throw new InventoryFetchException("The registry's token realm is not a valid URL.");
        // The challenge comes from the remote side: the feed's credential goes only to the registry
        // itself or to the token host configured for it, never to whatever host the challenge names.
        var trusted = string.Equals(realmUri.Host, _registry, StringComparison.OrdinalIgnoreCase)
                      || string.Equals(realmUri.Host, _authHost, StringComparison.OrdinalIgnoreCase);
        if (!trusted)
            throw new InventoryFetchException(
                $"The registry {_registry} asked for a token from {realmUri.Host}; set AuthHost for this feed to allow it.");
        var query = $"scope={Uri.EscapeDataString($"repository:{_repository}:pull")}";
        if (!string.IsNullOrEmpty(service)) query = $"service={Uri.EscapeDataString(service)}&{query}";
        var tokenUri = new UriBuilder(realmUri) { Query = query }.Uri;

        var response = await _http.SendAsync(new InventoryRequest(HttpMethod.Get, tokenUri)
        {
            Credential = RequestCredential.From(_credential, tokenUri.Host),
            ExpectJson = true,
        }, ct);
        if (response.Status == HttpStatusCode.Unauthorized)
            throw new InventoryFetchException($"The registry refused the credentials (401) at {InventoryHttpClient.Describe(tokenUri)}.", response.Status);

        try
        {
            using var doc = JsonDocument.Parse(response.Body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new InventoryFetchException($"{InventoryHttpClient.Describe(tokenUri)} returned no token.");
            var token = (root.TryGetProperty("access_token", out var a) ? Str(a) : null)
                        ?? (root.TryGetProperty("token", out var t) ? Str(t) : null)
                        ?? throw new InventoryFetchException($"{InventoryHttpClient.Describe(tokenUri)} returned no token.");
            var lifetime = root.TryGetProperty("expires_in", out var e) && e.ValueKind == JsonValueKind.Number
                           && e.TryGetInt32(out var seconds) ? seconds : 300;
            // Renew a little early so a token never expires between the check and the request.
            _tokens.Set(TokenKey, token, _clock.GetUtcNow().AddSeconds(Math.Max(30, lifetime - 30)));
            return token;
        }
        catch (JsonException)
        {
            throw new InventoryFetchException($"{InventoryHttpClient.Describe(tokenUri)} returned invalid JSON.");
        }
    }
}
