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
        var rows = _db.InventoryFeeds.AsNoTracking().Where(f => f.FeedType == type && f.Period == period);
        if (configured is not null)
            return await rows.FirstOrDefaultAsync(f => f.FeedName == configured, ct);
        return (await rows.ToListAsync(ct)).OrderByDescending(f => f.LastSuccessAt).FirstOrDefault();
    }

    public async Task<(T Document, InventorySnapshot Snapshot)?> DocumentAsync<T>(InventoryFeed? feed, CancellationToken ct)
        where T : class
    {
        if (feed?.CurrentSnapshotId is not { } id) return null;
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

    public static FeedFreshness Freshness(InventoryFeed? feed, InventorySnapshot? snapshot, string feedType) => new(
        feedType,
        string.IsNullOrEmpty(snapshot?.Period) ? null : snapshot!.Period,
        snapshot is null ? null : VersionLabel(snapshot),
        snapshot?.FetchedAt,
        feed?.LastSuccessAt,
        feed?.LastAttemptAt,
        feed?.LastError,
        feed?.ConsecutiveFailures ?? 0);

    public static string VersionLabel(InventorySnapshot s) => s.FeedType switch
    {
        InventoryFeedTypes.Catalog => s.DocumentVersion is { } v ? $"catalog v{v}" : "catalog",
        InventoryFeedTypes.Status => s.GeneratedAt is { } g ? $"generated {g:O}" : "status",
        _ => $"{s.Period}{(s.Complete == true ? "" : " to date")}",
    };
}
