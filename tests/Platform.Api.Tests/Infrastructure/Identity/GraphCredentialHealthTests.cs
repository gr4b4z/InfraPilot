using Azure.Identity;
using Platform.Api.Infrastructure.Identity;

namespace Platform.Api.Tests.Infrastructure.Identity;

/// <summary>
/// What <see cref="GraphCredentialHealth"/> keeps of a failed Graph sign-in: Entra's code and the
/// first sentence of its explanation — enough for the admin banner, nothing that belongs only in
/// the log.
/// </summary>
public class GraphCredentialHealthTests
{
    private readonly FakeTime _time = new();
    private readonly GraphCredentialHealth _sut;

    public GraphCredentialHealthTests() => _sut = new GraphCredentialHealth(_time);

    [Fact]
    public void Starts_healthy()
    {
        Assert.Null(_sut.Failure);
        Assert.Null(_sut.RecordSuccess());
    }

    [Fact]
    public void Keeps_the_code_and_first_sentence_but_not_the_trace_ids()
    {
        var failure = _sut.RecordFailure(new AuthenticationFailedException(
            "ClientSecretCredential authentication failed: AADSTS7000222: The provided client secret keys for app " +
            "'b8e60444-0000-4000-8000-000000000000' are expired. Visit the Azure portal to create new keys for your " +
            "app: https://aka.ms/NewClientSecret. Trace ID: 0d6a3c1e Correlation ID: 5f2b7c9d Timestamp: 2026-10-19 " +
            "00:15:10Z\nSee the troubleshooting guide for more information."));

        Assert.Equal("AADSTS7000222", failure.Code);
        Assert.Equal(
            "The provided client secret keys for app 'b8e60444-0000-4000-8000-000000000000' are expired.",
            failure.Message);
        Assert.DoesNotContain("Trace ID", failure.Message);
    }

    [Fact]
    public void Without_an_aadsts_code_keeps_the_first_line()
    {
        var failure = _sut.RecordFailure(new AuthenticationFailedException(
            "ClientSecretCredential authentication failed: Retry failed after 4 tries.\n   at Azure.Core.Pipeline…"));

        Assert.Null(failure.Code);
        Assert.Equal("ClientSecretCredential authentication failed: Retry failed after 4 tries.", failure.Message);
    }

    [Fact]
    public void A_long_message_is_cut_short()
    {
        var failure = _sut.RecordFailure(new AuthenticationFailedException(new string('x', 1000)));

        Assert.Equal(300, failure.Message.Length);
        Assert.EndsWith("…", failure.Message);
    }

    [Fact]
    public void Since_stays_at_the_first_failure_until_a_success_clears_it()
    {
        var start = _time.GetUtcNow();
        _sut.RecordFailure(new AuthenticationFailedException("AADSTS7000222: expired. More."));
        _time.Advance(TimeSpan.FromHours(20));
        var later = _sut.RecordFailure(new AuthenticationFailedException("AADSTS7000222: expired. More."));

        Assert.Equal(start, later.Since);
        Assert.Equal(start.AddHours(20), later.LastFailedAt);
        Assert.Same(later, _sut.Failure);

        Assert.Same(later, _sut.RecordSuccess());
        Assert.Null(_sut.Failure);

        _time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(_time.GetUtcNow(), _sut.RecordFailure(new AuthenticationFailedException("again")).Since);
    }

    [Fact]
    public void Finds_a_credential_failure_wrapped_in_another_exception()
    {
        var inner = new AuthenticationFailedException("AADSTS7000222: expired.");

        Assert.Same(inner, GraphCredentialHealth.CredentialFailureIn(new InvalidOperationException("outer", inner)));
        Assert.Null(GraphCredentialHealth.CredentialFailureIn(new HttpRequestException("network")));
    }

    private sealed class FakeTime : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 19, 0, 15, 10, TimeSpan.Zero);
        public void Advance(TimeSpan by) => _now += by;
        public override DateTimeOffset GetUtcNow() => _now;
    }
}
