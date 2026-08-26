using Microsoft.Extensions.Logging;
using NSubstitute;
using Platform.Api.Features.Settings;
using Platform.Api.Infrastructure.Auth;
using Platform.Api.Infrastructure.Persistence;

namespace Platform.Api.Tests;

/// <summary>
/// A real <see cref="EnvironmentAliasResolver"/> over the test's own DbContext, for the same reason
/// <see cref="TestProductOverrides"/> is real: with no settings row saved it resolves against the
/// built-in defaults, which carry no aliases, so every environment resolves to itself — what a test
/// not about aliases wants — while still running the production lookup. A test that wants an alias
/// saves a settings row and gets it for free.
/// </summary>
internal static class TestEnvironmentAliases
{
    public static EnvironmentAliasResolver For(PlatformDbContext db, ICurrentUser? user = null)
    {
        if (user is null)
        {
            user = Substitute.For<ICurrentUser>();
            user.Id.Returns("admin-1");
            user.Name.Returns("Ada Admin");
            user.Email.Returns("ada@example.com");
        }

        return new EnvironmentAliasResolver(
            new AppSettingsService(db, user),
            Substitute.For<ILogger<EnvironmentAliasResolver>>());
    }
}
