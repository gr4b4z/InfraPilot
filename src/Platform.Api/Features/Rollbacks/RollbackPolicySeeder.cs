using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Platform.Api.Features.Promotions.Models;
using Platform.Api.Features.Rollbacks.Models;
using Platform.Api.Infrastructure.Persistence;

namespace Platform.Api.Features.Rollbacks;

/// <summary>
/// One-time migration of the retired <c>rollback.enabledProducts</c> setting into
/// <see cref="RollbackPolicy"/> rows, so an install that had rollbacks enrolled keeps the same
/// <b>approval gate</b> after the upgrade instead of silently losing it.
///
/// <para>For each enrolled product, one policy row is written per target environment that the
/// product's promotion policies covered, carrying that environment's approval tree — reproducing what
/// the old code did at request time when it borrowed the gate via
/// <c>PromotionPolicyResolver.ResolveForTargetAsync</c> (product-default row preferred, else any row
/// for that environment).</para>
///
/// <para><b>Creators are deliberately left empty</b>, which means admins only. There is nothing to
/// migrate them from: the previous create path had no authorization at all, so any authenticated
/// caller could revert any enrolled product. Carrying that forward would mean shipping a permission
/// model whose migrated state is "everyone", and there is no group membership on record to narrow it
/// to. Admins can create and override from day one, so no environment becomes unrecoverable; the
/// settings page flags every creator-less policy so the gap is visible rather than latent.</para>
///
/// <para>Idempotent twice over: it returns immediately once any policy row exists, and the setting row
/// is deleted on success so a later re-run has nothing to find.</para>
/// </summary>
public static class RollbackPolicySeeder
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public static async Task MigrateEnrolledProducts(
        PlatformDbContext db, ILogger logger, CancellationToken ct = default)
    {
        // Any existing policy means this already ran (or an admin has configured rollbacks by hand).
        // Either way the setting is no longer authoritative and must not be re-applied over real config.
        if (await db.RollbackPolicies.AnyAsync(ct)) return;

        var setting = await db.PlatformSettings
            .FirstOrDefaultAsync(s => s.Key == RollbackService.EnabledProductsKey, ct);
        if (setting is null) return;

        List<string> products;
        try
        {
            products = JsonSerializer.Deserialize<List<string>>(setting.Value ?? "[]", JsonOptions) ?? new();
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Could not parse {Key}; skipping rollback policy migration",
                RollbackService.EnabledProductsKey);
            return;
        }

        products = products
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (products.Count == 0)
        {
            db.PlatformSettings.Remove(setting);
            await db.SaveChangesAsync(ct);
            return;
        }

        var promotionPolicies = await db.PromotionPolicies.AsNoTracking()
            .Where(p => products.Contains(p.Product))
            .ToListAsync(ct);

        var now = DateTimeOffset.UtcNow;
        var created = 0;
        var ungated = new List<string>();

        foreach (var product in products)
        {
            var forProduct = promotionPolicies
                .Where(p => string.Equals(p.Product, product, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (forProduct.Count == 0)
            {
                // Enrolled but with no promotion policy anywhere: the old resolver returned null for
                // every environment, so every rollback here auto-approved. That is the hole this change
                // closes — write a creator-less, gate-less product default so the product stays enrolled
                // and visible in settings, and let an admin fill it in.
                db.RollbackPolicies.Add(new RollbackPolicy
                {
                    Id = Guid.NewGuid(),
                    Product = product,
                    TargetEnv = null,
                    Creators = new PrincipalSet(),
                    ApprovalSteps = new(),
                    CreatedAt = now,
                    UpdatedAt = now,
                    UpdatedBy = "system (migrated)",
                });
                created++;
                ungated.Add(product);
                continue;
            }

            foreach (var envGroup in forProduct.GroupBy(p => p.TargetEnv, StringComparer.OrdinalIgnoreCase))
            {
                // Same precedence the old target-only resolution used: the product-default row for this
                // environment if there is one, otherwise any row for it.
                var source = envGroup.FirstOrDefault(p => p.Service is null) ?? envGroup.First();

                db.RollbackPolicies.Add(new RollbackPolicy
                {
                    Id = Guid.NewGuid(),
                    Product = product,
                    TargetEnv = envGroup.Key,
                    Creators = new PrincipalSet(),
                    ApprovalSteps = source.ApprovalSteps,
                    EscalationGroup = source.EscalationGroup,
                    CreatedAt = now,
                    UpdatedAt = now,
                    UpdatedBy = "system (migrated)",
                });
                created++;

                if (source.ApprovalSteps.All(s => s.Requirements.Count == 0))
                    ungated.Add($"{product}/{envGroup.Key}");
            }
        }

        // Drop the setting so enrollment has exactly one home from here on.
        db.PlatformSettings.Remove(setting);
        await db.SaveChangesAsync(ct);

        logger.LogWarning(
            "Migrated {Count} rollback policy row(s) for {Products} product(s) from {Key}. " +
            "Creator lists are empty, so only admins can create rollbacks until an admin sets them in " +
            "Settings → Rollbacks.",
            created, products.Count, RollbackService.EnabledProductsKey);

        if (ungated.Count > 0)
            logger.LogWarning(
                "These migrated rollback scopes have no approval requirements and will auto-approve: {Scopes}. " +
                "They were already ungated before the migration (no promotion policy resolved for them).",
                string.Join(", ", ungated));
    }
}
