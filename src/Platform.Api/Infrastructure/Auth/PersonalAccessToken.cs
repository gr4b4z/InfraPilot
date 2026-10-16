namespace Platform.Api.Infrastructure.Auth;

/// <summary>
/// A token a person mints for themselves in the portal so an agent (Claude Code, Copilot, a script)
/// can call the API and the MCP server as them — the self-service counterpart to the configured keys
/// in <c>Deployments:ApiKeys</c>, which need a deployment to add.
///
/// <para>Sent in the same <c>X-Api-Key</c> header as those keys, so every client set up for a key
/// works with a token. It authenticates as its owner, with the roles they held when they created it,
/// shaped by its <see cref="Access"/>: a standard token drops <c>InfraPortal.Admin</c> and cannot
/// make approval decisions; a QA or Admin token can (see <see cref="PersonalTokenAccess"/>). No token
/// uses the pipeline-only endpoints (see <see cref="PersonalAccessTokenService"/>). It always
/// expires.</para>
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

    /// <summary>Comma-separated roles the token carries — the owner's at creation, Admin only on an Admin token.</summary>
    public string Roles { get; set; } = "";

    /// <summary>What the token may decide; chosen at creation, never above the owner's own role.</summary>
    public PersonalTokenAccess Access { get; set; } = PersonalTokenAccess.Standard;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>Last successful use, refreshed at most every few minutes (see <see cref="PersonalAccessTokenService.TouchInterval"/>).</summary>
    public DateTimeOffset? LastUsedAt { get; set; }

    public DateTimeOffset? RevokedAt { get; set; }

    public IReadOnlyList<string> RoleList =>
        Roles.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

/// <summary>
/// How much a personal access token may do beyond reading and commenting as its owner. Ordered: each
/// level includes the one below it, as Admin includes QA's jurisdiction everywhere else in the portal.
/// </summary>
public enum PersonalTokenAccess
{
    /// <summary>The owner's roles minus Admin; no approvals, rejections or sign-offs. Anyone may create one.</summary>
    Standard = 0,

    /// <summary>
    /// Adds <c>InfraPortal.QA</c> (if the owner lacks it, an admin) and work-item sign-off: approve,
    /// raise an issue, block. Still no Admin and no promotion or rollback decisions. Needs QA or Admin.
    /// </summary>
    QA = 1,

    /// <summary>Keeps <c>InfraPortal.Admin</c> and makes every decision the owner can in the portal. Needs Admin.</summary>
    Admin = 2,
}
