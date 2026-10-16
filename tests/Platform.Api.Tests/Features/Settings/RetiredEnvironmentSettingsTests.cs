using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Platform.Api.Features.Settings;
using Platform.Api.Features.Settings.Models;
using Platform.Api.Infrastructure.Auth;
using Platform.Api.Infrastructure.Features;
using Platform.Api.Infrastructure.Persistence;

namespace Platform.Api.Tests.Features.Settings;

/// <summary>
/// The <c>retired</c> flag on a configured environment, as settings store and report it: it has to
/// survive the JSON round trip, rows saved before it existed have to read as "not retired", and the
/// retired set the read paths filter on has to cover every name the environment's rows can carry.
/// </summary>
public class RetiredEnvironmentSettingsTests : IDisposable
{
    private readonly PlatformDbContext _db;
    private readonly AppSettingsService _settings;

    public RetiredEnvironmentSettingsTests()
    {
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _db = new PlatformDbContext(options);

        var user = Substitute.For<ICurrentUser>();
        user.Email.Returns("admin@example.com");
        user.Name.Returns("Admin");
        _settings = new AppSettingsService(_db, user);
    }

    public void Dispose() => _db.Dispose();

    private Task SaveEnvironmentsAsync(params EnvironmentConfigDto[] environments)
        => _settings.SaveSettings(new AppSettingsDto([.. environments], [], []));

    private EnvironmentAliasResolver NewResolver()
        => new(_settings, Substitute.For<ILogger<EnvironmentAliasResolver>>());

    [Fact]
    public async Task Retired_RoundTripsThroughTheSettingsRow()
    {
        await SaveEnvironmentsAsync(
            new EnvironmentConfigDto("dev", "Development"),
            new EnvironmentConfigDto("stable", "Staging Candidate", Retired: true));

        var envs = (await _settings.GetSettings()).Environments;

        Assert.False(envs.Single(e => e.Key == "dev").Retired);
        Assert.True(envs.Single(e => e.Key == "stable").Retired);

        // Stored camelCase like every other field, so the web client reads it as `retired`.
        var raw = (await _db.PlatformSettings.AsNoTracking().SingleAsync(s => s.Key == AppSettingsService.SettingsKey)).Value;
        Assert.Contains("\"retired\":true", raw);
    }

    [Fact]
    public async Task RowsWrittenBeforeTheFlagExisted_ReadAsNotRetired()
    {
        _db.PlatformSettings.Add(new PlatformSetting
        {
            Key = AppSettingsService.SettingsKey,
            Value = """
                {"environments":[{"key":"stable","displayName":"Staging Candidate","color":null,"isProduction":false,"aliases":["release-track"]}],
                 "roles":[],"activityTemplate":[]}
                """,
        });
        await _db.SaveChangesAsync();

        var env = Assert.Single((await _settings.GetSettings()).Environments);

        Assert.Equal("stable", env.Key);
        Assert.False(env.Retired);
        Assert.Equal(["release-track"], env.Aliases);
        Assert.Empty(await _settings.GetRetiredEnvironmentKeysAsync());
    }

    [Fact]
    public async Task NothingRetired_IsAnEmptySet()
    {
        // Fresh install (built-in defaults) and an explicitly saved list alike.
        Assert.Empty(await _settings.GetRetiredEnvironmentKeysAsync());

        await SaveEnvironmentsAsync(new EnvironmentConfigDto("dev", "Development"));
        Assert.Empty(await _settings.GetRetiredEnvironmentKeysAsync());
    }

    [Fact]
    public async Task RetiredKeys_CoverTheAliases_AndMatchIgnoringCase()
    {
        await SaveEnvironmentsAsync(
            new EnvironmentConfigDto("dev", "Development", Aliases: ["develop"]),
            new EnvironmentConfigDto("stable", "Staging Candidate", Aliases: ["release-track"], Retired: true));

        var retired = await _settings.GetRetiredEnvironmentKeysAsync();

        // An un-merged alias row is the retired environment under another spelling.
        Assert.Equal(2, retired.Count);
        Assert.Contains("stable", retired);
        Assert.Contains("release-track", retired);
        Assert.Contains("STABLE", retired);
        Assert.DoesNotContain("dev", retired);
        Assert.DoesNotContain("develop", retired);
    }

    [Fact]
    public async Task Resolver_ReportsTheSameRetiredSet()
    {
        await SaveEnvironmentsAsync(
            new EnvironmentConfigDto("stable", "Staging Candidate", Aliases: ["release-track"], Retired: true));

        var retired = await NewResolver().RetiredKeysAsync();

        Assert.Equal(
            (await _settings.GetRetiredEnvironmentKeysAsync()).OrderBy(k => k),
            retired.OrderBy(k => k));
    }

    [Fact]
    public async Task AliasesOfARetiredEnvironment_StillResolveToIt()
    {
        // A straggling pipeline still posting the old name lands on the retired key, where an admin
        // can see it, instead of minting a brand-new environment.
        await SaveEnvironmentsAsync(
            new EnvironmentConfigDto("stable", "Staging Candidate", Aliases: ["release-track"], Retired: true));

        Assert.Equal("stable", await NewResolver().ResolveAsync("release-track"));
    }
}
