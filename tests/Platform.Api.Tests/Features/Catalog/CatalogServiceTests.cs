using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Platform.Api.Features.Catalog;
using Platform.Api.Features.Catalog.Models;
using Platform.Api.Infrastructure.Persistence;

namespace Platform.Api.Tests.Features.Catalog;

public class CatalogServiceTests : IDisposable
{
    private readonly PlatformDbContext _db;
    private readonly CatalogYamlLoader _loader;
    private readonly CatalogService _sut;

    private const string ValidYaml = """
        id: test-item
        name: Test Item
        description: A test catalog item
        category: infrastructure
        icon: settings
        inputs:
          - id: name
            component: TextInput
            label: Name
            required: true
        executor:
          type: manual
          parameters_map:
            name: "{{name}}"
        """;

    private const string MinimalYaml = """
        id: minimal
        name: Minimal
        category: ci-cd
        executor:
          type: manual
        """;

    public CatalogServiceTests()
    {
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _db = new PlatformDbContext(options);

        var config = Substitute.For<Microsoft.Extensions.Configuration.IConfiguration>();
        config["Catalog:Path"].Returns("nonexistent-path");
        _loader = new CatalogYamlLoader(config, Substitute.For<ILogger<CatalogYamlLoader>>());

        _sut = new CatalogService(_db, _loader, Substitute.For<ILogger<CatalogService>>());
    }

    public void Dispose() => _db.Dispose();

    private CatalogItem SeedItem(string slug, string name, string category, bool isActive = true)
    {
        var item = new CatalogItem
        {
            Id = Guid.NewGuid(),
            Slug = slug,
            Name = name,
            Category = category,
            IsActive = isActive,
            CurrentYamlHash = "hash",
        };
        item.Executor = new ExecutorConfig { Type = "manual" };
        _db.CatalogItems.Add(item);
        _db.SaveChanges();
        return item;
    }

    // ── GetAll ──

    [Fact]
    public async Task GetAll_ReturnsOnlyActiveItems()
    {
        SeedItem("active-1", "Active", "ci-cd", isActive: true);
        SeedItem("inactive-1", "Inactive", "ci-cd", isActive: false);

        var result = await _sut.GetAll();

        Assert.Single(result);
        Assert.Equal("active-1", result[0].Slug);
    }

    [Fact]
    public async Task GetAll_IncludeInactive_ReturnsAll()
    {
        SeedItem("a", "Active", "ci-cd", isActive: true);
        SeedItem("b", "Inactive", "ci-cd", isActive: false);

        var result = await _sut.GetAll(includeInactive: true);

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public async Task GetAll_FiltersByCategory()
    {
        SeedItem("ci", "CI Item", "ci-cd");
        SeedItem("infra", "Infra Item", "infrastructure");

        var result = await _sut.GetAll(category: "ci-cd");

        Assert.Single(result);
        Assert.Equal("ci", result[0].Slug);
    }

    [Fact]
    public async Task GetAll_FiltersBySearch()
    {
        SeedItem("dns", "Request DNS Record", "infrastructure");
        SeedItem("repo", "Create Repository", "ci-cd");

        var result = await _sut.GetAll(search: "DNS");

        Assert.Single(result);
        Assert.Equal("dns", result[0].Slug);
    }

    // ── GetBySlug ──

    [Fact]
    public async Task GetBySlug_ReturnsItem()
    {
        SeedItem("test", "Test", "ci-cd");

        var result = await _sut.GetBySlug("test");

        Assert.NotNull(result);
        Assert.Equal("Test", result.Name);
    }

    [Fact]
    public async Task GetBySlug_ReturnsNull_WhenInactive()
    {
        SeedItem("inactive", "Inactive", "ci-cd", isActive: false);

        var result = await _sut.GetBySlug("inactive");

        Assert.Null(result);
    }

    [Fact]
    public async Task GetBySlug_IncludeInactive_ReturnsInactiveItem()
    {
        SeedItem("inactive", "Inactive", "ci-cd", isActive: false);

        var result = await _sut.GetBySlug("inactive", includeInactive: true);

        Assert.NotNull(result);
    }

    // ── ValidateYaml ──

    [Fact]
    public void ValidateYaml_ValidContent_ReturnsValid()
    {
        var result = _sut.ValidateYaml(ValidYaml);

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void ValidateYaml_MissingId_ReturnsError()
    {
        var yaml = """
            name: No ID
            category: ci-cd
            executor:
              type: manual
            """;

        var result = _sut.ValidateYaml(yaml);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("'id'"));
    }

    [Fact]
    public void ValidateYaml_MissingExecutor_ReturnsError()
    {
        var yaml = """
            id: no-exec
            name: No Executor
            category: ci-cd
            """;

        var result = _sut.ValidateYaml(yaml);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("executor"));
    }

    [Fact]
    public void ValidateYaml_InvalidYaml_ReturnsError()
    {
        var result = _sut.ValidateYaml("{{invalid yaml");

        Assert.False(result.IsValid);
    }

    // ── Create ──

    [Fact]
    public async Task Create_ValidYaml_CreatesItemAndVersion()
    {
        var (item, errors) = await _sut.Create(ValidYaml);

        Assert.NotNull(item);
        Assert.Empty(errors);
        Assert.Equal("test-item", item.Slug);
        Assert.Equal("Test Item", item.Name);
        Assert.True(item.IsActive);

        var versions = _db.CatalogItemVersions.Where(v => v.CatalogItemId == item.Id).ToList();
        Assert.Single(versions);
        Assert.Contains("test-item", versions[0].YamlContent);
    }

    [Fact]
    public async Task Create_PopulatesJsonColumns()
    {
        var (item, _) = await _sut.Create(ValidYaml);

        Assert.NotNull(item);
        Assert.NotEqual("[]", item.InputsJson);
        Assert.Single(item.Inputs);
        Assert.Equal("name", item.Inputs[0].Id);
        Assert.NotNull(item.Executor);
        Assert.Equal("manual", item.Executor.Type);
    }

    [Fact]
    public async Task Create_DuplicateSlug_ReturnsError()
    {
        SeedItem("test-item", "Existing", "ci-cd");

        var (item, errors) = await _sut.Create(ValidYaml);

        Assert.Null(item);
        Assert.Single(errors);
        Assert.Contains("already exists", errors[0]);
    }

    [Fact]
    public async Task Create_InvalidYaml_ReturnsValidationErrors()
    {
        var yaml = """
            name: No ID or Executor
            category: ci-cd
            """;

        var (item, errors) = await _sut.Create(yaml);

        Assert.Null(item);
        Assert.NotEmpty(errors);
    }

    // ── Update ──

    [Fact]
    public async Task Update_ValidYaml_UpdatesAndCreatesVersion()
    {
        var original = SeedItem("test-item", "Original", "ci-cd");
        _db.CatalogItemVersions.Add(new CatalogItemVersion
        {
            Id = Guid.NewGuid(),
            CatalogItemId = original.Id,
            YamlContent = "original",
            YamlHash = "old-hash",
        });
        _db.SaveChanges();

        var (item, errors) = await _sut.Update("test-item", ValidYaml);

        Assert.NotNull(item);
        Assert.Empty(errors);
        Assert.Equal("Test Item", item.Name);

        var versions = _db.CatalogItemVersions.Where(v => v.CatalogItemId == original.Id).ToList();
        Assert.Equal(2, versions.Count);
    }

    [Fact]
    public async Task Update_NonExistentSlug_ReturnsError()
    {
        var (item, errors) = await _sut.Update("nonexistent", ValidYaml);

        Assert.Null(item);
        Assert.Contains("not found", errors[0]);
    }

    // ── ToggleActive ──

    [Fact]
    public async Task ToggleActive_FlipsState()
    {
        SeedItem("toggle-me", "Toggle", "ci-cd", isActive: true);

        var result = await _sut.ToggleActive("toggle-me", false);

        Assert.NotNull(result);
        Assert.False(result.IsActive);

        result = await _sut.ToggleActive("toggle-me", true);
        Assert.True(result!.IsActive);
    }

    // ── Delete ──

    [Fact]
    public async Task Delete_RemovesItemAndVersions()
    {
        var item = SeedItem("delete-me", "Delete", "ci-cd");
        _db.CatalogItemVersions.Add(new CatalogItemVersion
        {
            Id = Guid.NewGuid(),
            CatalogItemId = item.Id,
            YamlContent = "yaml",
            YamlHash = "hash",
        });
        _db.SaveChanges();

        var result = await _sut.Delete("delete-me");

        Assert.True(result);
        Assert.Null(await _db.CatalogItems.FirstOrDefaultAsync(c => c.Slug == "delete-me"));
        Assert.Empty(_db.CatalogItemVersions.Where(v => v.CatalogItemId == item.Id));
    }

    [Fact]
    public async Task Delete_NonExistentSlug_ReturnsFalse()
    {
        var result = await _sut.Delete("nonexistent");

        Assert.False(result);
    }
}
