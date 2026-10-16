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
        // `allowedAccess` is what this user may create, so the page offers only those levels.
        group.MapGet("/", async (PersonalAccessTokenService tokens, CancellationToken ct) =>
            Results.Ok(new
            {
                tokens = (await tokens.ListAsync(ct)).Select(ToDto),
                allowedAccess = tokens.AllowedAccess().Select(a => a.ToString()),
            }));

        // The secret is in this response and nowhere else, ever.
        group.MapPost("/", async (PersonalAccessTokenService tokens, CreateTokenRequest? body, CancellationToken ct) =>
        {
            var access = PersonalTokenAccess.Standard;
            if (!string.IsNullOrWhiteSpace(body?.Access)
                && !Enum.TryParse(body.Access.Trim(), ignoreCase: true, out access))
                return Results.BadRequest(new { error = "access must be one of: Standard, QA, Admin." });

            try
            {
                var (token, secret) = await tokens.CreateAsync(body?.Name, body?.ExpiresInDays ?? 30, access, ct);
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
                    access = token.Access.ToString(),
                });
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            catch (UnauthorizedAccessException ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status403Forbidden);
            }
        });

        group.MapDelete("/{id:guid}", async (PersonalAccessTokenService tokens, Guid id, CancellationToken ct) =>
            await tokens.RevokeAsync(id, ct)
                ? Results.NoContent()
                : Results.NotFound(new { error = "No such token." }));

        return group;
    }

    /// <param name="Access">Standard (default), QA or Admin — see <see cref="PersonalTokenAccess"/>.</param>
    public record CreateTokenRequest(string? Name, int? ExpiresInDays, string? Access = null);

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
        access = t.Access.ToString(),
    };
}
