using System.Security.Cryptography;
using System.Text;
using Platform.Api.Features.Integrations.Flux;

namespace Platform.Api.Tests.Features.Integrations.Flux;

public class FluxSignatureTests
{
    private const string Secret = "flux-shared-secret";
    private static readonly byte[] Body = Encoding.UTF8.GetBytes("{\"reason\":\"UpgradeSucceeded\"}");

    private static string Sign(string algorithm, byte[] body, string secret)
    {
        var key = Encoding.UTF8.GetBytes(secret);
        var mac = algorithm switch
        {
            "sha256" => HMACSHA256.HashData(key, body),
            "sha384" => HMACSHA384.HashData(key, body),
            "sha512" => HMACSHA512.HashData(key, body),
            _ => throw new ArgumentOutOfRangeException(nameof(algorithm)),
        };
        return $"{algorithm}={Convert.ToHexString(mac).ToLowerInvariant()}";
    }

    [Theory]
    [InlineData("sha256")]
    [InlineData("sha384")]
    [InlineData("sha512")]
    public void ValidSignature_Matches(string algorithm)
    {
        Assert.True(FluxSignatureAuthHandler.SignatureMatches(Sign(algorithm, Body, Secret), Body, Secret));
    }

    [Fact]
    public void UppercaseHex_Matches()
    {
        var header = Sign("sha256", Body, Secret);
        Assert.True(FluxSignatureAuthHandler.SignatureMatches(header.ToUpperInvariant(), Body, Secret));
    }

    [Fact]
    public void WrongSecret_DoesNotMatch()
    {
        Assert.False(FluxSignatureAuthHandler.SignatureMatches(Sign("sha256", Body, "other"), Body, Secret));
    }

    [Fact]
    public void ChangedBody_DoesNotMatch()
    {
        var header = Sign("sha256", Body, Secret);
        var tampered = Encoding.UTF8.GetBytes("{\"reason\":\"UpgradeFailed\"}");
        Assert.False(FluxSignatureAuthHandler.SignatureMatches(header, tampered, Secret));
    }

    [Theory]
    [InlineData("")]
    [InlineData("sha256")]
    [InlineData("sha256=")]
    [InlineData("=abcd")]
    [InlineData("sha256=not-hex")]
    [InlineData("md5=00112233445566778899aabbccddeeff")]
    [InlineData("sha224=00112233445566778899aabbccddeeff00112233445566778899aabb")]
    public void MalformedOrUnsupported_DoesNotMatch(string header)
    {
        Assert.False(FluxSignatureAuthHandler.SignatureMatches(header, Body, Secret));
    }
}
