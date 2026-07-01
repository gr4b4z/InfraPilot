using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Platform.Api.Features.Deployments.Models;
using Platform.Api.Features.Promotions.Models;
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

            return Results.Ok(new
            {
                candidates = candidates.Select(c =>
                {
                    targetVersions.TryGetValue((c.Product, c.Service, c.TargetEnv), out var targetCurrent);
                    // sourceEventReferences carries the candidate's own net change set so the list
                    // card keeps rendering refs without a deploy-event join (D14 dropped the link).
                    return ToDto(c, capability.GetValueOrDefault(c.Id),
                        sourceEventParticipants: Array.Empty<ParticipantDto>(),
                        sourceEventReferences: c.References,
                        targetCurrentVersion: targetCurrent);
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
            // canApprove stays a bool for back-compat: true iff the user can approve as ≥1 requirement.
            var canApprove = eligibleRequirements.Count > 0;

            var targetCurrent = await db.DeployEvents
                .AsNoTracking()
                .Where(e => e.Product == c.Product && e.Service == c.Service && e.Environment == c.TargetEnv)
                .OrderByDescending(e => e.DeployedAt)
                .Select(e => e.Version)
                .FirstOrDefaultAsync();

            var comments = await svc.GetCommentsAsync(id);
            var approvalProgress = await svc.GetApprovalProgressAsync(c);

            // The candidate is self-contained (D14): no source deploy event. The change set lives
            // on the candidate's own References, surfaced as `sourceEvent` so the detail view keeps
            // rendering work items / PRs without a join.
            return Results.Ok(new
            {
                candidate = ToDto(c, canApprove,
                    sourceEventParticipants: Array.Empty<ParticipantDto>(),
                    sourceEventReferences: c.References,
                    targetCurrentVersion: targetCurrent),
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
                approvalProgress,
            });
        });

        // Known participant roles (distinct, frequency-ordered) for the assign-participant picker.
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
        group.MapPost("/{id:guid}/participants", async (
            PromotionService svc, Guid id, UpsertParticipantRequest body) =>
        {
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

        // Create a promotion candidate from an external system (CI). The external computes the
        // authoritative net change set (env-to-env diff) and POSTs it; the tool records it verbatim.
        // Secured with API key + per-key rate limit + product scope — mirrors /api/deployments/events.
        // TODO(D16): gate behind a distinct promotion:create scope so a key can be granted deploy
        // ingestion without the ability to open gated releases. Reusing the product-scope guard for
        // now — adding a real scope to the API-key system is out of scope for Workstream A.
        group.MapPost("/", async (
            PromotionService svc, ClaimsPrincipal user, CreatePromotionDto dto, CancellationToken ct) =>
        {
            var errors = ValidateCreate(dto);
            if (errors.Count > 0)
                return Results.BadRequest(new { errors });

            // Enforce product scope when the key restricts which products it can post for.
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
            catch (SourceDeploymentNotFoundException ex)
            {
                // The (product, service, sourceEnv, version) has no succeeded deployment — cannot
                // promote an unknown source.
                return Results.UnprocessableEntity(new { error = ex.Message });
            }
            if (candidate is null)
            {
                // No policy resolved for this source→target edge — the product isn't enrolled.
                return Results.UnprocessableEntity(new
                {
                    error = $"No promotion policy is configured for '{dto.Product}'/'{dto.Service}' '{dto.SourceEnv}' → '{dto.TargetEnv}'",
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
        string? targetCurrentVersion = null) => new
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
        status = c.Status.ToString(),
        externalRunUrl = c.ExternalRunUrl,
        createdAt = c.CreatedAt,
        approvedAt = c.ApprovedAt,
        deployedAt = c.DeployedAt,
        supersededById = c.SupersededById,
        participants = c.Participants,
        sourceEventParticipants = sourceEventParticipants ?? Array.Empty<ParticipantDto>(),
        sourceEventReferences = sourceEventReferences ?? Array.Empty<ReferenceDto>(),
        canApprove,
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

public record PromotionDecisionRequest(string? Comment, string? StepName = null, string? RequirementName = null);
public record PromotionBulkRequest(Guid[] Ids, string? Comment);
public record UpsertParticipantRequest(string? Role, string? DisplayName, string? Email);
public record CommentRequest(string? Body);
