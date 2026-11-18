using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Platform.Api.Infrastructure.Auth;

namespace Platform.Api.Features.Integrations.Flux;

/// <summary>
/// Authenticates Flux notification-controller requests signed by its <c>generic-hmac</c> provider:
/// <c>X-Signature: &lt;hash-function&gt;=&lt;hex&gt;</c>, the HMAC of the raw request body keyed with
/// the provider secret's <c>token</c>. The client is the <c>{client}</c> route value; its secret
/// comes from <see cref="FluxIntegrationOptions"/>.
///
/// <para>On success the principal carries the client name (so the ingest rate limit partitions per
/// client, as it does per API key) and one <see cref="ApiKeyAuthHandler.AllowedProductClaim"/> per
/// allowed product (so the endpoint's product check is the same as for deploy ingest).</para>
/// </summary>
public class FluxSignatureAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "FluxSignature";
    public const string AuthMethod = "flux_hmac";
    public const string SignatureHeader = "X-Signature";

    /// <summary>Body size read for verification, matching the receiver example in Flux's docs.</summary>
    public const int MaxBodyBytes = 1024 * 1024;

    private readonly IOptionsMonitor<FluxIntegrationOptions> _flux;

    public FluxSignatureAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IOptionsMonitor<FluxIntegrationOptions> flux) : base(options, logger, encoder)
    {
        _flux = flux;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (Request.RouteValues["client"] is not string clientName || string.IsNullOrWhiteSpace(clientName))
            return AuthenticateResult.NoResult();

        var client = _flux.CurrentValue.Clients.FirstOrDefault(c =>
            !c.Revoked && string.Equals(c.Name, clientName, StringComparison.OrdinalIgnoreCase));
        if (client is null || string.IsNullOrEmpty(client.Secret))
            return AuthenticateResult.Fail("Unknown Flux client");

        if (!Request.Headers.TryGetValue(SignatureHeader, out var header) || string.IsNullOrWhiteSpace(header))
            return AuthenticateResult.Fail($"Missing {SignatureHeader} header");

        var body = await ReadBodyAsync();
        if (body is null)
            return AuthenticateResult.Fail($"Request body is larger than {MaxBodyBytes} bytes");

        if (!SignatureMatches(header.ToString(), body, client.Secret))
            return AuthenticateResult.Fail("Invalid signature");

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, $"flux:{client.Name}"),
            new(ClaimTypes.Name, $"flux:{client.Name}"),
            new("auth_method", AuthMethod),
        };
        foreach (var product in client.AllowedProducts.Where(p => !string.IsNullOrWhiteSpace(p)))
            claims.Add(new Claim(ApiKeyAuthHandler.AllowedProductClaim, product));

        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName));
        return AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName));
    }

    /// <summary>
    /// Reads the body for the signature and rewinds it so model binding reads it again. Null when it
    /// is over <see cref="MaxBodyBytes"/>.
    /// </summary>
    private async Task<byte[]?> ReadBodyAsync()
    {
        Request.EnableBuffering(MaxBodyBytes);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await Request.Body.ReadAsync(chunk, Context.RequestAborted)) > 0)
        {
            if (buffer.Length + read > MaxBodyBytes) return null;
            buffer.Write(chunk, 0, read);
        }
        Request.Body.Position = 0;
        return buffer.ToArray();
    }

    /// <summary>
    /// True when <paramref name="header"/> (<c>sha256=&lt;hex&gt;</c>) is the HMAC of
    /// <paramref name="body"/> under <paramref name="secret"/>. The hash functions are the ones the
    /// receiver example in Flux's docs accepts; Flux itself sends <c>sha256</c> today.
    /// </summary>
    public static bool SignatureMatches(string header, byte[] body, string secret)
    {
        var separator = header.IndexOf('=');
        if (separator <= 0 || separator == header.Length - 1) return false;

        var algorithm = header[..separator].Trim().ToLowerInvariant();
        byte[] expected;
        try
        {
            expected = Convert.FromHexString(header[(separator + 1)..].Trim());
        }
        catch (FormatException)
        {
            return false;
        }

        var key = Encoding.UTF8.GetBytes(secret);
        byte[]? actual = algorithm switch
        {
            "sha256" => HMACSHA256.HashData(key, body),
            "sha384" => HMACSHA384.HashData(key, body),
            "sha512" => HMACSHA512.HashData(key, body),
            "sha224" => null, // not in the BCL; accepted by Flux's example but never sent by Flux
            _ => null,
        };

        return actual is not null && CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}
