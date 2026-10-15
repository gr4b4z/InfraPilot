using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Internal;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Platform.Api.Infrastructure.Auth;
using Platform.Api.Infrastructure.Identity;

namespace Platform.Api.Tests.Infrastructure.Auth;

/// <summary>
/// <see cref="PersonalTokenOwnerCheck"/>: a token is refused only when the directory positively
/// reports its owner disabled or missing, and each owner is looked up at most once per window —
/// the window being how long a disabled owner's token can keep working.
/// </summary>
public class PersonalTokenOwnerCheckTests
{
    private readonly IIdentityService _directory = Substitute.For<IIdentityService>();
    private readonly FakeClock _clock = new();
    private readonly PersonalTokenOwnerCheck _sut;

    public PersonalTokenOwnerCheckTests()
    {
        var cache = new MemoryCache(new MemoryCacheOptions { Clock = _clock });
        _sut = new PersonalTokenOwnerCheck(_directory, cache, NullLogger<PersonalTokenOwnerCheck>.Instance);
    }

    private static PersonalAccessToken TokenOf(string ownerId) =>
        new() { Id = Guid.NewGuid(), UserId = ownerId, UserEmail = $"{ownerId}@example.com" };

    private void Answer(string ownerId, AccountStatus status) =>
        _directory.GetAccountStatus(ownerId, Arg.Any<CancellationToken>()).Returns(status);

    [Theory]
    [InlineData(AccountStatus.Active, true)]
    [InlineData(AccountStatus.Unknown, true)]
    [InlineData(AccountStatus.Disabled, false)]
    [InlineData(AccountStatus.Missing, false)]
    public async Task Only_a_positive_answer_refuses(AccountStatus status, bool allowed)
    {
        Answer("owner", status);
        Assert.Equal(allowed, await _sut.AllowsAsync(TokenOf("owner")));
    }

    [Fact]
    public async Task A_disabled_owner_is_noticed_within_the_check_interval()
    {
        Answer("owner", AccountStatus.Active);
        Assert.True(await _sut.AllowsAsync(TokenOf("owner")));

        // Disabled in Entra: still trusted until the cached answer runs out...
        Answer("owner", AccountStatus.Disabled);
        _clock.Advance(PersonalTokenOwnerCheck.CheckInterval - TimeSpan.FromSeconds(1));
        Assert.True(await _sut.AllowsAsync(TokenOf("owner")));

        // ...and refused from then on.
        _clock.Advance(TimeSpan.FromSeconds(2));
        Assert.False(await _sut.AllowsAsync(TokenOf("owner")));
        await _directory.Received(2).GetAccountStatus("owner", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task One_owners_tokens_share_one_lookup()
    {
        Answer("owner", AccountStatus.Active);

        for (var i = 0; i < 5; i++) Assert.True(await _sut.AllowsAsync(TokenOf("owner")));

        await _directory.Received(1).GetAccountStatus("owner", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_unknown_answer_is_retried_sooner()
    {
        Answer("owner", AccountStatus.Unknown);
        Assert.True(await _sut.AllowsAsync(TokenOf("owner")));

        Answer("owner", AccountStatus.Missing);
        _clock.Advance(PersonalTokenOwnerCheck.RetryAfterUnknown + TimeSpan.FromSeconds(1));
        Assert.False(await _sut.AllowsAsync(TokenOf("owner")));
    }

    [Fact]
    public async Task A_directory_that_throws_does_not_fail_the_request()
    {
        _directory.GetAccountStatus("owner", Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("graph down"));

        Assert.True(await _sut.AllowsAsync(TokenOf("owner")));
    }

    private sealed class FakeClock : ISystemClock
    {
        public DateTimeOffset UtcNow { get; private set; } = new(2026, 10, 15, 9, 0, 0, TimeSpan.Zero);
        public void Advance(TimeSpan by) => UtcNow += by;
    }
}
