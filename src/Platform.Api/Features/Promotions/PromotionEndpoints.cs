using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Platform.Api.Features.Deployments.Models;
using Platform.Api.Features.Deployments;
using Platform.Api.Features.Promotions.Models;
using Platform.Api.Features.Settings;
using Platform.Api.Infrastructure.Auth;
using Platform.Api.Infrastructure.Identity;
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
        // Vocabulary for the list page's filter dropdowns. Deliberately takes no filter arguments:
        // options derived from a filtered list collapse to whatever is already selected.
        group.MapGet("/filter-options", async (PromotionService svc, CancellationToken ct) =>
        {
            var options = await svc.GetFilterOptionsAsync(ct);
            return Results.Ok(new { products = options.Products, targetEnvs = options.TargetEnvs });
        });

        // List candidates with filters + capability flags.
        group.MapGet("/", async (
            PromotionService svc,
            PlatformDbContext db,
            string? status,
            string? product,
            string? service,
            string? targetEnv,
            string? reference,
            int? limit) =>
        {
            PromotionStatus? parsed = null;
            if (!string.IsNullOrEmpty(status))
            {
                if (!Enum.TryParse<PromotionStatus>(status, ignoreCase: true, out var s))
                    return Results.BadRequest(new { error = $"Unknown status '{status}'" });
                parsed = s;
            }

            // Different defaults when the UI is showing everything vs a single status.
            // - All-statuses view: cap the resolved tail at 25 (Pending is always uncapped).
            // - Single-status view: allow up to 200 so filtered Deployed/Rejected lists are useful.
            var defaultLimit = parsed is null ? 25 : 200;

            var query = new PromotionQuery(
                Status: parsed,
                Product: product,
                Service: service,
                TargetEnv: targetEnv,
                Limit: limit is > 0 ? limit.Value : defaultLimit);

            var candidates = await svc.GetAsync(query);

            // The candidate is self-contained — its own References are the net change set. The
            // reference filter matches any reference whose key, revision, provider, title, or URL
            // contains the search string (case-insensitive).
            var needle = (reference ?? "").Trim();
            if (needle.Length > 0)
            {
                bool RefMatches(ReferenceDto r) =>
                    ContainsIgnoreCase(r.Key, needle) ||
                    ContainsIgnoreCase(r.Revision, needle) ||
                    ContainsIgnoreCase(r.Provider, needle) ||
                    ContainsIgnoreCase(r.Url, needle) ||
                    ContainsIgnoreCase(r.Title, needle);

                candidates = candidates.Where(c => c.References.Any(RefMatches)).ToList();
            }

            var capability = await svc.CanUserApproveManyAsync(candidates);
            var targetVersions = await LoadTargetCurrentVersionsAsync(db, candidates);
            var sourceBranches = await LoadSourceBranchesAsync(db, candidates);

            return Results.Ok(new
            {
                candidates = candidates.Select(c =>
                {
                    targetVersions.TryGetValue((c.Product, c.Service, c.TargetEnv), out var targetCurrent);
                    sourceBranches.TryGetValue((c.Product, c.Service, c.Version), out var sourceBranch);
                    // sourceEventReferences carries the candidate's own net change set so the list
                    // card keeps rendering refs without a deploy-event join (D14 dropped the link).
                    return ToDto(c, capability.GetValueOrDefault(c.Id),
                        sourceEventParticipants: Array.Empty<ParticipantDto>(),
                        sourceEventReferences: c.References,
                        targetCurrentVersion: targetCurrent,
                        sourceBranch: sourceBranch);
                }),
            });
        });

        // Single candidate — includes the full approval trail for the detail view.
        group.MapGet("/{id:guid}", async (
            PromotionService svc, PlatformDbContext db, Guid id) =>
        {
            var c = await svc.GetByIdAsync(id);
            if (c is null) return Results.NotFound();
            var approvals = await svc.GetApprovalsAsync(id);
            var eligibleRequirements = await svc.GetEligibleRequirementsAsync(c);
            var progress = await svc.GetApprovalProgressAsync(c);
            // canApprove means "can approve right now", the same thing it means on the list (see
            // CanUserApproveManyAsync): an open requirement the user is authorized for AND no work-item
            // gate holding the promotion back. The approve card is rendered off eligibleRequirements
            // instead, so a blocked approver still gets the button — disabled, with the reason.
            var gateBlocking = progress.WorkItems is { Required: true, Satisfied: false };
            var canApprove = eligibleRequirements.Count > 0 && !gateBlocking;
            // Offered on an Approved candidate that hasn't been dispatched yet; mirrors the service's
            // own guards so the button appears exactly when the action would go through.
            var canCancelApproval = await svc.CanUserCancelApprovalAsync(c);

            var targetCurrent = await db.DeployEvents
                .AsNoTracking()
                .Where(e => e.Product == c.Product && e.Service == c.Service && e.Environment == c.TargetEnv)
                .OrderByDescending(e => e.DeployedAt)
                .Select(e => e.Version)
                .FirstOrDefaultAsync();

            // Build-sourced candidates only — see the `sourceBranch` note on ToDto.
            var sourceBranch = c.SourceEnv == Builds.BuildPromotions.SourceEnv
                ? await db.Builds
                    .AsNoTracking()
                    .Where(b => b.Product == c.Product && b.Service == c.Service && b.Version == c.Version)
                    .Select(b => b.Branch)
                    .FirstOrDefaultAsync()
                : null;

            var comments = await svc.GetCommentsAsync(id);

            // Surface an admin bypass, if any. A bypass records NO approval row, so without this a
            // force-approved candidate would show an empty approval trail with no trace of who did it
            // or why. Read the latest promotion.bypassed audit entry (the canonical record).
            var bypassEntry = await db.AuditLog.AsNoTracking()
                .Where(a => a.Action == "promotion.bypassed" && a.EntityId == id)
                .OrderByDescending(a => a.Timestamp)
                .Select(a => new { a.ActorName, a.Timestamp, a.AfterState })
                .FirstOrDefaultAsync();
            // …but a bypass that was subsequently cancelled is history, not the current state. Both
            // actions are audit entries on the same candidate, so the later one wins.
            var cancelledAt = await db.AuditLog.AsNoTracking()
                .Where(a => a.Action == "promotion.approval.cancelled" && a.EntityId == id)
                .OrderByDescending(a => a.Timestamp)
                .Select(a => (DateTimeOffset?)a.Timestamp)
                .FirstOrDefaultAsync();
            if (bypassEntry is not null && cancelledAt is { } cancelled && cancelled > bypassEntry.Timestamp)
                bypassEntry = null;
            object? bypass = null;
            if (bypassEntry is not null)
            {
                string? reason = null;
                if (!string.IsNullOrEmpty(bypassEntry.AfterState))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(bypassEntry.AfterState);
                        if (doc.RootElement.TryGetProperty("reason", out var r)) reason = r.GetString();
                    }
                    catch { /* best-effort — a malformed payload just yields a null reason */ }
                }
                bypass = new { byName = bypassEntry.ActorName, at = bypassEntry.Timestamp, reason };
            }

            // The deploy event that put this version live in the target environment. Nothing in
            // storage links a candidate to the deploy that closed it — completion is matched on
            // (product, service, targetEnv, version), see PromotionIngestHook.MatchCompletionAsync —
            // so the read path resolves it with that same rule. Only for a closed candidate: while one
            // is still open there is no landing to point at.
            Guid? deploymentEventId = null;
            if (c.Status == PromotionStatus.Deployed && c.DeployedAt is { } landedAt)
            {
                deploymentEventId = await db.DeployEvents
                    .AsNoTracking()
                    .Where(e => e.Product == c.Product
                             && e.Service == c.Service
                             && e.Environment == c.TargetEnv
                             && e.Version == c.Version
                             && e.Status == "succeeded")
                    // The landing whose timestamp the close stamped on the candidate is the one that
                    // closed it. Failing that (a candidate closed with no event timestamp to hand) the
                    // earliest succeeded landing — the same "when did this version go live" answer
                    // AssessAgainstDeployHistoryAsync gives.
                    .OrderByDescending(e => e.DeployedAt == landedAt)
                    .ThenBy(e => e.DeployedAt)
                    .Select(e => (Guid?)e.Id)
                    .FirstOrDefaultAsync();
            }

            // The candidate is self-contained (D14): no source deploy event. The change set lives
            // on the candidate's own References, surfaced as `sourceEvent` so the detail view keeps
            // rendering work items / PRs without a join.
            return Results.Ok(new
            {
                candidate = ToDto(c, canApprove,
                    sourceEventParticipants: Array.Empty<ParticipantDto>(),
                    sourceEventReferences: c.References,
                    targetCurrentVersion: targetCurrent,
                    sourceBranch: sourceBranch),
                approvals = approvals.Select(a => new
                {
                    a.Id,
                    a.ApproverEmail,
                    a.ApproverName,
                    a.Comment,
                    decision = a.Decision.ToString(),
                    a.StepName,
                    a.RequirementName,
                    a.CreatedAt,
                }),
                eligibleRequirements = eligibleRequirements.Select(r => new
                {
                    stepName = r.StepName,
                    requirementName = r.RequirementName,
                }),
                sourceEvent = new
                {
                    id = (Guid?)null,
                    deployedAt = c.CreatedAt,
                    source = "external",
                    references = c.References,
                    participants = c.Participants,
                    enrichment = (object?)null,
                },
                comments = comments.Select(ToCommentDto),
                approvalProgress = progress,
                bypass,
                canCancelApproval,
                deploymentEventId,
            });
        });

        // Participant roles actually *observed* in the data (distinct, frequency-ordered). Note this
        // is the opposite question from "which roles may I assign?" — that one is answered by the
        // configured vocabulary (Settings → Participant Roles), which is what the pickers list. This
        // route reports what producers have sent, including roles nobody configured.
        group.MapGet("/roles", async (PromotionService svc) =>
        {
            var roles = await svc.GetKnownRolesAsync();
            return Results.Ok(new { roles });
        });

        // User search for the assign-participant picker. Proxies to IIdentityService — hits Entra
        // Graph when configured, falls back to local users otherwise. Returns empty list for
        // short queries so we don't flood Graph on every keystroke.
        group.MapGet("/users/search", async (
            IIdentityService identity,
            ILoggerFactory loggerFactory,
            string? q,
            CancellationToken ct) =>
        {
            var log = loggerFactory.CreateLogger("PromotionEndpoints.UserSearch");
            var query = (q ?? "").Trim();
            // Sanitise any user-provided value before logging — strips CR/LF and other control
            // characters so a crafted query string can't inject fake log lines (log forging).
            var loggableQuery = SanitizeForLog(query);
            if (query.Length < 2)
            {
                log.LogInformation("User search skipped (query too short, length={Length})", query.Length);
                return Results.Ok(new { users = Array.Empty<object>() });
            }

            log.LogInformation(
                "User search started (provider={Provider}, query='{Query}')",
                identity.GetType().Name, loggableQuery);

            try
            {
                var users = await identity.SearchUsers(query, ct);
                log.LogInformation(
                    "User search returned {Count} result(s) for query '{Query}' via {Provider}",
                    users.Count, loggableQuery, identity.GetType().Name);

                return Results.Ok(new
                {
                    users = users.Select(u => new
                    {
                        id = u.Id,
                        displayName = u.DisplayName,
                        email = u.Email,
                    }),
                });
            }
            catch (Exception ex)
            {
                // Graph unreachable / misconfigured — return empty rather than error so the UI
                // silently falls back to manual entry. Log loudly so dev can see why.
                log.LogWarning(ex,
                    "User search failed for query '{Query}' via {Provider} — returning empty list",
                    loggableQuery, identity.GetType().Name);
                return Results.Ok(new { users = Array.Empty<object>() });
            }
        });

        // Group search for the approval-policy editor's group picker. Mirrors /users/search:
        // proxies to IIdentityService (Entra Graph when configured, static dev groups otherwise),
        // skips short queries, and swallows Graph failures into an empty list so the UI falls back
        // to manual entry.
        group.MapGet("/groups/search", async (
            IIdentityService identity,
            ILoggerFactory loggerFactory,
            string? q,
            CancellationToken ct) =>
        {
            var log = loggerFactory.CreateLogger("PromotionEndpoints.GroupSearch");
            var query = (q ?? "").Trim();
            var loggableQuery = SanitizeForLog(query);
            if (query.Length < 2)
            {
                log.LogInformation("Group search skipped (query too short, length={Length})", query.Length);
                return Results.Ok(new { groups = Array.Empty<object>() });
            }

            log.LogInformation(
                "Group search started (provider={Provider}, query='{Query}')",
                identity.GetType().Name, loggableQuery);

            try
            {
                var groups = await identity.SearchGroups(query, ct);
                log.LogInformation(
                    "Group search returned {Count} result(s) for query '{Query}' via {Provider}",
                    groups.Count, loggableQuery, identity.GetType().Name);

                return Results.Ok(new
                {
                    groups = groups.Select(g => new
                    {
                        id = g.Id,
                        displayName = g.DisplayName,
                    }),
                });
            }
            catch (Exception ex)
            {
                log.LogWarning(ex,
                    "Group search failed for query '{Query}' via {Provider} — returning empty list",
                    loggableQuery, identity.GetType().Name);
                return Results.Ok(new { groups = Array.Empty<object>() });
            }
        });

        // Upsert a participant on the candidate. Role is canonicalised to lower-kebab-case;
        // display is controlled by the admin-managed role dictionary on the frontend.
        //
        // The role must be one the operator has configured (Settings → Participant Roles). Ingest is
        // exempt — a producer's payload is recorded as sent — but a hand-made assignment onto an
        // unknown role only ever produces a slot nothing can filter, label, or route on.
        group.MapPost("/{id:guid}/participants", async (
            PromotionService svc, ParticipantRoleCatalog roleCatalog, Guid id, UpsertParticipantRequest body) =>
        {
            if (!await roleCatalog.IsConfiguredAsync(body.Role))
            {
                return Results.BadRequest(new
                {
                    error = ParticipantRoleCatalog.RejectionMessage(
                        body.Role, await roleCatalog.GetCanonicalKeysAsync()),
                });
            }
            try
            {
                var participant = new PromotionParticipant(
                    Role: body.Role ?? "",
                    DisplayName: body.DisplayName,
                    Email: body.Email);
                var candidate = await svc.UpsertParticipantAsync(id, participant);
                return Results.Ok(new { participants = candidate.Participants });
            }
            catch (KeyNotFoundException ex) { return Results.NotFound(new { error = ex.Message }); }
            catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        // Remove a participant by role.
        group.MapDelete("/{id:guid}/participants/{role}", async (
            PromotionService svc, Guid id, string role) =>
        {
            try
            {
                var candidate = await svc.RemoveParticipantAsync(id, role);
                return Results.Ok(new { participants = candidate.Participants });
            }
            catch (KeyNotFoundException ex) { return Results.NotFound(new { error = ex.Message }); }
        });

        // Assign / reassign / clear a participant on a specific work-item reference of a candidate.
        // This is what the work-items queue's "Assign" writes to (candidates are self-contained, so
        // there is no deploy event to override). Body: { role, assignee: { email, displayName } | null }.
        // Clearing a slot (assignee == null) is allowed on any role, configured or not: an ingested
        // payload can put someone on a role nobody configured, and that assignment has to remain
        // removable. Only naming a person requires a configured role.
        group.MapPatch("/{id:guid}/references/{referenceKey}/participants", async (
            PromotionService svc, ParticipantRoleCatalog roleCatalog,
            Guid id, string referenceKey, AssignReferenceParticipantRequest body) =>
        {
            if (string.IsNullOrWhiteSpace(body.Role))
                return Results.BadRequest(new { error = "role is required" });
            if (body.Assignee is not null && !await roleCatalog.IsConfiguredAsync(body.Role))
            {
                return Results.BadRequest(new
                {
                    error = ParticipantRoleCatalog.RejectionMessage(
                        body.Role, await roleCatalog.GetCanonicalKeysAsync()),
                });
            }
            try
            {
                var assignee = body.Assignee is null
                    ? null
                    : new ParticipantDto(body.Role, body.Assignee.DisplayName, body.Assignee.Email);
                var participants = await svc.UpsertReferenceParticipantAsync(id, referenceKey, body.Role, assignee);
                return Results.Ok(new { participants });
            }
            catch (KeyNotFoundException ex) { return Results.NotFound(new { error = ex.Message }); }
            catch (UnauthorizedAccessException ex) { return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status403Forbidden); }
            catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        // List comments.
        group.MapGet("/{id:guid}/comments", async (PromotionService svc, Guid id) =>
        {
            var comments = await svc.GetCommentsAsync(id);
            return Results.Ok(new { comments = comments.Select(ToCommentDto) });
        });

        // Add comment.
        group.MapPost("/{id:guid}/comments", async (
            PromotionService svc, Guid id, CommentRequest body) =>
        {
            try
            {
                var comment = await svc.AddCommentAsync(id, body.Body ?? "");
                return Results.Ok(ToCommentDto(comment));
            }
            catch (KeyNotFoundException ex) { return Results.NotFound(new { error = ex.Message }); }
            catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        // Edit comment (author or admin only).
        group.MapPatch("/comments/{commentId:guid}", async (
            PromotionService svc, Guid commentId, CommentRequest body) =>
        {
            try
            {
                var comment = await svc.UpdateCommentAsync(commentId, body.Body ?? "");
                return Results.Ok(ToCommentDto(comment));
            }
            catch (KeyNotFoundException ex) { return Results.NotFound(new { error = ex.Message }); }
            catch (UnauthorizedAccessException ex) { return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status403Forbidden); }
            catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        // Delete comment (author or admin only).
        group.MapDelete("/comments/{commentId:guid}", async (
            PromotionService svc, Guid commentId) =>
        {
            try
            {
                await svc.DeleteCommentAsync(commentId);
                return Results.NoContent();
            }
            catch (KeyNotFoundException ex) { return Results.NotFound(new { error = ex.Message }); }
            catch (UnauthorizedAccessException ex) { return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status403Forbidden); }
        });

        // Approve. The body may pin which requirement the approver approves as (stepName/requirementName)
        // when they are eligible for more than one open requirement.
        group.MapPost("/{id:guid}/approve", async (
            PromotionService svc, Guid id, PromotionDecisionRequest? body) =>
        {
            try
            {
                var candidate = await svc.ApproveAsync(id, body?.Comment, body?.StepName, body?.RequirementName);
                return Results.Ok(ToDto(candidate, canApprove: false));
            }
            catch (MultipleEligibleRequirementsException ex)
            {
                // 400 + the choices so the UI knows to prompt "approve as...".
                return Results.BadRequest(new
                {
                    error = ex.Message,
                    eligibleRequirements = ex.Options.Select(o => new { stepName = o.StepName, requirementName = o.RequirementName }),
                });
            }
            catch (RequirementAlreadySatisfiedException ex)
            {
                return Results.Conflict(new { error = ex.Message });
            }
            catch (KeyNotFoundException ex) { return Results.NotFound(new { error = ex.Message }); }
            catch (UnauthorizedAccessException ex) { return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status403Forbidden); }
            catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        // Reject.
        group.MapPost("/{id:guid}/reject", async (
            PromotionService svc, Guid id, PromotionDecisionRequest? body) =>
        {
            return await RunDecisionAsync(() => svc.RejectAsync(id, body?.Comment));
        });

        // Cancel approval — Approved back to Pending, for the wrong row approved by mistake. Only
        // while the candidate hasn't been dispatched; the service refuses everything else. Returns
        // whether the held promotion.approved webhook was caught in time, which is what the person
        // undoing wants to know.
        group.MapPost("/{id:guid}/cancel-approval", async (
            PromotionService svc, Guid id, PromotionDecisionRequest? body) =>
        {
            try
            {
                var result = await svc.CancelApprovalAsync(id, body?.Comment);
                return Results.Ok(new
                {
                    candidate = ToDto(result.Candidate, canApprove: false),
                    clearedApprovals = result.ClearedApprovals,
                    approvedWebhookStopped = result.ApprovedWebhookStopped,
                });
            }
            catch (KeyNotFoundException ex) { return Results.NotFound(new { error = ex.Message }); }
            catch (UnauthorizedAccessException ex) { return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status403Forbidden); }
            catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
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

        // The target envs a registered build can be promoted to for this service: every edge whose
        // source is the synthetic "build" env and whose policy resolves (service-specific row wins
        // over product default, same as PromotionPolicyResolver). Powers the deploy-a-build picker's
        // env selector; an empty list means the product isn't enrolled in build promotions.
        group.MapGet("/build-targets", async (
            PlatformDbContext db, string? product, string? service, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(product) || string.IsNullOrWhiteSpace(service))
                return Results.BadRequest(new { error = "'product' and 'service' are required" });

            var rows = await db.PromotionPolicies.AsNoTracking()
                .Where(p => p.Product == product
                         && p.SourceEnv == Builds.BuildPromotions.SourceEnv
                         && (p.Service == service || p.Service == null))
                .ToListAsync(ct);

            var targets = rows
                .GroupBy(p => p.TargetEnv)
                .Select(g => g.OrderBy(p => p.Service == null ? 1 : 0).First())
                .OrderBy(p => p.TargetEnv)
                .Select(p => new
                {
                    targetEnv = p.TargetEnv,
                    // Whether picking this target deploys without further ceremony — the picker
                    // says so up front rather than surprising people with an approval queue.
                    autoApprove = PromotionPolicyResolver.Project(p).IsAutoApprove,
                });
            return Results.Ok(new { targets });
        });

        // Create a candidate from a registered build — the human path ("deploy this build"), where
        // POST / below is the machine path. The server builds the change set from the build row
        // (same projection the auto-create hook uses), so the browser never assembles references,
        // and the caller is stamped as triggered-by. Any authenticated user may ask; the resolved
        // policy's approval gate is the control point, exactly as for external creates.
        group.MapPost("/from-build", async (
            PromotionService svc, PlatformDbContext db, ICurrentUser user,
            CreateFromBuildRequest req, CancellationToken ct) =>
        {
            if (req.BuildId == Guid.Empty || string.IsNullOrWhiteSpace(req.TargetEnv))
                return Results.BadRequest(new { error = "'buildId' and 'targetEnv' are required" });

            var build = await db.Builds.AsNoTracking().FirstOrDefaultAsync(b => b.Id == req.BuildId, ct);
            if (build is null)
                return Results.NotFound(new { error = $"Build {req.BuildId} is not registered" });

            var dto = new CreatePromotionDto(
                Product: build.Product,
                Service: build.Service,
                SourceEnv: Builds.BuildPromotions.SourceEnv,
                TargetEnv: req.TargetEnv.Trim(),
                Version: build.Version,
                FromRevision: null,
                ToRevision: build.CommitSha,
                References: Builds.BuildPromotions.BuildReferences(build),
                Participants: [new ParticipantDto("triggered-by", user.Name, user.Email)]);

            PromotionCandidate? candidate;
            try
            {
                candidate = await svc.CreateExternalCandidateAsync(dto, ct);
            }
            catch (SourceDeploymentNotFoundException)
            {
                // The policy on this edge still demands a source deploy, but nothing is ever
                // deployed to "build" — an edge misconfiguration, not a bad request.
                return Results.UnprocessableEntity(new
                {
                    code = "source_deploy_missing",
                    error = $"The '{Builds.BuildPromotions.SourceEnv}' → '{req.TargetEnv}' policy requires a "
                        + "source deployment, which the build source env never has. "
                        + "Set sourceRequiresDeploy=false on that policy.",
                });
            }
            catch (TargetAlreadyAtVersionException ex)
            {
                return Results.UnprocessableEntity(new { code = "target_already_at_version", error = ex.Message });
            }
            if (candidate is null)
            {
                return Results.UnprocessableEntity(new
                {
                    code = "policy_missing",
                    error = $"No promotion policy is configured for '{build.Product}'/'{build.Service}' "
                        + $"'{Builds.BuildPromotions.SourceEnv}' → '{req.TargetEnv}'",
                });
            }

            return Results.Created(
                $"/api/promotions/{candidate.Id}",
                new { id = candidate.Id, status = candidate.Status.ToString() });
        });

        // Create a promotion candidate from an external system (CI). The external computes the
        // authoritative net change set (env-to-env diff) and POSTs it; the tool records it verbatim.
        // Secured with API key + per-key rate limit + product scope — mirrors /api/deployments/events.
        // D16: keys that declare a Scopes list must hold promotion:create — separates "may report
        // deploys" from "may open gated releases". Keys without a Scopes list stay unrestricted.
        group.MapPost("/", async (
            PromotionService svc, ServiceProductOverrideService productOverrides,
            ClaimsPrincipal user, CreatePromotionDto dto, CancellationToken ct) =>
        {
            if (!ApiKeyAuthHandler.HasScope(user, ApiKeyScopes.PromotionCreate))
                return Results.Forbid();

            var errors = ValidateCreate(dto);
            if (errors.Count > 0)
                return Results.BadRequest(new { errors });

            // Enforce product scope when the key restricts which products it can post for. Checked
            // against the product the key SENT, not the one a ServiceProductOverride redirects it to:
            // the claim says what this key is entitled to talk about, and the redirect is an admin
            // decision the key neither chose nor can influence. Scoping on the resolved product would
            // instead break every pipeline whose key still names the product it is migrating off.
            var allowedProducts = user.FindAll(ApiKeyAuthHandler.AllowedProductClaim).Select(c => c.Value).ToList();
            if (allowedProducts.Count > 0 &&
                !allowedProducts.Contains(dto.Product, StringComparer.OrdinalIgnoreCase))
            {
                return Results.Forbid();
            }

            PromotionCandidate? candidate;
            try
            {
                candidate = await svc.CreateExternalCandidateAsync(dto, ct);
            }
            // Each 422 carries a stable machine-readable `code` alongside the human `error` so
            // pipeline callers can branch (skip / remediate / alert) without parsing messages.
            catch (SourceDeploymentNotFoundException ex)
            {
                // The (product, service, sourceEnv, version) has no succeeded deployment — cannot
                // promote an unknown source.
                return Results.UnprocessableEntity(new { code = "source_deploy_missing", error = ex.Message });
            }
            catch (TargetAlreadyAtVersionException ex)
            {
                // The target env already runs this version — nothing to promote.
                return Results.UnprocessableEntity(new { code = "target_already_at_version", error = ex.Message });
            }
            if (candidate is null)
            {
                // No policy resolved for this source→target edge — the product isn't enrolled. Name the
                // product the policy lookup actually used: when an override redirected this service,
                // echoing the sent product sends the caller off to configure a policy on a product that
                // was never consulted. The resolver memoises per request, so this costs nothing.
                var resolvedProduct = await productOverrides.ResolveProductAsync(dto.Product, dto.Service, ct);
                return Results.UnprocessableEntity(new
                {
                    code = "policy_missing",
                    error = $"No promotion policy is configured for '{resolvedProduct}'/'{dto.Service}' '{dto.SourceEnv}' → '{dto.TargetEnv}'",
                });
            }

            return Results.Created(
                $"/api/promotions/{candidate.Id}",
                new { id = candidate.Id, status = candidate.Status.ToString() });
        })
        .RequireAuthorization(ApiKeyAuthHandler.PolicyName)
        .RequireRateLimiting(DeploymentIngestionRateLimit.PolicyName);

        return group;
    }

    private static List<string> ValidateCreate(CreatePromotionDto dto)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(dto.Product)) errors.Add("product is required");
        if (string.IsNullOrWhiteSpace(dto.Service)) errors.Add("service is required");
        if (string.IsNullOrWhiteSpace(dto.SourceEnv)) errors.Add("sourceEnv is required");
        if (string.IsNullOrWhiteSpace(dto.TargetEnv)) errors.Add("targetEnv is required");
        if (string.IsNullOrWhiteSpace(dto.Version)) errors.Add("version is required");
        return errors;
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

    private static readonly JsonSerializerOptions SourceEventJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private static T? Deserialize<T>(string? json)
    {
        if (string.IsNullOrEmpty(json)) return default;
        return JsonSerializer.Deserialize<T>(json, SourceEventJsonOptions);
    }

    private static object ToDto(
        PromotionCandidate c,
        bool canApprove,
        IReadOnlyList<ParticipantDto>? sourceEventParticipants = null,
        IReadOnlyList<ReferenceDto>? sourceEventReferences = null,
        string? targetCurrentVersion = null,
        string? sourceBranch = null) => new
    {
        id = c.Id,
        product = c.Product,
        service = c.Service,
        sourceEnv = c.SourceEnv,
        targetEnv = c.TargetEnv,
        version = c.Version,
        // Display/traceability only — the target env's current SHA and the promoted SHA.
        fromRevision = c.FromRevision,
        toRevision = c.ToRevision,
        // Version currently deployed in the target environment (what this promotion
        // would replace). Null when the target has no prior deploy for this service.
        targetCurrentVersion,
        // Git ref the promoted build was produced from. Only build-sourced candidates have one:
        // "build" is a synthetic source env — nothing runs there — so the branch is the only thing
        // that says where the version actually came from. Null on every other edge, and on a
        // build-sourced candidate whose registry row has since been removed.
        sourceBranch,
        status = c.Status.ToString(),
        externalRunUrl = c.ExternalRunUrl,
        createdAt = c.CreatedAt,
        approvedAt = c.ApprovedAt,
        deployedAt = c.DeployedAt,
        supersededById = c.SupersededById,
        participants = c.Participants,
        sourceEventParticipants = sourceEventParticipants ?? Array.Empty<ParticipantDto>(),
        // Work-item references go out with their display lines resolved (tracker name on top, the
        // messages of the ticket's commits underneath — see Deployments.WorkItemDisplay), so the
        // promotion page names a ticket the same way the work-item queue and detail page do. Every
        // other reference is passed through untouched.
        sourceEventReferences = Deployments.WorkItemDisplay.ApplyToReferences(
            sourceEventReferences ?? Array.Empty<ReferenceDto>()),
        canApprove,
        // False ⇒ this edge creates no work items, so the UI drops the whole work-item affordance
        // (sign-off links, counts, completeness) and shows the references as change-set history only.
        tracksWorkItems = WorkItemRoleRequirements.TracksWorkItems(c),
        // Work-item completeness, derived from the candidate's own policy snapshot and participants
        // (see WorkItemRoleRequirements) — no extra query, and automatically correct after a late
        // work-item attachment, a reassignment, or a policy edit.
        requiredWorkItemRoles = WorkItemRoleRequirements.RequiredRoles(c),
        workItemRoleGaps = WorkItemRoleRequirements.Evaluate(c).Select(g => new
        {
            workItemKey = g.WorkItemKey,
            title = g.Title,
            missingRoles = g.MissingRoles,
        }),
    };

    // Batch-looks up the current (latest) deployed version per (product, service, targetEnv)
    // triple across the candidate set. Single query; returns a dictionary keyed by the triple.
    private static async Task<Dictionary<(string Product, string Service, string TargetEnv), string>> LoadTargetCurrentVersionsAsync(
        PlatformDbContext db,
        IReadOnlyCollection<PromotionCandidate> candidates,
        CancellationToken ct = default)
    {
        var triples = candidates
            .Select(c => new { c.Product, c.Service, c.TargetEnv })
            .Distinct()
            .ToList();
        if (triples.Count == 0) return new();

        var products = triples.Select(t => t.Product).Distinct().ToList();
        var services = triples.Select(t => t.Service).Distinct().ToList();
        var envs = triples.Select(t => t.TargetEnv).Distinct().ToList();

        // Over-fetch candidates with a coarse product/service/env IN filter, then
        // reduce in-memory to (product, service, env) -> latest version.
        var events = await db.DeployEvents
            .AsNoTracking()
            .Where(e => products.Contains(e.Product)
                     && services.Contains(e.Service)
                     && envs.Contains(e.Environment))
            .Select(e => new { e.Product, e.Service, e.Environment, e.Version, e.DeployedAt })
            .ToListAsync(ct);

        var wanted = triples.Select(t => (t.Product, t.Service, t.TargetEnv)).ToHashSet();
        return events
            .Where(e => wanted.Contains((e.Product, e.Service, e.Environment)))
            .GroupBy(e => (e.Product, e.Service, e.Environment))
            .ToDictionary(g => g.Key, g => g.OrderByDescending(e => e.DeployedAt).First().Version);
    }

    // Batch-looks up the branch each build-sourced candidate's version was built from, keyed by
    // (product, service, version) — the build registry's own unique triple. Candidates promoted
    // from a real environment are skipped: their provenance is the source env, not a git ref.
    // One query, and none at all for a candidate set with no build-sourced rows in it.
    private static async Task<Dictionary<(string Product, string Service, string Version), string>> LoadSourceBranchesAsync(
        PlatformDbContext db,
        IReadOnlyCollection<PromotionCandidate> candidates,
        CancellationToken ct = default)
    {
        var triples = candidates
            .Where(c => c.SourceEnv == Builds.BuildPromotions.SourceEnv)
            .Select(c => new { c.Product, c.Service, c.Version })
            .Distinct()
            .ToList();
        if (triples.Count == 0) return new();

        var products = triples.Select(t => t.Product).Distinct().ToList();
        var services = triples.Select(t => t.Service).Distinct().ToList();
        var versions = triples.Select(t => t.Version).Distinct().ToList();

        // Same shape as LoadTargetCurrentVersionsAsync: a coarse IN filter, then the exact triples
        // reduced in memory — the extra rows a coarse filter drags in are cheap next to a query
        // per candidate.
        var builds = await db.Builds
            .AsNoTracking()
            .Where(b => products.Contains(b.Product)
                     && services.Contains(b.Service)
                     && versions.Contains(b.Version))
            .Select(b => new { b.Product, b.Service, b.Version, b.Branch })
            .ToListAsync(ct);

        var wanted = triples.Select(t => (t.Product, t.Service, t.Version)).ToHashSet();
        return builds
            .Where(b => wanted.Contains((b.Product, b.Service, b.Version))
                     && !string.IsNullOrWhiteSpace(b.Branch))
            .GroupBy(b => (b.Product, b.Service, b.Version))
            .ToDictionary(g => g.Key, g => g.First().Branch);
    }

    private static bool ContainsIgnoreCase(string? haystack, string needle)
        => !string.IsNullOrEmpty(haystack)
           && haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);

    // Scrubs user-provided strings before they land in a log line. Drops ASCII control
    // characters (including CR/LF) so a crafted query can't inject fake log entries
    // (CWE-117, log forging). Also caps length so a huge value can't blow up log storage.
    private static string SanitizeForLog(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var trimmed = value.Length > 200 ? value[..200] : value;
        var sb = new System.Text.StringBuilder(trimmed.Length);
        foreach (var ch in trimmed)
        {
            // Skip C0 controls (0x00-0x1F) and DEL (0x7F); keep ordinary printable characters.
            if (ch < 0x20 || ch == 0x7F) continue;
            sb.Append(ch);
        }
        return sb.ToString();
    }

    private static object ToCommentDto(PromotionComment c) => new
    {
        id = c.Id,
        candidateId = c.CandidateId,
        authorEmail = c.AuthorEmail,
        authorName = c.AuthorName,
        body = c.Body,
        createdAt = c.CreatedAt,
        updatedAt = c.UpdatedAt,
    };
}

/// <summary>
/// External create-promotion payload. The caller (CI) computes the authoritative net change set
/// and POSTs it. <c>FromRevision</c>/<c>ToRevision</c> are display/traceability only (not gating).
/// <c>References</c> is the self-contained change set (work-item / pull-request / repository refs);
/// <c>Participants</c> are promotion-level participants. No idempotency key — a repeat for the same
/// natural key <c>(Product, Service, SourceEnv, TargetEnv, Version)</c> is a legitimate update (D15).
/// </summary>
public record CreatePromotionDto(
    string Product,
    string Service,
    string SourceEnv,
    string TargetEnv,
    string Version,
    string? FromRevision,
    string? ToRevision,
    List<ReferenceDto>? References,
    List<ParticipantDto>? Participants);

/// <summary>Body for the deploy-a-build endpoint: which registered build, to which target env.</summary>
public record CreateFromBuildRequest(Guid BuildId, string TargetEnv);

public record PromotionDecisionRequest(string? Comment, string? StepName = null, string? RequirementName = null);
public record PromotionBulkRequest(Guid[] Ids, string? Comment);
public record UpsertParticipantRequest(string? Role, string? DisplayName, string? Email);

/// <summary>Body for assigning a participant to a work-item reference of a candidate. A null
/// <c>Assignee</c> clears the given role on that reference.</summary>
public record AssignReferenceParticipantRequest(string? Role, AssignReferenceParticipantTarget? Assignee);
public record AssignReferenceParticipantTarget(string? Email, string? DisplayName);
public record CommentRequest(string? Body);
