using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Internal;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Platform.Api.Infrastructure.Auth;
using Platform.Api.Infrastructure.Identity;

namespace Platform.Api.Tests.Infrastructure.Auth;

/// <summary>
/// <see cref="DirectoryEmailClaimsTransformation"/>: someone whose sign-in name is not their mail
/// gets their mail as well, the directory is asked once per person per window, and a directory that
/// cannot answer leaves the request exactly as it was.
/// </summary>
public class DirectoryEmailClaimsTransformationTests
{
    private const string PersonId = "0f3b6a2e-1c4d-4e5f-8a9b-7c6d5e4f3a2b";
    private const string SignInName = "firstname.longlast@example.com";
    private const string Mail = "Firstname.Longlastname@example.com";

    private readonly IIdentityService _directory = Substitute.For<IIdentityService>();
    private readonly FakeClock _clock = new();
    private readonly DirectoryEmailClaimsTransformation _sut;

    public DirectoryEmailClaimsTransformationTests()
    {
        var cache = new MemoryCache(new MemoryCacheOptions { Clock = _clock });
        var http = Substitute.For<IHttpContextAccessor>();
        http.HttpContext.Returns(new DefaultHttpContext());
        _sut = new DirectoryEmailClaimsTransformation(
            _directory, cache, http, NullLogger<DirectoryEmailClaimsTransformation>.Instance);
    }

    private static ClaimsPrincipal SignedIn(params Claim[] claims) =>
        new(new ClaimsIdentity(claims, authenticationType: "test"));

    private static ClaimsPrincipal Person() =>
        SignedIn(new Claim("oid", PersonId), new Claim("upn", SignInName));

    private void Answer(IReadOnlyList<string>? addresses) =>
        _directory.GetEmailAddresses(PersonId, Arg.Any<CancellationToken>()).Returns(addresses);

    private static string[] Added(ClaimsPrincipal principal) =>
        principal.FindAll(DirectoryEmailClaimsTransformation.ClaimType).Select(c => c.Value).ToArray();

    [Fact]
    public async Task Adds_the_directory_addresses_other_than_the_sign_in_name()
    {
        Answer([Mail, SignInName.ToUpperInvariant()]);

        var result = await _sut.TransformAsync(Person());

        Assert.Equal([Mail], Added(result));
        // The sign-in name is untouched: it is what the portal records the person as.
        Assert.Equal(SignInName, result.FindFirstValue("upn"));
        Assert.True(result.Identity!.IsAuthenticated);
    }

    [Fact]
    public async Task Leaves_the_principal_alone_when_the_only_address_is_the_sign_in_name()
    {
        Answer([SignInName]);
        var principal = Person();

        Assert.Same(principal, await _sut.TransformAsync(principal));
    }

    [Fact]
    public async Task Asks_the_directory_once_per_person_per_window()
    {
        Answer([Mail]);

        await _sut.TransformAsync(Person());
        await _sut.TransformAsync(Person());
        _clock.Advance(DirectoryEmailClaimsTransformation.KnownTtl - TimeSpan.FromMinutes(1));
        await _sut.TransformAsync(Person());

        await _directory.Received(1).GetEmailAddresses(PersonId, Arg.Any<CancellationToken>());

        _clock.Advance(TimeSpan.FromMinutes(2));
        await _sut.TransformAsync(Person());
        await _directory.Received(2).GetEmailAddresses(PersonId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_already_transformed_principal_is_not_looked_up_again()
    {
        Answer([Mail]);
        var once = await _sut.TransformAsync(Person());
        _directory.ClearReceivedCalls();

        var twice = await _sut.TransformAsync(once);

        Assert.Same(once, twice);
        await _directory.DidNotReceiveWithAnyArgs().GetEmailAddresses(default!, default);
    }

    [Fact]
    public async Task A_directory_that_cannot_say_changes_nothing_and_is_asked_again_soon()
    {
        Answer(null);
        var principal = Person();

        Assert.Same(principal, await _sut.TransformAsync(principal));

        _clock.Advance(DirectoryEmailClaimsTransformation.RetryAfterUnknown + TimeSpan.FromSeconds(1));
        Answer([Mail]);
        Assert.Equal([Mail], Added(await _sut.TransformAsync(Person())));
    }

    [Fact]
    public async Task A_directory_that_throws_changes_nothing()
    {
        _directory.GetEmailAddresses(PersonId, Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("directory down"));
        var principal = Person();

        Assert.Same(principal, await _sut.TransformAsync(principal));
    }

    [Fact]
    public async Task A_configured_api_key_is_nobody_and_is_not_looked_up()
    {
        var apiKey = SignedIn(
            new Claim(ClaimTypes.NameIdentifier, "apikey:pipeline"),
            new Claim("auth_method", "api_key"));

        Assert.Same(apiKey, await _sut.TransformAsync(apiKey));
        await _directory.DidNotReceiveWithAnyArgs().GetEmailAddresses(default!, default);
    }

    [Fact]
    public async Task An_anonymous_request_is_not_looked_up()
    {
        var anonymous = new ClaimsPrincipal(new ClaimsIdentity());

        Assert.Same(anonymous, await _sut.TransformAsync(anonymous));
        await _directory.DidNotReceiveWithAnyArgs().GetEmailAddresses(default!, default);
    }

    [Fact]
    public async Task A_local_user_is_looked_up_by_their_sub()
    {
        _directory.GetEmailAddresses("local-id", Arg.Any<CancellationToken>()).Returns([Mail]);

        var result = await _sut.TransformAsync(
            SignedIn(new Claim("sub", "local-id"), new Claim("preferred_username", SignInName)));

        Assert.Equal([Mail], Added(result));
    }

    private sealed class FakeClock : ISystemClock
    {
        public DateTimeOffset UtcNow { get; private set; } = new(2026, 11, 16, 9, 0, 0, TimeSpan.Zero);
        public void Advance(TimeSpan by) => UtcNow += by;
    }
}
