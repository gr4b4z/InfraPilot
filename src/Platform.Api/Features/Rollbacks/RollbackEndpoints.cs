using Platform.Api.Infrastructure.Auth;
using Platform.Api.Features.Promotions;
using Platform.Api.Features.Rollbacks.Models;
using Platform.Api.Features.Settings;
using Platform.Api.Features.Users;

namespace Platform.Api.Features.Rollbacks;

/// <summary>
/// User-facing rollback endpoints (mounted at <c>/api/rollbacks</c>). The group-level policy is only
/// "authenticated"; the real authorization is per-product and lives in <see cref="RollbackService"/>
/// — creating requires membership of the resolved <c>RollbackPolicy</c>'s creator set, approving
/// requires membership of one of its requirements, and overriding requires admin. Policy
/// administration lives in <see cref="RollbackAdminEndpoints"/>.
/// </summary>
public static class RollbackEndpoints
{
    public static RouteGroupBuilder MapRollbackEndpoints(this RouteGroupBuilder group)
    {
        // List requests with filters + per-request approve/override capabilities.
        group.MapGet("/", async (
            RollbackService svc, EnvironmentAliasResolver environments,
            string? status, string? product, string? targetEnv, int? limit, CancellationToken ct) =>
        {
            RollbackStatus? parsed = null;
            if (!string.IsNullOrEmpty(status))
            {
                if (!Enum.TryParse<RollbackStatus>(status, ignoreCase: true, out var s))
                    return Results.BadRequest(new { error = $"Unknown status '{status}'" });
                parsed = s;
            }
            var env = await environments.ResolveFilterAsync(targetEnv, ct);
            var requests = await svc.GetAsync(new RollbackQuery(parsed, product, env, limit ?? 200));
            var caps = new Dictionary<Guid, (bool Approve, bool Override)>();
            foreach (var r in requests)
                caps[r.Id] = (await svc.CanUserApproveAsync(r), await svc.CanUserOverrideAsync(r));
            var failedAttempts = await svc.GetFailedAttemptsAsync(requests, ct);
            return Results.Ok(new
            {
                requests = requests.Select(r =>
                {
                    var c = caps.GetValueOrDefault(r.Id);
                    return ToDto(r, c.Approve, c.Override, failedAttempts);
                }),
            });
        });

        // Feeds the create-rollback product picker: products the caller may actually raise a rollback
        // for, minus their hidden ones. The hidden-product filter is applied here at the edge, not in
        // the service, because hiding a product from your own view is a display preference and must
        // never function as a permission — GetCreatableProductsAsync is the permission.
        group.MapGet("/enabled-products", async (
            RollbackService svc, UserPreferencesService prefs, CancellationToken ct) =>
        {
            var creatable = await svc.GetCreatableProductsAsync(ct);
            var hidden = await prefs.GetHiddenProductsAsync(ct);
            return Results.Ok(new { products = creatable.Where(p => !hidden.Contains(p)).ToList() });
        });

        // Probe behind the create form's disabled state and its inline explanation. Returns the
        // service's own refusal message so the UI never has to reconstruct the rule.
        group.MapGet("/can-create", async (
            RollbackService svc, string product, string targetEnv, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(product) || string.IsNullOrWhiteSpace(targetEnv))
                return Results.BadRequest(new { error = "'product' and 'targetEnv' are required" });
            var (allowed, reason) = await svc.CanCreateAsync(product, targetEnv, ct);
            return Results.Ok(new { allowed, reason });
        });

        group.MapGet("/{id:guid}", async (RollbackService svc, Guid id) =>
        {
            var r = await svc.GetByIdAsync(id);
            if (r is null) return Results.NotFound();
            var approvals = await svc.GetApprovalsAsync(id);
            var canApprove = await svc.CanUserApproveAsync(r);
            var canOverride = await svc.CanUserOverrideAsync(r);
            var (unconfigured, requirements) = await svc.GetGateAsync(r);
            var failedAttempts = await svc.GetFailedAttemptsAsync(new[] { r });
            return Results.Ok(ToDetailDto(r, canApprove, canOverride, unconfigured, requirements, approvals, failedAttempts));
        });

        // Dry-run: resolve the items (with skip reasons) without persisting — powers the UI preview.
        group.MapPost("/preview", async (RollbackService svc, CreateRollbackRequestDto body) =>
        {
            try { return Results.Ok(await svc.PreviewAsync(body)); }
            catch (UnauthorizedAccessException ex) { return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status403Forbidden); }
            catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        group.MapPost("/", async (RollbackService svc, CreateRollbackRequestDto body) =>
        {
            try
            {
                var r = await svc.CreateAsync(body);
                return Results.Created($"/api/rollbacks/{r.Id}", ToDto(r, canApprove: false, canOverride: false));
            }
            catch (UnauthorizedAccessException ex) { return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status403Forbidden); }
            catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        group.MapPost("/{id:guid}/approve", (RollbackService svc, Guid id, DecisionBody? body) =>
            Decide(() => svc.ApproveAsync(id, body?.Comment))).DenyPersonalTokens();

        group.MapPost("/{id:guid}/reject", (RollbackService svc, Guid id, DecisionBody? body) =>
            Decide(() => svc.RejectAsync(id, body?.Comment))).DenyPersonalTokens();

        // Admin-only bypass of the approval gate. Not in RollbackAdminEndpoints because it acts on a
        // request in the operator's own queue rather than on configuration, and the 403 for a
        // non-admin caller is more useful here than a 404 from a group they cannot reach.
        group.MapPost("/{id:guid}/override-approval", (RollbackService svc, Guid id, ReasonBody? body) =>
            Decide(() => svc.OverrideApprovalAsync(id, body?.Reason ?? ""))).DenyPersonalTokens();

        // Pending: the creator or an admin, reason optional. Approved/RollingBack: admin only, reason
        // required. Both rules are enforced in RollbackService.CancelAsync.
        group.MapPost("/{id:guid}/cancel", (RollbackService svc, Guid id, ReasonBody? body) =>
            Decide(() => svc.CancelAsync(id, body?.Reason)));

        // Admin-only, reason required: closes one open item of an in-flight request so the release
        // pipeline stops carrying it. Lives here rather than in RollbackAdminEndpoints for the same
        // reason as override-approval.
        group.MapPost("/{id:guid}/items/{itemId:guid}/skip", (RollbackService svc, Guid id, Guid itemId, ReasonBody? body) =>
            Decide(() => svc.SkipItemAsync(id, itemId, body?.Reason)));

        return group;
    }

    private static async Task<IResult> Decide(Func<Task<RollbackRequest>> action)
    {
        try { return Results.Ok(ToDto(await action(), canApprove: false, canOverride: false)); }
        catch (KeyNotFoundException ex) { return Results.NotFound(new { error = ex.Message }); }
        catch (UnauthorizedAccessException ex) { return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status403Forbidden); }
        catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
        catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
    }

    private static object ToDto(
        RollbackRequest r, bool canApprove, bool canOverride,
        IReadOnlyDictionary<Guid, FailedDeployAttempt>? failedAttempts = null) => new
    {
        id = r.Id,
        r.Product,
        r.TargetEnv,
        status = r.Status.ToString(),
        mode = r.Mode.ToString(),
        r.ReferenceEnv,
        exclusions = r.Exclusions,
        r.Reason,
        r.CreatedBy,
        r.CreatedByName,
        r.CreatedAt,
        r.ApprovedAt,
        r.CompletedAt,
        r.CancelledByName,
        r.CancelledAt,
        r.CancelReason,
        canApprove,
        canOverride,
        r.ApprovalOverridden,
        items = r.Items.Select(i => ToItemDto(i, failedAttempts)),
    };

    private static object ToDetailDto(
        RollbackRequest r,
        bool canApprove,
        bool canOverride,
        bool unconfigured,
        IReadOnlyList<RequirementOutcome> requirements,
        IEnumerable<RollbackApproval> approvals,
        IReadOnlyDictionary<Guid, FailedDeployAttempt> failedAttempts) => new
    {
        id = r.Id,
        r.Product,
        r.TargetEnv,
        status = r.Status.ToString(),
        mode = r.Mode.ToString(),
        r.ReferenceEnv,
        exclusions = r.Exclusions,
        r.Reason,
        r.CreatedBy,
        r.CreatedByName,
        r.CreatedAt,
        r.ApprovedAt,
        r.CompletedAt,
        r.CancelledByName,
        r.CancelledAt,
        r.CancelReason,
        canApprove,
        canOverride,
        r.ApprovalOverridden,
        // True when no rollback policy governed this environment: the request is Pending with nobody
        // authorized to approve it, so only an admin override can move it.
        unconfigured,
        gate = requirements.Select(o => new
        {
            name = o.Requirement.Name,
            groups = o.Requirement.Groups,
            users = o.Requirement.Users,
            o.Matched,
            o.Required,
            o.Satisfied,
        }),
        items = r.Items.Select(i => ToItemDto(i, failedAttempts)),
        approvals = approvals.Select(a => new
        {
            a.ApproverEmail, a.ApproverName, decision = a.Decision.ToString(), a.Comment, a.CreatedAt,
            a.IsOverride,
        }),
    };

    private static object ToItemDto(RollbackItem i, IReadOnlyDictionary<Guid, FailedDeployAttempt>? failedAttempts) => new
    {
        i.Id, i.Service, i.FromVersion, i.ToVersion, status = i.Status.ToString(),
        i.CompletedDeployEventId, i.ExternalRunUrl, i.CompletedAt, i.Note,
        // Open items only: the newest failed deploy of the target version since approval.
        lastFailedAttempt = failedAttempts?.GetValueOrDefault(i.Id),
    };

    public record DecisionBody(string? Comment);

    /// <summary>
    /// Body for the actions that take a reason: override-approval and skip always require one (blank ⇒
    /// 400), and cancel requires one once the request is approved.
    /// </summary>
    public record ReasonBody(string? Reason);
}
