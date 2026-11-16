using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Caching.Memory;
using Platform.Api.Infrastructure.Identity;

namespace Platform.Api.Infrastructure.Auth;

/// <summary>
/// Adds the directory's other addresses for the signed-in person to their principal, as
/// <see cref="ClaimType"/> claims, which <see cref="CurrentUser.Emails"/> reads.
///
/// <para>Why: a person signs in with their sign-in name (the token's <c>preferred_username</c> /
/// <c>upn</c>), but everything that names them from outside the token uses their mail: a ticket's QA
/// owner, the people picker, an approver list. The two are usually the same address, but not always.
/// A long name can be cut short in the sign-in name, or a name change can leave one of the two behind.
/// For such a person, "Assigned to me" was empty while everyone else could see their work items. The
/// token carries no mail claim, so the directory is asked: once per person per
/// <see cref="KnownTtl"/>, from the process-wide cache.</para>
///
/// <para>Only adds, never replaces: <see cref="ICurrentUser.Email"/> stays the sign-in name, so what
/// the portal records people as (decisions, comments, audit) does not change and does not depend on
/// the directory answering.</para>
///
/// <para>Fails open to "no other addresses". A directory that cannot answer within
/// <see cref="LookupTimeout"/>, or at all, leaves the request as it was, and it is asked again after
/// <see cref="RetryAfterUnknown"/>. Configured API keys carry no person id and are skipped.</para>
/// </summary>
public sealed class DirectoryEmailClaimsTransformation : IClaimsTransformation
{
    /// <summary>The claim each extra address is added as. Not a name any token issues.</summary>
    public const string ClaimType = "portal_directory_email";

    /// <summary>How long the directory's answer for a person is trusted. Addresses rarely change.</summary>
    public static readonly TimeSpan KnownTtl = TimeSpan.FromHours(1);

    /// <summary>How soon a person the directory could not answer for is looked up again.</summary>
    public static readonly TimeSpan RetryAfterUnknown = TimeSpan.FromMinutes(1);

    /// <summary>
    /// The longest a request waits for the directory. The lookup sits in front of the person's first
    /// request per <see cref="KnownTtl"/>, so a slow directory must not become a slow portal.
    /// </summary>
    public static readonly TimeSpan LookupTimeout = TimeSpan.FromSeconds(5);

    private readonly IIdentityService _identity;
    private readonly IMemoryCache _cache;
    private readonly IHttpContextAccessor _http;
    private readonly ILogger<DirectoryEmailClaimsTransformation> _logger;

    public DirectoryEmailClaimsTransformation(
        IIdentityService identity, IMemoryCache cache, IHttpContextAccessor http,
        ILogger<DirectoryEmailClaimsTransformation> logger)
    {
        _identity = identity;
        _cache = cache;
        _http = http;
        _logger = logger;
    }

    public async Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        // Called on every authentication of the request, so it must be cheap and idempotent.
        if (principal.Identity?.IsAuthenticated != true) return principal;
        if (principal.HasClaim(c => c.Type == ClaimType)) return principal;

        // A person: an Entra object id, a local user's id, or a personal token's owner. Configured API
        // keys have neither and are not anybody's mailbox.
        var personId = principal.FindFirstValue("oid") ?? principal.FindFirstValue("sub");
        if (string.IsNullOrWhiteSpace(personId)) return principal;

        var signInEmail = CurrentUser.EmailOf(principal);
        var known = await AddressesAsync(personId.Trim());
        var others = known
            .Where(a => !string.Equals(a, signInEmail, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (others.Count == 0) return principal;

        // A clone, not the incoming principal: the authentication handler may hand the same instance
        // to a later call. A separate unauthenticated identity adds claims without touching the one
        // authorization reads its type and name from.
        var transformed = principal.Clone();
        transformed.AddIdentity(new ClaimsIdentity(others.Select(a => new Claim(ClaimType, a))));
        return transformed;
    }

    private async Task<IReadOnlyList<string>> AddressesAsync(string personId)
    {
        var key = $"directory-emails:{personId.ToLowerInvariant()}";
        if (_cache.TryGetValue(key, out IReadOnlyList<string>? cached) && cached is not null) return cached;

        var aborted = _http.HttpContext?.RequestAborted ?? CancellationToken.None;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(aborted);
        timeout.CancelAfter(LookupTimeout);

        IReadOnlyList<string>? found;
        try
        {
            found = await _identity.GetEmailAddresses(personId, timeout.Token);
        }
        catch (OperationCanceledException) when (aborted.IsCancellationRequested)
        {
            // The caller went away; nothing was learned, so nothing is remembered.
            return [];
        }
        catch (Exception ex)
        {
            // The timeout, or an implementation breaking its no-throw contract: either way, unknown.
            _logger.LogDebug(ex, "Directory email lookup for {PersonId} did not answer; using the sign-in name only", personId);
            found = null;
        }

        IReadOnlyList<string> addresses = found ?? [];
        _cache.Set(key, addresses, found is null ? RetryAfterUnknown : KnownTtl);
        return addresses;
    }
}
