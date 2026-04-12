using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Platform.Api.Infrastructure.Auth;

public class ApiKeyAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "ApiKey";
    public const string PolicyName = "DeploymentIngestion";
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
        var match = apiKeys.FirstOrDefault(k => k.Key == providedKey);

        if (match is null)
            return Task.FromResult(AuthenticateResult.Fail("Invalid API key"));

        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, $"apikey:{match.Name}"),
            new Claim(ClaimTypes.Name, match.Name),
            new Claim("auth_method", "api_key"),
        };

        var identity = new ClaimsIdentity(claims, SchemeName);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, SchemeName);

        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}

public class ApiKeyEntry
{
    public string Name { get; set; } = "";
    public string Key { get; set; } = "";
}
