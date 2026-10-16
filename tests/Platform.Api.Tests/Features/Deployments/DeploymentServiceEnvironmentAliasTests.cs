using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Platform.Api.Features.Deployments;
using Platform.Api.Features.Deployments.Models;
using Platform.Api.Features.Promotions;
using Platform.Api.Features.Settings;
using Platform.Api.Features.Settings.Models;
using Platform.Api.Features.Webhooks;
using Platform.Api.Infrastructure.Auth;
using Platform.Api.Infrastructure.Persistence;

namespace Platform.Api.Tests.Features.Deployments;

/// <summary>
/// Ingest-side tests for environment aliases: a pipeline that calls production "prod" has to store
/// against whichever key an admin curated, or the same environment arrives as two — which is the
/// condition the alias list exists to prevent.
/// </summary>
public class DeploymentServiceEnvironmentAliasTests : IDisposable
{
    private readonly PlatformDbContext _db;
    private readonly AppSettingsService _settings;
    private readonly DeploymentService _sut;

    public DeploymentServiceEnvironmentAliasTests()
    {
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _db = new PlatformDbContext(options);

        var user = Substitute.For<ICurrentUser>();
        user.Id.Returns("admin-1");
        user.Name.Returns("Ada Admin");
        user.Email.Returns("ada@example.com");

        _settings = new AppSettingsService(_db, user);
        _sut = new DeploymentService(
            _db,
            Substitute.For<IWebhookDispatcher>(),
            Substitute.For<IPromotionIngestHook>(),
            TestOptions.Normalization(),
            new EnvironmentAliasResolver(_settings, Substitute.For<ILogger<EnvironmentAliasResolver>>()),
            TestUserPreferences.For(_db),
            TestServiceDeletions.For(_db),
            TestProductOverrides.For(_db),
            Substitute.For<ILogger<DeploymentService>>());
    }

    public void Dispose() => _db.Dispose();

    private Task ConfigureAsync(params EnvironmentConfigDto[] environments)
        => _settings.SaveSettings(new AppSettingsDto([.. environments], [], []));

    private static CreateDeployEventDto Deploy(string environment, string version = "1.0.0")
        => new(
            Product: "marketplace",
            Service: "api",
            Environment: environment,
            Version: version,
            Source: "github-actions",
            DeployedAt: new DateTimeOffset(2026, 08, 01, 10, 0, 0, TimeSpan.Zero),
            References: null,
            Participants: null,
            Metadata: null);

    [Fact]
    public async Task Ingest_StoresAnAliasUnderTheCanonicalKey()
    {
        await ConfigureAsync(new EnvironmentConfigDto("prod", "Production", null, true, ["production", "productions"]));

        var stored = await _sut.IngestEvent(Deploy("productions"));

        Assert.Equal("prod", stored.Environment);
    }

    [Fact]
    public async Task Ingest_LeavesUnconfiguredEnvironmentsAlone()
    {
        await ConfigureAsync(new EnvironmentConfigDto("prod", "Production", null, true, ["production"]));

        var stored = await _sut.IngestEvent(Deploy("cloudiq-test"));

        // Curating the list must not become a precondition for ingesting at all.
        Assert.Equal("cloudiq-test", stored.Environment);
    }

    [Fact]
    public async Task Ingest_AliasedDeploysShareOnePreviousVersionChain()
    {
        // The reason this matters beyond tidiness: PreviousVersion is derived from the latest event
        // for (product, service, environment). Two names for one environment means two chains, and
        // every deploy reports the wrong predecessor.
        await ConfigureAsync(new EnvironmentConfigDto("prod", "Production", null, true, ["production"]));

        await _sut.IngestEvent(Deploy("prod", "1.0.0"));
        var second = await _sut.IngestEvent(Deploy("production", "1.1.0") with
        {
            DeployedAt = new DateTimeOffset(2026, 08, 02, 10, 0, 0, TimeSpan.Zero),
        });

        Assert.Equal("prod", second.Environment);
        Assert.Equal("1.0.0", second.PreviousVersion);
        Assert.Equal(2, await _db.DeployEvents.CountAsync(e => e.Environment == "prod"));
    }

    [Fact]
    public async Task Ingest_AliasedReplayIsRecognisedAsTheSameEvent()
    {
        // The replay key includes the environment, so a retry that spells it differently has to
        // resolve to the same row rather than inserting a duplicate.
        await ConfigureAsync(new EnvironmentConfigDto("prod", "Production", null, true, ["production"]));

        var first = await _sut.IngestEventWithResult(Deploy("prod"));
        var retry = await _sut.IngestEventWithResult(Deploy("production"));

        Assert.False(first.Replayed);
        Assert.True(retry.Replayed);
        Assert.Equal(first.Event.Id, retry.Event.Id);
        Assert.Equal(1, await _db.DeployEvents.CountAsync());
    }

    [Fact]
    public async Task ManualEntry_ResolvesTheAliasBeforeLookingForTheEventToBaseOn()
    {
        // A manual entry is based on the latest event for the target. Asking for "production" when
        // the history is stored under "prod" would report "no prior deployment" for a service with
        // plenty.
        await ConfigureAsync(new EnvironmentConfigDto("prod", "Production", null, true, ["production"]));
        await _sut.IngestEvent(Deploy("prod", "1.0.0"));

        var created = await _sut.CreateManualEventAsync(
            new CreateManualDeployRequest("marketplace", "api", "production", "1.2.0", "hotfix", null),
            new ManualDeployActor("oid-1", "Ada Admin", "ada@example.com", "user"));

        Assert.Equal("prod", created.Environment);
        Assert.Equal("1.0.0", created.PreviousVersion);
    }
}
