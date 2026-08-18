using System.Security.Claims;
using Platform.Api.Features.Deployments.Models;
using Platform.Api.Infrastructure.Audit;
using Platform.Api.Infrastructure.Auth;

namespace Platform.Api.Features.Deployments;

public static class DeploymentEndpoints
{
    public static RouteGroupBuilder MapDeploymentEndpoints(this RouteGroupBuilder group)
    {
        // Ingestion — called by pipelines, secured with API key + per-key rate limit + optional product scope
        group.MapPost("/events", async (DeploymentService service, ClaimsPrincipal user, CreateDeployEventDto dto, CancellationToken ct) =>
        {
            var errors = Validate(dto);
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

            var result = await service.IngestEventWithResult(dto, ct);
            var body = new { result.Event.Id, result.Event.Version, result.Event.PreviousVersion, result.Replayed };
            // Replay of an already-ingested event (same natural key) → 200 with the existing row,
            // so retrying senders can distinguish "created" from "already there" but treat both as success.
            return result.Replayed
                ? Results.Ok(body)
                : Results.Created($"/api/deployments/events/{result.Event.Id}", body);
        })
        .RequireAuthorization(ApiKeyAuthHandler.PolicyName)
        .RequireRateLimiting(DeploymentIngestionRateLimit.PolicyName);

        // Manual deployment entry — a human (UI) or an agent (API) records a new deploy based on the
        // latest one, changing only version/status. Distinct from CI ingest: the server stamps
        // Source="manual" + triggered-by = the caller, so it's always attributable. A note is required.
        // Inherits the group's CanApprove gate (Bearer OR ApiKey, authenticated); finer checks below:
        // a human must be admin; an API key is product-scoped exactly like ingest.
        group.MapPost("/manual", async (
            DeploymentService service, ICurrentUser currentUser, IAuditLogger audit,
            ClaimsPrincipal user, CreateManualDeployRequest req, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Product) || string.IsNullOrWhiteSpace(req.Service)
                || string.IsNullOrWhiteSpace(req.Environment) || string.IsNullOrWhiteSpace(req.Version))
                return Results.BadRequest(new { error = "product, service, environment and version are required" });
            if (string.IsNullOrWhiteSpace(req.Note))
                return Results.BadRequest(new { error = "note is required for a manual deployment" });

            var isApiKey = user.Identities.Any(i => i.AuthenticationType == ApiKeyAuthHandler.SchemeName);
            ManualDeployActor actor;
            if (isApiKey)
            {
                // Product scope: honour the key's allowed_product claims exactly as /events does.
                var allowed = user.FindAll(ApiKeyAuthHandler.AllowedProductClaim).Select(c => c.Value).ToList();
                if (allowed.Count > 0 && !allowed.Contains(req.Product, StringComparer.OrdinalIgnoreCase))
                    return Results.Forbid();
                var keyName = user.FindFirstValue(ClaimTypes.Name) ?? "api-key";
                actor = new ManualDeployActor($"apikey:{keyName}", keyName, null, "api-key");
            }
            else
            {
                // Human caller: creating deploy records that drive promotions is admin-only.
                if (!currentUser.IsAdmin) return Results.Forbid();
                actor = new ManualDeployActor(currentUser.Id, currentUser.Name, currentUser.Email, "user");
            }

            try
            {
                var ev = await service.CreateManualEventAsync(req, actor, ct);
                await audit.Log(
                    "deployments", "deployment.manual.created",
                    actor.Id, actor.DisplayName, actor.ActorType,
                    "DeployEvent", ev.Id, null,
                    new { ev.Product, ev.Service, ev.Environment, ev.Version, ev.PreviousVersion, ev.Status, note = req.Note });
                return Results.Created($"/api/deployments/events/{ev.Id}",
                    new { ev.Id, ev.Version, ev.PreviousVersion, ev.Status, ev.Source });
            }
            catch (KeyNotFoundException ex)
            {
                return Results.NotFound(new { error = ex.Message });
            }
        });

        // Single deploy event — everything the deployment detail page shows: the event, its captured
        // pipeline output (as summaries; content is a separate call), the neighbouring deployments of
        // the same service, and the promotions / work items this deployment connects to.
        group.MapGet("/events/{id:guid}", async (
            DeploymentService service, Guid id, int? historyLimit, CancellationToken ct) =>
        {
            var detail = await service.GetEventDetail(id, historyLimit is > 0 ? historyLimit.Value : 10, ct);
            return detail is null ? Results.NotFound() : Results.Ok(detail);
        });

        // One block of captured output, fetched on demand — a Helm printout is too large to ride
        // along with the detail response for a page that may never expand it.
        group.MapGet("/events/{id:guid}/logs/{logId:guid}", async (
            DeploymentService service, Guid id, Guid logId, CancellationToken ct) =>
        {
            var log = await service.GetLogContent(id, logId, ct);
            return log is null ? Results.NotFound() : Results.Ok(log);
        });

        // Product overview
        group.MapGet("/products", async (DeploymentService service, CancellationToken ct) =>
        {
            return Results.Ok(await service.GetProductSummaries(ct));
        });

        // Current state matrix
        group.MapGet("/state", async (DeploymentService service, string? product, string? environment, string? serviceName, CancellationToken ct) =>
        {
            return Results.Ok(await service.GetState(product, environment, serviceName, ct));
        });

        // Cross-product service search — the deployments page's "find a service without knowing
        // its product" box. Case-insensitive substring match on the service name; a name shared by
        // two products returns two hits, because (product, service) is the identity.
        group.MapGet("/services/search", async (
            DeploymentService service, string? q, int? limit, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(q))
                return Results.BadRequest(new { error = "'q' is required" });

            var results = await service.SearchServices(q, limit is > 0 and <= 100 ? limit.Value : 20, ct);
            return Results.Ok(new { results });
        });

        // Everything the service detail page shows, in one round trip: current state per
        // environment, the last distinct versions, and the service's promotions. No collision with
        // /services/search above — that route has one segment fewer, so a product named "search"
        // would still resolve here.
        group.MapGet("/services/{product}/{serviceName}", async (
            DeploymentService service, string product, string serviceName,
            int? versionsLimit, CancellationToken ct) =>
        {
            var detail = await service.GetServiceDetail(
                product, serviceName, versionsLimit is > 0 and <= 50 ? versionsLimit.Value : 10, ct);
            return detail is null ? Results.NotFound() : Results.Ok(detail);
        });

        // Deployment history for a specific service
        group.MapGet("/history/{product}/{serviceName}", async (
            DeploymentService service, string product, string serviceName,
            string? environment, int? limit, CancellationToken ct) =>
        {
            return Results.Ok(await service.GetHistory(product, serviceName, environment, limit ?? 50, ct));
        });

        // Recent deployments across all environments for a product
        group.MapGet("/recent/{product}", async (
            DeploymentService service, string product,
            DateTimeOffset? since, int? limit, CancellationToken ct) =>
        {
            var sinceDate = since ?? DateTimeOffset.UtcNow.Date;
            return Results.Ok(await service.GetRecentByProduct(product, sinceDate, limit ?? 200, ct));
        });

        // Recent deployments for an environment
        group.MapGet("/recent/{product}/{environment}", async (
            DeploymentService service, string product, string environment,
            DateTimeOffset? since, CancellationToken ct) =>
        {
            var sinceDate = since ?? DateTimeOffset.UtcNow.Date;
            return Results.Ok(await service.GetRecentByEnvironment(product, environment, sinceDate, ct));
        });

        // Versions deployed to a given (product, environment[, service]) — powers the rollback
        // picker's "source: deployments/versions" catalog input.
        group.MapGet("/versions", async (
            DeploymentService service,
            string product, string environment, string? serviceName, int? limit,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(product) || string.IsNullOrWhiteSpace(environment))
                return Results.BadRequest(new { error = "'product' and 'environment' are required" });

            var versions = await service.GetVersions(product, environment, serviceName, limit ?? 50, ct);
            return Results.Ok(new { versions });
        });

        // Operator routing override: assign / reassign / clear a participant on a specific
        // reference of a deploy event. Lives separately from ingest so re-ingesting the same
        // upstream event won't clobber the manual override (the assignee is "just routing").
        //
        // Body: { role: string, assignee: { email, displayName } | null }
        //  - assignee non-null  → upsert override row.
        //  - assignee == null   → upsert tombstone row (suppresses lower layers — that's how
        //    operators express "remove the Jira-supplied person").
        // Auth: same baseline as the rest of /api/deployments (CanApprove). Only authenticated
        // users can mutate routing; this is intentionally NOT admin-only because the people who
        // need to reassign are the same people who triage the queue.
        group.MapPatch("/{eventId:guid}/references/{referenceKey}/participants", async (
            ReferenceParticipantOverrideService service,
            Guid eventId,
            string referenceKey,
            AssignReferenceParticipantRequest? body,
            CancellationToken ct) =>
        {
            if (body is null)
                return Results.BadRequest(new { error = "request body is required" });
            if (string.IsNullOrWhiteSpace(body.Role))
                return Results.BadRequest(new { error = "'role' is required" });

            try
            {
                var result = await service.AssignAsync(
                    eventId,
                    referenceKey,
                    body.Role,
                    assigneeEmail: body.Assignee?.Email,
                    assigneeDisplayName: body.Assignee?.DisplayName,
                    ct);
                return Results.Ok(new
                {
                    participants = result.Participants,
                    tombstone = result.Tombstone,
                    @override = result.Override,
                });
            }
            catch (KeyNotFoundException ex)
            {
                return Results.NotFound(new { error = ex.Message });
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        return group;
    }

    public record AssignReferenceParticipantRequest(string Role, AssigneeBody? Assignee);
    public record AssigneeBody(string? Email, string? DisplayName);

    private static readonly HashSet<string> ValidStatuses = new(StringComparer.OrdinalIgnoreCase)
        { "succeeded", "failed", "in_progress" };

    private static List<string> Validate(CreateDeployEventDto dto)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(dto.Product)) errors.Add("'product' is required");
        if (string.IsNullOrWhiteSpace(dto.Service)) errors.Add("'service' is required");
        if (string.IsNullOrWhiteSpace(dto.Environment)) errors.Add("'environment' is required");
        if (string.IsNullOrWhiteSpace(dto.Version)) errors.Add("'version' is required");
        if (string.IsNullOrWhiteSpace(dto.Source)) errors.Add("'source' is required");
        if (dto.DeployedAt == default) errors.Add("'deployedAt' is required");
        if (dto.Status is not null && !ValidStatuses.Contains(dto.Status))
            errors.Add($"'status' must be one of: {string.Join(", ", ValidStatuses)}");

        // Log blocks are identified by name (that's the replace-on-retry key), so an unnamed block
        // is rejected rather than silently dropped — a pipeline that captured output and got no
        // error would otherwise never learn why its logs aren't showing.
        if (dto.Logs is not null)
        {
            for (var i = 0; i < dto.Logs.Count; i++)
            {
                if (string.IsNullOrWhiteSpace(dto.Logs[i].Name))
                    errors.Add($"'logs[{i}].name' is required");
            }
        }

        // Reference-level participants: same shape as event-level — Role is required.
        if (dto.References is not null)
        {
            for (var i = 0; i < dto.References.Count; i++)
            {
                var nested = dto.References[i].Participants;
                if (nested is null) continue;
                for (var j = 0; j < nested.Count; j++)
                {
                    if (string.IsNullOrWhiteSpace(nested[j].Role))
                        errors.Add($"'references[{i}].participants[{j}].role' is required");
                }
            }
        }
        return errors;
    }
}
