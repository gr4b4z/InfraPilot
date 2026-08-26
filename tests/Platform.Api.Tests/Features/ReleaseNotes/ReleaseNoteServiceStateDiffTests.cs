using Microsoft.EntityFrameworkCore;
using Platform.Api.Features.Deployments.Models;
using Platform.Api.Features.ReleaseNotes;
using Platform.Api.Infrastructure.Persistence;

namespace Platform.Api.Tests.Features.ReleaseNotes;

/// <summary>
/// A release note is a diff of deployed state, not a log of deploy events. These cover the
/// difference: a pipeline that re-runs and re-posts the version already live manufactures a fresh
/// event every time (its <c>deployedAt</c> defeats the ingest replay key), and each one used to
/// become another entry in another note — the same release announced over and over.
/// </summary>
public class ReleaseNoteServiceStateDiffTests : IDisposable
{
    private static readonly DateTimeOffset Base = new(2026, 08, 17, 08, 0, 0, TimeSpan.Zero);

    private readonly PlatformDbContext _db;
    private readonly ReleaseNoteService _sut;

    public ReleaseNoteServiceStateDiffTests()
    {
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _db = new PlatformDbContext(options);
        _sut = new ReleaseNoteService(_db);
    }

    public void Dispose() => _db.Dispose();

    private void Add(
        string service, string version, DateTimeOffset deployedAt,
        string? previousVersion = null, string status = "succeeded",
        string product = "mpt-extensions", string environment = "dev")
    {
        _db.DeployEvents.Add(new DeployEvent
        {
            Id = Guid.NewGuid(),
            Product = product,
            Service = service,
            Environment = environment,
            Version = version,
            PreviousVersion = previousVersion,
            Status = status,
            Source = "helm-deploy",
            DeployedAt = deployedAt,
            CreatedAt = deployedAt,
            ReferencesJson = "[]",
            ParticipantsJson = "[]",
            MetadataJson = "{}",
        });
        _db.SaveChanges();
    }

    [Fact]
    public async Task Redeploy_of_the_version_already_live_is_not_reported()
    {
        // Live before the window, then the pipeline re-runs and re-posts the same version.
        Add("mpt-nav-stats", "1.0.3-geeff42ec", Base);
        Add("mpt-nav-stats", "1.0.3-geeff42ec", Base.AddHours(1), previousVersion: "2.0.0");

        var raw = await _sut.GetRawPreview(
            "mpt-extensions", "dev", Base.AddMinutes(30), Base.AddHours(2));

        Assert.Empty(raw.Services);
    }

    [Fact]
    public async Task A_stale_sender_asserted_previousVersion_does_not_rescue_the_entry()
    {
        // The real payload that duplicated: version never moved, but the sender reported a fixed
        // predecessor on every run, so version != previousVersion and any check against that field
        // would have passed the entry straight through.
        Add("mpt-nav-stats", "1.0.3-geeff42ec", Base, previousVersion: "2.0.0");
        foreach (var offset in new[] { 1, 2, 3, 4 })
            Add("mpt-nav-stats", "1.0.3-geeff42ec", Base.AddHours(offset), previousVersion: "2.0.0");

        var raw = await _sut.GetRawPreview(
            "mpt-extensions", "dev", Base.AddMinutes(30), Base.AddHours(5));

        Assert.Empty(raw.Services);
    }

    [Fact]
    public async Task A_real_version_change_is_reported()
    {
        Add("mpt-extension-producthub", "6.0.22-g30a95dc5", Base);
        Add("mpt-extension-producthub", "6.0.23-g82be2f77", Base.AddHours(1));

        var raw = await _sut.GetRawPreview(
            "mpt-extensions", "dev", Base.AddMinutes(30), Base.AddHours(2));

        var entry = Assert.Single(raw.Services);
        Assert.Equal("mpt-extension-producthub", entry.Service);
        Assert.Equal("6.0.23-g82be2f77", entry.CurrentVersion);
    }

    [Fact]
    public async Task A_first_ever_deploy_is_reported()
    {
        Add("mpt-extension-new", "1.0.0", Base.AddHours(1));

        var raw = await _sut.GetRawPreview(
            "mpt-extensions", "dev", Base.AddMinutes(30), Base.AddHours(2));

        Assert.Single(raw.Services);
    }

    [Fact]
    public async Task A_rollback_to_a_different_version_is_reported()
    {
        Add("mpt-extension-adobe", "6.0.186-gb24f370b", Base);
        Add("mpt-extension-adobe", "6.0.184-g30a9069e", Base.AddHours(1));

        var raw = await _sut.GetRawPreview(
            "mpt-extensions", "dev", Base.AddMinutes(30), Base.AddHours(2));

        var entry = Assert.Single(raw.Services);
        Assert.Equal("6.0.184-g30a9069e", entry.CurrentVersion);
    }

    [Fact]
    public async Task Churn_ending_where_it_started_is_not_reported()
    {
        // Deployed away and back inside one window: the channel has nothing new to hear.
        Add("mpt-extension-delivery", "6.0.121", Base);
        Add("mpt-extension-delivery", "6.0.122", Base.AddHours(1));
        Add("mpt-extension-delivery", "6.0.121", Base.AddHours(2));

        var raw = await _sut.GetRawPreview(
            "mpt-extensions", "dev", Base.AddMinutes(30), Base.AddHours(3));

        Assert.Empty(raw.Services);
    }

    [Fact]
    public async Task Only_the_unchanged_service_is_dropped_from_a_mixed_window()
    {
        Add("mpt-nav-stats", "1.0.3", Base);
        Add("mpt-nav-stats", "1.0.3", Base.AddHours(1));
        Add("mpt-extension-producthub", "6.0.22", Base);
        Add("mpt-extension-producthub", "6.0.23", Base.AddHours(1));

        var raw = await _sut.GetRawPreview(
            "mpt-extensions", "dev", Base.AddMinutes(30), Base.AddHours(2));

        var entry = Assert.Single(raw.Services);
        Assert.Equal("mpt-extension-producthub", entry.Service);
    }

    [Fact]
    public async Task The_baseline_ignores_other_environments()
    {
        // dev is on 1.0.3 and stays there; test moving to 1.0.3 in the window is still news.
        Add("mpt-nav-stats", "1.0.3", Base, environment: "dev");
        Add("mpt-nav-stats", "1.0.2", Base, environment: "test");
        Add("mpt-nav-stats", "1.0.3", Base.AddHours(1), environment: "test");

        var raw = await _sut.GetRawPreview(
            "mpt-extensions", "test", Base.AddMinutes(30), Base.AddHours(2));

        Assert.Single(raw.Services);
    }

    [Fact]
    public async Task A_failed_earlier_deploy_does_not_count_as_the_live_baseline()
    {
        Add("mpt-extension-adobe", "6.0.1", Base);
        Add("mpt-extension-adobe", "6.0.2", Base.AddMinutes(5), status: "failed");
        Add("mpt-extension-adobe", "6.0.2", Base.AddHours(1));

        var raw = await _sut.GetRawPreview(
            "mpt-extensions", "dev", Base.AddMinutes(30), Base.AddHours(2));

        var entry = Assert.Single(raw.Services);
        Assert.Equal("6.0.2", entry.CurrentVersion);
    }
}
