using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Platform.Api.Features.Deployments;
using Platform.Api.Features.Deployments.Models;
using Platform.Api.Features.Promotions;
using Platform.Api.Features.Webhooks;
using Platform.Api.Infrastructure.Persistence;

namespace Platform.Api.Tests.Features.Deployments;

public class DeploymentServiceVersionsTests : IDisposable
{
    private readonly PlatformDbContext _db;
    private readonly DeploymentService _sut;

    public DeploymentServiceVersionsTests()
    {
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _db = new PlatformDbContext(options);
        _sut = new DeploymentService(
            _db,
            Substitute.For<IWebhookDispatcher>(),
            Substitute.For<IPromotionIngestHook>(),
            Substitute.For<ILogger<DeploymentService>>());
    }

    public void Dispose() => _db.Dispose();

    private DeployEvent Add(
        string version,
        DateTimeOffset deployedAt,
        string product = "acme",
        string service = "api",
        string env = "prod",
        string status = "succeeded",
        bool isRollback = false,
        string? deployerEmail = "alice@example.com")
    {
        var participants = deployerEmail is null
            ? "[]"
            : JsonSerializer.Serialize(new[] { new { role = "deployer", email = deployerEmail } });

        var e = new DeployEvent
        {
            Id = Guid.NewGuid(),
            Product = product,
            Service = service,
            Environment = env,
            Version = version,
            Status = status,
            Source = "ci",
            DeployedAt = deployedAt,
            IsRollback = isRollback,
            ParticipantsJson = participants,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        _db.DeployEvents.Add(e);
        _db.SaveChanges();
        return e;
    }

    [Fact]
    public async Task ReturnsEmpty_WhenProductOrEnvironmentMissing()
    {
        Assert.Empty(await _sut.GetVersions("", "prod", null));
        Assert.Empty(await _sut.GetVersions("acme", "", null));
    }

    [Fact]
    public async Task ReturnsDistinctVersionsForService_MostRecentFirst()
    {
        var now = DateTimeOffset.UtcNow;
        Add("v1", now.AddHours(-3));
        Add("v2", now.AddHours(-2));
        Add("v2", now.AddHours(-1)); // re-deploy of v2 — should collapse
        Add("v3", now);

        var versions = await _sut.GetVersions("acme", "prod", "api");

        Assert.Equal(new[] { "v3", "v2", "v1" }, versions.Select(v => v.Version));
    }

    [Fact]
    public async Task SkipsFailedDeploys()
    {
        var now = DateTimeOffset.UtcNow;
        Add("v1", now.AddHours(-2));
        Add("v2", now.AddHours(-1), status: "failed");

        var versions = await _sut.GetVersions("acme", "prod", "api");
        Assert.Single(versions);
        Assert.Equal("v1", versions[0].Version);
    }

    [Fact]
    public async Task CapturesDeployerEmail_FromParticipants()
    {
        Add("v1", DateTimeOffset.UtcNow, deployerEmail: "carol@example.com");

        var versions = await _sut.GetVersions("acme", "prod", "api");

        Assert.Single(versions);
        Assert.Equal("carol@example.com", versions[0].DeployerEmail);
    }

    [Fact]
    public async Task WithoutServiceFilter_ReturnsAcrossAllServices()
    {
        Add("v1", DateTimeOffset.UtcNow.AddMinutes(-10), service: "api");
        Add("v9", DateTimeOffset.UtcNow, service: "web");

        var versions = await _sut.GetVersions("acme", "prod", serviceName: null);

        Assert.Equal(2, versions.Count);
        Assert.Contains(versions, v => v.Service == "api" && v.Version == "v1");
        Assert.Contains(versions, v => v.Service == "web" && v.Version == "v9");
    }

    [Fact]
    public async Task IgnoresOtherEnvironmentsAndProducts()
    {
        Add("v1", DateTimeOffset.UtcNow, env: "staging");
        Add("v2", DateTimeOffset.UtcNow, product: "other");

        Assert.Empty(await _sut.GetVersions("acme", "prod", "api"));
    }

    [Fact]
    public async Task RespectsLimit()
    {
        for (int i = 0; i < 10; i++)
            Add($"v{i}", DateTimeOffset.UtcNow.AddMinutes(-i));

        var versions = await _sut.GetVersions("acme", "prod", "api", limit: 3);
        Assert.Equal(3, versions.Count);
    }
}
