using Microsoft.EntityFrameworkCore;
using Platform.Api.Features.ServiceInventory.Models;
using Platform.Api.Infrastructure.Persistence;

namespace Platform.Api.Features.ServiceInventory;

/// <summary>
/// Prunes stored copies (docs/plans/service-inventory.md, "History kept"):
/// <list type="bullet">
/// <item>catalog: every version, forever;</item>
/// <item>status: all for 7 days, then the last of each UTC day for 90 days, then none;</item>
/// <item>cost: closed months forever; month-to-date copies for 90 days;</item>
/// <item>a snapshot a feed currently points to is always kept;</item>
/// <item>projected rows are kept only for current snapshots.</item>
/// </list>
/// </summary>
public class InventoryRetention
{
    public static readonly TimeSpan StatusKeepAll = TimeSpan.FromDays(7);
    public static readonly TimeSpan StatusKeepDaily = TimeSpan.FromDays(90);
    public static readonly TimeSpan CostKeepDaily = TimeSpan.FromDays(90);

    private readonly PlatformDbContext _db;
    private readonly TimeProvider _clock;

    public InventoryRetention(PlatformDbContext db, TimeProvider clock)
    {
        _db = db;
        _clock = clock;
    }

    public async Task<(int Snapshots, int Rows)> RunAsync(CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        var current = (await _db.InventoryFeeds.AsNoTracking()
                .Where(f => f.CurrentSnapshotId != null)
                .Select(f => f.CurrentSnapshotId!.Value)
                .ToListAsync(ct))
            .ToHashSet();

        var snapshots = await _db.InventorySnapshots.AsNoTracking()
            .Select(s => new { s.Id, s.FeedType, s.FeedName, s.Complete, s.FetchedAt })
            .ToListAsync(ct);

        var drop = new HashSet<Guid>();

        // Status: everything from the last 7 days; before that the last copy of each day, up to 90.
        foreach (var feed in snapshots.Where(s => s.FeedType == InventoryFeedTypes.Status).GroupBy(s => s.FeedName))
        {
            var keepDaily = feed
                .Where(s => now - s.FetchedAt >= StatusKeepAll && now - s.FetchedAt < StatusKeepDaily)
                .GroupBy(s => s.FetchedAt.UtcDateTime.Date)
                .Select(day => day.MaxBy(s => s.FetchedAt)!.Id)
                .ToHashSet();
            foreach (var s in feed)
            {
                var age = now - s.FetchedAt;
                if (age < StatusKeepAll || keepDaily.Contains(s.Id)) continue;
                drop.Add(s.Id);
            }
        }

        // Cost: closed months stay; open (month-to-date) copies for 90 days.
        foreach (var s in snapshots.Where(s => s.FeedType == InventoryFeedTypes.Cost))
            if (s.Complete != true && now - s.FetchedAt >= CostKeepDaily) drop.Add(s.Id);

        drop.ExceptWith(current);
        foreach (var id in drop)
        {
            // Remove the tracked instance when this context already holds one; otherwise a stub,
            // so the compressed content is never loaded just to delete it.
            var tracked = _db.InventorySnapshots.Local.FirstOrDefault(s => s.Id == id);
            _db.InventorySnapshots.Remove(tracked ?? new InventorySnapshot { Id = id });
        }

        // Projected rows of snapshots nothing points to any more. Small tables: loaded and removed,
        // which works on every provider.
        var keep = current.ToList();
        var services = await _db.InventoryServices.Where(r => !keep.Contains(r.SnapshotId)).ToListAsync(ct);
        var months = await _db.InventoryCostMonths.Where(r => !keep.Contains(r.SnapshotId)).ToListAsync(ct);
        var costServices = await _db.InventoryCostServices.Where(r => !keep.Contains(r.SnapshotId)).ToListAsync(ct);
        var unallocated = await _db.InventoryCostUnallocated.Where(r => !keep.Contains(r.SnapshotId)).ToListAsync(ct);
        _db.RemoveRange(services);
        _db.RemoveRange(months);
        _db.RemoveRange(costServices);
        _db.RemoveRange(unallocated);
        var rows = services.Count + months.Count + costServices.Count + unallocated.Count;

        await _db.SaveChangesAsync(ct);
        return (drop.Count, rows);
    }
}
