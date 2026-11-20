using System.Net;
using Microsoft.EntityFrameworkCore;
using Platform.Api.Features.ServiceInventory.Documents;
using Platform.Api.Features.ServiceInventory.Http;
using Platform.Api.Features.ServiceInventory.Models;
using Platform.Api.Features.ServiceInventory.Sources;
using Platform.Api.Infrastructure;
using Platform.Api.Infrastructure.Persistence;

namespace Platform.Api.Features.ServiceInventory;

/// <summary>
/// Refreshes one feed (docs/plans/service-inventory.md, "How a refresh replaces data"):
/// <list type="number">
/// <item>ask the source whether it changed; unchanged → record the attempt and stop;</item>
/// <item>download, hash; same content as the current copy → record and stop;</item>
/// <item>parse and check the format version; rejected → record why, keep the copy;</item>
/// <item>one SaveChanges: the snapshot, its rows and the pointer move together.</item>
/// </list>
/// A failure never removes or replaces data. Rows of a replaced snapshot are left for the retention
/// job, so a query already reading the old snapshot is not pulled out from under itself.
/// </summary>
public class InventoryRefresher
{
    /// <summary>Closed months fetched in one cycle at most, so a long backlog doesn't stall the loop.</summary>
    private const int MaxClosedMonthsPerCycle = 12;

    private readonly PlatformDbContext _db;
    private readonly IArtifactSourceFactory _sources;
    private readonly TimeProvider _clock;
    private readonly ILogger<InventoryRefresher> _logger;

    public InventoryRefresher(
        PlatformDbContext db, IArtifactSourceFactory sources, TimeProvider clock, ILogger<InventoryRefresher> logger)
    {
        _db = db;
        _sources = sources;
        _clock = clock;
        _logger = logger;
    }

    public async Task RefreshAsync(InventoryFeedOptions feed, CancellationToken ct)
    {
        var type = feed.Type.Trim().ToLowerInvariant();
        var tracker = await GetOrCreateRowAsync(feed.Name, type, "", ct);
        var source = _sources.Create(feed);

        await FetchAndAcceptAsync(tracker, type, source, expectedPeriod: null, ct);

        if (type == InventoryFeedTypes.Cost && feed.Source.ClosedMonths is { } closed)
            await RefreshClosedMonthsAsync(feed.Name, source, closed.From, ct);
    }

    private async Task RefreshClosedMonthsAsync(string feedName, IArtifactSource source, string from, CancellationToken ct)
    {
        var stored = (await _db.InventoryFeeds.AsNoTracking()
                .Where(f => f.FeedName == feedName && f.Period != "" && f.Complete)
                .Select(f => f.Period)
                .ToListAsync(ct))
            .ToHashSet(StringComparer.Ordinal);

        IReadOnlyList<string> months;
        try
        {
            months = await source.ListClosedMonthsAsync(from, stored, _clock.GetUtcNow(), ct);
        }
        catch (InventoryFetchException ex)
        {
            _logger.LogWarning("Service inventory: could not list the closed months of {Feed}: {Reason}",
                LogSanitizer.Clean(feedName), ex.Message);
            var tracker = await GetOrCreateRowAsync(feedName, InventoryFeedTypes.Cost, "", ct);
            await RecordFailureAsync(tracker, $"Could not list the closed months: {ex.Message}", ct);
            return;
        }

        foreach (var period in months.Take(MaxClosedMonthsPerCycle))
        {
            var row = await _db.InventoryFeeds.FirstOrDefaultAsync(f => f.FeedKey == InventoryFeed.KeyFor(feedName, period), ct);
            try
            {
                var result = await source.ForMonth(period).FetchIfChangedAsync(row?.LastSourceVersion, ct);
                row ??= await GetOrCreateRowAsync(feedName, InventoryFeedTypes.Cost, period, ct);
                await AcceptAsync(row, InventoryFeedTypes.Cost, result, expectedPeriod: period, ct);
            }
            catch (InventoryFetchException ex) when (ex.Status == HttpStatusCode.NotFound)
            {
                // Over plain HTTPS a month is found by fetching it: 404 means not published yet.
            }
            catch (InventoryFetchException ex)
            {
                row ??= await GetOrCreateRowAsync(feedName, InventoryFeedTypes.Cost, period, ct);
                await RecordFailureAsync(row, ex.Message, ct);
            }
        }
    }

    private async Task FetchAndAcceptAsync(
        InventoryFeed tracker, string type, IArtifactSource source, string? expectedPeriod, CancellationToken ct)
    {
        FetchResult result;
        try
        {
            result = await source.FetchIfChangedAsync(tracker.LastSourceVersion, ct);
        }
        catch (InventoryFetchException ex)
        {
            await RecordFailureAsync(tracker, ex.Message, ct);
            return;
        }
        await AcceptAsync(tracker, type, result, expectedPeriod, ct);
    }

    private async Task AcceptAsync(InventoryFeed tracker, string type, FetchResult result, string? expectedPeriod, CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        if (!result.Changed || result.File is null)
        {
            tracker.LastSourceVersion = result.Version ?? tracker.LastSourceVersion;
            MarkSuccess(tracker, now);
            await _db.SaveChangesAsync(ct);
            return;
        }

        var file = result.File;
        var read = DocumentReader.Read(type, file.Content);
        if (!read.Ok)
        {
            // The version is not recorded, so the file is read again next time and the reason stays
            // visible until the producer publishes a file this portal can read.
            await RecordFailureAsync(tracker, $"The file from {file.Location} was not accepted: {read.Error}.", ct);
            return;
        }
        if (expectedPeriod is not null && (read.Period != expectedPeriod || read.Complete != true))
        {
            await RecordFailureAsync(tracker,
                $"The file for {expectedPeriod} was not accepted: it is {(read.Complete == true ? "" : "an open month for ")}{read.Period}.", ct);
            return;
        }

        var sha = ContentHash.Sha256(file.Content);
        var target = type == InventoryFeedTypes.Cost && tracker.Period == ""
            ? await GetOrCreateRowAsync(tracker.FeedName, type, read.Period, ct)
            : tracker;

        var existing = await _db.InventorySnapshots.AsNoTracking()
            .Where(s => s.FeedName == tracker.FeedName && s.Period == read.Period && s.ContentSha256 == sha)
            .Select(s => new { s.Id })
            .FirstOrDefaultAsync(ct);

        if (existing is not null && target.CurrentSnapshotId == existing.Id)
        {
            // Same content as the copy already shown: nothing to store.
            tracker.CurrentSnapshotId ??= existing.Id;
            tracker.LastSourceVersion = Truncate(file.Version, 200);
            MarkSuccess(tracker, now);
            await _db.SaveChangesAsync(ct);
            return;
        }

        var snapshotId = existing?.Id ?? Guid.NewGuid();
        ProjectedRows rows;
        try
        {
            rows = InventoryProjector.Project(snapshotId, type, read.Document!);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Service inventory: projecting {Feed} failed", LogSanitizer.Clean(tracker.FeedKey));
            await RecordFailureAsync(tracker, "The file could not be turned into query rows; the previous copy is kept.", ct);
            return;
        }

        if (existing is null)
        {
            _db.InventorySnapshots.Add(new InventorySnapshot
            {
                Id = snapshotId,
                FeedName = tracker.FeedName,
                FeedType = type,
                Period = read.Period,
                SourceKind = file.SourceKind,
                SourceLocation = Truncate(file.Location, 500),
                SourceVersion = Truncate(file.Version, 200),
                ContentSha256 = sha,
                SchemaVersion = read.SchemaVersion,
                DocumentVersion = read.DocumentVersion,
                GeneratedAt = read.GeneratedAt,
                Complete = read.Complete,
                FetchedAt = now,
                ContentLength = file.Content.Length,
                ContentGzip = Gzip.Compress(file.Content),
            });
        }
        // The same content came back (a flapping tag, a re-published file): the snapshot is reused,
        // and its rows are re-created only if retention removed them while it was not current.
        // Rows are a pure function of the snapshot, so existing ones are already right.
        if (existing is null || !await HasRowsAsync(snapshotId, ct)) AddRows(rows);

        // A closed month is never replaced by an open copy of the same month.
        var movePeriodPointer = !(target != tracker && target.Complete && read.Complete != true);
        if (movePeriodPointer && target.CurrentSnapshotId != snapshotId)
        {
            target.CurrentSnapshotId = snapshotId;
            target.Complete = read.Complete == true;
            target.ProjectionVersion = InventoryProjector.Version;
            target.LastChangedAt = now;
            if (target != tracker) MarkSuccess(target, now);
        }
        if (tracker.CurrentSnapshotId != snapshotId)
        {
            tracker.CurrentSnapshotId = snapshotId;
            tracker.LastChangedAt = now;
        }
        tracker.ProjectionVersion = InventoryProjector.Version;
        tracker.Complete = target == tracker && read.Complete == true;
        tracker.LastSourceVersion = Truncate(file.Version, 200);
        MarkSuccess(tracker, now);

        try
        {
            await _db.SaveChangesAsync(ct);
            _logger.LogInformation("Service inventory: {Feed} refreshed ({Location}, {Period})",
                LogSanitizer.Clean(target.FeedKey), LogSanitizer.Clean(file.Location), read.Period);
        }
        catch (DbUpdateException ex)
        {
            _logger.LogError(ex, "Service inventory: storing {Feed} failed", LogSanitizer.Clean(tracker.FeedKey));
            _db.ChangeTracker.Clear();
            var fresh = await _db.InventoryFeeds.FirstAsync(f => f.FeedKey == tracker.FeedKey, ct);
            await RecordFailureAsync(fresh, "The file could not be stored; the previous copy is kept.", ct);
        }
    }

    private void AddRows(ProjectedRows rows)
    {
        _db.InventoryServices.AddRange(rows.Services);
        _db.InventoryCostMonths.AddRange(rows.CostMonths);
        _db.InventoryCostServices.AddRange(rows.CostServices);
        _db.InventoryCostUnallocated.AddRange(rows.CostUnallocated);
    }

    private async Task<bool> HasRowsAsync(Guid snapshotId, CancellationToken ct) =>
        await _db.InventoryServices.AnyAsync(r => r.SnapshotId == snapshotId, ct)
        || await _db.InventoryCostMonths.AnyAsync(r => r.SnapshotId == snapshotId, ct)
        || await _db.InventoryCostServices.AnyAsync(r => r.SnapshotId == snapshotId, ct)
        || await _db.InventoryCostUnallocated.AnyAsync(r => r.SnapshotId == snapshotId, ct);

    private async Task RemoveRowsAsync(Guid snapshotId, CancellationToken ct)
    {
        _db.InventoryServices.RemoveRange(await _db.InventoryServices.Where(r => r.SnapshotId == snapshotId).ToListAsync(ct));
        _db.InventoryCostMonths.RemoveRange(await _db.InventoryCostMonths.Where(r => r.SnapshotId == snapshotId).ToListAsync(ct));
        _db.InventoryCostServices.RemoveRange(await _db.InventoryCostServices.Where(r => r.SnapshotId == snapshotId).ToListAsync(ct));
        _db.InventoryCostUnallocated.RemoveRange(await _db.InventoryCostUnallocated.Where(r => r.SnapshotId == snapshotId).ToListAsync(ct));
    }

    /// <summary>
    /// Re-projects every current snapshot whose rows were built by an older projector (or all of
    /// them when <paramref name="force"/>), from its raw copy.
    /// </summary>
    public async Task<int> RebuildAsync(bool force, CancellationToken ct)
    {
        var feeds = await _db.InventoryFeeds
            .Where(f => f.CurrentSnapshotId != null && (force || f.ProjectionVersion < InventoryProjector.Version))
            .ToListAsync(ct);
        var rebuilt = 0;
        foreach (var group in feeds.GroupBy(f => f.CurrentSnapshotId!.Value))
        {
            var snapshot = await _db.InventorySnapshots.AsNoTracking().FirstOrDefaultAsync(s => s.Id == group.Key, ct);
            if (snapshot is null) continue;
            var read = DocumentReader.Read(snapshot.FeedType, Gzip.Decompress(snapshot.ContentGzip));
            if (!read.Ok)
            {
                _logger.LogWarning("Service inventory: snapshot {Id} can no longer be read: {Reason}", snapshot.Id, read.Error);
                continue;
            }
            var rows = InventoryProjector.Project(snapshot.Id, snapshot.FeedType, read.Document!);
            // Delete, then insert the same keys: two saves, in one transaction where the provider
            // has them, so a reader sees the old rows or the new ones.
            await using var tx = _db.Database.IsRelational() ? await _db.Database.BeginTransactionAsync(ct) : null;
            await RemoveRowsAsync(snapshot.Id, ct);
            await _db.SaveChangesAsync(ct);
            AddRows(rows);
            foreach (var feed in group) feed.ProjectionVersion = InventoryProjector.Version;
            await _db.SaveChangesAsync(ct);
            if (tx is not null) await tx.CommitAsync(ct);
            rebuilt++;
        }
        return rebuilt;
    }

    private async Task<InventoryFeed> GetOrCreateRowAsync(string feedName, string type, string period, CancellationToken ct)
    {
        var key = InventoryFeed.KeyFor(feedName, period);
        var local = _db.InventoryFeeds.Local.FirstOrDefault(f => f.FeedKey == key);
        if (local is not null) return local;
        var row = await _db.InventoryFeeds.FirstOrDefaultAsync(f => f.FeedKey == key, ct);
        if (row is not null) return row;
        row = new InventoryFeed { FeedKey = key, FeedName = feedName, FeedType = type, Period = period };
        _db.InventoryFeeds.Add(row);
        return row;
    }

    private static void MarkSuccess(InventoryFeed row, DateTimeOffset now)
    {
        row.LastAttemptAt = now;
        row.LastSuccessAt = now;
        row.LastError = null;
        row.LastErrorAt = null;
        row.ConsecutiveFailures = 0;
    }

    private async Task RecordFailureAsync(InventoryFeed row, string reason, CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        row.LastAttemptAt = now;
        row.LastErrorAt = now;
        row.LastError = Truncate(SecretRedactor.Redact(reason), 1000);
        row.ConsecutiveFailures++;
        _logger.LogWarning("Service inventory: {Feed} was not refreshed: {Reason}",
            LogSanitizer.Clean(row.FeedKey), LogSanitizer.Clean(row.LastError, 1000));
        await _db.SaveChangesAsync(ct);
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}
