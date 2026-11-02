using Microsoft.EntityFrameworkCore;
using Platform.Api.Features.Migrations.Models;
using Platform.Api.Infrastructure.Persistence;

namespace Platform.Api.Tests.Features.Migrations;

/// <summary>The local demo data: every flare state has an example and the seed runs only once.</summary>
public class MigrationSeedDataTests
{
    [Fact]
    public async Task Seed_BuildsSchemaAndDataMigrationsOverTheDemoDeployments()
    {
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var db = new PlatformDbContext(options);
        await DeploymentSeedData.Seed(db);

        await MigrationSeedData.Seed(db);
        await MigrationSeedData.Seed(db);

        var runs = await db.MigrationRuns.ToListAsync();
        Assert.Contains(runs, r => r.Kind == MigrationKinds.Schema && r.TotalCount > 0);
        Assert.Contains(runs, r => r.Kind == MigrationKinds.Data && r.Status == MigrationRunStatus.Pending);
        Assert.Contains(runs, r => r.Kind == MigrationKinds.Data && r.Status == MigrationRunStatus.Superseded && r.SupersededById != null);
        Assert.Contains(runs, r => r.Status == MigrationRunStatus.Failed && r.FailureReason != null);
        // Once per (kind, service, environment, version, run key), however often it runs.
        Assert.Equal(runs.Count, runs.Select(r => (r.Kind, r.Product, r.Service, r.Environment, r.Version, r.RunKey)).Distinct().Count());
        Assert.True(await db.MigrationRunItems.AnyAsync(i => i.Status == MigrationItemStatus.Applied && i.AppliedAt != null));
    }
}
