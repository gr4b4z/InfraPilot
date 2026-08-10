using Microsoft.Extensions.Logging;
using NSubstitute;
using Platform.Api.Features.Deployments;
using Platform.Api.Infrastructure.Auth;
using Platform.Api.Infrastructure.Persistence;

namespace Platform.Api.Tests;

/// <summary>
/// A real <see cref="ServiceDeletionService"/> over the test's own DbContext, for the same reason
/// <see cref="TestUserPreferences"/> is real: over an empty <c>deleted_services</c> table it returns
/// "nothing is retired", which is what a test not about retirement wants, while still running the
/// production query. A test that wants the retired behaviour seeds a row and gets it for free.
/// </summary>
internal static class TestServiceDeletions
{
    public static ServiceDeletionService For(PlatformDbContext db, ICurrentUser? user = null)
    {
        if (user is null)
        {
            user = Substitute.For<ICurrentUser>();
            user.Id.Returns("admin-1");
            user.Name.Returns("Ada Admin");
            user.Email.Returns("ada@example.com");
        }

        return new ServiceDeletionService(db, user, Substitute.For<ILogger<ServiceDeletionService>>());
    }
}
