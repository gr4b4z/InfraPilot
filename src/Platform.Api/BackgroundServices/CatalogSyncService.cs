using Microsoft.EntityFrameworkCore;
using Platform.Api.Features.Catalog;
using Platform.Api.Features.Catalog.Models;
using Platform.Api.Infrastructure.Persistence;

namespace Platform.Api.BackgroundServices;

public class CatalogSyncService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(60);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<CatalogSyncService> _logger;

    public CatalogSyncService(
        IServiceScopeFactory scopeFactory,
        ILogger<CatalogSyncService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("CatalogSyncService started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SyncCatalog(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during catalog sync");
            }

            await Task.Delay(Interval, stoppingToken);
        }

        _logger.LogInformation("CatalogSyncService stopped");
    }

    private async Task SyncCatalog(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var loader = scope.ServiceProvider.GetRequiredService<CatalogYamlLoader>();

        var definitions = loader.LoadAll();

        if (definitions.Count == 0)
        {
            _logger.LogDebug("No catalog definitions found on disk");
            return;
        }

        var existingItems = await db.CatalogItems
            .ToDictionaryAsync(ci => ci.Slug, ct);

        var changeCount = 0;

        foreach (var def in definitions)
        {
            if (string.IsNullOrEmpty(def.Id))
            {
                _logger.LogWarning("Skipping catalog definition with empty ID from {Path}", def.SourcePath);
                continue;
            }

            if (existingItems.TryGetValue(def.Id, out var existingItem))
            {
                if (existingItem.CurrentYamlHash == def.YamlHash)
                    continue;

                _logger.LogInformation(
                    "Catalog item '{Slug}' changed (hash {OldHash} -> {NewHash}), updating",
                    def.Id, existingItem.CurrentYamlHash, def.YamlHash);

                existingItem.Name = def.Name;
                existingItem.Description = def.Description;
                existingItem.Category = def.Category;
                existingItem.Icon = def.Icon;
                existingItem.CurrentYamlHash = def.YamlHash;
                existingItem.Inputs = def.Inputs;
                existingItem.Validations = def.Validations;
                existingItem.Approval = def.Approval;
                existingItem.Executor = def.Executor;
                existingItem.IsActive = true;
                existingItem.UpdatedAt = DateTimeOffset.UtcNow;

                db.CatalogItemVersions.Add(new CatalogItemVersion
                {
                    Id = Guid.NewGuid(),
                    CatalogItemId = existingItem.Id,
                    YamlContent = def.YamlContent,
                    YamlHash = def.YamlHash,
                    CreatedAt = DateTimeOffset.UtcNow
                });

                changeCount++;
            }
            else
            {
                _logger.LogInformation("New catalog item discovered: '{Slug}'", def.Id);

                var newItem = new CatalogItem
                {
                    Id = Guid.NewGuid(),
                    Slug = def.Id,
                    Name = def.Name,
                    Description = def.Description,
                    Category = def.Category,
                    Icon = def.Icon,
                    CurrentYamlHash = def.YamlHash,
                    Inputs = def.Inputs,
                    Validations = def.Validations,
                    Approval = def.Approval,
                    Executor = def.Executor,
                    IsActive = true,
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow
                };

                db.CatalogItems.Add(newItem);

                db.CatalogItemVersions.Add(new CatalogItemVersion
                {
                    Id = Guid.NewGuid(),
                    CatalogItemId = newItem.Id,
                    YamlContent = def.YamlContent,
                    YamlHash = def.YamlHash,
                    CreatedAt = DateTimeOffset.UtcNow
                });

                changeCount++;
            }
        }

        if (changeCount > 0)
        {
            await db.SaveChangesAsync(ct);
            _logger.LogInformation("Catalog sync completed: {ChangeCount} item(s) updated or added", changeCount);
        }
        else
        {
            _logger.LogDebug("Catalog sync completed: no changes detected");
        }
    }
}
