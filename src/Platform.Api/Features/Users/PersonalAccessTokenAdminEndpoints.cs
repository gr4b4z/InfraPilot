using Platform.Api.Infrastructure.Auth;

namespace Platform.Api.Features.Users;

/// <summary>
/// Everyone's personal access tokens, for administrators (mounted at <c>/api/tokens/admin</c>, gated
/// by <see cref="AuthorizationPolicies.CatalogAdmin"/>) — Settings → Agent Tokens. Incident response
/// when a token leaks, and clean-up when someone loses their access: find any token, revoke it, or
/// revoke all of one person's at once. Nobody can read a secret here; there is none to read.
///
/// <para>Of personal tokens, only an Admin one carries the role these routes need.</para>
/// </summary>
public static class PersonalAccessTokenAdminEndpoints
{
    public static RouteGroupBuilder MapPersonalAccessTokenAdminEndpoints(this RouteGroupBuilder group)
    {
        // Active tokens; with includeInactive=true also the ones that ended within the last 90 days.
        group.MapGet("/", async (PersonalAccessTokenService tokens, bool? includeInactive, CancellationToken ct) =>
            Results.Ok(new { tokens = (await tokens.ListAllAsync(includeInactive ?? false, ct)).Select(ToDto) }));

        // Idempotent: revoking a revoked token is a no-op success, as on the owner's own route.
        group.MapPost("/{id:guid}/revoke", async (
            PersonalAccessTokenService tokens, Guid id, RevokeRequest? body, CancellationToken ct) =>
        {
            try
            {
                return await tokens.RevokeAsAdminAsync(id, body?.Reason, ct)
                    ? Results.NoContent()
                    : Results.NotFound(new { error = "No such token." });
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        group.MapPost("/revoke-owner", async (
            PersonalAccessTokenService tokens, RevokeOwnerRequest? body, CancellationToken ct) =>
        {
            try
            {
                return Results.Ok(new { revoked = await tokens.RevokeAllForOwnerAsync(body?.Email, body?.Reason, ct) });
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        return group;
    }

    public record RevokeRequest(string? Reason);

    public record RevokeOwnerRequest(string? Email, string? Reason);

    private static object ToDto(PersonalAccessToken t) => new
    {
        t.Id,
        t.Name,
        t.Prefix,
        ownerEmail = t.UserEmail,
        ownerName = t.UserName,
        t.CreatedAt,
        t.ExpiresAt,
        t.LastUsedAt,
        t.RevokedAt,
        roles = t.RoleList,
        access = t.Access.ToString(),
    };
}
