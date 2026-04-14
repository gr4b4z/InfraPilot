using System.Text.Json;
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

    public async Task<List<CatalogItem>> GetAll(string? category = null, string? search = null, bool includeInactive = false)
    {
        var query = includeInactive
            ? _db.CatalogItems.AsQueryable()
            : _db.CatalogItems.Where(c => c.IsActive);

        if (!string.IsNullOrWhiteSpace(category))
            query = query.Where(c => c.Category == category);

        if (!string.IsNullOrWhiteSpace(search))
            query = query.Where(c => c.Name.Contains(search) || (c.Description != null && c.Description.Contains(search)));

        return await query.OrderBy(c => c.Category).ThenBy(c => c.Name).ToListAsync();
    }

    public async Task<CatalogItem?> GetBySlug(string slug, bool includeInactive = false)
    {
        return includeInactive
            ? await _db.CatalogItems.FirstOrDefaultAsync(c => c.Slug == slug)
            : await _db.CatalogItems.FirstOrDefaultAsync(c => c.Slug == slug && c.IsActive);
    }

    public CatalogValidationResult ValidateYaml(string yamlContent)
    {
        var errors = new List<string>();

        CatalogDefinition? def;
        try
        {
            def = _loader.DeserializeDefinition(yamlContent);
        }
        catch (Exception ex)
        {
            return new CatalogValidationResult(false, [$"Invalid YAML: {ex.Message}"]);
        }

        if (def is null)
            return new CatalogValidationResult(false, ["Failed to parse YAML"]);

        if (string.IsNullOrWhiteSpace(def.Id))
            errors.Add("'id' is required");
        else if (!System.Text.RegularExpressions.Regex.IsMatch(def.Id, @"^[a-z0-9][a-z0-9-]*$"))
            errors.Add("'id' must contain only lowercase letters, digits, and hyphens");

        if (string.IsNullOrWhiteSpace(def.Name))
            errors.Add("'name' is required");

        if (string.IsNullOrWhiteSpace(def.Category))
            errors.Add("'category' is required");

        if (def.Executor is null || string.IsNullOrWhiteSpace(def.Executor.Type))
            errors.Add("'executor' with a 'type' is required");

        return new CatalogValidationResult(errors.Count == 0, errors);
    }

    public async Task<(CatalogItem? Item, List<string> Errors)> Create(string yamlContent)
    {
        var validation = ValidateYaml(yamlContent);
        if (!validation.IsValid)
            return (null, validation.Errors);

        var def = _loader.DeserializeDefinition(yamlContent)!;

        var existing = await _db.CatalogItems.FirstOrDefaultAsync(c => c.Slug == def.Id);
        if (existing is not null)
            return (null, [$"Catalog item with slug '{def.Id}' already exists"]);

        var hash = ComputeHash(yamlContent);
        var item = new CatalogItem
        {
            Id = Guid.NewGuid(),
            Slug = def.Id,
            Name = def.Name,
            Description = def.Description,
            Category = def.Category,
            Icon = def.Icon,
            CurrentYamlHash = hash,
            IsActive = true,
            Inputs = def.Inputs,
            Validations = def.Validations,
            Approval = def.Approval,
            Executor = def.Executor,
        };

        _db.CatalogItems.Add(item);
        _db.CatalogItemVersions.Add(new CatalogItemVersion
        {
            Id = Guid.NewGuid(),
            CatalogItemId = item.Id,
            YamlContent = yamlContent,
            YamlHash = hash,
        });

        await _db.SaveChangesAsync();
        _logger.LogInformation("Created catalog item: {Slug}", def.Id);

        return (item, []);
    }

    public async Task<(CatalogItem? Item, List<string> Errors)> Update(string slug, string yamlContent)
    {
        var validation = ValidateYaml(yamlContent);
        if (!validation.IsValid)
            return (null, validation.Errors);

        var def = _loader.DeserializeDefinition(yamlContent)!;

        var item = await _db.CatalogItems.FirstOrDefaultAsync(c => c.Slug == slug);
        if (item is null)
            return (null, [$"Catalog item '{slug}' not found"]);

        var hash = ComputeHash(yamlContent);

        item.Slug = def.Id;
        item.Name = def.Name;
        item.Description = def.Description;
        item.Category = def.Category;
        item.Icon = def.Icon;
        item.CurrentYamlHash = hash;
        item.Inputs = def.Inputs;
        item.Validations = def.Validations;
        item.Approval = def.Approval;
        item.Executor = def.Executor;
        item.UpdatedAt = DateTimeOffset.UtcNow;

        _db.CatalogItemVersions.Add(new CatalogItemVersion
        {
            Id = Guid.NewGuid(),
            CatalogItemId = item.Id,
            YamlContent = yamlContent,
            YamlHash = hash,
        });

        await _db.SaveChangesAsync();
        _logger.LogInformation("Updated catalog item: {Slug}", SanitizeForLog(slug));

        return (item, []);
    }

    public async Task<string?> GetYamlContent(string slug)
    {
        var item = await _db.CatalogItems.FirstOrDefaultAsync(c => c.Slug == slug);
        if (item is null) return null;

        var version = await _db.CatalogItemVersions
            .Where(v => v.CatalogItemId == item.Id)
            .OrderByDescending(v => v.CreatedAt)
            .FirstOrDefaultAsync();

        return version?.YamlContent;
    }

    public async Task<bool> Delete(string slug)
    {
        var item = await _db.CatalogItems
            .Include(c => c.Versions)
            .FirstOrDefaultAsync(c => c.Slug == slug);
        if (item is null) return false;

        _db.CatalogItemVersions.RemoveRange(item.Versions);
        _db.CatalogItems.Remove(item);
        await _db.SaveChangesAsync();

        _logger.LogInformation("Deleted catalog item: {Slug}", SanitizeForLog(slug));
        return true;
    }

    public async Task<CatalogItem?> ToggleActive(string slug, bool isActive)
    {
        var item = await _db.CatalogItems.FirstOrDefaultAsync(c => c.Slug == slug);
        if (item is null) return null;

        item.IsActive = isActive;
        item.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync();

        _logger.LogInformation("Toggled catalog item {Slug} active={Active}", SanitizeForLog(slug), isActive);
        return item;
    }

    private static string SanitizeForLog(string value)
    {
        return value.Replace("\r", string.Empty).Replace("\n", string.Empty);
    }

    private static string ComputeHash(string content)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(content));
        return Convert.ToHexStringLower(bytes);
    }

    private static string SanitizeForLog(string value)
    {
        return value.Replace("\r", "").Replace("\n", "");
    }
}

public record CatalogValidationResult(bool IsValid, List<string> Errors);
