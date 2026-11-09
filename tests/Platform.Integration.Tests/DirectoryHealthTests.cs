using System.Net;
using System.Text;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Graph;
using Platform.Api.Infrastructure.Identity;

namespace Platform.Integration.Tests;

/// <summary>
/// <c>GET /api/settings/directory-health</c>: how an admin hears that the Graph app can't sign in.
/// On 2026-10-19 its client secret expired and nobody noticed for about 20 hours — every directory
/// read swallowed the failure (empty person searches, group approvals that never matched), and the
/// only trace was a Warning per search in the console log.
///
/// <para>The host runs the real <see cref="EntraIdGraphService"/> over a Graph client whose
/// credential can be made to fail like Azure.Identity's does, and whose HTTP handler answers every
/// request with an empty list — nothing leaves the process.</para>
/// </summary>
public class DirectoryHealthTests
{
    private const string ExpiredSecret =
        "ClientSecretCredential authentication failed: AADSTS7000222: The provided client secret keys for app " +
        "'b8e60444-0000-4000-8000-000000000000' are expired. Visit the Azure portal to create new keys for your " +
        "app: https://aka.ms/NewClientSecret. Trace ID: 0d6a3c1e-2f4b-4e5a-9c7d-8e9f0a1b2c3d Correlation ID: " +
        "5f2b7c9d-1e3a-4b6c-8d0e-2f4a6b8c0d1e Timestamp: 2026-10-19 00:15:10Z";

    [Fact]
    public async Task DirectoryHealth_IsAdminOnly()
    {
        using var factory = new GraphDirectoryFactory();
        using var admin = factory.CreateAdminClient();
        using var user = factory.CreateAuthenticatedClient("user@localhost", "user123");

        Assert.Equal(HttpStatusCode.Forbidden, (await user.GetAsync("/api/settings/directory-health")).StatusCode);

        var health = await admin.GetAsync("/api/settings/directory-health");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.Equal(JsonValueKind.Null, (await Deserialize(health)).GetProperty("credentialFailure").ValueKind);
    }

    [Fact]
    public async Task ExpiredGraphSecret_ShowsOnDirectoryHealth_UntilAReadGetsThrough()
    {
        using var factory = new GraphDirectoryFactory();
        using var admin = factory.CreateAdminClient();
        factory.Credential.FailWith = ExpiredSecret;

        // What the picker sees (PR #391): an outage, not "no matches".
        var search = await Deserialize(await admin.GetAsync("/api/promotions/users/search?q=mateusz"));
        Assert.True(search.GetProperty("directoryUnavailable").GetBoolean());

        // What the admin banner reads.
        var failure = (await Deserialize(await admin.GetAsync("/api/settings/directory-health")))
            .GetProperty("credentialFailure");
        Assert.Equal("AADSTS7000222", failure.GetProperty("code").GetString());
        var message = failure.GetProperty("message").GetString()!;
        Assert.Contains("are expired", message);
        Assert.DoesNotContain("Trace ID", message);
        Assert.True(failure.GetProperty("since").GetDateTimeOffset() <= failure.GetProperty("lastFailedAt").GetDateTimeOffset());

        // The secret is rotated: the next directory read gets through and the banner goes away.
        factory.Credential.FailWith = null;
        var recovered = await Deserialize(await admin.GetAsync("/api/promotions/users/search?q=mateusz"));
        Assert.False(recovered.TryGetProperty("directoryUnavailable", out _));
        Assert.Equal(JsonValueKind.Null,
            (await Deserialize(await admin.GetAsync("/api/settings/directory-health"))).GetProperty("credentialFailure").ValueKind);
    }

    private static async Task<JsonElement> Deserialize(HttpResponseMessage response)
    {
        response.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
    }

    /// <summary>The test host with Entra ID as its directory instead of the local stub.</summary>
    private sealed class GraphDirectoryFactory : TestFactory
    {
        public SwitchableCredential Credential { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton(new GraphServiceClient(new HttpClient(new EmptyDirectory()), Credential));
                services.AddSingleton<IIdentityService, EntraIdGraphService>();
            });
        }
    }

    /// <summary>The Graph app's client-secret credential: a token, or Azure.Identity's failure.</summary>
    private sealed class SwitchableCredential : TokenCredential
    {
        public volatile string? FailWith;

        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
            => FailWith is { } failure
                ? throw new AuthenticationFailedException(failure)
                : new AccessToken("test-token", DateTimeOffset.UtcNow.AddHours(1));

        public override ValueTask<AccessToken> GetTokenAsync(
            TokenRequestContext requestContext, CancellationToken cancellationToken)
            => new(GetToken(requestContext, cancellationToken));
    }

    /// <summary>A directory with nobody in it: every Graph read answers an empty collection.</summary>
    private sealed class EmptyDirectory : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"value":[]}""", Encoding.UTF8, "application/json"),
            });
    }
}
