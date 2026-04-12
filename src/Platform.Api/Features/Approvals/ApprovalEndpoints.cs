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

        group.MapGet("/{id:guid}", async (ApprovalService service, Guid id) =>
        {
            var approval = await service.GetById(id);
            if (approval is null) return Results.NotFound(new { error = "Approval not found" });
            return Results.Ok(new { approval });
        });

        group.MapPost("/{id:guid}/approve", async (ApprovalService service, Guid id, ApprovalActionDto? dto) =>
        {
            await service.RecordDecision(id, "Approved", dto?.Comment);
            return Results.Ok(new { message = "Approved" });
        });

        group.MapPost("/{id:guid}/reject", async (ApprovalService service, Guid id, ApprovalActionDto dto) =>
        {
            if (string.IsNullOrWhiteSpace(dto.Comment))
                return Results.BadRequest(new { error = "Comment is required for rejection" });
            await service.RecordDecision(id, "Rejected", dto.Comment);
            return Results.Ok(new { message = "Rejected" });
        });

        group.MapPost("/{id:guid}/request-changes", async (ApprovalService service, Guid id, ApprovalActionDto dto) =>
        {
            if (string.IsNullOrWhiteSpace(dto.Comment))
                return Results.BadRequest(new { error = "Comment is required for change requests" });
            await service.RecordDecision(id, "ChangesRequested", dto.Comment);
            return Results.Ok(new { message = "Changes requested" });
        });

        return group;
    }
}

public record ApprovalActionDto(string? Comment);
