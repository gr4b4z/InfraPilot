using Microsoft.Extensions.Caching.Memory;
using Platform.Api.Infrastructure.Identity;

namespace Platform.Api.Infrastructure.Auth;

/// <summary>
/// Whether a personal access token's owner still has an account that may sign in. A token carries
/// a snapshot of its owner taken when it was created, so without this it would keep working after
/// the owner is disabled or deleted in Entra, until it expires up to a year later.
///
/// <para>Checked when a token is used, through <see cref="IIdentityService.GetAccountStatus"/>
/// (Graph in Msal mode, the local users table in Local mode), and cached per owner for
/// <see cref="CheckInterval"/> in the process-wide cache. That cache is how a burst of agent calls
/// costs one directory read per owner per window on each replica, and that window is how long a
/// disabled owner's token can keep working.</para>
///
/// <para>Fails closed only on a positive answer: the directory says the account is disabled, or
/// that it does not exist. When it cannot say (Graph down or throttled, an id the stub does not
/// know), the token keeps working and the owner is looked up again after
/// <see cref="RetryAfterUnknown"/>. A directory outage should not stop every agent at once, and
/// the token stays bounded by its expiry and by revocation.</para>
///
/// <para>The owner's role assignments are not re-checked. Reading a user's app-role assignments
/// needs Graph's <c>Directory.Read.All</c>, which the portal's Graph app does not hold. Offboarding
/// disables the account, which this catches. An administrator revokes the tokens of someone whose
/// roles were taken away (Settings → Agent Tokens) — QA and Admin tokens above all, which keep
/// making decisions with the role until they expire.</para>
/// </summary>
public class PersonalTokenOwnerCheck
{
    /// <summary>How long an answer about an owner is trusted. Matches the other directory caches.</summary>
    public static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(5);

    /// <summary>How soon an owner the directory could not answer for is looked up again.</summary>
    public static readonly TimeSpan RetryAfterUnknown = TimeSpan.FromMinutes(1);

    private readonly IIdentityService _identity;
    private readonly IMemoryCache _cache;
    private readonly ILogger<PersonalTokenOwnerCheck> _logger;

    public PersonalTokenOwnerCheck(IIdentityService identity, IMemoryCache cache, ILogger<PersonalTokenOwnerCheck> logger)
    {
        _identity = identity;
        _cache = cache;
        _logger = logger;
    }

    /// <summary>False when the directory reports the token's owner disabled or missing.</summary>
    public async Task<bool> AllowsAsync(PersonalAccessToken token, CancellationToken ct = default)
    {
        // Keyed by owner, not token: one person's tokens share an account, so they share one read.
        var key = $"pat-owner:{token.UserId.ToLowerInvariant()}";
        if (!_cache.TryGetValue(key, out AccountStatus status))
        {
            status = await LookUpAsync(token.UserId, ct);
            _cache.Set(key, status, status == AccountStatus.Unknown ? RetryAfterUnknown : CheckInterval);

            // Once per owner per window, from the read that found it: the agent behind the token is
            // likely retrying, and every refusal after this one is served from the cache.
            if (IsRefused(status))
                _logger.LogWarning(
                    "Refusing the personal access tokens of {Owner} ({OwnerId}): the directory reports the account {Status}",
                    token.UserEmail, token.UserId, status);
        }
        return !IsRefused(status);
    }

    private async Task<AccountStatus> LookUpAsync(string userId, CancellationToken ct)
    {
        try
        {
            return await _identity.GetAccountStatus(userId, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // The contract is not to throw; a directory implementation that does must not fail the request.
            _logger.LogWarning(ex, "Account status lookup threw for {OwnerId}; treating it as unknown", userId);
            return AccountStatus.Unknown;
        }
    }

    private static bool IsRefused(AccountStatus status) => status is AccountStatus.Disabled or AccountStatus.Missing;
}
