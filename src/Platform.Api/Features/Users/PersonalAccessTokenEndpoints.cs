using Platform.Api.Infrastructure.Auth;

namespace Platform.Api.Features.Users;

/// <summary>
/// The signed-in user's own personal access tokens, under <c>/api/me/tokens</c> — the Agent access
/// page. Mounted with the <see cref="AuthorizationPolicies.SignedInSession"/> policy: only a person
/// in the portal manages tokens, never a token or a key.
/// </summary>
public static class PersonalAccessTokenEndpoints
{
    public static RouteGroupBuilder MapPersonalAccessTokenEndpoints(this RouteGroupBuilder group)
    {
        // Every token the user has made, revoked and expired ones included, so the page can show
        // what an agent was using when it stopped working.
        group.MapGet("/", async (PersonalAccessTokenService tokens, CancellationToken ct) =>
            Results.Ok(new { tokens = (await tokens.ListAsync(ct)).Select(ToDto) }));

        // The secret is in this response and nowhere else, ever.
        group.MapPost("/", async (PersonalAccessTokenService tokens, CreateTokenRequest? body, CancellationToken ct) =>
        {
            try
            {
                var (token, secret) = await tokens.CreateAsync(body?.Name, body?.ExpiresInDays ?? 30, ct);
                return Results.Created($"/api/me/tokens/{token.Id}", new
                {
                    token = secret,
                    token.Id,
                    token.Name,
                    token.Prefix,
                    token.CreatedAt,
                    token.ExpiresAt,
                    lastUsedAt = (DateTimeOffset?)null,
                    revokedAt = (DateTimeOffset?)null,
                    roles = token.RoleList,
                });
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        group.MapDelete("/{id:guid}", async (PersonalAccessTokenService tokens, Guid id, CancellationToken ct) =>
            await tokens.RevokeAsync(id, ct)
                ? Results.NoContent()
                : Results.NotFound(new { error = "No such token." }));

        return group;
    }

    public record CreateTokenRequest(string? Name, int? ExpiresInDays);

    private static object ToDto(PersonalAccessToken t) => new
    {
        t.Id,
        t.Name,
        t.Prefix,
        t.CreatedAt,
        t.ExpiresAt,
        t.LastUsedAt,
        t.RevokedAt,
        roles = t.RoleList,
    };
}
