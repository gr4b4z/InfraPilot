using System.Security.Claims;
using System.Threading.RateLimiting;

namespace Platform.Api.Infrastructure.Auth;

public static class DeploymentIngestionRateLimit
{
    public const string PolicyName = "DeploymentIngestion";

    /// <summary>
    /// Per-API-key sliding window with values loaded from configuration.
    /// Defaults are 120 requests per minute for authenticated keys and 10 for anonymous callers.
    /// </summary>
    public static void AddDeploymentIngestionRateLimit(this IServiceCollection services, IConfiguration configuration)
    {
        var configuredOptions = configuration
            .GetSection(DeploymentIngestionRateLimitOptions.SectionName)
            .Get<DeploymentIngestionRateLimitOptions>()
            ?? new DeploymentIngestionRateLimitOptions();

        var authenticatedPermitLimit = Math.Max(1, configuredOptions.AuthenticatedPermitLimit);
        var anonymousPermitLimit = Math.Max(1, configuredOptions.AnonymousPermitLimit);
        var window = TimeSpan.FromSeconds(Math.Max(1, configuredOptions.WindowSeconds));
        var segmentsPerWindow = Math.Max(1, configuredOptions.SegmentsPerWindow);

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            options.AddPolicy(PolicyName, context =>
            {
                var keyName = context.User.FindFirst(ClaimTypes.Name)?.Value ?? "anonymous";

                return RateLimitPartition.GetSlidingWindowLimiter(
                    partitionKey: keyName,
                    factory: _ => new SlidingWindowRateLimiterOptions
                    {
                        PermitLimit = keyName == "anonymous" ? anonymousPermitLimit : authenticatedPermitLimit,
                        Window = window,
                        SegmentsPerWindow = segmentsPerWindow,
                        QueueLimit = 0,
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                    });
            });
        });
    }
}
