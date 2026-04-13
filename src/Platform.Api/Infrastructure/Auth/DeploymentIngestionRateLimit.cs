using System.Security.Claims;
using System.Threading.RateLimiting;

namespace Platform.Api.Infrastructure.Auth;

public static class DeploymentIngestionRateLimit
{
    public const string PolicyName = "DeploymentIngestion";

    /// <summary>
    /// Per-API-key sliding window: 120 requests per minute per authenticated key.
    /// Unauthenticated or unknown callers are grouped into a shared, stricter bucket.
    /// </summary>
    public static void AddDeploymentIngestionRateLimit(this IServiceCollection services)
    {
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
                        PermitLimit = keyName == "anonymous" ? 10 : 120,
                        Window = TimeSpan.FromMinutes(1),
                        SegmentsPerWindow = 6,
                        QueueLimit = 0,
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                    });
            });
        });
    }
}
