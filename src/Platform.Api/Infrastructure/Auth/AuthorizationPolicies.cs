namespace Platform.Api.Infrastructure.Auth;

public static class AuthorizationPolicies
{
    public const string CatalogAdmin = "CatalogAdmin";
    public const string CanApprove = "CanApprove";
    public const string AuditViewer = "AuditViewer";
    public const string DeploymentIngestion = "DeploymentIngestion";

    /// <summary>Both Entra (human) and ApiKey (machine) schemes are accepted.</summary>
    private static readonly string[] AllSchemes = ["Bearer", ApiKeyAuthHandler.SchemeName];

    public static IServiceCollection AddPlatformAuthorization(this IServiceCollection services, IConfiguration config)
    {
        services.AddAuthorizationBuilder()
            // Admin — requires InfraPortal.Admin role via either auth scheme.
            .AddPolicy(CatalogAdmin, p =>
                p.AddAuthenticationSchemes(AllSchemes)
                 .RequireClaim("roles", "InfraPortal.Admin"))
            // Any authenticated user (Entra or API key).
            .AddPolicy(CanApprove, p =>
                p.AddAuthenticationSchemes(AllSchemes)
                 .RequireAuthenticatedUser())
            // Audit viewer — admin only.
            .AddPolicy(AuditViewer, p =>
                p.AddAuthenticationSchemes(AllSchemes)
                 .RequireClaim("roles", "InfraPortal.Admin"))
            // Deployment ingestion — any authenticated API key (backward compat).
            .AddPolicy(DeploymentIngestion, p =>
                p.AddAuthenticationSchemes(ApiKeyAuthHandler.SchemeName)
                 .RequireAuthenticatedUser());

        return services;
    }
}
