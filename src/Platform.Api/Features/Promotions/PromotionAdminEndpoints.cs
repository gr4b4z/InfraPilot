using Microsoft.EntityFrameworkCore;
using Platform.Api.Features.Promotions.Models;
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
        // ── Policies ────────────────────────────────────────────────────────

        group.MapGet("/policies", async (PlatformDbContext db) =>
        {
            var rows = await db.PromotionPolicies.AsNoTracking()
                .OrderBy(p => p.Product).ThenBy(p => p.Service).ThenBy(p => p.TargetEnv)
                .ToListAsync();
            return Results.Ok(new { policies = rows.Select(MapPolicy) });
        });

        group.MapGet("/policies/{id:guid}", async (PlatformDbContext db, Guid id) =>
        {
            var row = await db.PromotionPolicies.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id);
            return row is null ? Results.NotFound() : Results.Ok(MapPolicy(row));
        });

        group.MapPost("/policies", async (
            PlatformDbContext db, ICurrentUser user, UpsertPolicyRequest request) =>
        {
            var error = ValidatePolicyRequest(request);
            if (error is not null) return Results.BadRequest(new { error });

            // Duplicate-check: the DB-level unique index on (Product, Service, TargetEnv) is the
            // hard guard; this pre-check lets us return a friendly 409 instead of a 500 from EF.
            var existing = await db.PromotionPolicies
                .FirstOrDefaultAsync(p =>
                    p.Product == request.Product
                    && p.Service == request.Service
                    && p.TargetEnv == request.TargetEnv);
            if (existing is not null)
                return Results.Conflict(new { error = "A policy for this (product, service, target_env) already exists" });

            var now = DateTimeOffset.UtcNow;
            var policy = new PromotionPolicy
            {
                Id = Guid.NewGuid(),
                Product = request.Product,
                Service = string.IsNullOrWhiteSpace(request.Service) ? null : request.Service,
                TargetEnv = request.TargetEnv,
                ApprovalSteps = MapSteps(request.Steps),
                Gate = request.Gate,
                TimeoutHours = Math.Max(0, request.TimeoutHours),
                EscalationGroup = string.IsNullOrWhiteSpace(request.EscalationGroup) ? null : request.EscalationGroup,
                RequireAllWorkItemsApproved = request.RequireAllWorkItemsApproved,
                AutoApproveOnAllWorkItemsApproved = request.AutoApproveOnAllWorkItemsApproved,
                AutoApproveWhenNoWorkItems = request.AutoApproveWhenNoWorkItems,
                CreatedAt = now,
                UpdatedAt = now,
            };
            db.PromotionPolicies.Add(policy);
            await db.SaveChangesAsync();

            return Results.Created($"/api/promotions/admin/policies/{policy.Id}", MapPolicy(policy));
        });

        group.MapPut("/policies/{id:guid}", async (
            PlatformDbContext db, ICurrentUser user, Guid id, UpsertPolicyRequest request) =>
        {
            var error = ValidatePolicyRequest(request);
            if (error is not null) return Results.BadRequest(new { error });

            var policy = await db.PromotionPolicies.FirstOrDefaultAsync(p => p.Id == id);
            if (policy is null) return Results.NotFound();

            policy.Product = request.Product;
            policy.Service = string.IsNullOrWhiteSpace(request.Service) ? null : request.Service;
            policy.TargetEnv = request.TargetEnv;
            policy.ApprovalSteps = MapSteps(request.Steps);
            policy.Gate = request.Gate;
            policy.TimeoutHours = Math.Max(0, request.TimeoutHours);
            policy.EscalationGroup = string.IsNullOrWhiteSpace(request.EscalationGroup) ? null : request.EscalationGroup;
            policy.RequireAllWorkItemsApproved = request.RequireAllWorkItemsApproved;
            policy.AutoApproveOnAllWorkItemsApproved = request.AutoApproveOnAllWorkItemsApproved;
            policy.AutoApproveWhenNoWorkItems = request.AutoApproveWhenNoWorkItems;
            policy.UpdatedAt = DateTimeOffset.UtcNow;

            await db.SaveChangesAsync();
            return Results.Ok(MapPolicy(policy));
        });

        group.MapDelete("/policies/{id:guid}", async (PlatformDbContext db, Guid id) =>
        {
            var policy = await db.PromotionPolicies.FirstOrDefaultAsync(p => p.Id == id);
            if (policy is null) return Results.NotFound();
            db.PromotionPolicies.Remove(policy);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        // Topology removed (D19): the external system is the sole source of truth for edges; the
        // policy-resolution 422 on create is the de-facto edge guard. No /topology routes.

        return group;
    }

    private static object MapPolicy(PromotionPolicy p) => new
    {
        id = p.Id,
        product = p.Product,
        service = p.Service,
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
        gate = p.Gate.ToString(),
        timeoutHours = p.TimeoutHours,
        escalationGroup = p.EscalationGroup,
        requireAllWorkItemsApproved = p.RequireAllWorkItemsApproved,
        autoApproveOnAllWorkItemsApproved = p.AutoApproveOnAllWorkItemsApproved,
        autoApproveWhenNoWorkItems = p.AutoApproveWhenNoWorkItems,
        createdAt = p.CreatedAt,
        updatedAt = p.UpdatedAt,
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
    string TargetEnv,
    List<UpsertStepRequest>? Steps,
    PromotionGate Gate,
    int TimeoutHours,
    string? EscalationGroup,
    bool RequireAllWorkItemsApproved = false,
    bool AutoApproveOnAllWorkItemsApproved = false,
    bool AutoApproveWhenNoWorkItems = false);

/// <summary>One approval step in an <see cref="UpsertPolicyRequest"/>.</summary>
public record UpsertStepRequest(string? Name, List<UpsertRequirementRequest>? Requirements);

/// <summary>One requirement within an <see cref="UpsertStepRequest"/>.</summary>
public record UpsertRequirementRequest(
    string? Name,
    List<GroupRef>? Groups,
    List<string>? Users,
    int MinApprovers = 1);
