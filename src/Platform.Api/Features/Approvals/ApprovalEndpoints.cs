using Platform.Api.Infrastructure.Auth;

namespace Platform.Api.Features.Approvals;

public static class ApprovalEndpoints
{
    public static RouteGroupBuilder MapApprovalEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/", async (ApprovalService service, ICurrentUser user, string? status) =>
        {
            var items = await service.GetAll();
            return Results.Ok(new { items, total = items.Count });
        });

        group.MapGet("/{id:guid}", async (ApprovalService service, Guid id, CancellationToken ct) =>
        {
            var approval = await service.GetById(id);
            if (approval is null) return Results.NotFound(new { error = "Approval not found" });

            // Lets the page show why there are no decision buttons instead of failing on click.
            var check = await service.CheckCanDecide(approval, ct);
            return Results.Ok(new { approval, canDecide = check.Allowed, cannotDecideReason = check.Reason });
        });

        group.MapPost("/{id:guid}/approve", async (ApprovalService service, Guid id, ApprovalActionDto? dto) =>
            await Decide(() => service.RecordDecision(id, "Approved", dto?.Comment), "Approved"))
            .RequirePersonalTokenAccess(PersonalTokenAccess.Admin);

        group.MapPost("/{id:guid}/reject", async (ApprovalService service, Guid id, ApprovalActionDto dto) =>
        {
            if (string.IsNullOrWhiteSpace(dto.Comment))
                return Results.BadRequest(new { error = "Comment is required for rejection" });
            return await Decide(() => service.RecordDecision(id, "Rejected", dto.Comment), "Rejected");
        }).RequirePersonalTokenAccess(PersonalTokenAccess.Admin);

        group.MapPost("/{id:guid}/request-changes", async (ApprovalService service, Guid id, ApprovalActionDto dto) =>
        {
            if (string.IsNullOrWhiteSpace(dto.Comment))
                return Results.BadRequest(new { error = "Comment is required for change requests" });
            return await Decide(() => service.RecordDecision(id, "ChangesRequested", dto.Comment), "Changes requested");
        }).RequirePersonalTokenAccess(PersonalTokenAccess.Admin);

        return group;
    }

    /// <summary>
    /// Runs a decision, turning "you may not decide on this" into a 403 that carries the reason
    /// (the exception middleware would answer a bare "Access denied").
    /// </summary>
    private static async Task<IResult> Decide(Func<Task> record, string message)
    {
        try
        {
            await record();
            return Results.Ok(new { message });
        }
        catch (UnauthorizedAccessException ex)
        {
            return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status403Forbidden);
        }
    }
}

public record ApprovalActionDto(string? Comment);
