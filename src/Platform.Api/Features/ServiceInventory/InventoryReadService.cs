using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Platform.Api.Features.ServiceInventory.Documents;
using Platform.Api.Features.ServiceInventory.Models;
using Platform.Api.Infrastructure.Persistence;

namespace Platform.Api.Features.ServiceInventory;

/// <summary>How old a feed's data is and whether the last refresh worked.</summary>
public sealed record FeedFreshness(
    string Feed,
    string? Period,
    string? Version,
    DateTimeOffset? FetchedAt,
    DateTimeOffset? RefreshedAt,
    DateTimeOffset? LastAttemptAt,
    string? LastError,
    int ConsecutiveFailures);

/// <summary>
/// Reads the current inventory through the feed pointers. The parsed catalog and status files are
/// cached in memory per snapshot id: a snapshot never changes, so the cache never goes stale.
/// </summary>
public class InventoryReadService
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromHours(1);

    private readonly PlatformDbContext _db;
    private readonly IMemoryCache _cache;
    private readonly IOptionsMonitor<ServiceInventoryOptions> _options;

    public InventoryReadService(PlatformDbContext db, IMemoryCache cache, IOptionsMonitor<ServiceInventoryOptions> options)
    {
        _db = db;
        _cache = cache;
        _options = options;
    }

    /// <summary>
    /// The feed itself (period <c>""</c>) of a type, or one cost month, read through the feed
    /// configured for that type: a renamed feed leaves its old rows behind, and they are never
    /// picked by mistake. With no feed of that type configured (any more), the most recently
    /// refreshed one.
    /// </summary>
    public async Task<InventoryFeed?> FeedAsync(string type, string period = "", CancellationToken ct = default)
    {
        var configured = _options.CurrentValue.Feeds
            .FirstOrDefault(f => string.Equals(f.Type?.Trim(), type, StringComparison.OrdinalIgnoreCase))?.Name;
        if (configured is not null)
        {
            // By primary key.
            var key = InventoryFeed.KeyFor(configured, period);
            return await _db.InventoryFeeds.AsNoTracking().FirstOrDefaultAsync(f => f.FeedKey == key, ct);
        }
        var rows = await _db.InventoryFeeds.AsNoTracking().Where(f => f.FeedType == type && f.Period == period).ToListAsync(ct);
        return rows.OrderByDescending(f => f.LastSuccessAt).FirstOrDefault();
    }

    public async Task<(T Document, InventorySnapshot Snapshot)?> DocumentAsync<T>(InventoryFeed? feed, CancellationToken ct)
        where T : class
    {
        if (feed?.CurrentSnapshotId is not { } id) return null;
        return await SnapshotDocumentAsync<T>(id, ct);
    }

    /// <summary>
    /// One stored copy by its id. A caller that reads several things about one copy takes the id
    /// once and uses it for all of them, so a refresh in between never mixes two copies.
    /// </summary>
    public async Task<(T Document, InventorySnapshot Snapshot)?> SnapshotDocumentAsync<T>(Guid id, CancellationToken ct)
        where T : class
    {
        var snapshot = await _db.InventorySnapshots.AsNoTracking()
            .Where(s => s.Id == id)
            .Select(s => new InventorySnapshot
            {
                Id = s.Id, FeedName = s.FeedName, FeedType = s.FeedType, Period = s.Period, SourceKind = s.SourceKind,
                SourceLocation = s.SourceLocation, SourceVersion = s.SourceVersion, ContentSha256 = s.ContentSha256,
                SchemaVersion = s.SchemaVersion, DocumentVersion = s.DocumentVersion, GeneratedAt = s.GeneratedAt,
                Complete = s.Complete, FetchedAt = s.FetchedAt, ContentLength = s.ContentLength,
            })
            .FirstOrDefaultAsync(ct);
        if (snapshot is null) return null;

        var document = await _cache.GetOrCreateAsync(("service-inventory", id), async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheFor;
            var content = await _db.InventorySnapshots.AsNoTracking()
                .Where(s => s.Id == id).Select(s => s.ContentGzip).FirstAsync(ct);
            return DocumentReader.Read(snapshot.FeedType, Gzip.Decompress(content)).Document;
        });
        return document is T typed ? (typed, snapshot) : null;
    }

    /// <summary>
    /// The row whose refresh state describes a cost month. The month the latest file is for is
    /// refreshed through the feed itself, so its state is the feed's, however it was asked for —
    /// unless only the month's own row recorded a failure. Any other month asked for by name reports
    /// its own state (a closed month that failed says so).
    /// </summary>
    public static InventoryFeed? CostStateRow(InventoryFeed? feedRow, InventoryFeed? monthRow, bool requested)
    {
        var isLatestMonth = monthRow is not null && monthRow.CurrentSnapshotId == feedRow?.CurrentSnapshotId;
        return requested && !isLatestMonth
            ? monthRow
            : feedRow?.LastError is null && monthRow?.LastError is not null ? monthRow : feedRow;
    }

    /// <summary>What everyone but an admin is told when the last refresh failed.</summary>
    public const string RefreshFailed = "The last refresh failed. An administrator can see why.";

    /// <summary>
    /// <paramref name="detailed"/> is for admins: the recorded reason names the source (its host and
    /// path), which the rest of the portal's users are not shown. They learn that it failed, and when.
    /// </summary>
    public static FeedFreshness Freshness(InventoryFeed? feed, InventorySnapshot? snapshot, string feedType, bool detailed) => new(
        feedType,
        string.IsNullOrEmpty(snapshot?.Period) ? null : snapshot!.Period,
        snapshot is null ? null : VersionLabel(snapshot),
        snapshot?.FetchedAt,
        feed?.LastSuccessAt,
        feed?.LastAttemptAt,
        feed?.LastError is null ? null : detailed ? feed.LastError : RefreshFailed,
        feed?.ConsecutiveFailures ?? 0);

    public static string VersionLabel(InventorySnapshot s) => s.FeedType switch
    {
        InventoryFeedTypes.Catalog => s.DocumentVersion is { } v ? $"catalog v{v}" : "catalog",
        InventoryFeedTypes.Status => s.GeneratedAt is { } g ? $"generated {g:O}" : "status",
        _ => $"{s.Period}{(s.Complete == true ? "" : " to date")}",
    };
}
