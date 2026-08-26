using Microsoft.EntityFrameworkCore;
using Platform.Api.Features.Promotions.Models;
using Platform.Api.Features.Settings;
using Platform.Api.Infrastructure;
using Platform.Api.Infrastructure.Auth;
using Platform.Api.Infrastructure.Persistence;

namespace Platform.Api.Features.Promotions;

/// <summary>
/// Admin-only endpoints for configuring the promotion machinery: policies per product/service/env
/// and the environment topology. Mounted under <c>/api/promotions/admin</c> and gated by
/// <see cref="AuthorizationPolicies.CatalogAdmin"/>.
/// </summary>
public static class PromotionAdminEndpoints
{
    public static RouteGroupBuilder MapPromotionAdminEndpoints(this RouteGroupBuilder group)
    {
        // ── Candidate bypass (admin escape hatch) ───────────────────────────
        // Force a Pending candidate to Approved without satisfying its gate. Admin-only via the
        // group's CatalogAdmin policy. A reason is required; the existing promotion.approved webhook
        // still fires so downstream automation is unchanged.
        group.MapPost("/candidates/{id:guid}/bypass", async (
            PromotionService service, Guid id, BypassPromotionRequest? request, CancellationToken ct) =>
        {
            try
            {
                var candidate = await service.BypassAsync(id, request?.Reason ?? "", ct);
                return Results.Ok(new { candidate.Id, status = candidate.Status.ToString() });
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound();
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        // ── Completion reconciliation ───────────────────────────────────────
        // Settles open promotions that the target environment's deploy history has already decided:
        // closes the ones whose version shipped, supersedes the ones a newer version overtook.
        //
        // The repair pass for promotions stranded while completion lived only on the deploy-ingest path.
        // A promotion created after its own version had landed had no future event left to match it, and
        // an approved one the environment then passed by had nothing to retire it — both sat in
        // "approved, awaiting deploy" permanently.
        //
        // Evidence-driven throughout (see PromotionService.AssessAgainstDeployHistoryAsync): promotions
        // whose version never reached the target, or only failed there, or whose target rolled back, are
        // left exactly as they are. `dryRun=true` reports without writing — run it that way first.
        group.MapPost("/candidates/reconcile-completions", async (
            PromotionService service, EnvironmentAliasResolver environments,
            ReconcileCompletionsRequest? request, CancellationToken ct) =>
        {
            var result = await service.ReconcileCompletionsAsync(
                request?.Product,
                await environments.ResolveFilterAsync(request?.TargetEnv, ct),
                request?.DryRun ?? false, ct);

            return Results.Ok(new
            {
                examined = result.Examined,
                closed = result.Closed,
                superseded = result.Superseded,
                leftOpen = result.Examined - result.Closed - result.Superseded,
                dryRun = result.DryRun,
                candidates = result.Candidates.Select(c => new
                {
                    c.Id,
                    c.Product,
                    c.Service,
                    c.SourceEnv,
                    c.TargetEnv,
                    c.Version,
                    c.PreviousStatus,
                    c.Action,
                    c.At,
                    c.LandedVersion,
                }),
            });
        });

        // ── Stranded work items (Settings → Maintenance) ─────────────────────
        // Signs off every work item in the "No live promotion" state: its promotions were all
        // superseded or rejected, so no gate will ever consume the sign-off and no deploy will ever
        // retire the row — it sits in the work-item queue as pending work forever.
        //
        // Items a live promotion still carries are ordinary pending work and are never touched, and
        // neither is an item somebody already decided (an Issue or a Block is a deliberate hold).
        // See WorkItemApprovalService.ApproveOrphanedWorkItemsAsync. `dryRun=true` reports the list
        // without writing — the Maintenance card always previews first.
        group.MapPost("/work-items/approve-orphaned", async (
            WorkItemApprovalService service, ApproveOrphanedWorkItemsRequest? request, CancellationToken ct) =>
        {
            try
            {
                var result = await service.ApproveOrphanedWorkItemsAsync(request?.DryRun ?? false, ct);
                return Results.Ok(new
                {
                    examined = result.Examined,
                    approved = result.Approved,
                    failed = result.Failed,
                    dryRun = result.DryRun,
                    items = result.Items.Select(i => new
                    {
                        i.WorkItemKey,
                        i.Title,
                        i.Product,
                        i.TargetEnv,
                        i.Service,
                        i.Version,
                        i.CandidateStatus,
                        i.Error,
                    }),
                });
            }
            catch (UnauthorizedAccessException ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status403Forbidden);
            }
        });

        // ── Duplicate candidates (Settings → Maintenance) ────────────────────
        // Residue of a pre-D15 create path that minted a new row per external POST instead of
        // reusing the natural key — production carries groups of up to six copies of one promotion.
        // See PromotionService for what qualifies as a duplicate (deliberately narrower than
        // "same natural key": legitimate re-promote history is excluded). Preview via GET, then
        // DELETE — same contract as the deploy-event duplicates pair.
        group.MapGet("/duplicates", async (PromotionService service, CancellationToken ct) =>
        {
            var (groups, rows) = await service.CountDuplicateCandidatesAsync(ct);
            return Results.Ok(new { groups, rows });
        });

        group.MapDelete("/duplicates", async (PromotionService service, CancellationToken ct) =>
        {
            var (groups, rows) = await service.RemoveDuplicateCandidatesAsync(ct);
            return Results.Ok(new { groups, rows });
        });

        // ── Policies ────────────────────────────────────────────────────────

        group.MapGet("/policies", async (PlatformDbContext db) =>
        {
            var rows = await db.PromotionPolicies.AsNoTracking()
                .OrderBy(p => p.Product).ThenBy(p => p.Service).ThenBy(p => p.TargetEnv)
                .ToListAsync();
            return Results.Ok(new { policies = rows.Select(p => MapPolicy(p)) });
        });

        group.MapGet("/policies/{id:guid}", async (PlatformDbContext db, Guid id) =>
        {
            var row = await db.PromotionPolicies.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id);
            return row is null ? Results.NotFound() : Results.Ok(MapPolicy(row));
        });

        group.MapPost("/policies", async (
            PlatformDbContext db, PromotionService promotions, ICurrentUser user,
            EnvironmentAliasResolver environments, UpsertPolicyRequest request, CancellationToken ct) =>
        {
            var error = ValidatePolicyRequest(request);
            if (error is not null) return Results.BadRequest(new { error });

            // A policy is stored against the canonical environment so it governs every name the
            // edge answers to. An admin who types an alias here would otherwise create a second
            // policy for an edge that already has one, and neither would ever resolve for half the
            // traffic.
            request = await ResolveEnvironments(request, environments, ct);

            // Duplicate-check: the DB-level unique index on (Product, Service, SourceEnv, TargetEnv)
            // is the hard guard; this pre-check lets us return a friendly 409 instead of a 500 from EF.
            var existing = await db.PromotionPolicies
                .FirstOrDefaultAsync(p =>
                    p.Product == request.Product
                    && p.Service == request.Service
                    && p.SourceEnv == request.SourceEnv
                    && p.TargetEnv == request.TargetEnv);
            if (existing is not null)
                return Results.Conflict(new { error = "A policy for this (product, service, source_env, target_env) already exists" });

            var now = DateTimeOffset.UtcNow;
            var policy = new PromotionPolicy
            {
                Id = Guid.NewGuid(),
                Product = request.Product,
                Service = string.IsNullOrWhiteSpace(request.Service) ? null : request.Service,
                SourceEnv = request.SourceEnv,
                TargetEnv = request.TargetEnv,
                ApprovalSteps = MapSteps(request.Steps),
                TracksWorkItems = request.TracksWorkItems,
                RequiredWorkItemRoles = MapRequiredRoles(request.RequiredWorkItemRoles),
                EscalationGroup = string.IsNullOrWhiteSpace(request.EscalationGroup) ? null : request.EscalationGroup,
                RequireAllWorkItemsApproved = request.RequireAllWorkItemsApproved,
                AutoApproveOnAllWorkItemsApproved = request.AutoApproveOnAllWorkItemsApproved,
                AutoApproveWhenNoWorkItems = request.AutoApproveWhenNoWorkItems,
                SourceRequiresDeploy = request.SourceRequiresDeploy,
                AutoCreateFromBranches = MapBranchPatterns(request.AutoCreateFromBranches),
                ApprovedWebhookDelaySeconds = request.ApprovedWebhookDelaySeconds,
                CreatedAt = now,
                UpdatedAt = now,
            };
            db.PromotionPolicies.Add(policy);
            await db.SaveChangesAsync();

            // A new policy can out-specify what pending candidates on this edge were created under
            // (a fresh service-specific row overriding the product default), so re-apply it to them.
            var reapplied = await promotions.RefreshPolicySnapshotsAsync(
                policy.Product, policy.Service, policy.SourceEnv, policy.TargetEnv, ct);

            return Results.Created(
                $"/api/promotions/admin/policies/{policy.Id}",
                MapPolicy(policy, reapplied));
        });

        group.MapPut("/policies/{id:guid}", async (
            PlatformDbContext db, PromotionService promotions, ICurrentUser user, Guid id,
            EnvironmentAliasResolver environments, UpsertPolicyRequest request, CancellationToken ct) =>
        {
            var error = ValidatePolicyRequest(request);
            if (error is not null) return Results.BadRequest(new { error });

            request = await ResolveEnvironments(request, environments, ct);

            var policy = await db.PromotionPolicies.FirstOrDefaultAsync(p => p.Id == id);
            if (policy is null) return Results.NotFound();

            // The edit may re-scope the policy. Candidates under the OLD scope lose it (and fall back
            // to whatever else resolves), candidates under the NEW scope gain it — both need a refresh.
            var oldScope = (policy.Product, policy.Service, policy.SourceEnv, policy.TargetEnv);

            policy.Product = request.Product;
            policy.Service = string.IsNullOrWhiteSpace(request.Service) ? null : request.Service;
            policy.SourceEnv = request.SourceEnv;
            policy.TargetEnv = request.TargetEnv;
            policy.ApprovalSteps = MapSteps(request.Steps);
            policy.TracksWorkItems = request.TracksWorkItems;
            policy.RequiredWorkItemRoles = MapRequiredRoles(request.RequiredWorkItemRoles);
            policy.EscalationGroup = string.IsNullOrWhiteSpace(request.EscalationGroup) ? null : request.EscalationGroup;
            policy.RequireAllWorkItemsApproved = request.RequireAllWorkItemsApproved;
            policy.AutoApproveOnAllWorkItemsApproved = request.AutoApproveOnAllWorkItemsApproved;
            policy.AutoApproveWhenNoWorkItems = request.AutoApproveWhenNoWorkItems;
            policy.SourceRequiresDeploy = request.SourceRequiresDeploy;
            policy.AutoCreateFromBranches = MapBranchPatterns(request.AutoCreateFromBranches);
            policy.ApprovedWebhookDelaySeconds = request.ApprovedWebhookDelaySeconds;
            policy.UpdatedAt = DateTimeOffset.UtcNow;

            await db.SaveChangesAsync();

            var newScope = (policy.Product, policy.Service, policy.SourceEnv, policy.TargetEnv);
            var reapplied = await promotions.RefreshPolicySnapshotsAsync(
                newScope.Product, newScope.Service, newScope.SourceEnv, newScope.TargetEnv, ct);
            if (oldScope != newScope)
                reapplied += await promotions.RefreshPolicySnapshotsAsync(
                    oldScope.Product, oldScope.Service, oldScope.SourceEnv, oldScope.TargetEnv, ct);

            return Results.Ok(MapPolicy(policy, reapplied));
        });

        group.MapDelete("/policies/{id:guid}", async (
            PlatformDbContext db, PromotionService promotions, Guid id, CancellationToken ct) =>
        {
            var policy = await db.PromotionPolicies.FirstOrDefaultAsync(p => p.Id == id);
            if (policy is null) return Results.NotFound();

            var scope = (policy.Product, policy.Service, policy.SourceEnv, policy.TargetEnv);
            db.PromotionPolicies.Remove(policy);
            await db.SaveChangesAsync();

            // Pending candidates on this edge now resolve to the product-default policy, if one exists.
            // If nothing resolves they keep their original snapshot rather than becoming auto-approve.
            await promotions.RefreshPolicySnapshotsAsync(
                scope.Product, scope.Service, scope.SourceEnv, scope.TargetEnv, ct);

            return Results.NoContent();
        });

        // Topology removed (D19): the external system is the sole source of truth for edges; the
        // policy-resolution 422 on create is the de-facto edge guard. No /topology routes.

        return group;
    }

    /// <summary>
    /// Response shape for a policy. <paramref name="reappliedCandidates"/> reports how many pending
    /// promotions were re-snapshotted under the saved settings, so the UI can tell the operator their
    /// change affected in-flight work; it is omitted on read-only responses.
    /// </summary>
    private static object MapPolicy(PromotionPolicy p, int? reappliedCandidates = null) => new
    {
        id = p.Id,
        product = p.Product,
        service = p.Service,
        sourceEnv = p.SourceEnv,
        targetEnv = p.TargetEnv,
        steps = p.ApprovalSteps.Select(s => new
        {
            name = s.Name,
            requirements = s.Requirements.Select(r => new
            {
                name = r.Name,
                groups = r.Groups,
                users = r.Users,
                minApprovers = r.MinApprovers,
            }),
        }),
        // False ⇒ promotions on this edge create no work items at all, which makes every other
        // work-item setting below inert. See PromotionPolicy.TracksWorkItems.
        tracksWorkItems = p.TracksWorkItems,
        // Canonical role keys every work item on this edge must have somebody in. Empty ⇒ no
        // requirement. Not a gate — see PromotionPolicy.RequiredWorkItemRoles.
        requiredWorkItemRoles = p.RequiredWorkItemRoles,
        escalationGroup = p.EscalationGroup,
        requireAllWorkItemsApproved = p.RequireAllWorkItemsApproved,
        autoApproveOnAllWorkItemsApproved = p.AutoApproveOnAllWorkItemsApproved,
        autoApproveWhenNoWorkItems = p.AutoApproveWhenNoWorkItems,
        sourceRequiresDeploy = p.SourceRequiresDeploy,
        // Branch patterns that auto-create candidates from registered builds; empty ⇒ never.
        autoCreateFromBranches = p.AutoCreateFromBranches,
        approvedWebhookDelaySeconds = p.ApprovedWebhookDelaySeconds,
        createdAt = p.CreatedAt,
        updatedAt = p.UpdatedAt,
        reappliedCandidates,
    };

    /// <summary>
    /// Projects the request's step tree onto the model, normalising: trims names, drops blank
    /// group/user entries, and clamps <c>minApprovers</c> to ≥ 1.
    /// </summary>
    private static List<ApprovalStep> MapSteps(IReadOnlyList<UpsertStepRequest>? steps)
    {
        if (steps is null) return new();
        return steps.Select(s => new ApprovalStep(
            (s.Name ?? "").Trim(),
            (s.Requirements ?? new()).Select(r => new ApproverRequirement(
                (r.Name ?? "").Trim(),
                (r.Groups ?? new())
                    .Select(NormaliseGroup)
                    .Where(g => g is not null)
                    .Select(g => g!)
                    .ToList(),
                (r.Users ?? new()).Where(u => !string.IsNullOrWhiteSpace(u)).Select(u => u.Trim()).ToList(),
                Math.Max(1, r.MinApprovers)))
                .ToList()))
            .ToList();
    }

    /// <summary>
    /// Canonicalises the required work-item roles: normalises each entry, drops blanks, and dedupes
    /// while keeping the order the admin arranged. Stored canonical so every comparison downstream is
    /// a plain string match against an already-normalised participant role.
    /// </summary>
    private static List<string> MapRequiredRoles(IReadOnlyList<string>? roles)
    {
        if (roles is null) return new();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();
        foreach (var role in roles)
        {
            var canonical = RoleNormalizer.Normalize(role);
            if (canonical.Length == 0 || !seen.Add(canonical)) continue;
            result.Add(canonical);
        }
        return result;
    }

    /// <summary>
    /// Normalises an incoming group ref: trims id/name, drops blank entries, and defaults the name to
    /// the id when only the id was supplied (and vice versa). Returns <c>null</c> for a blank entry.
    /// </summary>
    private static GroupRef? NormaliseGroup(GroupRef g)
    {
        var id = (g.Id ?? "").Trim();
        var name = (g.Name ?? "").Trim();
        if (id.Length == 0 && name.Length == 0) return null;
        if (id.Length == 0) id = name;
        if (name.Length == 0) name = id;
        return new GroupRef(id, name);
    }

    /// <summary>
    /// Normalises the auto-create branch patterns: trims, drops blanks, dedupes (ordinal — git refs
    /// are case-sensitive) while keeping the admin's order.
    /// </summary>
    private static List<string> MapBranchPatterns(IReadOnlyList<string>? patterns)
    {
        if (patterns is null) return new();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return patterns
            .Select(p => (p ?? "").Trim())
            .Where(p => p.Length > 0 && seen.Add(p))
            .ToList();
    }

    /// <summary>
    /// Canonicalises both ends of the edge a policy governs. Applied after validation, so a blank
    /// environment is still reported as missing rather than silently becoming an empty key.
    /// </summary>
    private static async Task<UpsertPolicyRequest> ResolveEnvironments(
        UpsertPolicyRequest request, EnvironmentAliasResolver environments, CancellationToken ct)
        => request with
        {
            SourceEnv = await environments.ResolveAsync(request.SourceEnv, ct),
            TargetEnv = await environments.ResolveAsync(request.TargetEnv, ct),
        };

    private static string? ValidatePolicyRequest(UpsertPolicyRequest r)
    {
        if (string.IsNullOrWhiteSpace(r.Product)) return "Product is required";
        if (string.IsNullOrWhiteSpace(r.SourceEnv)) return "SourceEnv is required";
        if (string.IsNullOrWhiteSpace(r.TargetEnv)) return "TargetEnv is required";
        if (r.ApprovedWebhookDelaySeconds is < 0 or > 3600)
            return "approvedWebhookDelaySeconds must be between 0 and 3600";

        // An empty step tree is valid — it means auto-approve. But a requirement that lists neither
        // a group nor a user can never be satisfied, so reject it as a misconfiguration.
        foreach (var step in r.Steps ?? new())
        {
            foreach (var req in step.Requirements ?? new())
            {
                var hasGroup = (req.Groups ?? new()).Any(g =>
                    !string.IsNullOrWhiteSpace(g.Id) || !string.IsNullOrWhiteSpace(g.Name));
                var hasUser = (req.Users ?? new()).Any(u => !string.IsNullOrWhiteSpace(u));
                if (!hasGroup && !hasUser)
                    return "Each approval requirement must list at least one group or user";
                if (req.MinApprovers < 1)
                    return "minApprovers must be >= 1";
            }
        }
        return null;
    }
}

/// <summary>
/// Write shape for creating or updating a <see cref="PromotionPolicy"/>. <c>Service</c> may be
/// null/empty, which means "product-default" (applies to every service under this product).
///
/// <para>Authorization is the step tree (<see cref="Steps"/>): a list of steps, each with a list of
/// requirements, each satisfiable by a union of groups and users. An empty/omitted list ⇒
/// auto-approve.</para>
/// </summary>
public record UpsertPolicyRequest(
    string Product,
    string? Service,
    string SourceEnv,
    string TargetEnv,
    List<UpsertStepRequest>? Steps,
    string? EscalationGroup,
    /// <summary>
    /// Whether promotions on this edge create work items at all. Defaults to <c>true</c> so a caller
    /// that omits it (or predates the field) keeps tracking. See
    /// <see cref="PromotionPolicy.TracksWorkItems"/>.
    /// </summary>
    bool TracksWorkItems = true,
    /// <summary>
    /// Participant roles every work item on this edge must have somebody in (canonicalised on save).
    /// Omitted/empty ⇒ no requirement. Unlike manual assignment, an unconfigured role is accepted
    /// here: a policy may name a role before an admin adds it to the vocabulary, and the effect of
    /// getting it wrong is a work item flagged as incomplete, not a person put somewhere they
    /// shouldn't be.
    /// </summary>
    List<string>? RequiredWorkItemRoles = null,
    bool RequireAllWorkItemsApproved = false,
    bool AutoApproveOnAllWorkItemsApproved = false,
    bool AutoApproveWhenNoWorkItems = false,
    bool SourceRequiresDeploy = true,
    /// <summary>
    /// Branch patterns (full refs, <c>*</c> wildcards) for which registered builds auto-create a
    /// candidate on this edge. Only meaningful when <c>SourceEnv</c> is the synthetic
    /// <c>build</c> source. Omitted/empty ⇒ builds never auto-create here.
    /// </summary>
    List<string>? AutoCreateFromBranches = null,
    /// <summary>
    /// Per-edge override (seconds) of the approval → promotion.approved delivery delay. Null ⇒
    /// global default; 0 ⇒ dispatch immediately (no undo window).
    /// </summary>
    int? ApprovedWebhookDelaySeconds = null);

/// <summary>One approval step in an <see cref="UpsertPolicyRequest"/>.</summary>
public record UpsertStepRequest(string? Name, List<UpsertRequirementRequest>? Requirements);

/// <summary>One requirement within an <see cref="UpsertStepRequest"/>.</summary>
public record UpsertRequirementRequest(
    string? Name,
    List<GroupRef>? Groups,
    List<string>? Users,
    int MinApprovers = 1);

/// <summary>Body for the admin bypass endpoint. <c>Reason</c> is required (empty ⇒ 400).</summary>
public record BypassPromotionRequest(string? Reason);

/// <summary>
/// Body for the reconcile endpoint. All optional: omit <c>Product</c> / <c>TargetEnv</c> to sweep
/// everything, and pass <c>DryRun</c> to see what would close without writing.
/// </summary>
public record ReconcileCompletionsRequest(string? Product, string? TargetEnv, bool? DryRun);

/// <summary>
/// Body for the stranded work-item sweep. <c>DryRun</c> reports what would be signed off without
/// writing; omitted means apply.
/// </summary>
public record ApproveOrphanedWorkItemsRequest(bool? DryRun);
