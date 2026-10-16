using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Platform.Api.Features.Settings;
using Platform.Api.Features.Settings.Models;
using Platform.Api.Infrastructure.Auth;
using Platform.Api.Infrastructure.Features;
using Platform.Api.Infrastructure.Persistence;

namespace Platform.Api.Tests.Features.Settings;

/// <summary>
/// Tests for the one-time backfill of built-in participant roles into an already-saved settings row.
///
/// The rules that matter: it only ever adds, it never reorders or relabels what an admin arranged, and
/// it runs exactly once — so a role an admin deliberately removes stays removed.
/// </summary>
public class ParticipantRoleSeederTests : IDisposable
{
    private readonly PlatformDbContext _db;
    private readonly AppSettingsService _settings;

    public ParticipantRoleSeederTests()
    {
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _db = new PlatformDbContext(options);

        var currentUser = Substitute.For<ICurrentUser>();
        currentUser.Email.Returns("admin@example.com");
        currentUser.Name.Returns("Admin");
        _settings = new AppSettingsService(_db, currentUser);
    }

    public void Dispose() => _db.Dispose();

    private Task SaveRolesAsync(params (string Key, string DisplayName)[] roles)
        => _settings.SaveSettings(new AppSettingsDto(
            Environments: [new("development", "Development", "#2563eb")],
            Roles: roles.Select(r => new RoleConfigDto(r.Key, r.DisplayName)).ToList(),
            ActivityTemplate: []));

    [Fact]
    public async Task AddsOnlyTheMissingRoles_AndKeepsWhatWasConfigured()
    {
        // An install that saved its settings before the vocabulary grew: it has "qa" (relabelled) and a
        // role of its own, and is missing the three the Jira path now sends.
        await SaveRolesAsync(("qa", "Quality"), ("release-captain", "Release captain"));

        await ParticipantRoleSeeder.MergeDefaults(_db);

        var roles = (await _settings.GetSettings()).Roles;

        // The admin's own entries come first, untouched — including the label they chose.
        Assert.Equal("qa", roles[0].Key);
        Assert.Equal("Quality", roles[0].DisplayName);
        Assert.Equal("release-captain", roles[1].Key);

        // The missing built-ins are appended; "qa" is not duplicated.
        Assert.Single(roles, r => r.Key == "qa");
        Assert.Contains(roles, r => r.Key == "qa-owner");
        Assert.Contains(roles, r => r.Key == "assignee");
        Assert.Contains(roles, r => r.Key == "reporter");
    }

    [Fact]
    public async Task RunsOnce_SoARemovedRoleStaysRemoved()
    {
        await SaveRolesAsync(("qa", "QA"));
        await ParticipantRoleSeeder.MergeDefaults(_db);
        Assert.Contains((await _settings.GetSettings()).Roles, r => r.Key == "reporter");

        // The admin decides they don't want it.
        await SaveRolesAsync(("qa", "QA"));

        // A second pass must not put it back — otherwise the setting could never be made to stick.
        await ParticipantRoleSeeder.MergeDefaults(_db);

        var roles = (await _settings.GetSettings()).Roles;
        Assert.DoesNotContain(roles, r => r.Key == "reporter");
        Assert.Single(roles);
    }

    [Fact]
    public async Task NoSettingsRow_MarksItselfDoneWithoutWritingSettings()
    {
        // With no row the install already reads Defaults, which include every built-in role.
        await ParticipantRoleSeeder.MergeDefaults(_db);

        Assert.Null(await _db.PlatformSettings
            .FirstOrDefaultAsync(s => s.Key == AppSettingsService.SettingsKey));
        Assert.NotNull(await _db.PlatformSettings
            .FirstOrDefaultAsync(s => s.Key == ParticipantRoleSeeder.MarkerKey));
    }

    [Fact]
    public async Task MalformedSettingsRow_IsLeftAlone()
    {
        _db.PlatformSettings.Add(new PlatformSetting
        {
            Key = AppSettingsService.SettingsKey,
            Value = "{ not json",
            UpdatedAt = DateTimeOffset.UtcNow,
            UpdatedBy = "test",
        });
        await _db.SaveChangesAsync();

        await ParticipantRoleSeeder.MergeDefaults(_db);

        // AppSettingsService already serves Defaults for an unparseable row; rewriting it here would be
        // guesswork about what the admin meant.
        var row = await _db.PlatformSettings.FirstAsync(s => s.Key == AppSettingsService.SettingsKey);
        Assert.Equal("{ not json", row.Value);
    }

    [Fact]
    public async Task AlreadyCompleteVocabulary_IsUnchanged()
    {
        await SaveRolesAsync([.. AppSettingsService.DefaultRoles.Select(r => (r.Key, r.DisplayName))]);
        var before = (await _settings.GetSettings()).Roles.Select(r => r.Key).ToList();

        await ParticipantRoleSeeder.MergeDefaults(_db);

        Assert.Equal(before, (await _settings.GetSettings()).Roles.Select(r => r.Key));
    }
}
