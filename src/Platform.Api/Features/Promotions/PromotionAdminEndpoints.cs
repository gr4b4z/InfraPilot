using Microsoft.EntityFrameworkCore;
using Platform.Api.Features.Promotions.Models;
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
            UpsertPolicyRequest request, CancellationToken ct) =>
        {
            var error = ValidatePolicyRequest(request);
            if (error is not null) return Results.BadRequest(new { error });

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
            UpsertPolicyRequest request, CancellationToken ct) =>
        {
            var error = ValidatePolicyRequest(request);
            if (error is not null) return Results.BadRequest(new { error });

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

    private static string? ValidatePolicyRequest(UpsertPolicyRequest r)
    {
        if (string.IsNullOrWhiteSpace(r.Product)) return "Product is required";
        if (string.IsNullOrWhiteSpace(r.SourceEnv)) return "SourceEnv is required";
        if (string.IsNullOrWhiteSpace(r.TargetEnv)) return "TargetEnv is required";

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
    bool SourceRequiresDeploy = true);

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
