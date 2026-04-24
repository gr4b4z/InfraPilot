using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Platform.Api.Features.Deployments.Models;
using Platform.Api.Features.Promotions.Models;
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

            // Batch-load source deploy events so the list card can render deploy-event people and
            // references (PR, work-item, pipeline, ...) alongside promotion-level data. One query.
            var eventIds = candidates.Select(c => c.SourceDeployEventId).Distinct().ToList();
            var eventData = await db.DeployEvents
                .AsNoTracking()
                .Where(e => eventIds.Contains(e.Id))
                .Select(e => new { e.Id, e.ParticipantsJson, e.EnrichmentJson, e.ReferencesJson })
                .ToDictionaryAsync(e => e.Id, e => e);

            // Optional reference filter — matches any reference whose key, revision, provider, or
            // URL contains the search string (case-insensitive). Applied after load because the
            // references live inside a JSON column; the candidate set is already bounded.
            var needle = (reference ?? "").Trim();
            if (needle.Length > 0)
            {
                candidates = candidates.Where(c =>
                {
                    var src = eventData.GetValueOrDefault(c.SourceDeployEventId);
                    var refs = ExtractSourceReferences(src?.ReferencesJson);
                    return refs.Any(r =>
                        ContainsIgnoreCase(r.Key, needle) ||
                        ContainsIgnoreCase(r.Revision, needle) ||
                        ContainsIgnoreCase(r.Provider, needle) ||
                        ContainsIgnoreCase(r.Url, needle));
                }).ToList();
            }

            var capability = await svc.CanUserApproveManyAsync(candidates);
            var targetVersions = await LoadTargetCurrentVersionsAsync(db, candidates);

            return Results.Ok(new
            {
                candidates = candidates.Select(c =>
                {
                    var source = eventData.GetValueOrDefault(c.SourceDeployEventId);
                    var sourceParticipants = ExtractSourceParticipants(source?.ParticipantsJson, source?.EnrichmentJson);
                    var sourceReferences = ExtractSourceReferences(source?.ReferencesJson);
                    targetVersions.TryGetValue((c.Product, c.Service, c.TargetEnv), out var targetCurrent);
                    return ToDto(c, capability.GetValueOrDefault(c.Id), sourceParticipants, sourceReferences, targetCurrent);
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
            var canApprove = await svc.CanUserApproveAsync(c);

            // Source deploy event carries references (work items, PRs) and participants
            // (authors, reviewers) — surface them on the detail view. May be absent if the
            // event was deleted or the candidate predates the link.
            var sourceEvent = await db.DeployEvents
                .AsNoTracking()
                .FirstOrDefaultAsync(e => e.Id == c.SourceDeployEventId);

            var targetCurrent = await db.DeployEvents
                .AsNoTracking()
                .Where(e => e.Product == c.Product && e.Service == c.Service && e.Environment == c.TargetEnv)
                .OrderByDescending(e => e.DeployedAt)
                .Select(e => e.Version)
                .FirstOrDefaultAsync();

            // Deploy events inherited from superseded predecessors — their refs and participants
            // surface on the current candidate so the audit trail survives the supersede chain.
            var inheritedIds = c.SupersededSourceEventIds;
            var inheritedEvents = inheritedIds.Count == 0
                ? new List<DeployEvent>()
                : await db.DeployEvents
                    .AsNoTracking()
                    .Where(e => inheritedIds.Contains(e.Id))
                    .ToListAsync();

            var inheritedRefs = new List<object>();
            var inheritedParticipants = new List<object>();
            foreach (var ev in inheritedEvents)
            {
                foreach (var r in ExtractSourceReferences(ev.ReferencesJson))
                {
                    inheritedRefs.Add(new { reference = r, fromVersion = ev.Version, fromDeployedAt = ev.DeployedAt });
                }
                foreach (var p in ExtractSourceParticipants(ev.ParticipantsJson, ev.EnrichmentJson))
                {
                    inheritedParticipants.Add(new { participant = p, fromVersion = ev.Version, fromDeployedAt = ev.DeployedAt });
                }
            }

            var comments = await svc.GetCommentsAsync(id);

            return Results.Ok(new
            {
                candidate = ToDto(c, canApprove, targetCurrentVersion: targetCurrent),
                inheritedReferences = inheritedRefs,
                inheritedParticipants,
                approvals = approvals.Select(a => new
                {
                    a.Id,
                    a.ApproverEmail,
                    a.ApproverName,
                    a.Comment,
                    decision = a.Decision.ToString(),
                    a.CreatedAt,
                }),
                sourceEvent = sourceEvent is null ? null : ToSourceEventDto(sourceEvent),
                comments = comments.Select(ToCommentDto),
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

    private static readonly JsonSerializerOptions SourceEventJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private static object ToSourceEventDto(DeployEvent e)
    {
        var references = Deserialize<List<ReferenceDto>>(e.ReferencesJson) ?? [];
        var participants = Deserialize<List<ParticipantDto>>(e.ParticipantsJson) ?? [];
        var enrichment = string.IsNullOrEmpty(e.EnrichmentJson)
            ? null
            : Deserialize<EnrichmentDto>(e.EnrichmentJson);

        return new
        {
            id = e.Id,
            deployedAt = e.DeployedAt,
            source = e.Source,
            references,
            participants,
            enrichment,
        };
    }

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
        // Version currently deployed in the target environment (what this promotion
        // would replace). Null when the target has no prior deploy for this service.
        targetCurrentVersion,
        status = c.Status.ToString(),
        sourceDeployerName = c.SourceDeployerName,
        sourceDeployerEmail = c.SourceDeployerEmail,
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

    private static IReadOnlyList<ReferenceDto> ExtractSourceReferences(string? referencesJson)
        => Deserialize<List<ReferenceDto>>(referencesJson) ?? new();

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

    // Flattens direct + enrichment participants from a deploy event's JSON into a single list.
    private static IReadOnlyList<ParticipantDto> ExtractSourceParticipants(
        string? participantsJson, string? enrichmentJson)
    {
        var direct = Deserialize<List<ParticipantDto>>(participantsJson) ?? new();
        var enrichment = string.IsNullOrEmpty(enrichmentJson)
            ? null
            : Deserialize<EnrichmentDto>(enrichmentJson);
        if (enrichment?.Participants is { Count: > 0 } extra)
            direct.AddRange(extra);
        return direct;
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

public record PromotionDecisionRequest(string? Comment);
public record PromotionBulkRequest(Guid[] Ids, string? Comment);
public record UpsertParticipantRequest(string? Role, string? DisplayName, string? Email);
public record CommentRequest(string? Body);
