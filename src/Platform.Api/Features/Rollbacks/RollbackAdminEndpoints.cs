using Microsoft.EntityFrameworkCore;
using Platform.Api.Features.Promotions.Models;
using Platform.Api.Features.Rollbacks.Models;
using Platform.Api.Features.Settings;
using Platform.Api.Infrastructure.Audit;
using Platform.Api.Infrastructure.Auth;
using Platform.Api.Infrastructure.Persistence;

namespace Platform.Api.Features.Rollbacks;

/// <summary>
/// Admin endpoints for rollback policies (mounted at <c>/api/rollbacks/admin</c>, gated by
/// <see cref="AuthorizationPolicies.CatalogAdmin"/>). A policy says who may create rollbacks for a
/// product and who must approve them; one row per (product, target env), with a null target env acting
/// as the product default.
///
/// <para>The existence of a row is also enrollment, so these endpoints replace the previous
/// <c>/enabled-products</c> pair — there is no longer a separate "is this product allowed" list to keep
/// in sync with the permissions that make rollbacks usable.</para>
/// </summary>
public static class RollbackAdminEndpoints
{
    public static RouteGroupBuilder MapRollbackAdminEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/policies", async (PlatformDbContext db, CancellationToken ct) =>
        {
            var rows = await db.RollbackPolicies.AsNoTracking()
                .OrderBy(p => p.Product).ThenBy(p => p.TargetEnv)
                .ToListAsync(ct);
            return Results.Ok(new { policies = rows.Select(MapPolicy) });
        });

        group.MapGet("/policies/{id:guid}", async (PlatformDbContext db, Guid id, CancellationToken ct) =>
        {
            var row = await db.RollbackPolicies.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct);
            return row is null ? Results.NotFound() : Results.Ok(MapPolicy(row));
        });

        group.MapPost("/policies", async (
            PlatformDbContext db, ICurrentUser user, IAuditLogger audit,
            EnvironmentAliasResolver environments,
            UpsertRollbackPolicyRequest request, CancellationToken ct) =>
        {
            var error = Validate(request);
            if (error is not null) return Results.BadRequest(new { error });

            // Stored against the canonical environment so the policy governs every name the
            // environment answers to â€” CanCreateAsync resolves the same way before looking it up.
            var targetEnv = await environments.ResolveFilterAsync(request.TargetEnv, ct);

            // Pre-check for a friendly 409. The unique index covers env-specific rows; product-default
            // rows have a NULL TargetEnv, which no provider treats as duplicable, so this check is the
            // only guard against a second product default.
            var clash = await db.RollbackPolicies
                .AnyAsync(p => p.Product == request.Product && p.TargetEnv == targetEnv, ct);
            if (clash)
                return Results.Conflict(new
                {
                    error = targetEnv is null
                        ? $"A default rollback policy for '{request.Product}' already exists"
                        : $"A rollback policy for '{request.Product}' in {targetEnv} already exists",
                });

            var now = DateTimeOffset.UtcNow;
            var policy = new RollbackPolicy
            {
                Id = Guid.NewGuid(),
                Product = request.Product.Trim(),
                TargetEnv = targetEnv,
                Creators = MapCreators(request.Creators),
                ApprovalSteps = MapSteps(request.Steps),
                EscalationGroup = Blank(request.EscalationGroup) ? null : request.EscalationGroup!.Trim(),
                CreatedAt = now,
                UpdatedAt = now,
                UpdatedBy = Actor(user),
            };
            db.RollbackPolicies.Add(policy);
            await db.SaveChangesAsync(ct);

            await audit.Log("rollbacks", "rollback.policy.created",
                user.Id, user.Name, "user", "RollbackPolicy", policy.Id, null, Describe(policy));

            return Results.Created($"/api/rollbacks/admin/policies/{policy.Id}", MapPolicy(policy));
        });

        group.MapPut("/policies/{id:guid}", async (
            PlatformDbContext db, ICurrentUser user, IAuditLogger audit, Guid id,
            EnvironmentAliasResolver environments,
            UpsertRollbackPolicyRequest request, CancellationToken ct) =>
        {
            var error = Validate(request);
            if (error is not null) return Results.BadRequest(new { error });

            var policy = await db.RollbackPolicies.FirstOrDefaultAsync(p => p.Id == id, ct);
            if (policy is null) return Results.NotFound();

            var targetEnv = await environments.ResolveFilterAsync(request.TargetEnv, ct);
            var clash = await db.RollbackPolicies
                .AnyAsync(p => p.Id != id && p.Product == request.Product && p.TargetEnv == targetEnv, ct);
            if (clash)
                return Results.Conflict(new { error = "Another rollback policy already covers that scope" });

            var before = Describe(policy);

            policy.Product = request.Product.Trim();
            policy.TargetEnv = targetEnv;
            policy.Creators = MapCreators(request.Creators);
            policy.ApprovalSteps = MapSteps(request.Steps);
            policy.EscalationGroup = Blank(request.EscalationGroup) ? null : request.EscalationGroup!.Trim();
            policy.UpdatedAt = DateTimeOffset.UtcNow;
            policy.UpdatedBy = Actor(user);
            await db.SaveChangesAsync(ct);

            // Pending requests keep the snapshot they were created under, exactly as promotions do:
            // the gate a request is judged by is the one that existed when it was raised. Editing a
            // policy therefore governs future rollbacks only.
            await audit.Log("rollbacks", "rollback.policy.updated",
                user.Id, user.Name, "user", "RollbackPolicy", policy.Id, before, Describe(policy));

            return Results.Ok(MapPolicy(policy));
        });

        group.MapDelete("/policies/{id:guid}", async (
            PlatformDbContext db, ICurrentUser user, IAuditLogger audit, Guid id, CancellationToken ct) =>
        {
            var policy = await db.RollbackPolicies.FirstOrDefaultAsync(p => p.Id == id, ct);
            if (policy is null) return Results.NotFound();

            var before = Describe(policy);
            db.RollbackPolicies.Remove(policy);
            await db.SaveChangesAsync(ct);

            // Deleting the last policy for a product un-enrolls it: nobody but an admin can create a
            // rollback there any more, and pending requests keep their snapshot and stay decidable.
            await audit.Log("rollbacks", "rollback.policy.deleted",
                user.Id, user.Name, "user", "RollbackPolicy", id, before, null);

            return Results.NoContent();
        });

        return group;
    }

    private static object MapPolicy(RollbackPolicy p)
    {
        var creators = p.Creators;
        return new
        {
            id = p.Id,
            product = p.Product,
            // Null ⇒ the product default, applying to every environment without its own row.
            targetEnv = p.TargetEnv,
            creators = new { groups = creators.Groups, users = creators.Users },
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
            escalationGroup = p.EscalationGroup,
            // Surfaced so the settings UI can flag the two states that look configured but grant
            // nothing: no creators (only admins can raise a rollback) and no requirements (no gate).
            hasCreators = !creators.IsEmpty,
            isAutoApprove = p.ApprovalSteps.All(s => s.Requirements.Count == 0),
            createdAt = p.CreatedAt,
            updatedAt = p.UpdatedAt,
            updatedBy = p.UpdatedBy,
        };
    }

    /// <summary>Compact shape for the audit before/after payloads — scope plus who was granted what.</summary>
    private static object Describe(RollbackPolicy p) => new
    {
        p.Product,
        p.TargetEnv,
        creatorGroups = p.Creators.Groups.Select(g => g.Name).ToList(),
        creatorUsers = p.Creators.Users,
        requirements = p.ApprovalSteps.SelectMany(s => s.Requirements).Select(r => new
        {
            r.Name,
            groups = r.Groups.Select(g => g.Name).ToList(),
            r.Users,
            r.MinApprovers,
        }).ToList(),
        p.EscalationGroup,
    };

    private static PrincipalSet MapCreators(PrincipalSetRequest? creators)
    {
        if (creators is null) return new();
        return new PrincipalSet(
            (creators.Groups ?? new()).Select(NormaliseGroup).OfType<GroupRef>().ToList(),
            (creators.Users ?? new()).Where(u => !string.IsNullOrWhiteSpace(u)).Select(u => u.Trim()).ToList());
    }

    /// <summary>
    /// Projects the request's step tree onto the model, normalising the same way
    /// <c>PromotionAdminEndpoints.MapSteps</c> does — trims names, drops blank group/user entries,
    /// clamps <c>minApprovers</c> to ≥ 1 — so a requirement means the same thing on both surfaces.
    /// </summary>
    private static List<ApprovalStep> MapSteps(IReadOnlyList<UpsertRollbackStepRequest>? steps)
    {
        if (steps is null) return new();
        return steps.Select(s => new ApprovalStep(
            (s.Name ?? "").Trim(),
            (s.Requirements ?? new()).Select(r => new ApproverRequirement(
                (r.Name ?? "").Trim(),
                (r.Groups ?? new()).Select(NormaliseGroup).OfType<GroupRef>().ToList(),
                (r.Users ?? new()).Where(u => !string.IsNullOrWhiteSpace(u)).Select(u => u.Trim()).ToList(),
                Math.Max(1, r.MinApprovers)))
                .ToList()))
            .ToList();
    }

    /// <summary>
    /// Normalises an incoming group ref: trims id/name, drops blank entries, and defaults each of
    /// id/name to the other when only one was supplied. Returns <c>null</c> for a blank entry.
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

    private static string? Validate(UpsertRollbackPolicyRequest r)
    {
        if (string.IsNullOrWhiteSpace(r.Product)) return "Product is required";

        // An empty creator set and an empty step tree are both valid and both meaningful — "admins
        // only" and "no approval needed" respectively — so neither is rejected here. What cannot be
        // accepted is a requirement naming nobody, which no one could ever satisfy.
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

    private static bool Blank(string? s) => string.IsNullOrWhiteSpace(s);

    private static string Actor(ICurrentUser user)
        => string.IsNullOrEmpty(user.Email) ? user.Name : user.Email;
}

/// <summary>
/// Write shape for a <see cref="RollbackPolicy"/>. <c>TargetEnv</c> may be null/empty, meaning the
/// product default that covers every environment without its own row.
/// </summary>
public record UpsertRollbackPolicyRequest(
    string Product,
    string? TargetEnv,
    /// <summary>
    /// Who may raise a rollback in this scope. Omitted/empty ⇒ admins only (an empty set grants
    /// nobody — it is never read as "everyone").
    /// </summary>
    PrincipalSetRequest? Creators,
    /// <summary>
    /// The approval tree. Omitted/empty ⇒ rollbacks in this scope need no approval, which is a
    /// deliberate choice and distinct from having no policy row at all.
    /// </summary>
    List<UpsertRollbackStepRequest>? Steps,
    string? EscalationGroup);

/// <summary>A group ∪ user set in a write request.</summary>
public record PrincipalSetRequest(List<GroupRef>? Groups, List<string>? Users);

/// <summary>One approval step in an <see cref="UpsertRollbackPolicyRequest"/>.</summary>
public record UpsertRollbackStepRequest(string? Name, List<UpsertRollbackRequirementRequest>? Requirements);

/// <summary>One requirement within an <see cref="UpsertRollbackStepRequest"/>.</summary>
public record UpsertRollbackRequirementRequest(
    string? Name,
    List<GroupRef>? Groups,
    List<string>? Users,
    int MinApprovers = 1);
