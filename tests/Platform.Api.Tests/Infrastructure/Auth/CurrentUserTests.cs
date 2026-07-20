using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using NSubstitute;
using Platform.Api.Infrastructure.Auth;

namespace Platform.Api.Tests.Infrastructure.Auth;

public class CurrentUserTests
{
    private static CurrentUser BuildFor(params Claim[] claims)
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "test"));
        var accessor = Substitute.For<IHttpContextAccessor>();
        accessor.HttpContext.Returns(new DefaultHttpContext { User = principal });
        return new CurrentUser(accessor);
    }

    [Fact]
    public void Email_prefers_preferred_username_for_v2_tokens()
    {
        var sut = BuildFor(
            new Claim("preferred_username", "v2@softwareone.com"),
            new Claim("upn", "ignored@softwareone.com"));

        Assert.Equal("v2@softwareone.com", sut.Email);
    }

    [Fact]
    public void Email_falls_back_to_upn_for_v1_tokens()
    {
        // A real Entra v1.0 access token: no "preferred_username", no "email" — the identity
        // lives in "upn" / "unique_name". Regression guard: previously Email resolved to "".
        var sut = BuildFor(
            new Claim("upn", "sylwester.grabowski@softwareone.com"),
            new Claim("unique_name", "sylwester.grabowski@softwareone.com"),
            new Claim("name", "Grabowski, Sylwester"));

        Assert.Equal("sylwester.grabowski@softwareone.com", sut.Email);
    }

    [Fact]
    public void Email_falls_back_to_unique_name_when_only_that_is_present()
    {
        var sut = BuildFor(new Claim("unique_name", "legacy@softwareone.com"));

        Assert.Equal("legacy@softwareone.com", sut.Email);
    }

    [Fact]
    public void Email_trims_surrounding_whitespace()
    {
        var sut = BuildFor(new Claim("preferred_username", "  padded@softwareone.com  "));

        Assert.Equal("padded@softwareone.com", sut.Email);
    }

    [Fact]
    public void Email_skips_blank_claims_and_uses_the_next_non_blank_one()
    {
        var sut = BuildFor(
            new Claim("preferred_username", "   "),
            new Claim("upn", "real@softwareone.com"));

        Assert.Equal("real@softwareone.com", sut.Email);
    }

    [Fact]
    public void Email_is_empty_when_no_identity_claim_is_present()
    {
        var sut = BuildFor(new Claim("name", "No Email Here"));

        Assert.Equal("", sut.Email);
    }
}
