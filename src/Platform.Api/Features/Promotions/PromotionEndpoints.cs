using Microsoft.EntityFrameworkCore;
using Platform.Api.Features.Promotions.Models;
using Platform.Api.Infrastructure.Persistence;

namespace Platform.Api.Features.Promotions;

/// <summary>
/// Non-admin endpoints for listing and acting on promotion candidates. Mounted at
/// <c>/api/promotions</c>; gated by the standard CanApprove policy so any authenticated
/// user can see the queue (per-candidate capability is layered on via the <c>canApprove</c>
/// flag in the response).
/// </summary>
public static class PromotionEndpoints
{
    public static RouteGroupBuilder MapPromotionEndpoints(this RouteGroupBuilder group)
    {
        // List candidates with filters + capability flags.
        group.MapGet("/", async (
            PromotionService svc,
            string? status,
            string? product,
            string? service,
            string? targetEnv,
            int? limit) =>
        {
            PromotionStatus? parsed = null;
            if (!string.IsNullOrEmpty(status))
            {
                if (!Enum.TryParse<PromotionStatus>(status, ignoreCase: true, out var s))
                    return Results.BadRequest(new { error = $"Unknown status '{status}'" });
                parsed = s;
            }

            var query = new PromotionQuery(
                Status: parsed,
                Product: product,
                Service: service,
                TargetEnv: targetEnv,
                Limit: limit is > 0 ? limit.Value : 200);

            var candidates = await svc.GetAsync(query);
            var capability = await svc.CanUserApproveManyAsync(candidates);

            return Results.Ok(new
            {
                candidates = candidates.Select(c => ToDto(c, capability.GetValueOrDefault(c.Id))),
            });
        });

        // Single candidate — includes the full approval trail for the detail view.
        group.MapGet("/{id:guid}", async (
            PromotionService svc, PlatformDbContext db, Guid id) =>
        {
            var c = await svc.GetByIdAsync(id);
            if (c is null) return Results.NotFound();
            var approvals = await svc.GetApprovalsAsync(id);
            var canApprove = await svc.CanUserApproveAsync(c);

            return Results.Ok(new
            {
                candidate = ToDto(c, canApprove),
                approvals = approvals.Select(a => new
                {
                    a.Id,
                    a.ApproverEmail,
                    a.ApproverName,
                    a.Comment,
                    decision = a.Decision.ToString(),
                    a.CreatedAt,
                }),
            });
        });

        // Approve.
        group.MapPost("/{id:guid}/approve", async (
            PromotionService svc, Guid id, PromotionDecisionRequest? body) =>
        {
            return await RunDecisionAsync(() => svc.ApproveAsync(id, body?.Comment));
        });

        // Reject.
        group.MapPost("/{id:guid}/reject", async (
            PromotionService svc, Guid id, PromotionDecisionRequest? body) =>
        {
            return await RunDecisionAsync(() => svc.RejectAsync(id, body?.Comment));
        });

        // Bulk approve — succeeds partially: returns per-id outcome so the UI can show
        // which ones went through and which failed. Rejecting in bulk is intentionally
        // omitted — treating mass-reject as a lighter action is a UX footgun.
        group.MapPost("/bulk/approve", async (
            PromotionService svc, PromotionBulkRequest body) =>
        {
            var results = new List<object>();
            foreach (var id in body.Ids ?? Array.Empty<Guid>())
            {
                try
                {
                    var candidate = await svc.ApproveAsync(id, body.Comment);
                    results.Add(new { id, ok = true, status = candidate.Status.ToString() });
                }
                catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or UnauthorizedAccessException)
                {
                    results.Add(new { id, ok = false, error = ex.Message });
                }
            }

            return Results.Ok(new { results });
        });

        return group;
    }

    private static async Task<IResult> RunDecisionAsync(Func<Task<PromotionCandidate>> op)
    {
        try
        {
            var candidate = await op();
            return Results.Ok(ToDto(candidate, canApprove: false));
        }
        catch (KeyNotFoundException ex)
        {
            return Results.NotFound(new { error = ex.Message });
        }
        catch (UnauthorizedAccessException ex)
        {
            return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status403Forbidden);
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
    }

    private static object ToDto(PromotionCandidate c, bool canApprove) => new
    {
        id = c.Id,
        product = c.Product,
        service = c.Service,
        sourceEnv = c.SourceEnv,
        targetEnv = c.TargetEnv,
        version = c.Version,
        status = c.Status.ToString(),
        sourceDeployerName = c.SourceDeployerName,
        sourceDeployerEmail = c.SourceDeployerEmail,
        externalRunUrl = c.ExternalRunUrl,
        createdAt = c.CreatedAt,
        approvedAt = c.ApprovedAt,
        deployedAt = c.DeployedAt,
        supersededById = c.SupersededById,
        canApprove,
    };
}

public record PromotionDecisionRequest(string? Comment);
public record PromotionBulkRequest(Guid[] Ids, string? Comment);
