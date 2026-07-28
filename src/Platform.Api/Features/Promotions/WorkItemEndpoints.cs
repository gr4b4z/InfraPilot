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
            WorkItemApprovalService svc,
            string? assignee, string? role, string? status, string? since,
            CancellationToken ct) =>
        {
            // status: "pending" (default) | "decided" (combined approved + rejected for the user).
            // For decided views, an optional `since` ISO timestamp narrows to recent decisions.
            // Omitting `since` means "all time" — the client's time-frame picker sends an explicit
            // cutoff for every bounded choice (last day / 7d / 30d) and nothing for "All time", so a
            // missing param must pass through as null (no cutoff), not a server-side 24h default.
            var normalized = status?.Trim().ToLowerInvariant();
            DateTimeOffset? sinceCutoff = null;
            if (DateTimeOffset.TryParse(since, out var parsed)) sinceCutoff = parsed;

            var queue = normalized switch
            {
                // On the decided view `assignee` narrows by the decider (who clicked Approve /
                // Reject) — a single email, resolved client-side ("Me" → current user). The
                // "unassigned" sentinel is pending-only (a decided row always has a decider), so
                // ignore it here rather than filter to a literal "unassigned" email.
                "decided" => await svc.GetDecidedAsync(
                    decision: null,
                    since: sinceCutoff,
                    decidedBy: string.Equals(assignee?.Trim(), "unassigned", StringComparison.OrdinalIgnoreCase)
                        ? null
                        : assignee,
                    ct),
                _ => await svc.GetPendingForCurrentUserAsync(ct, assignee, role),
            };
            return Results.Ok(new
            {
                tickets = queue.Tickets,
                assignees = queue.Assignees,
                roles = queue.Roles,
            });
        });

        // Full detail for the work-item page: display fields, people, decision trail, comment
        // thread, and every candidate carrying the ticket. Mounted before /{key} so "detail" is
        // never swallowed as a key segment (it wouldn't be — the shapes differ — but keeping the
        // more specific route first makes the intent obvious).
        group.MapGet("/{key}/detail", async (
            WorkItemApprovalService svc,
            string key,
            string product,
            string targetEnv,
            CancellationToken ct) =>
        {
            var decoded = Uri.UnescapeDataString(key ?? "");
            var detail = await svc.GetDetailAsync(decoded, product, targetEnv, ct);
            return detail is null
                ? Results.NotFound(new { error = $"Work item '{decoded}' not found for {product}/{targetEnv}" })
                : Results.Ok(ToDetailDto(detail));
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

        // Record a block — holds the item back without vetoing the promotion. Reversible: the same
        // user may later POST /approvals to release it.
        group.MapPost("/{key}/blocks", async (
            WorkItemApprovalService svc,
            string key,
            WorkItemDecisionRequest body,
            CancellationToken ct) =>
        {
            var decoded = Uri.UnescapeDataString(key ?? "");
            return await RunDecisionAsync(() => svc.BlockAsync(
                decoded, body.Product ?? "", body.TargetEnv ?? "", body.Comment, ct));
        });

        // ── Comment thread ────────────────────────────────────────────────
        // Keyed by (key, product, targetEnv) like the decisions, so the thread survives a
        // superseded candidate. Edit/delete route by comment id — no key in the path — because the
        // id alone identifies the row and the author check is the only authorisation that matters.

        group.MapGet("/{key}/comments", async (
            WorkItemApprovalService svc,
            string key,
            string product,
            string targetEnv,
            CancellationToken ct) =>
        {
            var decoded = Uri.UnescapeDataString(key ?? "");
            var comments = await svc.GetCommentsAsync(decoded, product, targetEnv, ct);
            return Results.Ok(new { comments = comments.Select(ToCommentDto) });
        });

        group.MapPost("/{key}/comments", async (
            WorkItemApprovalService svc,
            string key,
            WorkItemCommentRequest body,
            CancellationToken ct) =>
        {
            var decoded = Uri.UnescapeDataString(key ?? "");
            try
            {
                var comment = await svc.AddCommentAsync(
                    decoded, body.Product ?? "", body.TargetEnv ?? "", body.Body ?? "", ct);
                return Results.Ok(ToCommentDto(comment));
            }
            catch (KeyNotFoundException ex) { return Results.NotFound(new { error = ex.Message }); }
            catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        group.MapPatch("/comments/{commentId:guid}", async (
            WorkItemApprovalService svc,
            Guid commentId,
            WorkItemCommentRequest body,
            CancellationToken ct) =>
        {
            try
            {
                var comment = await svc.UpdateCommentAsync(commentId, body.Body ?? "", ct);
                return Results.Ok(ToCommentDto(comment));
            }
            catch (KeyNotFoundException ex) { return Results.NotFound(new { error = ex.Message }); }
            catch (UnauthorizedAccessException ex) { return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status403Forbidden); }
            catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        group.MapDelete("/comments/{commentId:guid}", async (
            WorkItemApprovalService svc,
            Guid commentId,
            CancellationToken ct) =>
        {
            try
            {
                await svc.DeleteCommentAsync(commentId, ct);
                return Results.NoContent();
            }
            catch (KeyNotFoundException ex) { return Results.NotFound(new { error = ex.Message }); }
            catch (UnauthorizedAccessException ex) { return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status403Forbidden); }
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
        updatedAt = a.UpdatedAt,
    };

    private static object ToContextDto(TicketContext ctx) => new
    {
        workItemKey = ctx.WorkItemKey,
        product = ctx.Product,
        targetEnv = ctx.TargetEnv,
        pendingCandidateId = ctx.PendingCandidateId,
        canApprove = ctx.CanApprove,
        blockedReason = ctx.BlockedReason,
        myDecision = ctx.MyDecision,
        approvals = ctx.Approvals.Select(ToApprovalDto),
    };

    private static object ToCommentDto(WorkItemComment c) => new
    {
        id = c.Id,
        workItemKey = c.WorkItemKey,
        product = c.Product,
        targetEnv = c.TargetEnv,
        authorEmail = c.AuthorEmail,
        authorName = c.AuthorName,
        body = c.Body,
        createdAt = c.CreatedAt,
        updatedAt = c.UpdatedAt,
    };

    private static object ToDetailDto(WorkItemDetail d) => new
    {
        workItemKey = d.WorkItemKey,
        product = d.Product,
        targetEnv = d.TargetEnv,
        title = d.Title,
        url = d.Url,
        provider = d.Provider,
        pendingCandidateId = d.PendingCandidateId,
        primaryCandidateId = d.PrimaryCandidateId,
        canApprove = d.CanApprove,
        canManage = d.CanManage,
        blockedReason = d.BlockedReason,
        myDecision = d.MyDecision,
        participants = d.Participants,
        approvals = d.Approvals.Select(ToApprovalDto),
        comments = d.Comments.Select(ToCommentDto),
        candidates = d.Candidates.Select(c => new
        {
            id = c.Id,
            service = c.Service,
            version = c.Version,
            sourceEnv = c.SourceEnv,
            targetEnv = c.TargetEnv,
            status = c.Status,
            createdAt = c.CreatedAt,
            isPrimary = c.IsPrimary,
        }),
    };
}

public record WorkItemDecisionRequest(string? Product, string? TargetEnv, string? Comment);

/// <summary>Body for posting/editing a work-item comment. Product/TargetEnv are required on
/// create (they complete the thread key) and ignored on edit.</summary>
public record WorkItemCommentRequest(string? Product, string? TargetEnv, string? Body);
