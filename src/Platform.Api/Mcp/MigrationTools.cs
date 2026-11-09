using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Platform.Api.Agent;
using Platform.Api.Features.Migrations;
using Platform.Api.Features.Migrations.Models;

namespace Platform.Api.Mcp;

/// <summary>
/// Database migrations, read-only: what is pending or failed, and whether a given migration has run
/// somewhere. Schema migrations run inside each deploy (Helm hook), data migrations after it; only the
/// newest data migration of a service in an environment ever runs.
/// </summary>
[McpServerToolType]
public sealed class MigrationTools(McpContext ctx, MigrationService migrations)
{
    [McpServerTool(Name = "list_migrations", Title = "List migrations", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Database migrations of deployments, newest first. Schema migrations run inside the deploy; data migrations run " +
                 "after it and are pending until they do — a newer version's data migration supersedes an older pending or failed one. " +
                 "Default status filter is \"attention\" (pending, running, failed). Each row has the individual migration counts, the " +
                 "failure reason, and the deploy event it belongs to.")]
    public async Task<string> ListMigrations(
        [Description("Product name.")] string? product = null,
        [Description("Service name or fragment.")] string? service = null,
        [Description("Environment, e.g. \"staging\".")] string? environment = null,
        [Description("\"schema\" or \"data\".")] string? kind = null,
        [Description("\"attention\" (default: pending, running, failed), \"all\", or one status: pending, running, succeeded, failed, skipped, superseded.")] string? status = "attention",
        [Description("Maximum rows (1-200, default 50).")] int limit = 50,
        CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(kind) && !MigrationKinds.All.Contains(kind.ToLowerInvariant()))
            throw new McpException("`kind` must be \"schema\" or \"data\".");
        var resolvedProduct = await ctx.OptionalProductAsync(product, ct);
        var services = string.IsNullOrWhiteSpace(service)
            ? [null]
            : (await ctx.ServiceMatchesAsync(resolvedProduct, service, ct)).Select(m => m.Service).Distinct().Cast<string?>().ToList();
        var env = await ctx.EnvironmentFilterAsync(environment, ct);

        var rows = new List<MigrationRunSummaryDto>();
        var total = 0;
        foreach (var s in services)
        {
            var page = await migrations.ListAsync(resolvedProduct, s, env, kind, status, null, null, null, null,
                Math.Clamp(limit, 1, 200), 0, ct);
            rows.AddRange(page.Items);
            total += page.Total;
        }

        return ctx.Result(new
        {
            total,
            migrations = rows.OrderByDescending(r => r.QueuedAt).Take(Math.Clamp(limit, 1, 200)).Select(r => new
            {
                id = r.Id,
                r.Kind,
                r.Product,
                r.Service,
                r.Environment,
                r.Version,
                r.Status,
                r.QueuedAt,
                r.CompletedAt,
                applied = r.AppliedCount,
                failed = r.FailedCount,
                named = r.TotalCount,
                r.FailureReason,
                r.SupersededById,
                r.DeployEventId,
                deploymentUrl = r.DeployEventId is { } e ? ctx.Url(PortalRoutes.DeployEvent(e)) : null,
                url = ctx.Url(PortalRoutes.Migration(r.Id)),
            }),
        });
    }

    [McpServerTool(Name = "find_migration", Title = "Find a migration", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Has a migration run yet, and where? Matches the name partially and fuzzily (typos, word order) — or the script's " +
                 "order number — and reports per service and environment: applied (with when and in which version), failed, " +
                 "rolled back, or not run. An environment missing from the hits has no captured output mentioning it, which for a " +
                 "migration applied before the portal recorded migrations is not proof it never ran.")]
    public async Task<string> FindMigration(
        [Description("Migration name or part of it, e.g. \"AlterAccountName\" or \"account name\", or its order number \"4650\".")] string query,
        [Description("Product name.")] string? product = null,
        [Description("Environment, e.g. \"staging\".")] string? environment = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Trim().Length < 2) throw new McpException("`query` needs at least 2 characters.");
        var result = await migrations.SearchAsync(query, await ctx.OptionalProductAsync(product, ct),
            await ctx.EnvironmentFilterAsync(environment, ct), null, 10, ct);
        return ctx.Result(new
        {
            result.Query,
            matches = result.Matches.Select(m => new
            {
                m.Name,
                exact = m.Score == 100,
                where = m.Hits.Select(h => new
                {
                    h.Product, h.Service, h.Environment, h.Kind, h.Status, h.AppliedAt, h.Version, h.Error,
                    url = ctx.Url(PortalRoutes.Migration(h.RunId)),
                }),
            }),
            result.Truncated,
            searchUrl = ctx.Url(PortalRoutes.MigrationSearch(query)),
            note = result.Matches.Count == 0 ? "No migration name on record is like that. Migrations are recorded from deploy and data migration output; one that ran before that has no record." : null,
        });
    }
}
