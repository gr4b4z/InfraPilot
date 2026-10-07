using NSubstitute;
using Platform.Api.Features.Settings;
using Platform.Api.Infrastructure.Auth;
using Platform.Api.Infrastructure.Persistence;

namespace Platform.Api.Tests;

/// <summary>
/// A real <see cref="ParticipantRoleCatalog"/> over the test's own DbContext, for the same reason
/// <see cref="TestEnvironmentAliases"/> is real: with no settings row saved it reads the built-in
/// role defaults, which carry no aliases, so every role resolves to itself — what a test not about
/// roles wants — while still running the production lookup.
/// </summary>
internal static class TestRoleCatalog
{
    public static ParticipantRoleCatalog For(PlatformDbContext db, ICurrentUser? user = null)
    {
        if (user is null)
        {
            user = Substitute.For<ICurrentUser>();
            user.Id.Returns("admin-1");
            user.Name.Returns("Ada Admin");
            user.Email.Returns("ada@example.com");
        }

        return new ParticipantRoleCatalog(new AppSettingsService(db, user));
    }
}
