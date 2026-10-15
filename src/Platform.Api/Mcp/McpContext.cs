using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol;
using Platform.Api.Agent;
using Platform.Api.Features.Settings;
using Platform.Api.Infrastructure.Auth;
using Platform.Api.Infrastructure.Persistence;

namespace Platform.Api.Mcp;

/// <summary>
/// Per-request plumbing every MCP tool shares: matching the names an agent types to the names the
/// data uses, canonicalising environments, minting portal links, and serialising results compactly.
///
/// <para>Name matching follows the in-app assistant (<see cref="NameResolver"/>): an exact name is used
/// as given, a near miss or a fragment that fits one name is used and reported in
/// <see cref="Notes"/>, and anything ambiguous or unknown fails with the real names in the message so
/// the agent can correct itself on the next call instead of reading an empty result as "nothing
/// there".</para>
/// </summary>
public sealed class McpContext
{
    private readonly PlatformDbContext _db;
    private readonly EnvironmentAliasResolver _environments;
    private readonly IHttpContextAccessor _http;
    private readonly IConfiguration _config;
    private List<(string Product, string Service)>? _index;

    public McpContext(
        PlatformDbContext db, EnvironmentAliasResolver environments,
        IHttpContextAccessor http, IConfiguration config)
    {
        _db = db;
        _environments = environments;
        _http = http;
        _config = config;
    }

    /// <summary>Corrections made on the caller's behalf this request, for the result's <c>nameResolution</c>.</summary>
    public List<string> Notes { get; } = [];

    public ClaimsPrincipal User => _http.HttpContext?.User ?? new ClaimsPrincipal();

    // ── Names ────────────────────────────────────────────────────────────

    /// <summary>
    /// Every (product, service) the portal knows: anything deployed, plus anything only registered as a
    /// build. Two DISTINCT scans; the tools resolve at most a couple of names per request.
    /// </summary>
    private async Task<List<(string Product, string Service)>> IndexAsync(CancellationToken ct)
    {
        if (_index is not null) return _index;
        var deployed = await _db.DeployEvents.AsNoTracking()
            .Select(e => new { e.Product, e.Service }).Distinct().ToListAsync(ct);
        var built = await _db.Builds.AsNoTracking()
            .Select(b => new { b.Product, b.Service }).Distinct().ToListAsync(ct);
        _index = deployed.Concat(built)
            .Select(x => (x.Product, x.Service))
            .Distinct()
            .ToList();
        return _index;
    }

    public async Task<string> ProductAsync(string raw, CancellationToken ct)
    {
        var products = (await IndexAsync(ct)).Select(x => x.Product).Distinct().OrderBy(p => p).ToList();
        return Pick(NameResolver.Resolve(raw, products), "product", products);
    }

    public async Task<string?> OptionalProductAsync(string? raw, CancellationToken ct) =>
        string.IsNullOrWhiteSpace(raw) ? null : await ProductAsync(raw, ct);

    /// <summary>
    /// Resolves a service, and its product when the caller did not name one. A service name shared by
    /// two products is two services, so without a product it must be unique.
    /// </summary>
    public async Task<(string Product, string Service)> ServiceAsync(string? rawProduct, string rawService, CancellationToken ct)
    {
        var index = await IndexAsync(ct);
        var product = await OptionalProductAsync(rawProduct, ct);

        var pool = product is null ? index : index.Where(x => x.Product == product).ToList();
        var names = pool.Select(x => x.Service).Distinct().OrderBy(s => s).ToList();
        var resolution = NameResolver.Resolve(rawService, names);

        if (!resolution.Found && product is not null)
        {
            // Deployed, just not under the product the caller named — say where it lives.
            var elsewhere = NameResolver.Resolve(rawService, index.Select(x => x.Service).Distinct().ToList());
            if (elsewhere.Single is { } other)
            {
                var owners = index.Where(x => x.Service == other).Select(x => x.Product).Distinct();
                throw new McpException(
                    $"'{rawService}' is not a service of '{product}'. '{other}' is deployed under: {string.Join(", ", owners)}.");
            }
        }

        var service = Pick(resolution, "service", names, product is null ? null : $" of '{product}'");
        if (product is not null) return (product, service);

        var ownersOf = index.Where(x => x.Service == service).Select(x => x.Product).Distinct().OrderBy(p => p).ToList();
        if (ownersOf.Count > 1)
            throw new McpException(
                $"Service '{service}' exists in several products: {string.Join(", ", ownersOf)}. Pass `product` to say which.");
        return (ownersOf[0], service);
    }

    /// <summary>
    /// Like <see cref="ServiceAsync"/>, but a fragment or a tie that fits several services answers for
    /// all of them (at most <see cref="NameResolver.MaxPartialMatches"/>) instead of failing — for
    /// reads where "adobe" reasonably means every adobe service.
    /// </summary>
    public async Task<List<(string Product, string Service)>> ServiceMatchesAsync(
        string? rawProduct, string rawService, CancellationToken ct)
    {
        var index = await IndexAsync(ct);
        var product = await OptionalProductAsync(rawProduct, ct);
        var pool = product is null ? index : index.Where(x => x.Product == product).ToList();
        var resolution = NameResolver.Resolve(rawService, pool.Select(x => x.Service).Distinct().ToList());

        if (resolution is { Found: true, Single: null })
        {
            if (resolution.Note("service") is { } note) Notes.Add(note);
            return pool.Where(x => resolution.Matches.Contains(x.Service)).Distinct()
                .OrderBy(x => x.Product).ThenBy(x => x.Service).ToList();
        }

        // One service name deployed under several products (a service moving between products keeps
        // its name) is several services — answer for each rather than refuse a read.
        if (resolution.Single is { } single && product is null)
        {
            var owners = pool.Where(x => x.Service == single).Distinct().OrderBy(x => x.Product).ToList();
            if (owners.Count > 1)
            {
                if (resolution.Note("service") is { } note) Notes.Add(note);
                Notes.Add($"'{single}' is deployed under {owners.Count} products ({string.Join(", ", owners.Select(o => o.Product))}); answered for each — say which is which.");
                return owners;
            }
        }
        return [await ServiceAsync(rawProduct, rawService, ct)];
    }

    private string Pick(NameResolution r, string what, List<string> known, string? scope = null)
    {
        if (r.Single is { } single)
        {
            if (r.Note(what) is { } note) Notes.Add(note);
            return single;
        }
        if (r.Found)
            throw new McpException(
                $"'{r.Asked}' matches several {what}s{scope}: {string.Join(", ", r.Matches)}. Call again with one of them.");

        var sample = known.Take(40).ToList();
        var more = known.Count > sample.Count ? $" (and {known.Count - sample.Count} more — use find_services)" : "";
        throw new McpException(
            known.Count == 0
                ? $"No {what} named '{r.Asked}', and none are known{scope}."
                : $"No {what} named '{r.Asked}'{scope}. Known {what}s: {string.Join(", ", sample)}{more}.");
    }

    // ── Environments ─────────────────────────────────────────────────────

    /// <summary>An optional environment filter, alias-resolved like the REST reads.</summary>
    public Task<string?> EnvironmentFilterAsync(string? raw, CancellationToken ct) =>
        _environments.ResolveFilterAsync(raw, ct);

    public async Task<string> EnvironmentAsync(string raw, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(raw)) throw new McpException("An environment is required.");
        return await _environments.ResolveAsync(raw, ct);
    }

    // ── Ids ──────────────────────────────────────────────────────────────

    public static Guid Id(string raw, string what, string findWith)
    {
        if (Guid.TryParse(raw?.Trim(), out var id)) return id;
        throw new McpException($"'{raw}' is not a valid {what} id — it must be a GUID. Use {findWith} to find it.");
    }

    // ── Links ────────────────────────────────────────────────────────────

    /// <summary>
    /// An absolute portal link for a <see cref="PortalRoutes"/> path. <c>Portal:BaseUrl</c> when set;
    /// otherwise the origin the request came in on — in production the portal and the API share one
    /// (nginx serves both), and TLS ends at the ingress, so anything but localhost is https.
    /// </summary>
    public string Url(string path)
    {
        var configured = _config["Portal:BaseUrl"];
        if (!string.IsNullOrWhiteSpace(configured)) return configured.TrimEnd('/') + path;

        var request = _http.HttpContext?.Request;
        if (request is null || !request.Host.HasValue) return path;
        var local = request.Host.Host is "localhost" or "127.0.0.1" or "[::1]";
        return $"{(local ? request.Scheme : "https")}://{request.Host.Value}{path}";
    }

    // ── Caller ───────────────────────────────────────────────────────────

    /// <summary>
    /// True when the caller authenticated with a configured (pipeline) API key — the only credential
    /// the pipeline-only endpoints accept. Personal tokens and bearer users are people.
    /// </summary>
    public bool IsPipelineKey => User.FindFirstValue("auth_method") == "api_key";

    /// <summary>The products a configured key may act for; empty means every product.</summary>
    public IReadOnlyList<string> AllowedProducts =>
        User.FindAll(ApiKeyAuthHandler.AllowedProductClaim).Select(c => c.Value).ToList();

    public bool MayActFor(string product) =>
        AllowedProducts.Count == 0 || AllowedProducts.Contains(product, StringComparer.OrdinalIgnoreCase);

    // ── Output ───────────────────────────────────────────────────────────

    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
        // The default encoder escapes for embedding in HTML — every "+" in a timestamp becomes
        // +, every arrow and backtick a six-character escape. A tool result is read by a model,
        // never placed in a page, so only what JSON itself requires is escaped.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// The tool result: compact JSON (no indentation, nulls dropped — tokens are the agent's budget),
    /// with this request's name corrections attached when there were any.
    /// </summary>
    public string Result(object payload)
    {
        var node = JsonSerializer.SerializeToNode(payload, JsonOptions);
        if (Notes.Count > 0 && node is System.Text.Json.Nodes.JsonObject obj)
            obj["nameResolution"] = JsonSerializer.SerializeToNode(Notes.Distinct().ToList(), JsonOptions);
        return node?.ToJsonString(JsonOptions) ?? "null";
    }
}
