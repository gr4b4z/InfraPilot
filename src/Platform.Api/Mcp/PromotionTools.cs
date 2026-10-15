using System.ComponentModel;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Platform.Api.Agent;
using Platform.Api.Features.Builds;
using Platform.Api.Features.Deployments;
using Platform.Api.Features.Deployments.Models;
using Platform.Api.Features.Promotions;
using Platform.Api.Features.Promotions.Models;
using Platform.Api.Features.Settings;
using Platform.Api.Infrastructure.Auth;
using Platform.Api.Infrastructure.Persistence;

namespace Platform.Api.Mcp;

[McpServerToolType]
public sealed class PromotionTools(
    McpContext ctx, PromotionService promotions, DeploymentService deployments,
    ParticipantRoleCatalog roleCatalog, ServiceProductOverrideService productOverrides,
    ICurrentUser user, PlatformDbContext db)
{
    [McpServerTool(Name = "list_promotions", Title = "List promotions", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Promotion candidates, newest first, with what each is still waiting on (approval steps, or its work items). " +
                 "Filter by status, product, service, target environment, or a reference (ticket key, PR, commit) the change carries.")]
    public async Task<string> ListPromotions(
        [Description("Pending, Approved, Deploying, Deployed, Rejected or Superseded. Omit for all.")] string? status = null,
        [Description("Product name.")] string? product = null,
        [Description("Service name (substring).")] string? service = null,
        [Description("Target environment.")] string? targetEnv = null,
        [Description("Text a reference must contain: a work-item key such as \"MPT-1234\", a PR number, a commit SHA.")] string? reference = null,
        [Description("Maximum rows (1-200). Default 25, or 50 with a status.")] int? limit = null,
        CancellationToken ct = default)
    {
        PromotionStatus? parsed = null;
        if (!string.IsNullOrWhiteSpace(status))
        {
            if (!Enum.TryParse<PromotionStatus>(status.Trim(), ignoreCase: true, out var s))
                throw new McpException($"Unknown status '{status}'. Use one of: {string.Join(", ", Enum.GetNames<PromotionStatus>())}.");
            parsed = s;
        }

        var p = await ctx.OptionalProductAsync(product, ct);
        var env = await ctx.EnvironmentFilterAsync(targetEnv, ct);
        var cap = Math.Clamp(limit ?? (parsed is null ? 25 : 50), 1, 200);
        var needle = reference?.Trim() ?? "";

        // The reference filter runs in memory over each candidate's own change set, as REST does, so
        // fetch past the cap when it is set rather than filter a page that may hold none of them.
        var candidates = await promotions.GetAsync(
            new PromotionQuery(parsed, p, service?.Trim(), env, needle.Length > 0 ? 500 : cap), ct);
        if (needle.Length > 0)
            candidates = candidates.Where(c => c.References.Any(r =>
                Has(r.Key, needle) || Has(r.Revision, needle) || Has(r.Title, needle) || Has(r.Url, needle))).ToList();
        candidates = candidates.Take(cap).ToList();

        var gates = await promotions.GetGateStatusesAsync(candidates, ct);
        return ctx.Result(new
        {
            promotions = candidates.Select(c => new
            {
                id = c.Id,
                product = c.Product,
                service = c.Service,
                edge = $"{c.SourceEnv} → {c.TargetEnv}",
                version = c.Version,
                fromVersion = c.FromVersion,
                status = c.Status.ToString(),
                waitingOn = WaitingOn(gates.GetValueOrDefault(c.Id)),
                workItems = c.References.Count(r => r.Type == "work-item") is var n and > 0 ? n : (int?)null,
                createdAt = c.CreatedAt,
                approvedAt = c.ApprovedAt,
                deployedAt = c.DeployedAt,
                url = ctx.Url(PortalRoutes.Promotion(c.Id)),
            }),
            note = candidates.Count == 0 ? "No promotions match." : null,
        });
    }

    [McpServerTool(Name = "get_promotion", Title = "Promotion detail", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("One promotion in full: versions on both sides, approval progress per step (and who can approve each requirement), " +
                 "the approvals and comments so far, its change set (work items, PRs, commits), people, failed deploy attempts, and " +
                 "the deploy that landed it. If it looks stuck, diagnose_promotion explains why.")]
    public async Task<string> GetPromotion(
        [Description("Promotion id (GUID).")] string promotionId,
        CancellationToken ct = default)
    {
        var id = McpContext.Id(promotionId, "promotion", "list_promotions");
        var c = await promotions.GetByIdAsync(id, ct) ?? throw new McpException($"No promotion with id {id}.");

        var approvals = await promotions.GetApprovalsAsync(id, ct);
        var comments = await promotions.GetCommentsAsync(id, ct);
        var progress = await promotions.GetApprovalProgressAsync(c, ct);
        var target = (await deployments.GetStateSummary(c.Product, c.TargetEnv, c.Service, ct))
            .FirstOrDefault(r => r.Service == c.Service);

        var failed = await db.DeployEvents.AsNoTracking()
            .Where(e => e.Product == c.Product && e.Service == c.Service && e.Environment == c.TargetEnv
                     && e.Version == c.Version && e.Status == "failed")
            .OrderByDescending(e => e.DeployedAt)
            .Take(10)
            .Select(e => new { id = e.Id, e.DeployedAt, e.Source })
            .ToListAsync(ct);

        // Completion is matched on (product, service, targetEnv, version), as the detail endpoint does.
        Guid? landedBy = null;
        if (c.Status == PromotionStatus.Deployed)
            landedBy = await db.DeployEvents.AsNoTracking()
                .Where(e => e.Product == c.Product && e.Service == c.Service && e.Environment == c.TargetEnv
                         && e.Version == c.Version && e.Status == "succeeded")
                .OrderBy(e => e.DeployedAt)
                .Select(e => (Guid?)e.Id)
                .FirstOrDefaultAsync(ct);

        return ctx.Result(new
        {
            id = c.Id,
            product = c.Product,
            service = c.Service,
            sourceEnv = c.SourceEnv,
            targetEnv = c.TargetEnv,
            version = c.Version,
            fromVersion = c.FromVersion,
            targetCurrentVersion = target?.Version,
            fromRevision = c.FromRevision,
            toRevision = c.ToRevision,
            status = c.Status.ToString(),
            createdAt = c.CreatedAt,
            approvedAt = c.ApprovedAt,
            deployedAt = c.DeployedAt,
            supersededById = c.SupersededById,
            externalRunUrl = c.ExternalRunUrl,
            url = ctx.Url(PortalRoutes.Promotion(c.Id)),
            approval = new
            {
                progress.RequiresApproval,
                progress.AllSatisfied,
                approved = $"{progress.TotalApproved}/{progress.TotalRequired}",
                steps = progress.Steps.Select(s => new
                {
                    s.Name,
                    s.Satisfied,
                    requirements = s.Requirements.Select(r => new
                    {
                        r.Name,
                        approved = $"{r.Approved}/{r.Required}",
                        r.Satisfied,
                        approverGroups = r.Groups.Count == 0 ? null : r.Groups,
                        approverUsers = r.Users.Count == 0 ? null : r.Users,
                    }),
                }),
                workItems = progress.WorkItems,
            },
            approvals = approvals.Select(a => new
            {
                by = a.ApproverName,
                email = a.ApproverEmail,
                decision = a.Decision.ToString(),
                step = a.StepName,
                requirement = a.RequirementName,
                a.Comment,
                at = a.CreatedAt,
            }),
            comments = comments.Select(Comment),
            changeSet = McpShapes.References(c.References),
            participants = c.Participants.Select(pp => new { role = pp.Role, name = pp.DisplayName, email = pp.Email }),
            failedDeployments = failed.Count == 0 ? null : failed,
            landedByEventId = landedBy,
        });
    }

    [McpServerTool(Name = "create_promotion", Title = "Create promotion", Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Opens a promotion candidate: service `version` moves `sourceEnv` → `targetEnv` under that edge's approval policy. " +
                 "The version must have a succeeded deploy in the source environment. Idempotent on (product, service, edge, version); " +
                 "a newer version on the same edge supersedes the pending older one. This is the release pipelines' action: it " +
                 "needs a configured pipeline API key with the promotion:create scope. To deploy a registered build, use promote_build.")]
    public async Task<string> CreatePromotion(
        [Description("Product name.")] string product,
        [Description("Service name.")] string service,
        [Description("Source environment the version is already deployed to.")] string sourceEnv,
        [Description("Target environment.")] string targetEnv,
        [Description("Version to promote, exactly as deployed.")] string version,
        [Description("Work-item keys the change carries (e.g. [\"MPT-1234\"]). Only work items feed the approval gate.")] string[]? workItems = null,
        [Description("Commit the target currently runs.")] string? fromRevision = null,
        [Description("Commit being promoted.")] string? toRevision = null,
        CancellationToken ct = default)
    {
        if (!ctx.IsPipelineKey)
            throw new McpException("Creating a promotion is a pipeline action and needs a configured pipeline API key. " +
                                   "Personal tokens and signed-in users can deploy a registered build with promote_build.");
        if (!ApiKeyAuthHandler.HasScope(ctx.User, ApiKeyScopes.PromotionCreate))
            throw new McpException($"This API key lacks the '{ApiKeyScopes.PromotionCreate}' scope.");

        var (p, s) = await ctx.ServiceAsync(product, service, ct);
        // Checked against the product as resolved here, which is the name the data uses — the same
        // name the REST endpoint compares when a pipeline sends it.
        if (!ctx.MayActFor(p))
            throw new McpException($"This API key may not act for product '{p}'. It is limited to: {string.Join(", ", ctx.AllowedProducts)}.");
        if (string.IsNullOrWhiteSpace(version)) throw new McpException("`version` is required.");

        var dto = new CreatePromotionDto(
            Product: p,
            Service: s,
            SourceEnv: await ctx.EnvironmentAsync(sourceEnv, ct),
            TargetEnv: await ctx.EnvironmentAsync(targetEnv, ct),
            Version: version.Trim(),
            FromRevision: fromRevision,
            ToRevision: toRevision,
            References: workItems?.Where(k => !string.IsNullOrWhiteSpace(k))
                .Select(k => new ReferenceDto("work-item", Key: k.Trim())).ToList(),
            Participants: [new ParticipantDto("triggered-by", user.Name, string.IsNullOrEmpty(user.Email) ? null : user.Email)]);

        PromotionCandidate? candidate;
        try
        {
            candidate = await promotions.CreateExternalCandidateAsync(dto, ct);
        }
        catch (SourceDeploymentNotFoundException ex) { throw new McpException($"source_deploy_missing: {ex.Message}"); }
        catch (TargetAlreadyAtVersionException ex) { throw new McpException($"target_already_at_version: {ex.Message}"); }

        if (candidate is null)
        {
            var policyProduct = await productOverrides.ResolveProductAsync(p, s, ct);
            throw new McpException($"policy_missing: no promotion policy is configured for '{policyProduct}'/'{s}' '{dto.SourceEnv}' → '{dto.TargetEnv}'.");
        }

        return ctx.Result(new { id = candidate.Id, status = candidate.Status.ToString(), url = ctx.Url(PortalRoutes.Promotion(candidate.Id)) });
    }

    [McpServerTool(Name = "promote_build", Title = "Promote a registered build", Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Deploys a registered build to an environment by opening a promotion from it — the portal's \"deploy this build\". " +
                 "The change set comes from the build record and you are recorded as triggered-by. The edge's approval policy " +
                 "still applies; auto-approve edges dispatch right away. get_build lists the environments a build can go to.")]
    public async Task<string> PromoteBuild(
        [Description("Build id (GUID), from list_builds.")] string buildId,
        [Description("Target environment.")] string targetEnv,
        CancellationToken ct = default)
    {
        var id = McpContext.Id(buildId, "build", "list_builds");
        var build = await db.Builds.AsNoTracking().FirstOrDefaultAsync(b => b.Id == id, ct)
            ?? throw new McpException($"Build {id} is not registered.");
        var env = await ctx.EnvironmentAsync(targetEnv, ct);

        var dto = new CreatePromotionDto(
            Product: build.Product,
            Service: build.Service,
            SourceEnv: BuildPromotions.SourceEnv,
            TargetEnv: env,
            Version: build.Version,
            FromRevision: null,
            ToRevision: build.CommitSha,
            References: BuildPromotions.BuildReferences(build),
            Participants: [new ParticipantDto("triggered-by", user.Name, string.IsNullOrEmpty(user.Email) ? null : user.Email)]);

        PromotionCandidate? candidate;
        try
        {
            candidate = await promotions.CreateExternalCandidateAsync(dto, ct);
        }
        catch (SourceDeploymentNotFoundException)
        {
            throw new McpException($"source_deploy_missing: the '{BuildPromotions.SourceEnv}' → '{env}' policy requires a source " +
                                   "deployment, which the build source never has. An admin must set sourceRequiresDeploy=false on it.");
        }
        catch (TargetAlreadyAtVersionException ex) { throw new McpException($"target_already_at_version: {ex.Message}"); }

        if (candidate is null)
            throw new McpException($"policy_missing: '{build.Product}'/'{build.Service}' has no '{BuildPromotions.SourceEnv}' → '{env}' " +
                                   "promotion policy. get_build lists the environments this build can be promoted to.");

        return ctx.Result(new { id = candidate.Id, status = candidate.Status.ToString(), url = ctx.Url(PortalRoutes.Promotion(candidate.Id)) });
    }

    [McpServerTool(Name = "add_promotion_comment", Title = "Comment on a promotion", Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Adds a comment to a promotion's thread, authored as you. Comments are visible to everyone in the portal.")]
    public async Task<string> AddPromotionComment(
        [Description("Promotion id (GUID).")] string promotionId,
        [Description("Comment text (Markdown).")] string body,
        CancellationToken ct = default)
    {
        var id = McpContext.Id(promotionId, "promotion", "list_promotions");
        try
        {
            var comment = await promotions.AddCommentAsync(id, body, ct);
            return ctx.Result(Comment(comment));
        }
        catch (KeyNotFoundException) { throw new McpException($"No promotion with id {id}."); }
        catch (InvalidOperationException ex) { throw new McpException(ex.Message); }
    }

    [McpServerTool(Name = "set_promotion_participant", Title = "Assign a promotion participant", Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Puts a person in a role on a promotion (e.g. qa-owner), replacing whoever held it. The role must be one configured " +
                 "in the portal's Participant Roles; an unknown role fails with the configured list.")]
    public async Task<string> SetPromotionParticipant(
        [Description("Promotion id (GUID).")] string promotionId,
        [Description("Role key.")] string role,
        [Description("The person's email.")] string? email = null,
        [Description("The person's display name.")] string? displayName = null,
        CancellationToken ct = default)
    {
        var id = McpContext.Id(promotionId, "promotion", "list_promotions");
        if (string.IsNullOrWhiteSpace(email) && string.IsNullOrWhiteSpace(displayName))
            throw new McpException("Give the person's `email` (preferred) or `displayName`. To clear a role, use remove_promotion_participant.");
        if (!await roleCatalog.IsConfiguredAsync(role))
            throw new McpException(ParticipantRoleCatalog.RejectionMessage(role, await roleCatalog.GetCanonicalKeysAsync()));
        try
        {
            var updated = await promotions.UpsertParticipantAsync(id, new PromotionParticipant(role, displayName, email), ct);
            return ctx.Result(new { participants = updated.Participants });
        }
        catch (KeyNotFoundException) { throw new McpException($"No promotion with id {id}."); }
        catch (InvalidOperationException ex) { throw new McpException(ex.Message); }
    }

    [McpServerTool(Name = "remove_promotion_participant", Title = "Remove a promotion participant", Destructive = true, Idempotent = true, OpenWorld = false)]
    [Description("Clears a role on a promotion.")]
    public async Task<string> RemovePromotionParticipant(
        [Description("Promotion id (GUID).")] string promotionId,
        [Description("Role key to clear.")] string role,
        CancellationToken ct = default)
    {
        var id = McpContext.Id(promotionId, "promotion", "list_promotions");
        try
        {
            var updated = await promotions.RemoveParticipantAsync(id, role, ct);
            return ctx.Result(new { participants = updated.Participants });
        }
        catch (KeyNotFoundException) { throw new McpException($"No promotion with id {id}."); }
    }

    private static object Comment(PromotionComment c) => new
    {
        id = c.Id,
        by = c.AuthorName,
        email = string.IsNullOrEmpty(c.AuthorEmail) ? null : c.AuthorEmail,
        body = c.Body,
        at = c.CreatedAt,
        editedAt = c.UpdatedAt,
    };

    /// <summary>
    /// What a Pending candidate still needs, in words: the work items when their gate holds it (no
    /// step can be signed before they clear), otherwise the approval steps short of sign-offs.
    /// </summary>
    private static IReadOnlyList<string>? WaitingOn(PromotionGateStatus? gate)
    {
        if (gate is null) return null;
        if (gate.WorkItemsOutstanding) return ["work items"];
        return gate.OutstandingSteps.Count == 0 ? null : gate.OutstandingSteps.Select(PromotionService.GateName).ToList();
    }

    private static bool Has(string? value, string needle) =>
        value is not null && value.Contains(needle, StringComparison.OrdinalIgnoreCase);
}
