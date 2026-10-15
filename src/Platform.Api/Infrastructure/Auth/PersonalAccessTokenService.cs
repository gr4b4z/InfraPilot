using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Platform.Api.Infrastructure.Audit;
using Platform.Api.Infrastructure.Persistence;

namespace Platform.Api.Infrastructure.Auth;

/// <summary>
/// Mints, lists, revokes and authenticates <see cref="PersonalAccessToken"/>s.
///
/// <para>What a token may do is decided here and in two places that read its claims:
/// <list type="bullet">
/// <item>It carries the owner's roles minus <c>InfraPortal.Admin</c>, so the admin groups refuse it.
/// Admin work stays in the portal, where the session reflects the role as Entra grants it today
/// rather than as it was when the token was made.</item>
/// <item>Its <c>auth_method</c> is <c>personal_token</c>, which the pipeline-only policy
/// (<see cref="AuthorizationPolicies.DeploymentIngestion"/>) rejects — ingest, build registration and
/// external promotion creation are for the configured pipeline keys — and which the decision routes
/// refuse (<see cref="PersonalTokenRestrictions.DenyPersonalTokens{TBuilder}"/>): an approval is a
/// person's judgement, and a token is how an agent acts for them.</item>
/// </list></para>
/// </summary>
public class PersonalAccessTokenService
{
    /// <summary>What every token starts with — how the key handler tells one from a configured key.</summary>
    public const string Prefix = "ipt_";

    public const string AuthMethod = "personal_token";
    public const string TokenIdClaim = "token_id";

    /// <summary>The lifetimes the portal offers. Always finite: a forgotten token stops working on its own.</summary>
    public static readonly int[] AllowedLifetimesDays = [7, 30, 90];

    /// <summary>Active (unexpired, unrevoked) tokens one person may hold at once.</summary>
    public const int MaxActivePerUser = 10;

    /// <summary>How stale <see cref="PersonalAccessToken.LastUsedAt"/> may get before a use writes it again.</summary>
    public static readonly TimeSpan TouchInterval = TimeSpan.FromMinutes(5);

    private const string AdminRole = "InfraPortal.Admin";
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

    /// <summary>Creates a token for the signed-in user. Returns the row and the secret, which is never stored.</summary>
    /// <exception cref="ArgumentException">Bad name, lifetime, or too many active tokens.</exception>
    public async Task<(PersonalAccessToken Token, string Secret)> CreateAsync(
        string? name, int expiresInDays, CancellationToken ct = default)
    {
        var owner = OwnerEmail;
        if (owner.Length == 0)
            throw new ArgumentException("Your sign-in carries no email address, which a token needs to act as you.");

        var label = (name ?? "").Trim();
        if (label.Length == 0) throw new ArgumentException("Give the token a name, so you can tell it apart later.");
        if (label.Length > 100) throw new ArgumentException("The name can be at most 100 characters.");
        if (!AllowedLifetimesDays.Contains(expiresInDays))
            throw new ArgumentException($"expiresInDays must be one of: {string.Join(", ", AllowedLifetimesDays)}.");

        var now = DateTimeOffset.UtcNow;
        var active = await _db.PersonalAccessTokens
            .CountAsync(t => t.UserEmail == owner && t.RevokedAt == null && t.ExpiresAt > now, ct);
        if (active >= MaxActivePerUser)
            throw new ArgumentException($"You already have {MaxActivePerUser} active tokens. Revoke one you no longer use first.");

        var roles = _user.Roles
            .Where(r => !string.Equals(r, AdminRole, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
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
            CreatedAt = now,
            ExpiresAt = now.AddDays(expiresInDays),
        };
        _db.PersonalAccessTokens.Add(token);
        await _db.SaveChangesAsync(ct);

        await _audit.Log("auth", "personal_token.created", _user.Id, _user.Name, "user",
            nameof(PersonalAccessToken), token.Id, null,
            new { token.Name, token.Prefix, token.ExpiresAt, roles });

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

    /// <summary>
    /// The principal a presented secret stands for, or null when it is unknown, revoked or expired.
    /// Static over a context so the key handler can call it without the per-user service.
    /// </summary>
    public static async Task<ClaimsPrincipal?> AuthenticateAsync(
        PlatformDbContext db, string secret, string authenticationType, CancellationToken ct = default)
    {
        var hash = Hash(secret);
        var token = await db.PersonalAccessTokens.AsNoTracking().FirstOrDefaultAsync(t => t.TokenHash == hash, ct);
        var now = DateTimeOffset.UtcNow;
        if (token is null || token.RevokedAt is not null || token.ExpiresAt <= now) return null;

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
            new(ApiKeyAuthHandler.ExpiresAtClaim, token.ExpiresAt.ToString("O")),
        };
        claims.AddRange(token.RoleList.Select(r => new Claim("roles", r)));

        // Its own authentication type rather than the ApiKey scheme's: code that asks "was this a
        // configured key?" (the manual-deploy route) must answer no for a person's token.
        return new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType, "name", "roles"));
    }

    public static bool LooksLikeToken(string presented) => presented.StartsWith(Prefix, StringComparison.Ordinal);

    public static bool IsPersonalToken(ClaimsPrincipal user) => user.FindFirstValue("auth_method") == AuthMethod;

    public static string Hash(string secret) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
