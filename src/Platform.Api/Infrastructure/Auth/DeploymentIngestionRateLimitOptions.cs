namespace Platform.Api.Infrastructure.Auth;

public class DeploymentIngestionRateLimitOptions
{
    public const string SectionName = "Deployments:IngestionRateLimit";

    public int AuthenticatedPermitLimit { get; set; } = 120;
    public int AnonymousPermitLimit { get; set; } = 10;
    public int WindowSeconds { get; set; } = 60;
    public int SegmentsPerWindow { get; set; } = 6;
}