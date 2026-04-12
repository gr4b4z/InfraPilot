using Microsoft.EntityFrameworkCore;
using Platform.Api.Features.Catalog.Models;
using Platform.Api.Infrastructure.Persistence;

namespace Platform.Api.Features.Catalog;

public class CatalogService
{
    private readonly PlatformDbContext _db;
    private readonly CatalogYamlLoader _loader;
    private readonly ILogger<CatalogService> _logger;

    public CatalogService(PlatformDbContext db, CatalogYamlLoader loader, ILogger<CatalogService> logger)
    {
        _db = db;
        _loader = loader;
        _logger = logger;
    }

    public async Task<List<CatalogItem>> GetAll(string? category = null, string? search = null)
    {
        var query = _db.CatalogItems.Where(c => c.IsActive);

        if (!string.IsNullOrWhiteSpace(category))
            query = query.Where(c => c.Category == category);

        if (!string.IsNullOrWhiteSpace(search))
            query = query.Where(c => c.Name.Contains(search) || (c.Description != null && c.Description.Contains(search)));

        return await query.OrderBy(c => c.Category).ThenBy(c => c.Name).ToListAsync();
    }

    public async Task<CatalogItem?> GetBySlug(string slug)
    {
        return await _db.CatalogItems.FirstOrDefaultAsync(c => c.Slug == slug && c.IsActive);
    }

    public CatalogDefinition? GetDefinition(string slug)
    {
        var definitions = _loader.LoadAll();
        return definitions.FirstOrDefault(d => d.Id == slug);
    }

    public async Task SyncFromYaml()
    {
        var definitions = _loader.LoadAll();

        foreach (var def in definitions)
        {
            var existing = await _db.CatalogItems.FirstOrDefaultAsync(c => c.Slug == def.Id);

            if (existing is null)
            {
                var item = new CatalogItem
                {
                    Id = Guid.NewGuid(),
                    Slug = def.Id,
                    Name = def.Name,
                    Description = def.Description,
                    Category = def.Category,
                    Icon = def.Icon,
                    CurrentYamlHash = def.YamlHash,
                    IsActive = true,
                };

                _db.CatalogItems.Add(item);
                _db.CatalogItemVersions.Add(new CatalogItemVersion
                {
                    Id = Guid.NewGuid(),
                    CatalogItemId = item.Id,
                    YamlContent = def.YamlContent,
                    YamlHash = def.YamlHash,
                });

                _logger.LogInformation("Added catalog item: {Slug}", def.Id);
            }
            else if (existing.CurrentYamlHash != def.YamlHash)
            {
                existing.Name = def.Name;
                existing.Description = def.Description;
                existing.Category = def.Category;
                existing.Icon = def.Icon;
                existing.CurrentYamlHash = def.YamlHash;
                existing.UpdatedAt = DateTimeOffset.UtcNow;

                _db.CatalogItemVersions.Add(new CatalogItemVersion
                {
                    Id = Guid.NewGuid(),
                    CatalogItemId = existing.Id,
                    YamlContent = def.YamlContent,
                    YamlHash = def.YamlHash,
                });

                _logger.LogInformation("Updated catalog item: {Slug}", def.Id);
            }
        }

        await _db.SaveChangesAsync();
    }
}
