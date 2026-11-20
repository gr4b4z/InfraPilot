namespace Platform.Api.Infrastructure.Auth;

public interface ICurrentUser
{
    string Id { get; }
    string Name { get; }

    /// <summary>
    /// The address the user signed in with: their sign-in name. What the portal records them as
    /// (decisions, comments, audit), so it stays the same from one request to the next.
    /// </summary>
    string Email { get; }

    /// <summary>
    /// Every address that is this user: <see cref="Email"/> first, then the other addresses the
    /// directory knows them by (see <see cref="DirectoryEmailClaimsTransformation"/>). Records made
    /// elsewhere name people by their mail, which need not be their sign-in name, so "is this me?"
    /// is a question for this list (<see cref="CurrentUserEmails.IsMe"/>), not for <see cref="Email"/>.
    /// </summary>
    IReadOnlyList<string> Emails { get; }

    IReadOnlyList<string> Groups { get; }
    IReadOnlyList<string> Roles { get; }
    bool IsAdmin { get; }
    /// <summary>
    /// True when the user holds <c>InfraPortal.QA</c> — a lightweight promotion-capable role
    /// for small teams that don't need AD security groups.
    /// </summary>
    bool IsQA { get; }
    bool IsInGroup(string groupId);
}

/// <summary>
/// "Is this address me?" over <see cref="ICurrentUser.Emails"/>. Extension methods rather than
/// interface members so every <see cref="ICurrentUser"/> answers the same way, test doubles included:
/// <see cref="ICurrentUser.Email"/> always counts, even when an implementation leaves
/// <see cref="ICurrentUser.Emails"/> empty.
/// </summary>
public static class CurrentUserEmails
{
    /// <summary>True when <paramref name="email"/> is one of the current user's addresses (any case).</summary>
    public static bool IsMe(this ICurrentUser user, string? email)
    {
        var candidate = email?.Trim();
        if (string.IsNullOrEmpty(candidate)) return false;
        return string.Equals(user.Email?.Trim(), candidate, StringComparison.OrdinalIgnoreCase)
            || (user.Emails ?? []).Any(e => string.Equals(e, candidate, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The addresses that name the same person as <paramref name="email"/>: every one of the current
    /// user's when it is theirs, otherwise just <paramref name="email"/>. Other people's addresses
    /// can't be widened — the directory is only asked about the person signed in.
    /// </summary>
    public static IReadOnlyList<string> SamePersonAs(this ICurrentUser user, string email)
    {
        if (!user.IsMe(email)) return [email];
        return new[] { email, user.Email }
            .Concat(user.Emails ?? [])
            .Where(e => !string.IsNullOrWhiteSpace(e))
            .Select(e => e.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
