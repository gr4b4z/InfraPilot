using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Platform.Api.Agent;
using Platform.Api.Features.Promotions;
using Platform.Api.Features.Promotions.Models;

namespace Platform.Api.Mcp;

/// <summary>
/// Work items — the tickets a promotion's change set carries, signed off per (key, product, service,
/// target environment). Reads and comments only: sign-off is a reviewer's decision made in the portal.
/// </summary>
[McpServerToolType]
public sealed class WorkItemTools(McpContext ctx, WorkItemApprovalService workItems)
{
    [McpServerTool(Name = "get_work_item", Title = "Work item detail", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("A work item (ticket) on one promotion edge: title and tracker link, where the change is deployed, the roles its " +
                 "policy requires and which are unfilled, the sign-off decisions and comments, its commits and PRs, and the " +
                 "promotions carrying it. A ticket carried by several services of a product is one work item per service; " +
                 "without `service`, the call lists them if there is more than one. To find the promotions carrying a ticket, " +
                 "use list_promotions with `reference`.")]
    public async Task<string> GetWorkItem(
        [Description("Ticket key, e.g. \"MPT-1234\".")] string key,
        [Description("Product name.")] string product,
        [Description("Target environment of the promotion edge, e.g. \"production\".")] string targetEnv,
        [Description("Service carrying the ticket. Optional when only one does.")] string? service = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(key)) throw new McpException("`key` is required.");
        var p = await ctx.ProductAsync(product, ct);
        var env = await ctx.EnvironmentAsync(targetEnv, ct);
        var s = await ServiceForAsync(key.Trim(), p, env, service, ct);

        var d = await workItems.GetDetailAsync(key.Trim(), p, s, env, ct)
            ?? throw new McpException($"Work item '{key}' not found for {p}/{s} → {env}.");

        return ctx.Result(new
        {
            key = d.WorkItemKey,
            product = d.Product,
            service = d.Service,
            targetEnv = d.TargetEnv,
            title = d.Title,
            subTitle = d.SubTitle,
            type = d.WorkItemType,
            d.Priority,
            trackerUrl = d.Url,
            url = ctx.Url(PortalRoutes.WorkItem(d.Service, d.WorkItemKey)),
            content = McpShapes.Clip(d.Content, 2000),
            deployedTo = d.Environments.Select(e => new { e.Environment, e.Version, e.DeployedAt }),
            blockedReason = d.BlockedReason,
            requiredRoles = d.RequiredRoles,
            missingRoles = d.MissingRoles.Count == 0 ? null : d.MissingRoles,
            participants = McpShapes.People(d.Participants),
            decisions = d.Approvals.Select(a => new
            {
                by = a.ApproverName,
                email = a.ApproverEmail,
                decision = a.Decision.ToString(),
                a.Comment,
                at = a.UpdatedAt ?? a.CreatedAt,
            }),
            comments = d.Comments.Select(Comment),
            commits = d.Commits.Select(c => new { c.Hash, c.Title, c.Url }),
            pullRequests = d.PullRequests.Select(pr => new { pr.Key, pr.Title, pr.Url }),
            promotions = d.Candidates.Select(c => new
            {
                id = c.Id,
                c.Service,
                edge = $"{c.SourceEnv} → {c.TargetEnv}",
                c.Version,
                c.Status,
                c.CreatedAt,
                primary = c.IsPrimary ? true : (bool?)null,
            }),
            overall = d.Overall,
        });
    }

    [McpServerTool(Name = "add_work_item_comment", Title = "Comment on a work item", Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Adds a comment to a work item's thread on one promotion edge, authored as you. The thread outlives superseded " +
                 "promotions, so it is the place for notes about the ticket itself.")]
    public async Task<string> AddWorkItemComment(
        [Description("Ticket key, e.g. \"MPT-1234\".")] string key,
        [Description("Product name.")] string product,
        [Description("Target environment of the promotion edge.")] string targetEnv,
        [Description("Comment text (Markdown).")] string body,
        [Description("Service carrying the ticket. Optional when only one does.")] string? service = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(key)) throw new McpException("`key` is required.");
        var p = await ctx.ProductAsync(product, ct);
        var env = await ctx.EnvironmentAsync(targetEnv, ct);
        var s = await ServiceForAsync(key.Trim(), p, env, service, ct);
        try
        {
            return ctx.Result(Comment(await workItems.AddCommentAsync(key.Trim(), p, s, env, body, ct)));
        }
        catch (KeyNotFoundException ex) { throw new McpException(ex.Message); }
        catch (InvalidOperationException ex) { throw new McpException(ex.Message); }
    }

    /// <summary>
    /// The service half of a work item's identity: as given (name-matched), or the only service of the
    /// product carrying the ticket on that edge.
    /// </summary>
    private async Task<string> ServiceForAsync(string key, string product, string env, string? service, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(service))
            return (await ctx.ServiceAsync(product, service, ct)).Service;

        var overall = (await workItems.GetOverallStatusAsync(key, product, env, ct))?.ToSummary();
        var instances = overall?.InstanceStatuses ?? [];
        return instances.Count switch
        {
            1 => instances[0].Service,
            0 => throw new McpException($"No promotion of {product} → {env} carries '{key}'. Check the key and edge, or search with list_promotions(reference: \"{key}\")."),
            _ => throw new McpException($"'{key}' is carried by several {product} services on → {env}: " +
                                        $"{string.Join(", ", instances.Select(i => $"{i.Service} ({i.State})"))}. Pass `service`."),
        };
    }

    private static object Comment(WorkItemComment c) => new
    {
        id = c.Id,
        by = c.AuthorName,
        email = string.IsNullOrEmpty(c.AuthorEmail) ? null : c.AuthorEmail,
        body = c.Body,
        decision = c.Decision?.ToString(),
        at = c.CreatedAt,
        editedAt = c.UpdatedAt,
    };
}
