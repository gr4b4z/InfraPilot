using System.Security.Claims;

namespace Platform.Api.Infrastructure.Auth;

public static class AuthorizationPolicies
{
    public const string CatalogAdmin = "CatalogAdmin";
    public const string CanApprove = "CanApprove";
    public const string AuditViewer = "AuditViewer";
    public const string DeploymentIngestion = "DeploymentIngestion";
    public const string SignedInSession = "SignedInSession";

    /// <summary>Both Entra (human) and ApiKey (machine) schemes are accepted.</summary>
    private static readonly string[] AllSchemes = ["Bearer", ApiKeyAuthHandler.SchemeName];

    private const string AdminRole = "InfraPortal.Admin";

    public static IServiceCollection AddPlatformAuthorization(this IServiceCollection services, IConfiguration config)
    {
        services.AddAuthorizationBuilder()
            // Admin — requires InfraPortal.Admin role via either auth scheme.
            .AddPolicy(CatalogAdmin, p =>
                p.AddAuthenticationSchemes(AllSchemes)
                 .RequireAuthenticatedUser()
                 .RequireAssertion(ctx => HasRole(ctx.User, AdminRole)))
            // Any authenticated user (Entra or API key).
            .AddPolicy(CanApprove, p =>
                p.AddAuthenticationSchemes(AllSchemes)
                 .RequireAuthenticatedUser())
            // Audit viewer — admin only.
            .AddPolicy(AuditViewer, p =>
                p.AddAuthenticationSchemes(AllSchemes)
                 .RequireAuthenticatedUser()
                 .RequireAssertion(ctx => HasRole(ctx.User, AdminRole)))
            // Deployment ingestion — any authenticated configured API key (backward compat). Pipeline
            // work only: a personal token rides the same scheme but is a person, not a producer.
            .AddPolicy(DeploymentIngestion, p =>
                p.AddAuthenticationSchemes(ApiKeyAuthHandler.SchemeName)
                 .RequireAuthenticatedUser()
                 .RequireClaim("auth_method", "api_key"))
            // A person signed in to the portal — never a key or a token. For managing the tokens
            // themselves: a token must not be able to mint or revoke tokens.
            .AddPolicy(SignedInSession, p =>
                p.AddAuthenticationSchemes("Bearer")
                 .RequireAuthenticatedUser());

        return services;
    }

    /// <summary>
    /// Check for the role in both the literal "roles" claim (API keys, some Entra configs)
    /// and ClaimTypes.Role (Microsoft.Identity.Web default JWT mapping).
    /// </summary>
    private static bool HasRole(ClaimsPrincipal user, string role) =>
        user.Claims.Any(c =>
            (c.Type == "roles" || c.Type == ClaimTypes.Role) &&
            string.Equals(c.Value, role, StringComparison.OrdinalIgnoreCase));
}
