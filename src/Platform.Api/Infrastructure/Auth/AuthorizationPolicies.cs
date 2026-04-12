namespace Platform.Api.Infrastructure.Auth;

public static class AuthorizationPolicies
{
    public const string CatalogAdmin = "CatalogAdmin";
    public const string CanApprove = "CanApprove";
    public const string AuditViewer = "AuditViewer";
    public const string DeploymentIngestion = ApiKeyAuthHandler.PolicyName;

    public static IServiceCollection AddPlatformAuthorization(this IServiceCollection services, IConfiguration config)
    {
        services.AddAuthorizationBuilder()
            .AddPolicy(CatalogAdmin, p =>
                p.RequireClaim("roles", "InfraPortal.Admin"))
            .AddPolicy(CanApprove, p =>
                p.RequireAuthenticatedUser())
            .AddPolicy(AuditViewer, p =>
                p.RequireClaim("roles", "InfraPortal.Admin"))
            .AddPolicy(DeploymentIngestion, p =>
                p.AddAuthenticationSchemes(ApiKeyAuthHandler.SchemeName)
                 .RequireAuthenticatedUser());

        return services;
    }
}
