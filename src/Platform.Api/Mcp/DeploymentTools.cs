using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Platform.Api.Agent;
using Platform.Api.Features.Deployments;
using Platform.Api.Features.Deployments.Models;

namespace Platform.Api.Mcp;

[McpServerToolType]
public sealed class DeploymentTools(McpContext ctx, DeploymentService deployments)
{
    [McpServerTool(Name = "list_products", Title = "List products", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Every product with, per environment, how many of its services are deployed there and when the last deploy was. " +
                 "The starting point when you don't know the product names.")]
    public async Task<string> ListProducts(CancellationToken ct = default)
    {
        var products = await deployments.GetProductSummaries(ct);
        return ctx.Result(new
        {
            products = products.Select(p => new
            {
                product = p.Product,
                environments = p.Environments.ToDictionary(
                    e => e.Key,
                    e => new { deployed = $"{e.Value.DeployedServices}/{e.Value.TotalServices}", lastDeployedAt = e.Value.LastDeployedAt }),
                url = ctx.Url(PortalRoutes.Product(p.Product)),
            }),
        });
    }

    [McpServerTool(Name = "find_services", Title = "Find services", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Finds services by name across all products (case-insensitive substring), with the environments each is deployed to. " +
                 "Use it when you know a service but not its product.")]
    public async Task<string> FindServices(
        [Description("Part of the service name, e.g. \"adobe\" or \"search-api\".")] string query,
        [Description("Maximum results (1-100, default 20).")] int limit = 20,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query)) throw new McpException("`query` is required.");
        var hits = await deployments.SearchServices(query, Math.Clamp(limit, 1, 100), ct);
        return ctx.Result(new
        {
            services = hits.Select(h => new
            {
                product = h.Product,
                service = h.Service,
                environments = h.Environments.Select(e => e.Environment),
                lastDeployedAt = h.LastDeployedAt,
                url = ctx.Url(PortalRoutes.Service(h.Product, h.Service)),
            }),
            note = hits.Count == 0 ? "No service name contains that text. Try a shorter fragment, or list_products." : null,
        });
    }

    [McpServerTool(Name = "get_deployment_state", Title = "Current deployment state", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("What is deployed where right now: for each service, the current version, status and time per environment, " +
                 "with the deploy event id behind each cell. Filter by product, service and/or environment — at least one is " +
                 "required. A service fragment (\"adobe\") answers for every service it matches.")]
    public async Task<string> GetDeploymentState(
        [Description("Product, e.g. \"marketplace\". Optional when `service` is unique.")] string? product = null,
        [Description("Service name or fragment, e.g. \"mpt-extension-adobe\" or \"adobe\".")] string? service = null,
        [Description("Environment, e.g. \"staging\" or \"prod\".")] string? environment = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(product) && string.IsNullOrWhiteSpace(service) && string.IsNullOrWhiteSpace(environment))
            throw new McpException("Pass at least one of `product`, `service` or `environment` — the whole matrix is large. Call list_products to see the products.");

        var env = await ctx.EnvironmentFilterAsync(environment, ct);
        var rows = new List<DeploymentStateSummaryDto>();
        if (!string.IsNullOrWhiteSpace(service))
        {
            foreach (var (p, s) in await ctx.ServiceMatchesAsync(product, service, ct))
                rows.AddRange(await deployments.GetStateSummary(p, env, s, ct));
        }
        else
        {
            rows = await deployments.GetStateSummary(await ctx.OptionalProductAsync(product, ct), env, null, ct);
        }

        return ctx.Result(new
        {
            services = rows
                .GroupBy(r => (r.Product, r.Service))
                .OrderBy(g => g.Key.Product).ThenBy(g => g.Key.Service)
                .Select(g => new
                {
                    product = g.Key.Product,
                    service = g.Key.Service,
                    environments = g.ToDictionary(r => r.Environment, r => new
                    {
                        version = r.Version,
                        status = r.Status,
                        isRollback = r.IsRollback ? true : (bool?)null,
                        deployedAt = r.DeployedAt,
                        eventId = r.Id,
                    }),
                    url = ctx.Url(PortalRoutes.Service(g.Key.Product, g.Key.Service)),
                }),
            note = rows.Count == 0 ? "Nothing is deployed for that filter." : null,
        });
    }

    [McpServerTool(Name = "get_service", Title = "Service detail", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("One service: where it runs now (per environment), its recent distinct versions and which environments each reached, " +
                 "and its recent promotions.")]
    public async Task<string> GetService(
        [Description("Service name.")] string service,
        [Description("Product. Optional when the service name is unique.")] string? product = null,
        [Description("How many recent versions to include (1-50, default 10).")] int versions = 10,
        CancellationToken ct = default)
    {
        var (p, s) = await ctx.ServiceAsync(product, service, ct);
        var detail = await deployments.GetServiceDetail(p, s, Math.Clamp(versions, 1, 50), ct)
            ?? throw new McpException($"'{p}/{s}' has no deployments.");

        return ctx.Result(new
        {
            product = detail.Product,
            service = detail.Service,
            url = ctx.Url(PortalRoutes.Service(p, s)),
            environments = detail.Environments.Select(e => new
            {
                environment = e.Environment,
                version = e.Version,
                previousVersion = e.PreviousVersion,
                status = e.Status,
                isRollback = e.IsRollback ? true : (bool?)null,
                deployedAt = e.DeployedAt,
                failureReason = e.Run?.FailureReason,
                eventId = e.Id,
            }),
            recentVersions = detail.RecentVersions.Select(v => new
            {
                version = v.Version,
                lastDeployedAt = v.LastDeployedAt,
                environments = v.Environments.Select(e => new { e.Environment, e.Status, e.DeployedAt, e.EventId }),
            }),
            promotions = detail.Promotions.Take(15).Select(pr => new
            {
                id = pr.Id,
                edge = $"{pr.SourceEnv} → {pr.TargetEnv}",
                version = pr.Version,
                status = pr.Status,
                createdAt = pr.CreatedAt,
                deployedAt = pr.DeployedAt,
            }),
        });
    }

    [McpServerTool(Name = "get_deployment_history", Title = "Deployment history", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("A service's deployments, newest first, optionally in one environment. Each row has the event id for get_deployment.")]
    public async Task<string> GetDeploymentHistory(
        [Description("Service name.")] string service,
        [Description("Product. Optional when the service name is unique.")] string? product = null,
        [Description("Only this environment.")] string? environment = null,
        [Description("Maximum rows (1-200, default 20).")] int limit = 20,
        CancellationToken ct = default)
    {
        var (p, s) = await ctx.ServiceAsync(product, service, ct);
        var env = await ctx.EnvironmentFilterAsync(environment, ct);
        var events = await deployments.GetHistory(p, s, env, Math.Clamp(limit, 1, 200), ct);
        return ctx.Result(new { deployments = events.Select(e => McpShapes.Event(ctx, e)) });
    }

    [McpServerTool(Name = "list_recent_deployments", Title = "Recent deployments", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("A product's deployments since a point in time (default: the last 24 hours), newest first, optionally in one " +
                 "environment. Answers \"what changed today\" and \"what failed recently\".")]
    public async Task<string> ListRecentDeployments(
        [Description("Product name.")] string product,
        [Description("Only this environment.")] string? environment = null,
        [Description("ISO-8601 start of the window. Default: 24 hours ago.")] DateTimeOffset? since = null,
        [Description("Only events with this status: succeeded, failed or in_progress.")] string? status = null,
        [Description("Maximum rows (1-500, default 50).")] int limit = 50,
        CancellationToken ct = default)
    {
        var p = await ctx.ProductAsync(product, ct);
        var from = since ?? DateTimeOffset.UtcNow.AddHours(-24);
        var cap = Math.Clamp(limit, 1, 500);

        var events = string.IsNullOrWhiteSpace(environment)
            ? await deployments.GetRecentByProduct(p, from, 500, ct)
            : await deployments.GetRecentByEnvironment(p, await ctx.EnvironmentAsync(environment, ct), from, ct);
        if (!string.IsNullOrWhiteSpace(status))
            events = events.Where(e => string.Equals(e.Status, status.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();

        return ctx.Result(new
        {
            since = from,
            total = events.Count,
            failed = events.Count(e => e.Status == "failed"),
            deployments = events.OrderByDescending(e => e.DeployedAt).Take(cap).Select(e => McpShapes.Event(ctx, e)),
            truncated = events.Count > cap ? true : (bool?)null,
            note = events.Count == 0 ? "Nothing deployed in this window. For when it last happened, use get_deployment_history or an earlier `since`." : null,
        });
    }

    [McpServerTool(Name = "get_deployment", Title = "Deploy event detail", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("One deploy event in full: the run that performed it and its failure reason, the change it carried (work items, PRs, " +
                 "commits), the people involved, its captured log blocks (read them with get_deployment_log), the service's " +
                 "neighbouring deploys, and the promotions it relates to. For a failed deploy, diagnose_deployment explains it.")]
    public async Task<string> GetDeployment(
        [Description("Deploy event id (GUID).")] string eventId,
        [Description("How many neighbouring deploys of the same service to include (0-50, default 5).")] int historyLimit = 5,
        CancellationToken ct = default)
    {
        var id = McpContext.Id(eventId, "deploy event", "get_deployment_history or list_recent_deployments");
        var detail = await deployments.GetEventDetail(id, Math.Clamp(historyLimit, 0, 50), ct)
            ?? throw new McpException($"No deploy event with id {id}.");
        var e = detail.Event;

        return ctx.Result(new
        {
            @event = McpShapes.Event(ctx, e),
            run = McpShapes.Run(e.Run),
            references = McpShapes.References(e.References),
            participants = McpShapes.People(e.Participants),
            logs = detail.Logs.Select(l => new { id = l.Id, l.Name, l.Source, l.LineCount, l.ByteCount, truncated = l.Truncated ? true : (bool?)null }),
            history = detail.History.Select(h => new
            {
                id = h.Id,
                h.Environment,
                h.Version,
                h.Status,
                isRollback = h.IsRollback ? true : (bool?)null,
                h.DeployedAt,
                h.FailureReason,
            }),
            promotions = detail.Promotions.Select(pr => new
            {
                id = pr.Id,
                edge = $"{pr.SourceEnv} → {pr.TargetEnv}",
                pr.Version,
                pr.Status,
                pr.Direction,
                pr.CreatedAt,
                pr.DeployedAt,
            }),
            workItems = detail.WorkItems.Select(w => new { w.Key, w.Title, w.Url, w.SignOffTargetEnvs }),
            metadata = e.Metadata,
        });
    }

    [McpServerTool(Name = "get_deployment_log", Title = "Deployment log", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Reads a block of output a deploy captured (Helm printout, failure diagnostics). Returns the last `tail` lines, or only " +
                 "the lines containing `match` (case-insensitive) with line numbers — logs can be megabytes, so narrow it.")]
    public async Task<string> GetDeploymentLog(
        [Description("Deploy event id (GUID).")] string eventId,
        [Description("Log block id, from get_deployment's `logs`.")] string logId,
        [Description("Only lines containing this text, e.g. \"error\" or a pod name.")] string? match = null,
        [Description("Lines to return from the end when `match` is not given (1-2000, default 200).")] int tail = 200,
        CancellationToken ct = default)
    {
        var id = McpContext.Id(eventId, "deploy event", "get_deployment_history or list_recent_deployments");
        var lid = McpContext.Id(logId, "log", "get_deployment");
        var log = await deployments.GetLogContent(id, lid, ct)
            ?? throw new McpException($"Deploy event {id} has no log block {lid}. get_deployment lists its logs.");

        var lines = log.Content.Split('\n');
        IEnumerable<(string Line, int Number)> numbered = lines.Select((line, i) => (line.TrimEnd('\r'), i + 1));
        const int matchCap = 400;
        List<(string Line, int Number)> picked;
        if (!string.IsNullOrWhiteSpace(match))
            picked = numbered.Where(x => x.Line.Contains(match, StringComparison.OrdinalIgnoreCase)).Take(matchCap).ToList();
        else
            picked = numbered.TakeLast(Math.Clamp(tail, 1, 2000)).ToList();

        return ctx.Result(new
        {
            name = log.Name,
            source = log.Source,
            totalLines = lines.Length,
            returnedLines = picked.Count,
            truncatedAtSource = log.Truncated ? true : (bool?)null,
            content = string.Join('\n', picked.Select(x => $"{x.Number}: {x.Line}")),
            note = !string.IsNullOrWhiteSpace(match) && picked.Count == matchCap ? $"Stopped at {matchCap} matching lines; narrow `match`." : null,
        });
    }
}
