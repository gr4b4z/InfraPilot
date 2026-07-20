using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Platform.Api.Features.Deployments;
using Platform.Api.Features.Deployments.Models;
using Platform.Api.Features.Promotions;
using Platform.Api.Features.Webhooks;
using Platform.Api.Infrastructure.Persistence;

namespace Platform.Api.Tests.Features.Deployments;

/// <summary>
/// Tests for <see cref="DeploymentService.CreateManualEventAsync"/> — the human/agent-authored
/// manual deployment path built on top of the CI ingest pipeline.
/// </summary>
public class DeploymentServiceManualTests : IDisposable
{
    private readonly PlatformDbContext _db;
    private readonly IWebhookDispatcher _webhooks = Substitute.For<IWebhookDispatcher>();
    private readonly DeploymentService _sut;

    private static readonly ManualDeployActor Actor =
        new("3d56-oid", "Grabowski, Sylwester", "sylwester.grabowski@softwareone.com", "user");

    public DeploymentServiceManualTests()
    {
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _db = new PlatformDbContext(options);
        _sut = new DeploymentService(
            _db, _webhooks, Substitute.For<IPromotionIngestHook>(),
            TestOptions.Normalization(), Substitute.For<ILogger<DeploymentService>>());
    }

    public void Dispose() => _db.Dispose();

    private DeployEvent SeedLatest(
        string version = "v1.0.0",
        string status = "succeeded",
        string source = "ci",
        string participantsJson = "[]",
        string referencesJson = "[]")
    {
        var e = new DeployEvent
        {
            Id = Guid.NewGuid(),
            Product = "acme",
            Service = "api",
            Environment = "production",
            Version = version,
            Status = status,
            Source = source,
            DeployedAt = new DateTimeOffset(2026, 07, 01, 12, 0, 0, TimeSpan.Zero),
            CreatedAt = new DateTimeOffset(2026, 07, 01, 12, 0, 0, TimeSpan.Zero),
            ReferencesJson = referencesJson,
            ParticipantsJson = participantsJson,
            MetadataJson = "{}",
        };
        _db.DeployEvents.Add(e);
        _db.SaveChanges();
        return e;
    }

    private static CreateManualDeployRequest Req(string version = "v1.1.0", string? status = null, string note = "manual fix") =>
        new(Product: "acme", Service: "api", Environment: "production", Version: version, Note: note, Status: status);

    [Fact]
    public async Task CreatesNewEvent_StampedManual_AndAttributedToCaller()
    {
        SeedLatest(version: "v1.0.0");

        var ev = await _sut.CreateManualEventAsync(Req(version: "v1.1.0"), Actor);

        Assert.Equal("v1.1.0", ev.Version);
        Assert.Equal("manual", ev.Source);
        // PreviousVersion is derived from the prior latest by the reused ingest path.
        Assert.Equal("v1.0.0", ev.PreviousVersion);
        // Attribution: triggered-by is the caller, not CI.
        var triggeredBy = ev.Participants.Single(p => p.Role == "triggered-by");
        Assert.Equal("sylwester.grabowski@softwareone.com", triggeredBy.Email);
        // Two events now exist; the manual one is the most recent.
        Assert.Equal(2, _db.DeployEvents.Count());
    }

    [Fact]
    public async Task Status_DefaultsToLatest_WhenOmitted_AndIsOverridable()
    {
        SeedLatest(status: "succeeded");

        var inherited = await _sut.CreateManualEventAsync(Req(version: "v2", status: null), Actor);
        Assert.Equal("succeeded", inherited.Status);

        var overridden = await _sut.CreateManualEventAsync(Req(version: "v3", status: "failed"), Actor);
        Assert.Equal("failed", overridden.Status);
    }

    [Fact]
    public async Task DropsInheritedTriggeredBy_AndReplacesWithCaller()
    {
        // The latest event was triggered by CI; the manual entry must not keep CI as triggered-by.
        SeedLatest(participantsJson:
            "[{\"role\":\"triggered-by\",\"displayName\":\"CI Bot\",\"email\":\"ci@acme.com\"}," +
            "{\"role\":\"reviewer\",\"displayName\":\"Bob\",\"email\":\"bob@acme.com\"}]");

        var ev = await _sut.CreateManualEventAsync(Req(), Actor);

        var triggeredBy = ev.Participants.Where(p => p.Role == "triggered-by").ToList();
        Assert.Single(triggeredBy);
        Assert.Equal("sylwester.grabowski@softwareone.com", triggeredBy[0].Email);
        // Non-triggered-by participants are carried over.
        Assert.Contains(ev.Participants, p => p.Role == "reviewer" && p.Email == "bob@acme.com");
    }

    [Fact]
    public async Task RecordsProvenanceInMetadata()
    {
        var latest = SeedLatest();

        var ev = await _sut.CreateManualEventAsync(Req(note: "hotfix INC-42"), Actor);

        Assert.NotNull(ev.Metadata);
        Assert.Equal("hotfix INC-42", ev.Metadata!["note"].ToString());
        Assert.Equal(latest.Id.ToString(), ev.Metadata!["basedOnEventId"].ToString());
    }

    [Fact]
    public async Task NoPriorDeployment_Throws()
    {
        // Nothing seeded for this target.
        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => _sut.CreateManualEventAsync(Req(), Actor));
    }

    [Fact]
    public async Task FiresDeploymentWebhook_LikeCi()
    {
        SeedLatest();

        await _sut.CreateManualEventAsync(Req(), Actor);

        await _webhooks.Received(1).DispatchAsync(
            "deployment.created",
            Arg.Any<object>(),
            Arg.Any<WebhookEventFilters>());
    }
}
