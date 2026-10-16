using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Platform.Api.Infrastructure.Audit;
using Platform.Api.Infrastructure.Persistence;

namespace Platform.Api.Infrastructure.Auth;

/// <summary>
/// Mints, lists, revokes and authenticates <see cref="PersonalAccessToken"/>s: the signed-in
/// person's own, and for administrators everyone's (listing and revoking only).
///
/// <para>What a token may do is decided here and in two places that read its claims:
/// <list type="bullet">
/// <item>Its <see cref="PersonalTokenAccess"/>, chosen at creation and never above the owner's own
/// role. A standard token carries the owner's roles minus <c>InfraPortal.Admin</c>, so the admin
/// groups refuse it; a QA token adds <c>InfraPortal.QA</c>; only an Admin token keeps Admin. The roles
/// are a snapshot: an Admin token stays Admin until it expires or is revoked, even if Entra takes the
/// role away sooner.</item>
/// <item>Its <c>auth_method</c> is <c>personal_token</c>, which the pipeline-only policy
/// (<see cref="AuthorizationPolicies.DeploymentIngestion"/>) rejects — ingest, build registration and
/// external promotion creation are for the configured pipeline keys — and which the decision routes
/// check against the token's access
/// (<see cref="PersonalTokenRestrictions.RequirePersonalTokenAccess{TBuilder}"/>): a standard token
/// makes no decisions, a QA token signs off work items, an Admin token decides what its owner can.</item>
/// </list></para>
/// </summary>
public class PersonalAccessTokenService
{
    /// <summary>What every token starts with — how the key handler tells one from a configured key.</summary>
    public const string Prefix = "ipt_";

    public const string AuthMethod = "personal_token";
    public const string TokenIdClaim = "token_id";

    /// <summary>The token's <see cref="PersonalTokenAccess"/>, by name.</summary>
    public const string AccessClaim = "token_access";

    /// <summary>The lifetimes the portal offers. Always finite: a forgotten token stops working on its own.</summary>
    public static readonly int[] AllowedLifetimesDays = [7, 30, 90];

    /// <summary>Active (unexpired, unrevoked) tokens one person may hold at once.</summary>
    public const int MaxActivePerUser = 10;

    /// <summary>How stale <see cref="PersonalAccessToken.LastUsedAt"/> may get before a use writes it again.</summary>
    public static readonly TimeSpan TouchInterval = TimeSpan.FromMinutes(5);

    private const string AdminRole = "InfraPortal.Admin";
    private const string QaRole = "InfraPortal.QA";
    private const string DefaultRole = "InfraPortal.User";

    private readonly PlatformDbContext _db;
    private readonly ICurrentUser _user;
    private readonly IAuditLogger _audit;

    public PersonalAccessTokenService(PlatformDbContext db, ICurrentUser user, IAuditLogger audit)
    {
        _db = db;
        _user = user;
        _audit = audit;
    }

    private string OwnerEmail => (_user.Email ?? "").Trim().ToLowerInvariant();

    public async Task<List<PersonalAccessToken>> ListAsync(CancellationToken ct = default)
    {
        var owner = OwnerEmail;
        return await _db.PersonalAccessTokens.AsNoTracking()
            .Where(t => t.UserEmail == owner)
            .OrderByDescending(t => t.CreatedAt)
            .ToListAsync(ct);
    }

    /// <summary>The access levels the signed-in user may give a token: never above their own role.</summary>
    public IReadOnlyList<PersonalTokenAccess> AllowedAccess() =>
        _user.IsAdmin ? [PersonalTokenAccess.Standard, PersonalTokenAccess.QA, PersonalTokenAccess.Admin]
        : _user.IsQA ? [PersonalTokenAccess.Standard, PersonalTokenAccess.QA]
        : [PersonalTokenAccess.Standard];

    /// <summary>Creates a token for the signed-in user. Returns the row and the secret, which is never stored.</summary>
    /// <exception cref="ArgumentException">Bad name, lifetime, or too many active tokens.</exception>
    /// <exception cref="UnauthorizedAccessException">An access level above the user's own role.</exception>
    public async Task<(PersonalAccessToken Token, string Secret)> CreateAsync(
        string? name, int expiresInDays, PersonalTokenAccess access = PersonalTokenAccess.Standard,
        CancellationToken ct = default)
    {
        var owner = OwnerEmail;
        if (owner.Length == 0)
            throw new ArgumentException("Your sign-in carries no email address, which a token needs to act as you.");

        var label = (name ?? "").Trim();
        if (label.Length == 0) throw new ArgumentException("Give the token a name, so you can tell it apart later.");
        if (label.Length > 100) throw new ArgumentException("The name can be at most 100 characters.");
        if (!AllowedLifetimesDays.Contains(expiresInDays))
            throw new ArgumentException($"expiresInDays must be one of: {string.Join(", ", AllowedLifetimesDays)}.");
        if (!Enum.IsDefined(access))
            throw new ArgumentException("access must be one of: Standard, QA, Admin.");
        if (!AllowedAccess().Contains(access))
            throw new UnauthorizedAccessException(access == PersonalTokenAccess.Admin
                ? "Only an administrator can create an Admin token."
                : "Only a QA member or an administrator can create a QA token.");

        var now = DateTimeOffset.UtcNow;
        var active = await _db.PersonalAccessTokens
            .CountAsync(t => t.UserEmail == owner && t.RevokedAt == null && t.ExpiresAt > now, ct);
        if (active >= MaxActivePerUser)
            throw new ArgumentException($"You already have {MaxActivePerUser} active tokens. Revoke one you no longer use first.");

        var roles = _user.Roles
            .Where(r => access == PersonalTokenAccess.Admin || !string.Equals(r, AdminRole, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        // An administrator without the QA role holds its jurisdiction through Admin, which a QA token
        // drops — so the token gets the QA role itself, or it could sign off nothing.
        if (access == PersonalTokenAccess.QA && !roles.Contains(QaRole, StringComparer.OrdinalIgnoreCase))
            roles.Add(QaRole);
        if (roles.Count == 0) roles.Add(DefaultRole);

        var secret = Prefix + Base64Url(RandomNumberGenerator.GetBytes(32));
        var token = new PersonalAccessToken
        {
            Id = Guid.NewGuid(),
            UserEmail = owner,
            UserName = string.IsNullOrWhiteSpace(_user.Name) ? owner : _user.Name,
            UserId = _user.Id,
            Name = label,
            TokenHash = Hash(secret),
            Prefix = secret[..(Prefix.Length + 8)],
            Roles = string.Join(",", roles),
            Access = access,
            CreatedAt = now,
            ExpiresAt = now.AddDays(expiresInDays),
        };
        _db.PersonalAccessTokens.Add(token);
        await _db.SaveChangesAsync(ct);

        await _audit.Log("auth", "personal_token.created", _user.Id, _user.Name, "user",
            nameof(PersonalAccessToken), token.Id, null,
            new { token.Name, token.Prefix, token.ExpiresAt, roles, access = access.ToString() });

        return (token, secret);
    }

    /// <summary>Revokes one of the signed-in user's tokens. False when it is not theirs (or does not exist).</summary>
    public async Task<bool> RevokeAsync(Guid id, CancellationToken ct = default)
    {
        var owner = OwnerEmail;
        var token = await _db.PersonalAccessTokens.FirstOrDefaultAsync(t => t.Id == id && t.UserEmail == owner, ct);
        if (token is null) return false;
        if (token.RevokedAt is not null) return true;

        token.RevokedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        await _audit.Log("auth", "personal_token.revoked", _user.Id, _user.Name, "user",
            nameof(PersonalAccessToken), token.Id, null, new { token.Name, token.Prefix });
        return true;
    }

    // ── Administration: everyone's tokens (Settings → Agent Tokens, /api/tokens/admin) ──────────

    /// <summary>How long a revoked or expired token stays in the admin list — as long as a token can live.</summary>
    public static readonly TimeSpan InactiveHistory = TimeSpan.FromDays(90);

    public const int MaxReasonLength = 500;

    /// <summary>
    /// Every person's tokens, newest first: the active ones, plus with <paramref name="includeInactive"/>
    /// the revoked and expired ones that ended within <see cref="InactiveHistory"/>, so the list stays
    /// bounded however long the portal runs.
    /// </summary>
    public async Task<List<PersonalAccessToken>> ListAllAsync(bool includeInactive, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var cutoff = now - InactiveHistory;
        var query = _db.PersonalAccessTokens.AsNoTracking();
        query = includeInactive
            ? query.Where(t => (t.RevokedAt ?? t.ExpiresAt) > cutoff)
            : query.Where(t => t.RevokedAt == null && t.ExpiresAt > now);
        return await query.OrderByDescending(t => t.CreatedAt).ToListAsync(ct);
    }

    /// <summary>Revokes anyone's token, as an administrator. False when there is no such token.</summary>
    /// <exception cref="ArgumentException">The reason is too long.</exception>
    public async Task<bool> RevokeAsAdminAsync(Guid id, string? reason, CancellationToken ct = default)
    {
        var why = CleanReason(reason);
        var token = await _db.PersonalAccessTokens.FirstOrDefaultAsync(t => t.Id == id, ct);
        if (token is null) return false;
        if (token.RevokedAt is null) await RevokeAndAuditAsync([token], why, ct);
        return true;
    }

    /// <summary>
    /// Revokes every active token <paramref name="ownerEmail"/> holds, as an administrator — the
    /// response to a leak whose token is unknown, or to someone losing their access. Returns how many.
    /// </summary>
    /// <exception cref="ArgumentException">No owner given, or the reason is too long.</exception>
    public async Task<int> RevokeAllForOwnerAsync(string? ownerEmail, string? reason, CancellationToken ct = default)
    {
        var owner = (ownerEmail ?? "").Trim().ToLowerInvariant();
        if (owner.Length == 0) throw new ArgumentException("Say whose tokens to revoke (email).");
        var why = CleanReason(reason);

        var now = DateTimeOffset.UtcNow;
        var tokens = await _db.PersonalAccessTokens
            .Where(t => t.UserEmail == owner && t.RevokedAt == null && t.ExpiresAt > now)
            .ToListAsync(ct);
        await RevokeAndAuditAsync(tokens, why, ct);
        return tokens.Count;
    }

    private static string? CleanReason(string? reason)
    {
        var why = reason?.Trim();
        if (why?.Length > MaxReasonLength)
            throw new ArgumentException($"The reason can be at most {MaxReasonLength} characters.");
        return string.IsNullOrEmpty(why) ? null : why;
    }

    // One audit entry per token, in the shape the owner's own revoke writes plus whose token it was
    // and why. The actor is the administrator, so the log tells the two kinds of revoke apart.
    private async Task RevokeAndAuditAsync(List<PersonalAccessToken> tokens, string? reason, CancellationToken ct)
    {
        if (tokens.Count == 0) return;
        var now = DateTimeOffset.UtcNow;
        foreach (var token in tokens) token.RevokedAt = now;
        await _db.SaveChangesAsync(ct);

        foreach (var token in tokens)
            await _audit.Log("auth", "personal_token.revoked", _user.Id, _user.Name, "user",
                nameof(PersonalAccessToken), token.Id, null,
                new { token.Name, token.Prefix, owner = token.UserEmail },
                new { byAdmin = true, reason });
    }

    /// <summary>
    /// The principal a presented secret stands for, or null when it is unknown, revoked or expired,
    /// or when the directory reports its owner disabled or deleted (<see cref="PersonalTokenOwnerCheck"/>).
    /// Static over a context so the key handler can call it without the per-user service.
    /// </summary>
    public static async Task<ClaimsPrincipal?> AuthenticateAsync(
        PlatformDbContext db, PersonalTokenOwnerCheck owners, string secret, string authenticationType,
        CancellationToken ct = default)
    {
        var hash = Hash(secret);
        var token = await db.PersonalAccessTokens.AsNoTracking().FirstOrDefaultAsync(t => t.TokenHash == hash, ct);
        var now = DateTimeOffset.UtcNow;
        if (token is null || token.RevokedAt is not null || token.ExpiresAt <= now) return null;
        // Before the touch below, so a refused attempt is not recorded as a use.
        if (!await owners.AllowsAsync(token, ct)) return null;

        if (token.LastUsedAt is null || now - token.LastUsedAt > TouchInterval)
        {
            await db.PersonalAccessTokens.Where(t => t.Id == token.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.LastUsedAt, now), ct);
        }

        // The claim names CurrentUser reads for a signed-in person, so everything downstream —
        // comment authorship, triggered-by, audit actor — names the owner, not the token.
        var claims = new List<Claim>
        {
            new("oid", token.UserId),
            new(ClaimTypes.NameIdentifier, token.UserId),
            new("name", token.UserName),
            new(ClaimTypes.Name, token.UserName),
            new("preferred_username", token.UserEmail),
            new("auth_method", AuthMethod),
            new(TokenIdClaim, token.Id.ToString()),
            new(AccessClaim, token.Access.ToString()),
            new(ApiKeyAuthHandler.ExpiresAtClaim, token.ExpiresAt.ToString("O")),
        };
        claims.AddRange(token.RoleList.Select(r => new Claim("roles", r)));

        // Its own authentication type rather than the ApiKey scheme's: code that asks "was this a
        // configured key?" (the manual-deploy route) must answer no for a person's token.
        return new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType, "name", "roles"));
    }

    public static bool LooksLikeToken(string presented) => presented.StartsWith(Prefix, StringComparison.Ordinal);

    public static bool IsPersonalToken(ClaimsPrincipal user) => user.FindFirstValue("auth_method") == AuthMethod;

    /// <summary>
    /// A personal token's access level, or null when the caller is not a personal token. A missing or
    /// unreadable claim reads as <see cref="PersonalTokenAccess.Standard"/>: the least a token can do.
    /// </summary>
    public static PersonalTokenAccess? AccessOf(ClaimsPrincipal user) =>
        !IsPersonalToken(user) ? null
        : Enum.TryParse<PersonalTokenAccess>(user.FindFirstValue(AccessClaim), out var access) && Enum.IsDefined(access)
            ? access
            : PersonalTokenAccess.Standard;

    public static string Hash(string secret) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
