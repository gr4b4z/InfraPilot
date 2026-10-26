using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Platform.Api.Features.Analytics;

namespace Platform.Api.Mcp;

/// <summary>
/// The analytics reads, passed through as the REST endpoints answer them. Their responses already
/// carry a <c>definition</c> and a <c>coverage</c> block saying how each number was counted, which is
/// exactly what an agent must quote alongside the number — so these tools add no shaping of their own.
/// </summary>
[McpServerToolType]
public sealed class AnalyticsTools(McpContext ctx, AnalyticsService analytics, DeliveryMetricsService delivery)
{
    private const string Window =
        "ISO-8601. The window is half-open [from, to); the default is the last 14 days.";

    [McpServerTool(Name = "get_deployment_frequency", Title = "Deployment frequency", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("How often deployments happen, with failure and rollback counts and change-failure rate, per series. Quote the " +
                 "response's definition and coverage when reporting numbers; durations are percentiles, never averages.")]
    public async Task<string> GetDeploymentFrequency(
        [Description("Product name.")] string? product = null,
        [Description("Service name.")] string? service = null,
        [Description("Environment.")] string? environment = null,
        [Description("Window start. " + Window)] DateTimeOffset? from = null,
        [Description("Window end. " + Window)] DateTimeOffset? to = null,
        [Description("Split series by: none (default), service, environment or product.")] string groupBy = "none",
        [Description("Bucket size for the per-bucket series: day or week (default).")] string bucket = "week",
        [Description("Include the per-bucket series, not just the summary. Default false.")] bool includeBuckets = false,
        CancellationToken ct = default)
    {
        if (!GroupBys.Contains(groupBy)) throw new McpException("`groupBy` must be one of: none, service, environment, product.");
        if (!Buckets.Contains(bucket)) throw new McpException("`bucket` must be day or week.");
        var (p, s) = await ScopeAsync(product, service, ct);
        var result = await analytics.GetDeploymentFrequency(
            p, s, await ctx.EnvironmentFilterAsync(environment, ct), from, to,
            bucket, groupBy, TimeZoneInfo.Utc, includeRollbacks: false, includeRedeploys: false,
            summaryOnly: !includeBuckets, ct);
        return ctx.Result(result);
    }

    [McpServerTool(Name = "get_promotion_queue", Title = "Promotion queue", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("What is waiting to be promoted right now per (product, target environment), and how long approval and dispatch " +
                 "took (p50/p75/p90) for promotions that closed in the window.")]
    public async Task<string> GetPromotionQueue(
        [Description("Product name.")] string? product = null,
        [Description("Window start. " + Window)] DateTimeOffset? from = null,
        [Description("Window end. " + Window)] DateTimeOffset? to = null,
        CancellationToken ct = default)
    {
        var result = await analytics.GetPromotionQueue(await ctx.OptionalProductAsync(product, ct), from, to, ct);
        return ctx.Result(result);
    }

    [McpServerTool(Name = "get_lead_time", Title = "Lead time", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("DORA-style lead time from commit to first successful deploy per environment, as percentiles. Depends on producers " +
                 "sending commit times — check the coverage block before trusting a figure.")]
    public async Task<string> GetLeadTime(
        [Description("Product name.")] string? product = null,
        [Description("Service name.")] string? service = null,
        [Description("Environment.")] string? environment = null,
        [Description("Window start. " + Window)] DateTimeOffset? from = null,
        [Description("Window end. " + Window)] DateTimeOffset? to = null,
        CancellationToken ct = default)
    {
        var (p, s) = await ScopeAsync(product, service, ct);
        var result = await analytics.GetLeadTime(
            p, s, await ctx.EnvironmentFilterAsync(environment, ct), from, to, "week", TimeZoneInfo.Utc, ct);
        return ctx.Result(result);
    }

    [McpServerTool(Name = "get_delivery_metrics", Title = "Delivery metrics", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("The delivery metrics report: DORA (deployment frequency, lead time, change failure rate, time to restore) plus flow, " +
                 "quality, cadence and data-quality metrics, over one or several products pooled as one population, with per-product and " +
                 "per-service breakdowns. Every metric carries its definition, sample size, coverage and delta vs the previous equal window; " +
                 "an unavailable metric says what data would make it measurable. Quote definition and coverage with any number.")]
    public async Task<string> GetDeliveryMetrics(
        [Description("Product names, comma-separated. Empty means every product.")] string? products = null,
        [Description("Window start. " + Window)] DateTimeOffset? from = null,
        [Description("Window end. " + Window)] DateTimeOffset? to = null,
        [Description("Trend bucket: day, week (default) or month.")] string bucket = "week",
        CancellationToken ct = default)
    {
        if (!AnalyticsCalendar.Buckets.Contains(bucket)) throw new McpException("`bucket` must be day, week or month.");
        var selected = new List<string>();
        foreach (var raw in (products ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            selected.Add(await ctx.ProductAsync(raw, ct));
        var result = await delivery.Get(selected, from, to, bucket, TimeZoneInfo.Utc, ct);
        return ctx.Result(result);
    }

    private async Task<(string? Product, string? Service)> ScopeAsync(string? product, string? service, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(service)) return (await ctx.OptionalProductAsync(product, ct), null);
        var (p, s) = await ctx.ServiceAsync(product, service, ct);
        return (p, s);
    }

    private static readonly string[] Buckets = ["day", "week"];
    private static readonly string[] GroupBys = ["none", "service", "environment", "product"];
}
