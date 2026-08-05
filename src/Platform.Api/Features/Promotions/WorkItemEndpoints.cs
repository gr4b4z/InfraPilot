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
        // Optional `assignee` narrows the list (display only — authorisation is unchanged):
        //  - null                 → full authorized list (no narrowing).
        //  - assignee=email       → candidates where that email holds a role on the work item
        //                           (any role, or the policy-required roles when
        //                           roleRequirement=assigned — the queue's person filter).
        //  - assignee=unassigned  → candidates with no participant in any role that counts as an
        //                           assignment ("unassigned" is case-insensitive).
        // Response carries the rendered tickets plus an `assignees` rollup of (email, role) →
        // count built from the authorized list <i>before</i> person narrowing — the person
        // dropdown's contents, limited to people holding a policy-required role (the only picks
        // the queue's person filter can match).
        //
        // `roleRequirement` narrows by the promotion policy's work-item role requirement — the roles
        // that make somebody answerable for an item (see WorkItemRoleRequirements). Two values:
        //  - "assigned" → the person must hold a role the item's own policy REQUIRES. This is what
        //                 the "Assigned to me" tab and the queue's person filter mean: being named
        //                 as, say, a ticket's reporter isn't being made answerable for it.
        //  - "missing"  → items where at least one policy-required role has nobody in it ("Not
        //                 assigned"). Independent of `assignee`.
        // Anything else (including omitted) leaves the person behaviour untouched.
        group.MapGet("/me/pending", async (
            WorkItemApprovalService svc,
            string? assignee, string? status, string? since, string? roleRequirement,
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
                _ => await svc.GetPendingForCurrentUserAsync(
                    ct, assignee, ParseRoleRequirement(roleRequirement)),
            };
            return Results.Ok(new
            {
                tickets = queue.Tickets,
                assignees = queue.Assignees,
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

        // Raise an issue — flags a problem on the item without calling it undeliverable.
        group.MapPost("/{key}/issues", async (
            WorkItemApprovalService svc,
            string key,
            WorkItemDecisionRequest body,
            CancellationToken ct) =>
        {
            var decoded = Uri.UnescapeDataString(key ?? "");
            return await RunDecisionAsync(() => svc.RaiseIssueAsync(
                decoded, body.Product ?? "", body.TargetEnv ?? "", body.Comment, ct));
        });

        // Record a block — holds the item back. Neither this nor /issues vetoes the promotion, and
        // both are reversible: the same user may later POST /approvals to release the item.
        //
        // These two routes replaced /blocks (which meant today's /issues) and /rejections (which
        // meant today's /blocks). Old paths are gone rather than aliased: an alias would have kept
        // /blocks working while silently changing which decision it records.
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

    /// <summary>
    /// Maps the <c>roleRequirement</c> query value onto the filter. Unknown values fall back to
    /// <see cref="WorkItemRoleRequirementFilter.Any"/> rather than 400: the parameter only narrows a
    /// read, so a typo should return the unnarrowed queue, not fail the page.
    /// </summary>
    private static WorkItemRoleRequirementFilter ParseRoleRequirement(string? value)
        => (value?.Trim().ToLowerInvariant()) switch
        {
            "assigned" => WorkItemRoleRequirementFilter.Assigned,
            "missing" => WorkItemRoleRequirementFilter.Missing,
            _ => WorkItemRoleRequirementFilter.Any,
        };

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
        // Set on the entries written automatically for a sign-off; null for human discussion. The UI
        // styles the two differently and only offers edit/delete on the latter.
        decision = c.Decision?.ToString(),
        createdAt = c.CreatedAt,
        updatedAt = c.UpdatedAt,
    };

    private static object ToDetailDto(WorkItemDetail d) => new
    {
        workItemKey = d.WorkItemKey,
        product = d.Product,
        targetEnv = d.TargetEnv,
        // Where the change is actually running — the environments a reviewer can exercise it in.
        // Resolved from deploy events matching the carrying version, not from the promotion edge.
        environments = d.Environments.Select(e => new
        {
            environment = e.Environment,
            service = e.Service,
            version = e.Version,
            deployedAt = e.DeployedAt,
        }),
        title = d.Title,
        // The work item's body, verbatim from the source system. Null when the producer sent none —
        // the page renders no Content section at all in that case.
        content = d.Content,
        url = d.Url,
        provider = d.Provider,
        pendingCandidateId = d.PendingCandidateId,
        primaryCandidateId = d.PrimaryCandidateId,
        canApprove = d.CanApprove,
        canManage = d.CanManage,
        blockedReason = d.BlockedReason,
        myDecision = d.MyDecision,
        participants = d.Participants,
        // Work-item completeness: the roles the carrying promotion's policy requires somebody in, and
        // the ones still empty. A non-empty missingRoles is what makes the page ask for an assignment.
        requiredRoles = d.RequiredRoles,
        missingRoles = d.MissingRoles,
        approvals = d.Approvals.Select(ToApprovalDto),
        comments = d.Comments.Select(ToCommentDto),
        // The change behind the ticket: the commits whose messages referenced it, and the pull
        // requests those commits merged. Empty when the producer didn't declare `commits` on the
        // work-item reference.
        commits = d.Commits.Select(c => new
        {
            hash = c.Hash,
            title = c.Title,
            url = c.Url,
            provider = c.Provider,
            participants = c.Participants,
        }),
        pullRequests = d.PullRequests.Select(p => new
        {
            key = p.Key,
            title = p.Title,
            url = p.Url,
            provider = p.Provider,
            revision = p.Revision,
            participants = p.Participants,
        }),
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
