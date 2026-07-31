using Microsoft.Extensions.Logging;
using NSubstitute;
using Platform.Api.Features.Users;
using Platform.Api.Infrastructure.Auth;
using Platform.Api.Infrastructure.Persistence;

namespace Platform.Api.Tests;

/// <summary>
/// A real <see cref="UserPreferencesService"/> over the test's own DbContext.
///
/// <para>Real rather than substituted: the class is concrete with non-virtual methods, and more to
/// the point a real one over an empty table returns exactly what these tests want — "this user
/// hides nothing" — while still exercising the query the production path runs. A test that wants
/// the filtered behaviour seeds a <c>user_preferences</c> row and gets it for free.</para>
/// </summary>
internal static class TestUserPreferences
{
    public static UserPreferencesService For(PlatformDbContext db, ICurrentUser? user = null)
    {
        if (user is null)
        {
            user = Substitute.For<ICurrentUser>();
            user.Email.Returns("alice@example.com");
        }

        return new UserPreferencesService(db, user, Substitute.For<ILogger<UserPreferencesService>>());
    }
}
