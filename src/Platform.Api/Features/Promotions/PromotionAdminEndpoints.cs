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
                ApproverGroup = string.IsNullOrWhiteSpace(request.ApproverGroup) ? null : request.ApproverGroup,
                Strategy = request.Strategy,
                MinApprovers = Math.Max(1, request.MinApprovers),
                ExcludeDeployer = request.ExcludeDeployer,
                TimeoutHours = Math.Max(0, request.TimeoutHours),
                EscalationGroup = string.IsNullOrWhiteSpace(request.EscalationGroup) ? null : request.EscalationGroup,
                ExecutorKind = string.IsNullOrWhiteSpace(request.ExecutorKind) ? null : request.ExecutorKind,
                ExecutorConfigJson = string.IsNullOrWhiteSpace(request.ExecutorConfigJson) ? null : request.ExecutorConfigJson,
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
            policy.ApproverGroup = string.IsNullOrWhiteSpace(request.ApproverGroup) ? null : request.ApproverGroup;
            policy.Strategy = request.Strategy;
            policy.MinApprovers = Math.Max(1, request.MinApprovers);
            policy.ExcludeDeployer = request.ExcludeDeployer;
            policy.TimeoutHours = Math.Max(0, request.TimeoutHours);
            policy.EscalationGroup = string.IsNullOrWhiteSpace(request.EscalationGroup) ? null : request.EscalationGroup;
            policy.ExecutorKind = string.IsNullOrWhiteSpace(request.ExecutorKind) ? null : request.ExecutorKind;
            policy.ExecutorConfigJson = string.IsNullOrWhiteSpace(request.ExecutorConfigJson) ? null : request.ExecutorConfigJson;
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

        // ── Topology ────────────────────────────────────────────────────────

        group.MapGet("/topology", async (PromotionTopologyService svc) =>
        {
            var topo = await svc.GetAsync();
            return Results.Ok(topo);
        });

        group.MapPut("/topology", async (
            PromotionTopologyService svc, ICurrentUser user, PromotionTopology request) =>
        {
            try
            {
                await svc.SaveAsync(request, user.Email ?? user.Name);
                return Results.Ok(request);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        return group;
    }

    private static object MapPolicy(PromotionPolicy p) => new
    {
        id = p.Id,
        product = p.Product,
        service = p.Service,
        targetEnv = p.TargetEnv,
        approverGroup = p.ApproverGroup,
        strategy = p.Strategy.ToString(),
        minApprovers = p.MinApprovers,
        excludeDeployer = p.ExcludeDeployer,
        timeoutHours = p.TimeoutHours,
        escalationGroup = p.EscalationGroup,
        executorKind = p.ExecutorKind,
        executorConfigJson = p.ExecutorConfigJson,
        createdAt = p.CreatedAt,
        updatedAt = p.UpdatedAt,
    };

    private static string? ValidatePolicyRequest(UpsertPolicyRequest r)
    {
        if (string.IsNullOrWhiteSpace(r.Product)) return "Product is required";
        if (string.IsNullOrWhiteSpace(r.TargetEnv)) return "TargetEnv is required";
        if (r.Strategy == PromotionStrategy.NOfM && r.MinApprovers < 1)
            return "NOfM strategy requires MinApprovers >= 1";
        return null;
    }
}

/// <summary>
/// Write shape for creating or updating a <see cref="PromotionPolicy"/>. <c>Service</c> may be
/// null/empty, which means "product-default" (applies to every service under this product).
/// </summary>
public record UpsertPolicyRequest(
    string Product,
    string? Service,
    string TargetEnv,
    string? ApproverGroup,
    PromotionStrategy Strategy,
    int MinApprovers,
    bool ExcludeDeployer,
    int TimeoutHours,
    string? EscalationGroup,
    string? ExecutorKind = null,
    string? ExecutorConfigJson = null);
