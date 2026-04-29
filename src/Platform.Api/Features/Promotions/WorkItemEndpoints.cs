using Platform.Api.Features.Promotions.Models;

namespace Platform.Api.Features.Promotions;

/// <summary>
/// Ticket-level (work-item) approval endpoints. Mounted at <c>/api/work-items</c>; gated by the
/// same CanApprove policy as <see cref="PromotionEndpoints"/> — any authenticated user can hit
/// these and per-action authority is enforced server-side.
///
/// <para>Recording an approval here only persists a row; it does not transition any
/// PromotionCandidate. The PR3 gate evaluator consumes these rows.</para>
///
/// <para>Inbox endpoint <c>/api/work-items/me/pending</c> is intentionally co-located with the
/// rest of the work-item routes rather than under a fresh <c>/api/me</c> group: it's a read of
/// the same resource, it shares the auth policy, and it keeps the OpenAPI grouping clean.</para>
/// </summary>
public static class WorkItemEndpoints
{
    public static RouteGroupBuilder MapWorkItemEndpoints(this RouteGroupBuilder group)
    {
        // Inbox: tickets the current user could sign off right now. Mounted under the work-items
        // group at /me/pending — see class summary for the route choice.
        //
        // Optional `assignee` and `role` query parameters narrow the list (display only —
        // authorisation is unchanged). The matrix:
        //  - both null            → full authorized list (no narrowing).
        //  - role only            → candidates with at least one participant in that role.
        //  - assignee=email       → candidates where that email holds a role in the assignee
        //                           set (or the role-filter when set).
        //  - assignee=unassigned  → candidates with no participant in the effective role set
        //                           ("unassigned" is case-insensitive).
        // Response carries the rendered tickets plus an `assignees` rollup of (email, role) →
        // count built from the authorized list <i>before</i> role/person narrowing, plus the
        // canonical `roles` set — both feed the front-end's dropdowns without a second call.
        group.MapGet("/me/pending", async (
            WorkItemApprovalService svc, string? assignee, string? role, CancellationToken ct) =>
        {
            var queue = await svc.GetPendingForCurrentUserAsync(ct, assignee, role);
            return Results.Ok(new
            {
                tickets = queue.Tickets,
                assignees = queue.Assignees,
                roles = queue.Roles,
            });
        });

        // Ticket context — authority + decision history for a specific (key, product, env).
        group.MapGet("/{key}", async (
            WorkItemApprovalService svc,
            string key,
            string product,
            string targetEnv,
            CancellationToken ct) =>
        {
            var decoded = Uri.UnescapeDataString(key ?? "");
            var ctx = await svc.GetTicketContextAsync(decoded, product, targetEnv, ct);
            return Results.Ok(ToContextDto(ctx));
        });

        // Record approval. Body carries (product, targetEnv, comment?). Returns the row + the
        // candidate id it was attached to so the UI can deep-link back.
        group.MapPost("/{key}/approvals", async (
            WorkItemApprovalService svc,
            string key,
            WorkItemDecisionRequest body,
            CancellationToken ct) =>
        {
            var decoded = Uri.UnescapeDataString(key ?? "");
            return await RunDecisionAsync(() => svc.ApproveAsync(
                decoded, body.Product ?? "", body.TargetEnv ?? "", body.Comment, ct));
        });

        // Record rejection.
        group.MapPost("/{key}/rejections", async (
            WorkItemApprovalService svc,
            string key,
            WorkItemDecisionRequest body,
            CancellationToken ct) =>
        {
            var decoded = Uri.UnescapeDataString(key ?? "");
            return await RunDecisionAsync(() => svc.RejectAsync(
                decoded, body.Product ?? "", body.TargetEnv ?? "", body.Comment, ct));
        });

        return group;
    }

    private static async Task<IResult> RunDecisionAsync(Func<Task<WorkItemApproval>> op)
    {
        try
        {
            var row = await op();
            return Results.Ok(ToApprovalDto(row));
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

    private static object ToApprovalDto(WorkItemApproval a) => new
    {
        id = a.Id,
        workItemKey = a.WorkItemKey,
        product = a.Product,
        targetEnv = a.TargetEnv,
        approverEmail = a.ApproverEmail,
        approverName = a.ApproverName,
        decision = a.Decision.ToString(),
        comment = a.Comment,
        createdAt = a.CreatedAt,
    };

    private static object ToContextDto(TicketContext ctx) => new
    {
        workItemKey = ctx.WorkItemKey,
        product = ctx.Product,
        targetEnv = ctx.TargetEnv,
        pendingCandidateId = ctx.PendingCandidateId,
        canApprove = ctx.CanApprove,
        blockedReason = ctx.BlockedReason,
        approvals = ctx.Approvals.Select(ToApprovalDto),
    };
}

public record WorkItemDecisionRequest(string? Product, string? TargetEnv, string? Comment);
