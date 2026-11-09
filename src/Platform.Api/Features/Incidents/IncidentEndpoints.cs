using Platform.Api.Features.Incidents.Models;

namespace Platform.Api.Features.Incidents;

/// <summary>
/// Incident records — admin-only, by decision: the aggregates they feed (change failure rate, time
/// to restore) are on the Delivery metrics report for everyone, the records themselves are not. The
/// group is registered under <c>/api/incidents</c> with the CatalogAdmin policy. Machine ingest from
/// alerting tools is a later, separately gated route; the (source, externalId) key is already here.
/// </summary>
public static class IncidentEndpoints
{
    public static RouteGroupBuilder MapIncidentEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/", async (
            IncidentService incidents,
            string? product, string? environment, bool? open,
            DateTimeOffset? from, DateTimeOffset? to, int? limit, int? offset,
            CancellationToken ct) =>
        {
            if (from is not null && to is not null && from >= to)
                return Results.BadRequest(new { error = "'from' must be before 'to'" });
            return Results.Ok(await incidents.ListAsync(
                product, environment, open, from, to,
                Math.Clamp(limit ?? 100, 1, 500), Math.Max(offset ?? 0, 0), ct));
        });

        group.MapGet("/{id:guid}", async (IncidentService incidents, Guid id, CancellationToken ct) =>
        {
            var dto = await incidents.GetAsync(id, ct);
            return dto is null ? Results.NotFound() : Results.Ok(dto);
        });

        group.MapPost("/", async (IncidentService incidents, UpsertIncidentRequest body, CancellationToken ct) =>
        {
            if (body is null) return Results.BadRequest(new { error = "request body is required" });
            try
            {
                var dto = await incidents.CreateAsync(body, ct);
                return Results.Created($"/api/incidents/{dto.Id}", dto);
            }
            catch (IncidentValidationException e) { return Results.BadRequest(new { error = e.Message }); }
            catch (IncidentConflictException e) { return Results.Conflict(new { error = e.Message, existingId = e.ExistingId }); }
        });

        group.MapPut("/{id:guid}", async (IncidentService incidents, Guid id, UpsertIncidentRequest body, CancellationToken ct) =>
        {
            if (body is null) return Results.BadRequest(new { error = "request body is required" });
            try
            {
                var dto = await incidents.UpdateAsync(id, body, ct);
                return dto is null ? Results.NotFound() : Results.Ok(dto);
            }
            catch (IncidentValidationException e) { return Results.BadRequest(new { error = e.Message }); }
            catch (IncidentConflictException e) { return Results.Conflict(new { error = e.Message, existingId = e.ExistingId }); }
        });

        group.MapDelete("/{id:guid}", async (IncidentService incidents, Guid id, CancellationToken ct) =>
            await incidents.DeleteAsync(id, ct) ? Results.NoContent() : Results.NotFound());

        return group;
    }
}
