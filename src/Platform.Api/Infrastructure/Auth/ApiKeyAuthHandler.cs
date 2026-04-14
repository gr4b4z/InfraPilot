using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Platform.Api.Infrastructure.Auth;

public class ApiKeyAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "ApiKey";
    public const string PolicyName = "DeploymentIngestion";
    public const string AllowedProductClaim = "allowed_product";
    private const string HeaderName = "X-Api-Key";

    private readonly IConfiguration _config;

    public ApiKeyAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IConfiguration config) : base(options, logger, encoder)
    {
        _config = config;
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(HeaderName, out var apiKeyHeader))
            return Task.FromResult(AuthenticateResult.NoResult());

        var providedKey = apiKeyHeader.ToString();
        if (string.IsNullOrEmpty(providedKey))
            return Task.FromResult(AuthenticateResult.Fail("API key is empty"));

        var apiKeys = _config.GetSection("Deployments:ApiKeys").Get<List<ApiKeyEntry>>() ?? [];
        var providedKeyBytes = Encoding.UTF8.GetBytes(providedKey);
        var providedKeyHash = SHA256.HashData(providedKeyBytes);

        ApiKeyEntry? match = null;
        foreach (var entry in apiKeys)
        {
            if (entry.Revoked) continue;
            if (IsMatch(entry, providedKey, providedKeyBytes, providedKeyHash))
            {
                match = entry;
                break;
            }
        }

        if (match is null)
            return Task.FromResult(AuthenticateResult.Fail("Invalid API key"));

        // Resolve effective roles: explicit list, or default to User so existing
        // keys that only pushed deploy events keep working without config changes.
        var roles = match.Roles is { Count: > 0 }
            ? match.Roles
            : ["InfraPortal.User"];

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, $"apikey:{match.Name}"),
            new(ClaimTypes.Name, match.Name),
            new("auth_method", "api_key"),
        };
        foreach (var role in roles)
            claims.Add(new Claim("roles", role));
        foreach (var product in match.AllowedProducts)
        {
            if (!string.IsNullOrWhiteSpace(product))
                claims.Add(new Claim(AllowedProductClaim, product));
        }

        var identity = new ClaimsIdentity(claims, SchemeName);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, SchemeName);

        return Task.FromResult(AuthenticateResult.Success(ticket));
    }

    private static bool IsMatch(ApiKeyEntry entry, string providedKey, byte[] providedKeyBytes, byte[] providedKeyHash)
    {
        // Prefer KeyHash (production); fall back to plaintext Key (dev convenience).
        if (!string.IsNullOrEmpty(entry.KeyHash))
        {
            if (!TryFromHex(entry.KeyHash, out var expectedHash)) return false;
            return CryptographicOperations.FixedTimeEquals(providedKeyHash, expectedHash);
        }
        if (!string.IsNullOrEmpty(entry.Key))
        {
            var expectedBytes = Encoding.UTF8.GetBytes(entry.Key);
            return CryptographicOperations.FixedTimeEquals(providedKeyBytes, expectedBytes);
        }
        return false;
    }

    private static bool TryFromHex(string hex, out byte[] bytes)
    {
        try { bytes = Convert.FromHexString(hex); return true; }
        catch { bytes = []; return false; }
    }
}

public class ApiKeyEntry
{
    public string Name { get; set; } = "";
    /// <summary>Plaintext key — use for local/dev. For production, prefer KeyHash.</summary>
    public string Key { get; set; } = "";
    /// <summary>Lowercase SHA-256 hex of the key. If set, takes precedence over Key.</summary>
    public string KeyHash { get; set; } = "";
    /// <summary>When true, the entry is rejected even if the key matches.</summary>
    public bool Revoked { get; set; }
    /// <summary>Product slugs this key is allowed to post events for. Empty = all products.</summary>
    public List<string> AllowedProducts { get; set; } = [];
    /// <summary>Authorization roles granted to this key (e.g. "InfraPortal.User", "InfraPortal.Admin"). Defaults to ["InfraPortal.User"] when empty.</summary>
    public List<string> Roles { get; set; } = [];
}
