using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Platform.Api.Features.Promotions;
using Platform.Api.Features.Promotions.Models;
using Platform.Api.Features.Webhooks;
using Platform.Api.Infrastructure.Audit;
using Platform.Api.Infrastructure.Auth;
using Platform.Api.Infrastructure.Identity;
using Platform.Api.Infrastructure.Persistence;

namespace Platform.Api.Tests.Features.Promotions;

/// <summary>
/// Freeze journal: <see cref="PromotionFreezeService.GetHistoryAsync"/> derives freeze/unfreeze
/// events from the audit log. Uses the real <see cref="AuditLogger"/> so the stored JSON payload
/// is exactly what production writes.
/// </summary>
public class PromotionFreezeHistoryTests : IDisposable
{
    private readonly PlatformDbContext _db;
    private readonly ICurrentUser _currentUser = Substitute.For<ICurrentUser>();
    private readonly PromotionFreezeService _sut;

    public PromotionFreezeHistoryTests()
    {
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _db = new PlatformDbContext(options);

        _currentUser.Id.Returns("admin-id");
        _currentUser.Name.Returns("Admin User");
        _currentUser.Email.Returns("admin@example.com");
        _currentUser.IsAdmin.Returns(true);
        _currentUser.Roles.Returns(new List<string>().AsReadOnly());
        _currentUser.Groups.Returns(new List<string>().AsReadOnly());

        var audit = new AuditLogger(_db, Substitute.For<IHttpContextAccessor>());
        var auth = new PromotionApprovalAuthorizer(
            _currentUser, Substitute.For<IIdentityService>(),
            Substitute.For<ILogger<PromotionApprovalAuthorizer>>());
        var promotions = new PromotionService(
            _db, new PromotionPolicyResolver(_db), auth, _currentUser, audit,
            Substitute.For<ILogger<PromotionService>>(), Substitute.For<IWebhookDispatcher>(),
            TestOptions.Normalization(), TestEnvironmentAliases.For(_db), TestUserPreferences.For(_db),
            TestProductOverrides.For(_db), TestRoleCatalog.For(_db));
        _sut = new PromotionFreezeService(
            _db, promotions, _currentUser, audit, Substitute.For<IWebhookDispatcher>(),
            Substitute.For<ILogger<PromotionFreezeService>>());
    }

    public void Dispose() => _db.Dispose();

    private void SeedPolicy(string product, string targetEnv = "prod")
    {
        _db.PromotionPolicies.Add(new PromotionPolicy
        {
            Id = Guid.NewGuid(),
            Product = product,
            Service = null,
            SourceEnv = "staging",
            TargetEnv = targetEnv,
            ApprovalSteps = new List<ApprovalStep>(),
        });
        _db.SaveChanges();
    }

    // Audit timestamps are taken at write time; a short pause keeps the order unambiguous.
    private static Task TickAsync() => Task.Delay(15);

    [Fact]
    public async Task History_is_empty_without_freeze_events()
    {
        Assert.Empty(await _sut.GetHistoryAsync(10));
    }

    [Fact]
    public async Task History_lists_freeze_and_lift_newest_first_with_fields()
    {
        SeedPolicy("acme");
        await _sut.FreezeAsync("acme", "prod", "Release window");
        await TickAsync();
        _currentUser.Name.Returns("Other Admin");
        await _sut.LiftAsync("acme", "prod");

        var history = await _sut.GetHistoryAsync(10);

        Assert.Equal(2, history.Count);
        Assert.Equal("unfrozen", history[0].Action);
        Assert.Equal("Other Admin", history[0].Actor);
        Assert.Equal("acme", history[0].Product);
        Assert.Equal("prod", history[0].TargetEnv);
        Assert.Equal("Release window", history[0].Reason);
        Assert.Equal("frozen", history[1].Action);
        Assert.Equal("Admin User", history[1].Actor);
        Assert.Equal("acme", history[1].Product);
        Assert.Equal("prod", history[1].TargetEnv);
        Assert.Equal("Release window", history[1].Reason);
        Assert.True(history[0].At > history[1].At);
    }

    [Fact]
    public async Task History_ignores_other_audit_actions()
    {
        SeedPolicy("acme");
        _db.AuditLog.Add(new AuditEntry
        {
            Id = Guid.NewGuid(), Module = "promotions", Action = "promotion.approved",
            ActorName = "Someone", AfterState = "{\"product\":\"acme\"}",
        });
        _db.SaveChanges();
        await _sut.FreezeAsync("acme", "prod", "Why");

        var history = await _sut.GetHistoryAsync(10);

        var only = Assert.Single(history);
        Assert.Equal("frozen", only.Action);
    }

    [Fact]
    public async Task History_respects_limit_and_returns_the_latest()
    {
        SeedPolicy("acme");
        for (var i = 0; i < 3; i++)
        {
            await _sut.FreezeAsync("acme", "prod", $"Reason {i}");
            await TickAsync();
            await _sut.LiftAsync("acme", "prod");
            await TickAsync();
        }

        var history = await _sut.GetHistoryAsync(4);

        Assert.Equal(4, history.Count);
        Assert.Equal("unfrozen", history[0].Action);
        Assert.Equal("Reason 2", history[0].Reason);
        Assert.Equal("frozen", history[1].Action);
        Assert.Equal("Reason 2", history[1].Reason);
        Assert.Equal("Reason 1", history[3].Reason);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(500, 50)]
    public void ClampHistoryLimit_keeps_limit_between_one_and_fifty(int requested, int expected)
    {
        Assert.Equal(expected, PromotionFreezeService.ClampHistoryLimit(requested));
    }

    [Fact]
    public async Task Windows_list_an_active_freeze_with_an_open_end()
    {
        SeedPolicy("acme");
        var freeze = await _sut.FreezeAsync("acme", "prod", "Release window");

        var only = Assert.Single(await _sut.GetWindowsAsync(null));

        Assert.Equal("acme", only.Product);
        Assert.Equal("prod", only.TargetEnv);
        Assert.Equal("Release window", only.Reason);
        Assert.Equal("Admin User", only.FrozenBy);
        Assert.Equal(freeze.FrozenAt, only.From);
        Assert.Null(only.Until);
        Assert.Null(only.LiftedBy);
    }

    [Fact]
    public async Task Windows_rebuild_a_lifted_freeze_from_the_audit_log()
    {
        SeedPolicy("acme");
        var freeze = await _sut.FreezeAsync("acme", "prod", "Release window");
        await TickAsync();
        _currentUser.Name.Returns("Other Admin");
        await _sut.LiftAsync("acme", "prod");

        var only = Assert.Single(await _sut.GetWindowsAsync("acme"));

        Assert.Equal(freeze.FrozenAt, only.From);
        Assert.NotNull(only.Until);
        Assert.True(only.Until > only.From);
        Assert.Equal("Admin User", only.FrozenBy);
        Assert.Equal("Other Admin", only.LiftedBy);
        Assert.Equal("Release window", only.Reason);
    }

    [Fact]
    public async Task Windows_filter_by_product_and_list_newest_first()
    {
        SeedPolicy("acme");
        SeedPolicy("other");
        await _sut.FreezeAsync("acme", "prod", "First");
        await TickAsync();
        await _sut.LiftAsync("acme", "prod");
        await TickAsync();
        await _sut.FreezeAsync("other", "prod", "Other product");
        await TickAsync();
        await _sut.FreezeAsync("acme", "prod", "Second");

        var acme = await _sut.GetWindowsAsync("acme");

        Assert.Equal(2, acme.Count);
        Assert.Equal("Second", acme[0].Reason);
        Assert.Null(acme[0].Until);
        Assert.Equal("First", acme[1].Reason);
        Assert.NotNull(acme[1].Until);
        Assert.Equal(3, (await _sut.GetWindowsAsync(null)).Count);
    }

    [Fact]
    public async Task Windows_skip_unfrozen_entries_without_a_pair_or_start()
    {
        _db.AuditLog.AddRange(
            new AuditEntry
            {
                Id = Guid.NewGuid(), Module = "promotions", Action = "promotion.unfrozen",
                ActorName = "Admin User", AfterState = "{\"product\":\"acme\",\"targetEnv\":\"prod\"}",
            },
            new AuditEntry
            {
                Id = Guid.NewGuid(), Module = "promotions", Action = "promotion.unfrozen",
                ActorName = "Admin User", AfterState = "not json",
            });
        _db.SaveChanges();

        Assert.Empty(await _sut.GetWindowsAsync(null));
    }

    [Fact]
    public async Task History_and_windows_carry_the_service_of_a_service_freeze()
    {
        SeedPolicy("acme");
        _db.DeployEvents.Add(new Platform.Api.Features.Deployments.Models.DeployEvent
        {
            Id = Guid.NewGuid(), Product = "acme", Service = "api", Environment = "staging", Version = "v1",
            Status = "succeeded", Source = "ci", DeployedAt = DateTimeOffset.UtcNow, ParticipantsJson = "[]",
            CreatedAt = DateTimeOffset.UtcNow,
        });
        _db.SaveChanges();
        await _sut.FreezeAsync("acme", "prod", "api window", service: "api");
        await TickAsync();
        await _sut.FreezeAsync("acme", "prod", "whole product");
        await TickAsync();
        await _sut.LiftAsync("acme", "prod", service: "api");

        var history = await _sut.GetHistoryAsync(10);
        var windows = await _sut.GetWindowsAsync("acme");

        Assert.Equal(3, history.Count);
        Assert.Equal(("unfrozen", "api"), (history[0].Action, history[0].Service));
        Assert.Equal(("frozen", (string?)null), (history[1].Action, history[1].Service));
        Assert.Equal(("frozen", "api"), (history[2].Action, history[2].Service));
        Assert.Equal(2, windows.Count);
        Assert.Null(windows[0].Service);
        Assert.Null(windows[0].Until);
        Assert.Equal("api", windows[1].Service);
        Assert.NotNull(windows[1].Until);
    }

    [Fact]
    public async Task History_and_windows_treat_a_legacy_payload_without_service_as_whole_product()
    {
        _db.AuditLog.Add(new AuditEntry
        {
            Id = Guid.NewGuid(), Module = "promotions", Action = "promotion.unfrozen", ActorName = "Admin User",
            AfterState = "{\"product\":\"acme\",\"targetEnv\":\"prod\",\"reason\":\"old\",\"frozenBy\":\"X\",\"frozenAt\":\"2026-01-01T08:00:00Z\"}",
        });
        _db.SaveChanges();

        Assert.Null(Assert.Single(await _sut.GetHistoryAsync(10)).Service);
        Assert.Null(Assert.Single(await _sut.GetWindowsAsync(null)).Service);
    }

    [Fact]
    public void History_reason_is_null_when_payload_has_none()
    {
        _db.AuditLog.Add(new AuditEntry
        {
            Id = Guid.NewGuid(), Module = "promotions", Action = "promotion.frozen",
            ActorName = "Admin User", AfterState = "{\"product\":\"acme\",\"targetEnv\":\"prod\"}",
        });
        _db.SaveChanges();

        var only = Assert.Single(_sut.GetHistoryAsync(10).Result);
        Assert.Null(only.Reason);
    }
}
