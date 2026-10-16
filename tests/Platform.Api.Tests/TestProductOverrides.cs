using Microsoft.Extensions.Logging;
using NSubstitute;
using Platform.Api.Features.Deployments;
using Platform.Api.Infrastructure.Auth;
using Platform.Api.Infrastructure.Persistence;

namespace Platform.Api.Tests;

/// <summary>
/// A real <see cref="ServiceProductOverrideService"/> over the test's own DbContext, for the same
/// reason <see cref="TestServiceDeletions"/> and <see cref="TestUserPreferences"/> are real: over an
/// empty <c>service_product_overrides</c> table it resolves every product to the one the caller sent,
/// which is what a test not about overrides wants, while still running the production lookup. A test
/// that wants the redirect seeds a row and gets it for free.
/// </summary>
internal static class TestProductOverrides
{
    public static ServiceProductOverrideService For(PlatformDbContext db, ICurrentUser? user = null)
    {
        if (user is null)
        {
            user = Substitute.For<ICurrentUser>();
            user.Id.Returns("admin-1");
            user.Name.Returns("Ada Admin");
            user.Email.Returns("ada@example.com");
        }

        return new ServiceProductOverrideService(
            db, user, Substitute.For<ILogger<ServiceProductOverrideService>>());
    }
}
