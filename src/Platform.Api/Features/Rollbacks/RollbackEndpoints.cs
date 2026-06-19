using Platform.Api.Features.Rollbacks.Models;

namespace Platform.Api.Features.Rollbacks;

/// <summary>
/// User-facing rollback endpoints (mounted at <c>/api/rollbacks</c>, gated by CanApprove). The
/// queue/detail/approve surface mirrors promotions — a rollback request is just another gated
/// change. Per-product enrollment management lives in <see cref="RollbackAdminEndpoints"/>.
/// </summary>
public static class RollbackEndpoints
{
    public static RouteGroupBuilder MapRollbackEndpoints(this RouteGroupBuilder group)
    {
        // List requests with filters + per-request canApprove capability.
        group.MapGet("/", async (RollbackService svc, string? status, string? product, string? targetEnv, int? limit) =>
        {
            RollbackStatus? parsed = null;
            if (!string.IsNullOrEmpty(status))
            {
                if (!Enum.TryParse<RollbackStatus>(status, ignoreCase: true, out var s))
                    return Results.BadRequest(new { error = $"Unknown status '{status}'" });
                parsed = s;
            }
            var requests = await svc.GetAsync(new RollbackQuery(parsed, product, targetEnv, limit ?? 200));
            var caps = new Dictionary<Guid, bool>();
            foreach (var r in requests) caps[r.Id] = await svc.CanUserApproveAsync(r);
            return Results.Ok(new { requests = requests.Select(r => ToDto(r, caps.GetValueOrDefault(r.Id))) });
        });

        group.MapGet("/enabled-products", async (RollbackService svc) =>
            Results.Ok(new { products = await svc.GetEnabledProductsAsync() }));

        group.MapGet("/{id:guid}", async (RollbackService svc, Guid id) =>
        {
            var r = await svc.GetByIdAsync(id);
            if (r is null) return Results.NotFound();
            var approvals = await svc.GetApprovalsAsync(id);
            var canApprove = await svc.CanUserApproveAsync(r);
            return Results.Ok(ToDetailDto(r, canApprove, approvals));
        });

        // Dry-run: resolve the items (with skip reasons) without persisting — powers the UI preview.
        group.MapPost("/preview", async (RollbackService svc, CreateRollbackRequestDto body) =>
        {
            try { return Results.Ok(await svc.PreviewAsync(body)); }
            catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        group.MapPost("/", async (RollbackService svc, CreateRollbackRequestDto body) =>
        {
            try
            {
                var r = await svc.CreateAsync(body);
                return Results.Created($"/api/rollbacks/{r.Id}", ToDto(r, canApprove: false));
            }
            catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        group.MapPost("/{id:guid}/approve", (RollbackService svc, Guid id, DecisionBody? body) =>
            Decide(() => svc.ApproveAsync(id, body?.Comment)));

        group.MapPost("/{id:guid}/reject", (RollbackService svc, Guid id, DecisionBody? body) =>
            Decide(() => svc.RejectAsync(id, body?.Comment)));

        group.MapPost("/{id:guid}/cancel", (RollbackService svc, Guid id) =>
            Decide(() => svc.CancelAsync(id)));

        return group;
    }

    private static async Task<IResult> Decide(Func<Task<RollbackRequest>> action)
    {
        try { return Results.Ok(ToDto(await action(), canApprove: false)); }
        catch (KeyNotFoundException ex) { return Results.NotFound(new { error = ex.Message }); }
        catch (UnauthorizedAccessException ex) { return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status403Forbidden); }
        catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
    }

    private static object ToDto(RollbackRequest r, bool canApprove) => new
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
        canApprove,
        items = r.Items.Select(ToItemDto),
    };

    private static object ToDetailDto(RollbackRequest r, bool canApprove, IEnumerable<RollbackApproval> approvals) => new
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
        canApprove,
        items = r.Items.Select(ToItemDto),
        approvals = approvals.Select(a => new
        {
            a.ApproverEmail, a.ApproverName, decision = a.Decision.ToString(), a.Comment, a.CreatedAt,
        }),
    };

    private static object ToItemDto(RollbackItem i) => new
    {
        i.Id, i.Service, i.FromVersion, i.ToVersion, status = i.Status.ToString(),
        i.CompletedDeployEventId, i.ExternalRunUrl, i.CompletedAt,
    };

    public record DecisionBody(string? Comment);
}
