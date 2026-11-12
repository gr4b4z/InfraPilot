using Platform.Api.Features.Releases.Models;

namespace Platform.Api.Features.Releases;

/// <summary>
/// <c>/api/releases</c> — versioned releases and their checklists (see <see cref="Release"/>).
/// Registered for any signed-in caller; <see cref="ReleaseService"/> decides who may change what.
/// The <c>/progress</c> read is the portal's own evidence for the release — computed on each call.
/// </summary>
public static class ReleaseEndpoints
{
    public static RouteGroupBuilder MapReleaseEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/", async (ReleaseService releases, string? status, string? product, int? limit, int? offset, CancellationToken ct) =>
        {
            if (!string.IsNullOrWhiteSpace(status) && status != "active" && !ReleaseStatuses.All.Contains(status))
                return Results.BadRequest(new { error = $"'status' must be active or one of {string.Join(", ", ReleaseStatuses.All)}." });
            return Results.Ok(await releases.ListAsync(status, product, Math.Clamp(limit ?? 50, 1, 200), Math.Max(offset ?? 0, 0), ct));
        });

        group.MapGet("/{id:guid}", async (ReleaseService releases, Guid id, CancellationToken ct) =>
        {
            var dto = await releases.GetAsync(id, ct);
            return dto is null ? Results.NotFound() : Results.Ok(dto);
        });

        group.MapGet("/{id:guid}/progress", async (ReleaseProgressService progress, Guid id, CancellationToken ct) =>
        {
            var dto = await progress.GetAsync(id, ct);
            return dto is null ? Results.NotFound() : Results.Ok(dto);
        });

        group.MapPost("/", (ReleaseService releases, CreateReleaseRequest body, CancellationToken ct) =>
            Handle(async () =>
            {
                if (body is null) return Results.BadRequest(new { error = "request body is required" });
                var dto = await releases.CreateAsync(body, ct);
                return Results.Created($"/api/releases/{dto.Id}", dto);
            }));

        group.MapPut("/{id:guid}", (ReleaseService releases, Guid id, UpdateReleaseRequest body, CancellationToken ct) =>
            Handle(async () =>
            {
                if (body is null) return Results.BadRequest(new { error = "request body is required" });
                return OkOrNotFound(await releases.UpdateAsync(id, body, ct));
            }));

        group.MapPost("/{id:guid}/complete", (ReleaseService releases, Guid id, CompleteReleaseRequest? body, CancellationToken ct) =>
            Handle(async () => OkOrNotFound(await releases.CompleteAsync(id, body ?? new CompleteReleaseRequest(), ct))));

        group.MapPost("/{id:guid}/cancel", (ReleaseService releases, Guid id, CancelReleaseRequest body, CancellationToken ct) =>
            Handle(async () => OkOrNotFound(await releases.CancelAsync(id, body, ct))));

        group.MapPost("/{id:guid}/reopen", (ReleaseService releases, Guid id, CancellationToken ct) =>
            Handle(async () => OkOrNotFound(await releases.ReopenAsync(id, ct))));

        group.MapDelete("/{id:guid}", (ReleaseService releases, Guid id, CancellationToken ct) =>
            Handle(async () => await releases.DeleteAsync(id, ct) ? Results.NoContent() : Results.NotFound()));

        group.MapPost("/{id:guid}/steps", (ReleaseService releases, Guid id, AddReleaseStepRequest body, CancellationToken ct) =>
            Handle(async () => OkOrNotFound(await releases.AddStepAsync(id, body, ct))));

        group.MapPatch("/{id:guid}/steps/{stepId:guid}", (ReleaseService releases, Guid id, Guid stepId, UpdateReleaseStepRequest body, CancellationToken ct) =>
            Handle(async () => OkOrNotFound(await releases.UpdateStepAsync(id, stepId, body, ct))));

        group.MapDelete("/{id:guid}/steps/{stepId:guid}", (ReleaseService releases, Guid id, Guid stepId, CancellationToken ct) =>
            Handle(async () => OkOrNotFound(await releases.DeleteStepAsync(id, stepId, ct))));

        return group;
    }

    private static IResult OkOrNotFound(ReleaseDto? dto) => dto is null ? Results.NotFound() : Results.Ok(dto);

    private static async Task<IResult> Handle(Func<Task<IResult>> action)
    {
        try { return await action(); }
        catch (ReleaseValidationException e) { return Results.BadRequest(new { error = e.Message }); }
        catch (ReleaseConflictException e) { return Results.Conflict(new { error = e.Message, existingId = e.ExistingId }); }
        catch (ReleaseForbiddenException e) { return Results.Json(new { error = e.Message }, statusCode: StatusCodes.Status403Forbidden); }
    }
}
