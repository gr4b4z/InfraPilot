using System.ComponentModel;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Platform.Api.Features.Builds;
using Platform.Api.Features.Builds.Models;
using Platform.Api.Features.Promotions;
using Platform.Api.Infrastructure.Persistence;

namespace Platform.Api.Mcp;

[McpServerToolType]
public sealed class BuildTools(McpContext ctx, BuildService builds, PlatformDbContext db)
{
    [McpServerTool(Name = "list_builds", Title = "List builds", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Registered builds, newest first, with where each has been deployed (and whether it is still current there). " +
                 "Filter by product, service, branch (substring), exact version, free text, or registration time.")]
    public async Task<string> ListBuilds(
        [Description("Product name.")] string? product = null,
        [Description("Service name.")] string? service = null,
        [Description("Branch, substring of the full ref — e.g. \"release/5\" or \"main\".")] string? branch = null,
        [Description("Exact version, to find the one build behind it.")] string? version = null,
        [Description("Free text matched against service, version, branch and commit.")] string? query = null,
        [Description("ISO-8601: only builds registered at or after this time.")] DateTimeOffset? since = null,
        [Description("Maximum rows (1-200, default 20).")] int limit = 20,
        CancellationToken ct = default)
    {
        string? p = null, s = null;
        if (!string.IsNullOrWhiteSpace(service)) (p, s) = await ctx.ServiceAsync(product, service, ct);
        else p = await ctx.OptionalProductAsync(product, ct);

        var results = await builds.ListAsync(
            new BuildQuery(p, s, branch?.Trim(), version?.Trim(), query?.Trim(), since), Math.Clamp(limit, 1, 200), ct);

        return ctx.Result(new
        {
            builds = results.Select(b => new
            {
                id = b.Id,
                product = b.Product,
                service = b.Service,
                version = b.Version,
                branch = b.Branch,
                commitSha = b.CommitSha,
                buildUrl = b.BuildUrl,
                createdAt = b.CreatedAt,
                deployedTo = b.Deployments.Count == 0 ? null : b.Deployments.Select(Deployment),
            }),
            note = results.Count == 0 ? "No builds match." : null,
        });
    }

    [McpServerTool(Name = "get_build", Title = "Build detail", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("One registered build: its source (branch, commit, CI run), artifact, the environments it can be promoted to with " +
                 "promote_build (and whether each auto-approves), and optionally its manifest.")]
    public async Task<string> GetBuild(
        [Description("Build id (GUID).")] string buildId,
        [Description("Include the build's inline manifest (can be large). Default false.")] bool includeManifest = false,
        CancellationToken ct = default)
    {
        var id = McpContext.Id(buildId, "build", "list_builds");
        var b = await builds.GetAsync(id, ct) ?? throw new McpException($"Build {id} is not registered.");

        // The edges out of the synthetic "build" environment, resolved as the build-targets endpoint
        // does: a service-specific policy wins over the product default for the same target.
        var policies = await db.PromotionPolicies.AsNoTracking()
            .Where(x => x.Product == b.Product && x.SourceEnv == BuildPromotions.SourceEnv
                     && (x.Service == b.Service || x.Service == null))
            .ToListAsync(ct);
        var targets = policies
            .GroupBy(x => x.TargetEnv)
            .Select(g => g.OrderBy(x => x.Service == null ? 1 : 0).First())
            .OrderBy(x => x.TargetEnv)
            .Select(x => new { targetEnv = x.TargetEnv, autoApprove = PromotionPolicyResolver.Project(x).IsAutoApprove })
            .ToList();

        return ctx.Result(new
        {
            id = b.Id,
            product = b.Product,
            service = b.Service,
            version = b.Version,
            branch = b.Branch,
            commitSha = b.CommitSha,
            ciBuildId = b.BuildId,
            buildUrl = b.BuildUrl,
            artifactRef = b.ArtifactRef,
            artifactDigest = b.ArtifactDigest,
            createdAt = b.CreatedAt,
            updatedAt = b.UpdatedAt,
            promotableTo = targets,
            note = targets.Count == 0 ? $"{b.Product} has no build promotion policies, so promote_build cannot deploy this build." : null,
            manifest = includeManifest ? b.Manifest : null,
        });
    }

    private static object Deployment(BuildDeploymentDto d) => new
    {
        d.Environment,
        d.Status,
        d.DeployedAt,
        current = d.IsCurrent,
        eventId = d.EventId,
    };
}
