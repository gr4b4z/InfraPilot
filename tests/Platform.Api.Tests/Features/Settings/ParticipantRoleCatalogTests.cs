using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Platform.Api.Features.Settings;
using Platform.Api.Features.Settings.Models;
using Platform.Api.Infrastructure.Auth;
using Platform.Api.Infrastructure.Persistence;

namespace Platform.Api.Tests.Features.Settings;

/// <summary>
/// Unit tests for the participant-role vocabulary reader. The rules that matter to callers: keys
/// are canonicalised (so a lookup never depends on how an admin typed them), configured order is
/// preserved (the pickers render in it), and an explicitly emptied list is honoured rather than
/// quietly refilled with defaults — the web client reads the same list, so a server-side fallback
/// would put the two out of step about which roles exist.
/// </summary>
public class ParticipantRoleCatalogTests : IDisposable
{
    private readonly PlatformDbContext _db;
    private readonly ParticipantRoleCatalog _sut;
    private readonly AppSettingsService _settings;

    public ParticipantRoleCatalogTests()
    {
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _db = new PlatformDbContext(options);

        var currentUser = Substitute.For<ICurrentUser>();
        currentUser.Email.Returns("admin@example.com");
        currentUser.Name.Returns("Admin");

        _settings = new AppSettingsService(_db, currentUser);
        _sut = new ParticipantRoleCatalog(_settings);
    }

    public void Dispose() => _db.Dispose();

    private Task SaveRolesAsync(params (string Key, string DisplayName)[] roles)
        => _settings.SaveSettings(new AppSettingsDto(
            Environments: [],
            Roles: roles.Select(r => new RoleConfigDto(r.Key, r.DisplayName)).ToList(),
            ActivityTemplate: []));

    [Fact]
    public async Task FreshDb_ReturnsTheBuiltInDefaults()
    {
        var keys = await _sut.GetCanonicalKeysAsync();
        // Covers every role the deploy-ingest and Jira paths emit, so a producer-sent role isn't
        // flagged as unconfigured on a fresh install.
        Assert.Equal(
            new[] { "triggered-by", "author", "reviewer", "qa", "qa-owner", "assignee", "reporter" },
            keys);
    }

    [Fact]
    public async Task Keys_AreCanonicalised_AndKeepConfiguredOrder()
    {
        await SaveRolesAsync(("QA Owner", "QA owner"), ("triggeredBy", "Triggered by"));

        var keys = await _sut.GetCanonicalKeysAsync();
        Assert.Equal(new[] { "qa-owner", "triggered-by" }, keys);
    }

    [Fact]
    public async Task Keys_DropBlanksAndDuplicates()
    {
        await SaveRolesAsync(("qa", "QA"), ("QA", "Quality"), ("   ", "Blank"), ("reviewer", "Reviewer"));

        var keys = await _sut.GetCanonicalKeysAsync();
        Assert.Equal(new[] { "qa", "reviewer" }, keys);
    }

    [Fact]
    public async Task IsConfigured_MatchesRegardlessOfCasingOrSeparators()
    {
        await SaveRolesAsync(("qa-owner", "QA owner"));

        Assert.True(await _sut.IsConfiguredAsync("qa-owner"));
        Assert.True(await _sut.IsConfiguredAsync("QA Owner"));
        Assert.True(await _sut.IsConfiguredAsync("qa_owner"));
        Assert.True(await _sut.IsConfiguredAsync("qaOwner"));

        Assert.False(await _sut.IsConfiguredAsync("qa"));
        Assert.False(await _sut.IsConfiguredAsync(""));
        Assert.False(await _sut.IsConfiguredAsync(null));
    }

    [Fact]
    public async Task EmptyConfiguredList_IsAnEmptyVocabulary_NotTheDefaults()
    {
        await SaveRolesAsync();

        Assert.Empty(await _sut.GetCanonicalKeysAsync());
        Assert.False(await _sut.IsConfiguredAsync("qa"));
    }

    [Fact]
    public void RejectionMessage_NamesTheRoleAndWhereToFixIt()
    {
        var message = ParticipantRoleCatalog.RejectionMessage("QA Owner", new[] { "qa", "reviewer" });

        Assert.Contains("qa-owner", message);
        Assert.Contains("qa, reviewer", message);
        Assert.Contains("Participant Roles", message);
    }
}
