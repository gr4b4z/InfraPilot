using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Platform.Api.Features.Deployments;
using Platform.Api.Features.Deployments.Models;
using Platform.Api.Features.Webhooks;
using Platform.Api.Infrastructure.Persistence;

namespace Platform.Api.Tests.Features.Deployments;

public class DeploymentServiceDedupTests : IDisposable
{
    private readonly PlatformDbContext _db;
    private readonly DeploymentService _sut;

    public DeploymentServiceDedupTests()
    {
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _db = new PlatformDbContext(options);

        var webhooks = Substitute.For<IWebhookDispatcher>();
        _sut = new DeploymentService(_db, webhooks, Substitute.For<ILogger<DeploymentService>>());
    }

    public void Dispose() => _db.Dispose();

    private DeployEvent MakeEvent(
        string product = "marketplace",
        string service = "api",
        string environment = "production",
        string version = "1.2.3",
        DateTimeOffset? deployedAt = null,
        string source = "github-actions",
        DateTimeOffset? createdAt = null)
    {
        return new DeployEvent
        {
            Id = Guid.NewGuid(),
            Product = product,
            Service = service,
            Environment = environment,
            Version = version,
            Status = "succeeded",
            Source = source,
            DeployedAt = deployedAt ?? new DateTimeOffset(2026, 04, 01, 12, 0, 0, TimeSpan.Zero),
            CreatedAt = createdAt ?? DateTimeOffset.UtcNow,
            ReferencesJson = "[]",
            ParticipantsJson = "[]",
            MetadataJson = "{}",
        };
    }

    [Fact]
    public async Task CountDuplicates_three_identical_rows_reports_one_group_and_two_rows()
    {
        var t = new DateTimeOffset(2026, 04, 10, 9, 30, 0, TimeSpan.Zero);
        _db.DeployEvents.AddRange(
            MakeEvent(deployedAt: t, createdAt: t.AddSeconds(1)),
            MakeEvent(deployedAt: t, createdAt: t.AddSeconds(2)),
            MakeEvent(deployedAt: t, createdAt: t.AddSeconds(3)));
        await _db.SaveChangesAsync();

        var (groups, rows) = await _sut.CountDuplicates();

        Assert.Equal(1, groups);
        Assert.Equal(2, rows);
    }

    [Fact]
    public async Task RemoveDuplicates_keeps_the_earliest_created_row_in_each_group()
    {
        var t = new DateTimeOffset(2026, 04, 10, 9, 30, 0, TimeSpan.Zero);
        var keeper = MakeEvent(deployedAt: t, createdAt: t);            // earliest
        var dup1 = MakeEvent(deployedAt: t, createdAt: t.AddSeconds(5));
        var dup2 = MakeEvent(deployedAt: t, createdAt: t.AddMinutes(3));
        _db.DeployEvents.AddRange(keeper, dup1, dup2);
        await _db.SaveChangesAsync();

        var (groups, rows) = await _sut.RemoveDuplicates();

        Assert.Equal(1, groups);
        Assert.Equal(2, rows);

        var remaining = await _db.DeployEvents.ToListAsync();
        Assert.Single(remaining);
        Assert.Equal(keeper.Id, remaining[0].Id);
    }

    [Fact]
    public async Task CountDuplicates_distinct_rows_reports_zero()
    {
        _db.DeployEvents.AddRange(
            MakeEvent(version: "1.0.0"),
            MakeEvent(version: "1.0.1"),
            MakeEvent(version: "1.0.2"));
        await _db.SaveChangesAsync();

        var (groups, rows) = await _sut.CountDuplicates();

        Assert.Equal(0, groups);
        Assert.Equal(0, rows);
    }

    [Fact]
    public async Task RemoveDuplicates_no_duplicates_is_noop()
    {
        _db.DeployEvents.AddRange(
            MakeEvent(version: "1.0.0"),
            MakeEvent(version: "1.0.1"));
        await _db.SaveChangesAsync();

        var (groups, rows) = await _sut.RemoveDuplicates();

        Assert.Equal(0, groups);
        Assert.Equal(0, rows);
        Assert.Equal(2, await _db.DeployEvents.CountAsync());
    }

    [Fact]
    public async Task Rows_differing_only_in_Source_are_not_duplicates()
    {
        var t = new DateTimeOffset(2026, 04, 10, 9, 30, 0, TimeSpan.Zero);
        _db.DeployEvents.AddRange(
            MakeEvent(deployedAt: t, source: "github-actions"),
            MakeEvent(deployedAt: t, source: "azure-devops"));
        await _db.SaveChangesAsync();

        var (groups, rows) = await _sut.CountDuplicates();

        Assert.Equal(0, groups);
        Assert.Equal(0, rows);
    }

    [Fact]
    public async Task Multiple_duplicate_groups_are_cleaned_in_one_call()
    {
        var t1 = new DateTimeOffset(2026, 04, 10, 9, 30, 0, TimeSpan.Zero);
        var t2 = new DateTimeOffset(2026, 04, 11, 15, 0, 0, TimeSpan.Zero);

        // Group A: 2 rows with same key
        _db.DeployEvents.AddRange(
            MakeEvent(service: "api", deployedAt: t1, createdAt: t1),
            MakeEvent(service: "api", deployedAt: t1, createdAt: t1.AddSeconds(1)));

        // Group B: 3 rows with same key
        _db.DeployEvents.AddRange(
            MakeEvent(service: "ui", deployedAt: t2, createdAt: t2),
            MakeEvent(service: "ui", deployedAt: t2, createdAt: t2.AddSeconds(1)),
            MakeEvent(service: "ui", deployedAt: t2, createdAt: t2.AddSeconds(2)));

        // Lone row — not a duplicate
        _db.DeployEvents.Add(MakeEvent(service: "worker"));
        await _db.SaveChangesAsync();

        var (groups, rows) = await _sut.RemoveDuplicates();

        Assert.Equal(2, groups);
        Assert.Equal(3, rows); // 1 from A + 2 from B
        Assert.Equal(3, await _db.DeployEvents.CountAsync()); // 2 kept + 1 untouched
    }
}
