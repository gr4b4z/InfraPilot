using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Platform.Api.Features.Users.Models;
using Platform.Api.Infrastructure.Auth;
using Platform.Api.Infrastructure.Persistence;

namespace Platform.Api.Features.Users;

/// <summary>
/// Reads and writes the current user's preferences.
///
/// <para>Scoped, and the hidden-product set is memoised for the lifetime of the request: it is
/// consulted by nearly every list query, and one request routinely runs several of them. Without
/// the cache a promotions page load would issue the same lookup half a dozen times.</para>
///
/// <para><b>No identity ⇒ no filtering.</b> Pipeline ingest authenticates with an API key and has
/// no human behind it, so <see cref="ICurrentUser.Email"/> is blank there. Every read returns the
/// empty set in that case, which is what keeps a personal display preference from ever changing
/// what an automated caller is allowed to write or read back.</para>
/// </summary>
public class UserPreferencesService
{
    private readonly PlatformDbContext _db;
    private readonly ICurrentUser _user;
    private readonly ILogger<UserPreferencesService> _logger;

    /// <summary>Request-lifetime memo of <see cref="GetHiddenProductsAsync"/>.</summary>
    private IReadOnlySet<string>? _hiddenProducts;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public UserPreferencesService(
        PlatformDbContext db, ICurrentUser user, ILogger<UserPreferencesService> logger)
    {
        _db = db;
        _user = user;
        _logger = logger;
    }

    /// <summary>Canonical storage form for the owning identity. Mirrors the approver-email rule.</summary>
    public static string NormalizeEmail(string? email) => (email ?? "").Trim().ToLowerInvariant();

    /// <summary>
    /// The products the current user has hidden.
    ///
    /// <para>Matching is <b>exact</b>, the way product names are compared everywhere else in this
    /// system. It also has to be: the set is handed to EF as a <c>NOT IN</c>, and a case-insensitive
    /// C# comparer would not survive the trip — the database applies its own collation, which is
    /// case-sensitive on Postgres and case-insensitive on SQL Server. Exact matching behaves the
    /// same on both, and the UI only ever writes names it was given by the API.</para>
    ///
    /// <para>Empty for an unauthenticated or non-human caller, and empty if the stored value fails
    /// to parse — a corrupt preference must degrade to "show everything", never to a blank UI the
    /// user can't explain or escape.</para>
    /// </summary>
    public async Task<IReadOnlySet<string>> GetHiddenProductsAsync(CancellationToken ct = default)
    {
        if (_hiddenProducts is not null) return _hiddenProducts;

        var empty = (IReadOnlySet<string>)new HashSet<string>(StringComparer.Ordinal);

        var email = NormalizeEmail(_user.Email);
        if (email.Length == 0) return _hiddenProducts = empty;

        var raw = await _db.UserPreferences.AsNoTracking()
            .Where(p => p.UserEmail == email && p.Key == UserPreferenceKeys.HiddenProducts)
            .Select(p => p.Value)
            .FirstOrDefaultAsync(ct);

        if (string.IsNullOrWhiteSpace(raw)) return _hiddenProducts = empty;

        try
        {
            var names = JsonSerializer.Deserialize<List<string>>(raw, JsonOpts) ?? new();
            return _hiddenProducts = new HashSet<string>(
                names.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim()),
                StringComparer.Ordinal);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex,
                "Malformed {Key} preference for a user; treating as no products hidden",
                UserPreferenceKeys.HiddenProducts);
            return _hiddenProducts = empty;
        }
    }

    /// <summary>
    /// Replaces the hidden-product set. Names are trimmed and de-duplicated case-insensitively;
    /// blanks are dropped. Names that match no known product are kept rather than pruned — a
    /// product that disappears for a release should come back hidden, the way the user left it.
    /// </summary>
    /// <returns>The stored set.</returns>
    public async Task<IReadOnlyList<string>> SetHiddenProductsAsync(
        IEnumerable<string>? products, CancellationToken ct = default)
    {
        var email = NormalizeEmail(_user.Email);
        if (email.Length == 0)
            throw new UnauthorizedAccessException("Preferences require a signed-in user.");

        var cleaned = (products ?? Enumerable.Empty<string>())
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p.Trim())
            .DistinctBy(p => p.ToLowerInvariant())
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var value = JsonSerializer.Serialize(cleaned, JsonOpts);

        var row = await _db.UserPreferences.FirstOrDefaultAsync(
            p => p.UserEmail == email && p.Key == UserPreferenceKeys.HiddenProducts, ct);

        if (row is null)
        {
            _db.UserPreferences.Add(new UserPreference
            {
                Id = Guid.NewGuid(),
                UserEmail = email,
                Key = UserPreferenceKeys.HiddenProducts,
                Value = value,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
        }
        else
        {
            row.Value = value;
            row.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await _db.SaveChangesAsync(ct);

        // The request may go on to read the set back (the endpoint returns it); keep the memo honest.
        _hiddenProducts = new HashSet<string>(cleaned, StringComparer.Ordinal);

        return cleaned;
    }
}
