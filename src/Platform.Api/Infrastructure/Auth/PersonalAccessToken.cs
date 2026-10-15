namespace Platform.Api.Infrastructure.Auth;

/// <summary>
/// A token a person mints for themselves in the portal so an agent (Claude Code, Copilot, a script)
/// can call the API and the MCP server as them — the self-service counterpart to the configured keys
/// in <c>Deployments:ApiKeys</c>, which need a deployment to add.
///
/// <para>Sent in the same <c>X-Api-Key</c> header as those keys, so every client set up for a key
/// works with a token. It authenticates as its owner, with the roles they held when they created it
/// minus <c>InfraPortal.Admin</c>; it cannot make approval decisions or use the pipeline-only
/// endpoints (see <see cref="PersonalAccessTokenService"/>). It always expires.</para>
///
/// <para>Owned by the lower-cased email, like every other per-person row (see
/// <c>UserPreference</c>). Only a SHA-256 of the secret is stored; the secret is shown once.</para>
/// </summary>
public class PersonalAccessToken
{
    public Guid Id { get; set; }

    /// <summary>Owner, lower-cased email.</summary>
    public string UserEmail { get; set; } = "";

    /// <summary>Owner's display name when the token was created — what comments and audit entries show.</summary>
    public string UserName { get; set; } = "";

    /// <summary>Owner's identity-provider id (Entra <c>oid</c>, or the local user id), carried as the token's <c>oid</c>.</summary>
    public string UserId { get; set; } = "";

    /// <summary>The owner's label for it ("Claude Code on my laptop").</summary>
    public string Name { get; set; } = "";

    /// <summary>Lower-case hex SHA-256 of the whole secret. Unique; the lookup key.</summary>
    public string TokenHash { get; set; } = "";

    /// <summary>The secret's first characters, safe to display, so the owner can tell their tokens apart.</summary>
    public string Prefix { get; set; } = "";

    /// <summary>Comma-separated roles the token carries — the owner's at creation, never Admin.</summary>
    public string Roles { get; set; } = "";

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>Last successful use, refreshed at most every few minutes (see <see cref="PersonalAccessTokenService.TouchInterval"/>).</summary>
    public DateTimeOffset? LastUsedAt { get; set; }

    public DateTimeOffset? RevokedAt { get; set; }

    public IReadOnlyList<string> RoleList =>
        Roles.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
