using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Platform.Api.Features.Catalog;
using Platform.Api.Features.Catalog.Models;
using Platform.Api.Infrastructure.Persistence;

namespace Platform.Api.Tests.Infrastructure.Persistence;

public class CatalogSeederTests : IDisposable
{
    private readonly PlatformDbContext _db;
    private readonly CatalogYamlLoader _loader;

    public CatalogSeederTests()
    {
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _db = new PlatformDbContext(options);

        // Point loader at the real catalog/examples directory
        var catalogPath = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "catalog", "examples"));

        var config = Substitute.For<IConfiguration>();
        config["Catalog:Path"].Returns(catalogPath);
        _loader = new CatalogYamlLoader(config, Substitute.For<ILogger<CatalogYamlLoader>>());
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task SeedCatalog_FreshDb_AllItemsActive()
    {
        await SeedData.SeedCatalog(_db, _loader);

        var items = await _db.CatalogItems.ToListAsync();

        Assert.NotEmpty(items);
        Assert.All(items, i => Assert.True(i.IsActive));
    }

    [Fact]
    public async Task SeedCatalog_FreshDb_PopulatesJsonColumns()
    {
        await SeedData.SeedCatalog(_db, _loader);

        var items = await _db.CatalogItems.ToListAsync();
        Assert.NotEmpty(items);

        // At least one item should have inputs and executor
        var itemWithInputs = items.FirstOrDefault(i => i.InputsJson != "[]");
        Assert.NotNull(itemWithInputs);
        Assert.NotEmpty(itemWithInputs.Inputs);

        var itemWithExecutor = items.FirstOrDefault(i => i.ExecutorJson != null);
        Assert.NotNull(itemWithExecutor);
        Assert.NotNull(itemWithExecutor.Executor);
    }

    [Fact]
    public async Task SeedCatalog_ExistingDb_NewItemsInactive()
    {
        // Pre-populate one item
        _db.CatalogItems.Add(new CatalogItem
        {
            Id = Guid.NewGuid(),
            Slug = "create-repo",
            Name = "Existing Repo Item",
            Category = "ci-cd",
            CurrentYamlHash = "existing-hash",
            IsActive = true,
        });
        await _db.SaveChangesAsync();

        await SeedData.SeedCatalog(_db, _loader);

        var items = await _db.CatalogItems.ToListAsync();

        // The pre-existing item stays active
        var existing = items.First(i => i.Slug == "create-repo");
        Assert.True(existing.IsActive);
        Assert.Equal("Existing Repo Item", existing.Name); // Not overwritten

        // New items are inactive
        var newItems = items.Where(i => i.Slug != "create-repo").ToList();
        Assert.NotEmpty(newItems);
        Assert.All(newItems, i => Assert.False(i.IsActive));
    }

    [Fact]
    public async Task SeedCatalog_ExistingSlug_NotOverwritten()
    {
        var originalName = "My Custom Name";
        _db.CatalogItems.Add(new CatalogItem
        {
            Id = Guid.NewGuid(),
            Slug = "create-repo",
            Name = originalName,
            Category = "custom-category",
            CurrentYamlHash = "custom-hash",
            IsActive = true,
        });
        await _db.SaveChangesAsync();

        await SeedData.SeedCatalog(_db, _loader);

        var item = await _db.CatalogItems.FirstAsync(i => i.Slug == "create-repo");
        Assert.Equal(originalName, item.Name);
        Assert.Equal("custom-category", item.Category);
    }

    [Fact]
    public async Task SeedCatalog_CreatesVersionRecords()
    {
        await SeedData.SeedCatalog(_db, _loader);

        var items = await _db.CatalogItems.ToListAsync();
        var versions = await _db.CatalogItemVersions.ToListAsync();

        // Each item should have exactly one version
        Assert.Equal(items.Count, versions.Count);
        foreach (var item in items)
        {
            Assert.Single(versions, v => v.CatalogItemId == item.Id);
        }
    }

    [Fact]
    public async Task SeedCatalog_IsIdempotent()
    {
        await SeedData.SeedCatalog(_db, _loader);
        var countAfterFirst = await _db.CatalogItems.CountAsync();

        await SeedData.SeedCatalog(_db, _loader);
        var countAfterSecond = await _db.CatalogItems.CountAsync();

        Assert.Equal(countAfterFirst, countAfterSecond);
    }
}
