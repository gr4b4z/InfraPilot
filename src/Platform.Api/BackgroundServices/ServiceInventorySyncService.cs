using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Platform.Api.Features.ServiceInventory;
using Platform.Api.Infrastructure;
using Platform.Api.Infrastructure.Features;
using Platform.Api.Infrastructure.Persistence;

namespace Platform.Api.BackgroundServices;

/// <summary>
/// Refreshes the service inventory's feeds (ADR-0004), on the pattern of
/// <see cref="CatalogSyncService"/>: a loop with a scope per cycle. Each feed is refreshed when its
/// <c>PollMinutes</c> have passed since its last attempt; one feed's failure never stops the others.
/// Does nothing while the feature flag is off or the configuration is invalid. Once a day it prunes
/// stored copies; on its first enabled cycle it re-projects snapshots built by an older projector.
/// </summary>
public class ServiceInventorySyncService : BackgroundService
{
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan RetentionEvery = TimeSpan.FromHours(24);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptionsMonitor<ServiceInventoryOptions> _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<ServiceInventorySyncService> _logger;

    private string? _reportedConfiguration;
    private bool _rebuilt;
    private DateTimeOffset? _lastRetention;

    public ServiceInventorySyncService(
        IServiceScopeFactory scopeFactory, IOptionsMonitor<ServiceInventoryOptions> options,
        TimeProvider clock, ILogger<ServiceInventorySyncService> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options;
        _clock = clock;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Service inventory: refresh cycle failed");
            }

            try { await Task.Delay(Tick, _clock, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>One cycle. Public so tests drive it without the timer.</summary>
    public async Task RunOnceAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var flags = scope.ServiceProvider.GetRequiredService<IFeatureFlags>();
        if (!await flags.IsEnabled(FeatureFlagKeys.ServiceInventory, ct)) return;

        var options = _options.CurrentValue;
        if (options.Feeds.Count == 0) return;
        var errors = ServiceInventoryOptionsValidator.Validate(options);
        if (errors.Count > 0)
        {
            var report = string.Join("; ", errors);
            if (report != _reportedConfiguration)
            {
                _logger.LogError("Service inventory is disabled: the configuration is invalid: {Errors}", LogSanitizer.Clean(report, 2000));
                _reportedConfiguration = report;
            }
            return;
        }
        _reportedConfiguration = null;

        var refresher = scope.ServiceProvider.GetRequiredService<InventoryRefresher>();
        if (!_rebuilt)
        {
            var rebuilt = await refresher.RebuildAsync(force: false, ct);
            if (rebuilt > 0) _logger.LogInformation("Service inventory: re-projected {Count} snapshot(s)", rebuilt);
            _rebuilt = true;
        }

        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var lastAttempts = await db.InventoryFeeds.AsNoTracking()
            .Where(f => f.Period == "")
            .Select(f => new { f.FeedName, f.LastAttemptAt })
            .ToDictionaryAsync(f => f.FeedName, f => f.LastAttemptAt, StringComparer.OrdinalIgnoreCase, ct);

        var now = _clock.GetUtcNow();
        foreach (var feed in options.Feeds)
        {
            var last = lastAttempts.GetValueOrDefault(feed.Name);
            if (last is not null && now - last.Value < TimeSpan.FromMinutes(feed.PollMinutes)) continue;
            try
            {
                // A fresh scope per feed: one feed's failed save never leaves state for the next.
                using var feedScope = _scopeFactory.CreateScope();
                await feedScope.ServiceProvider.GetRequiredService<InventoryRefresher>().RefreshAsync(feed, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Service inventory: refreshing {Feed} failed", LogSanitizer.Clean(feed.Name));
            }
        }

        if (_lastRetention is null || now - _lastRetention.Value >= RetentionEvery)
        {
            var (snapshots, rows) = await scope.ServiceProvider.GetRequiredService<InventoryRetention>().RunAsync(ct);
            if (snapshots + rows > 0)
                _logger.LogInformation("Service inventory: pruned {Snapshots} snapshot(s) and {Rows} row(s)", snapshots, rows);
            _lastRetention = now;
        }
    }
}
