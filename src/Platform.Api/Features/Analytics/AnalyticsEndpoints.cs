using Platform.Api.Features.Analytics.Models;

namespace Platform.Api.Features.Analytics;

/// <summary>
/// Read-only analytics over deploy events and promotion candidates. Group is registered under
/// <c>/api/analytics</c> with the CanApprove policy — same audience as the deployments pages
/// these aggregates summarize. All endpoints answer with a <c>definition</c> and/or
/// <c>coverage</c> block so a number can always explain how it was counted.
/// </summary>
public static class AnalyticsEndpoints
{
    private static readonly string[] Buckets = ["day", "week"];
    private static readonly string[] GroupBys = ["none", "service", "environment", "product"];

    public static RouteGroupBuilder MapAnalyticsEndpoints(this RouteGroupBuilder group)
    {
        // Deployment frequency: how often does (product/service/environment) change, bucketed
        // for charting, with cadence stats per series.
        group.MapGet("/deployments/frequency", async (
            AnalyticsService analytics,
            string? product, string? serviceName, string? environment,
            DateTimeOffset? from, DateTimeOffset? to,
            string? bucket, string? groupBy, string? tz,
            bool? includeRollbacks, bool? includeRedeploys,
            bool? summaryOnly,
            CancellationToken ct) =>
        {
            var resolvedBucket = bucket ?? "day";
            if (!Buckets.Contains(resolvedBucket))
                return Results.BadRequest(new { error = "'bucket' must be one of: day, week" });
            var resolvedGroupBy = groupBy ?? "none";
            if (!GroupBys.Contains(resolvedGroupBy))
                return Results.BadRequest(new { error = "'groupBy' must be one of: none, service, environment, product" });
            if (!TryResolveTz(tz, out var tzInfo))
                return Results.BadRequest(new { error = $"unknown timezone '{tz}'" });
            if (from is not null && to is not null && from >= to)
                return Results.BadRequest(new { error = "'from' must be before 'to'" });

            return Results.Ok(await analytics.GetDeploymentFrequency(
                product, serviceName, environment, from, to,
                resolvedBucket, resolvedGroupBy, tzInfo, includeRollbacks ?? false, includeRedeploys ?? false,
                summaryOnly ?? false, ct));
        });

        // Work-item × environment matrix: which stories are deployed / awaiting where. The window
        // selects stories (any activity, or an open candidate); cells show full state.
        group.MapGet("/work-items/matrix", async (
            AnalyticsService analytics,
            string? product, string? environment, string? reachedEnv,
            DateTimeOffset? from, DateTimeOffset? to,
            int? limit, int? offset,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(product))
                return Results.BadRequest(new { error = "'product' is required" });

            return Results.Ok(await analytics.GetWorkItemMatrix(
                product, environment, reachedEnv, from, to,
                Math.Clamp(limit ?? 100, 1, 500), Math.Max(offset ?? 0, 0), ct));
        });

        // Promotion queue: what is waiting per (product, targetEnv) right now, plus how long
        // approval and dispatch took for candidates that closed inside the window.
        group.MapGet("/promotions/queue", async (
            AnalyticsService analytics,
            string? product, DateTimeOffset? from, DateTimeOffset? to,
            CancellationToken ct) =>
        {
            return Results.Ok(await analytics.GetPromotionQueue(product, from, to, ct));
        });

        // Lead time: commit → first successful deploy per environment (cumulative). Reports
        // empty stats with coverage 0 — never 404 — when producers don't send occurredAt yet.
        group.MapGet("/lead-time", async (
            AnalyticsService analytics,
            string? product, string? serviceName, string? environment,
            DateTimeOffset? from, DateTimeOffset? to,
            string? bucket, string? tz,
            CancellationToken ct) =>
        {
            var resolvedBucket = bucket ?? "week";
            if (!Buckets.Contains(resolvedBucket))
                return Results.BadRequest(new { error = "'bucket' must be one of: day, week" });
            if (!TryResolveTz(tz, out var tzInfo))
                return Results.BadRequest(new { error = $"unknown timezone '{tz}'" });

            return Results.Ok(await analytics.GetLeadTime(
                product, serviceName, environment, from, to, resolvedBucket, tzInfo, ct));
        });

        return group;
    }

    private static bool TryResolveTz(string? tz, out TimeZoneInfo tzInfo)
    {
        if (string.IsNullOrWhiteSpace(tz))
        {
            tzInfo = TimeZoneInfo.Utc;
            return true;
        }
        try
        {
            tzInfo = TimeZoneInfo.FindSystemTimeZoneById(tz);
            return true;
        }
        catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            tzInfo = TimeZoneInfo.Utc;
            return false;
        }
    }
}
