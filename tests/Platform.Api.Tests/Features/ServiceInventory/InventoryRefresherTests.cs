using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.Api.Features.ServiceInventory;
using Platform.Api.Features.ServiceInventory.Http;
using Platform.Api.Features.ServiceInventory.Models;
using Platform.Api.Features.ServiceInventory.Sources;
using Platform.Api.Infrastructure.Persistence;

namespace Platform.Api.Tests.Features.ServiceInventory;

/// <summary>A source whose answers the test scripts; records what it was asked.</summary>
internal sealed class ScriptedSource : IArtifactSource
{
    public Func<string?, FetchResult> Next { get; set; } = _ => throw new InvalidOperationException("no answer scripted");
    public Dictionary<string, Func<FetchResult>> Months { get; } = new();
    public List<string> ListedMonths { get; set; } = [];
    public List<string?> AskedWith { get; } = [];

    public string Kind => "test";
    public string Location => "test.example.com/feed";

    public Task<FetchResult> FetchIfChangedAsync(string? lastVersion, CancellationToken ct)
    {
        AskedWith.Add(lastVersion);
        return Task.FromResult(Next(lastVersion));
    }

    public Task<IReadOnlyList<string>> ListClosedMonthsAsync(string from, ISet<string> stored, DateTimeOffset now, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<string>>(ListedMonths.Where(m => !stored.Contains(m)).ToList());

    public IArtifactSource ForMonth(string period) => new MonthSource(Months[period]);

    private sealed class MonthSource(Func<FetchResult> answer) : IArtifactSource
    {
        public string Kind => "test";
        public string Location => "test.example.com/month";
        public Task<FetchResult> FetchIfChangedAsync(string? lastVersion, CancellationToken ct) => Task.FromResult(answer());
        public Task<IReadOnlyList<string>> ListClosedMonthsAsync(string from, ISet<string> stored, DateTimeOffset now, CancellationToken ct) => throw new NotSupportedException();
        public IArtifactSource ForMonth(string period) => throw new NotSupportedException();
    }

    public static FetchResult File(byte[] content, string version) =>
        FetchResult.Downloaded(new ArtifactFile(content, version, "test.example.com/feed", "test"));
}

internal sealed class ScriptedFactory(IArtifactSource source) : IArtifactSourceFactory
{
    public IArtifactSource Create(InventoryFeedOptions feed) => source;
}

public class InventoryRefresherTests
{
    private readonly PlatformDbContext _db = Db.InMemory();
    private readonly ScriptedSource _source = new();
    private readonly ManualClock _clock = new(new DateTimeOffset(2026, 11, 20, 16, 0, 0, TimeSpan.Zero));

    private InventoryRefresher Refresher() => new(_db, new ScriptedFactory(_source), _clock, NullLogger<InventoryRefresher>.Instance);

    private static InventoryFeedOptions Feed(string type, ClosedMonthsOptions? closed = null) => new()
    {
        Name = type, Type = type,
        Source = new() { Kind = "https", Url = "https://test.example.com/feed", ClosedMonths = closed },
    };

    private InventoryFeed Row(string key) => _db.InventoryFeeds.AsNoTracking().Single(f => f.FeedKey == key);

    private static byte[] Modified(string fixture, Action<JsonNode> change)
    {
        var node = JsonNode.Parse(Fixture.Bytes(fixture))!;
        change(node);
        return Encoding.UTF8.GetBytes(node.ToJsonString());
    }

    [Fact]
    public async Task A_new_file_stores_the_snapshot_its_rows_and_the_pointer_together()
    {
        _source.Next = _ => ScriptedSource.File(Fixture.Bytes("catalog.json"), "sha256:v1");

        await Refresher().RefreshAsync(Feed("catalog"), default);

        var row = Row("catalog");
        var snapshot = _db.InventorySnapshots.Single();
        Assert.Equal(snapshot.Id, row.CurrentSnapshotId);
        Assert.Equal("sha256:v1", row.LastSourceVersion);
        Assert.Equal(_clock.Now, row.LastSuccessAt);
        Assert.Null(row.LastError);
        Assert.Equal(5, snapshot.DocumentVersion);
        Assert.Equal(Fixture.Bytes("catalog.json"), Gzip.Decompress(snapshot.ContentGzip));
        Assert.Equal(2, _db.InventoryServices.Count(s => s.SnapshotId == snapshot.Id));
    }

    [Fact]
    public async Task An_unchanged_source_records_the_attempt_and_stores_nothing()
    {
        _source.Next = _ => ScriptedSource.File(Fixture.Bytes("catalog.json"), "sha256:v1");
        await Refresher().RefreshAsync(Feed("catalog"), default);

        _clock.Now = _clock.Now.AddMinutes(15);
        _source.Next = v => FetchResult.Unchanged(v);
        await Refresher().RefreshAsync(Feed("catalog"), default);

        Assert.Equal("sha256:v1", _source.AskedWith.Last());
        Assert.Single(_db.InventorySnapshots);
        Assert.Equal(_clock.Now, Row("catalog").LastSuccessAt);
    }

    [Fact]
    public async Task A_new_version_with_the_same_content_stores_no_new_snapshot()
    {
        _source.Next = _ => ScriptedSource.File(Fixture.Bytes("catalog.json"), "digest-1");
        await Refresher().RefreshAsync(Feed("catalog"), default);

        _source.Next = _ => ScriptedSource.File(Fixture.Bytes("catalog.json"), "digest-2");
        await Refresher().RefreshAsync(Feed("catalog"), default);

        Assert.Single(_db.InventorySnapshots);
        Assert.Equal("digest-2", Row("catalog").LastSourceVersion);
    }

    [Theory]
    [InlineData("fetch")]
    [InlineData("blocked")]
    [InlineData("oversize")]
    [InlineData("unsupported")]
    [InlineData("not-json")]
    public async Task A_failed_attempt_keeps_the_previous_copy_and_records_why(string failure)
    {
        _source.Next = _ => ScriptedSource.File(Fixture.Bytes("cost-2026-11.json"), "v1");
        await Refresher().RefreshAsync(Feed("cost"), default);
        var before = Row("cost:2026-11").CurrentSnapshotId;
        var rowsBefore = _db.InventoryCostServices.Count();

        _clock.Now = _clock.Now.AddHours(1);
        _source.Next = failure switch
        {
            "fetch" => _ => throw new InventoryFetchException("files.example.com/cost.json answered 500 Internal Server Error.", HttpStatusCode.InternalServerError),
            "blocked" => _ => throw new InventoryFetchException("Refused evil.example.net/x: the host is not in AllowedHosts."),
            "oversize" => _ => throw new InventoryFetchException("files.example.com/cost.json is more than the 1024 bytes allowed."),
            "unsupported" => _ => ScriptedSource.File(Fixture.Bytes("cost-v1.json"), "v2"),
            _ => _ => ScriptedSource.File("<html>"u8.ToArray(), "v2"),
        };
        await Refresher().RefreshAsync(Feed("cost"), default);

        var tracker = Row("cost");
        Assert.Equal(before, Row("cost:2026-11").CurrentSnapshotId);
        Assert.Equal(before, tracker.CurrentSnapshotId);
        Assert.Equal(rowsBefore, _db.InventoryCostServices.Count());
        Assert.Single(_db.InventorySnapshots);
        Assert.NotNull(tracker.LastError);
        Assert.Equal(1, tracker.ConsecutiveFailures);
        Assert.Equal(_clock.Now, tracker.LastAttemptAt);
        Assert.Equal(_clock.Now.AddHours(-1), tracker.LastSuccessAt);
        // A rejected file's version is not recorded, so it is read again next time.
        Assert.Equal("v1", tracker.LastSourceVersion);
        if (failure == "unsupported") Assert.Contains("unsupported schemaVersion 1", tracker.LastError);
    }

    [Fact]
    public async Task A_later_success_clears_the_error()
    {
        _source.Next = _ => throw new InventoryFetchException("down");
        await Refresher().RefreshAsync(Feed("catalog"), default);
        await Refresher().RefreshAsync(Feed("catalog"), default);
        Assert.Equal(2, Row("catalog").ConsecutiveFailures);

        _source.Next = _ => ScriptedSource.File(Fixture.Bytes("catalog.json"), "v1");
        await Refresher().RefreshAsync(Feed("catalog"), default);

        Assert.Null(Row("catalog").LastError);
        Assert.Equal(0, Row("catalog").ConsecutiveFailures);
    }

    [Fact]
    public async Task A_stored_error_never_holds_a_credential()
    {
        _source.Next = _ => throw new InventoryFetchException("Authorization: Bearer abc.def.ghi failed");
        await Refresher().RefreshAsync(Feed("catalog"), default);
        Assert.DoesNotContain("abc.def.ghi", Row("catalog").LastError);
    }

    [Fact]
    public async Task The_cost_feed_points_its_month_at_the_file_and_keeps_closed_months_once()
    {
        _source.Next = _ => ScriptedSource.File(Fixture.Bytes("cost-2026-11.json"), "latest-1");
        _source.ListedMonths = ["2026-10"];
        var fetchedOctober = 0;
        _source.Months["2026-10"] = () => { fetchedOctober++; return ScriptedSource.File(Fixture.Bytes("cost-2026-10.json"), "oct-1"); };
        var feed = Feed("cost", new ClosedMonthsOptions { From = "2026-10" });

        await Refresher().RefreshAsync(feed, default);
        await Refresher().RefreshAsync(feed, default);

        Assert.NotNull(Row("cost:2026-11").CurrentSnapshotId);
        Assert.False(Row("cost:2026-11").Complete);
        var october = Row("cost:2026-10");
        Assert.True(october.Complete);
        Assert.Equal(1, fetchedOctober);
        Assert.Equal(["EUR", "USD"], _db.InventoryCostMonths
            .Where(m => m.SnapshotId == october.CurrentSnapshotId && m.Source == "").Select(m => m.Currency).OrderBy(c => c).ToList());
    }

    [Fact]
    public async Task A_closed_month_is_not_replaced_by_an_open_copy_of_the_same_month()
    {
        var closed = Modified("cost-2026-11.json", n => n["complete"] = true);
        _source.Next = _ => FetchResult.Unchanged(null);
        _source.ListedMonths = ["2026-11"];
        _source.Months["2026-11"] = () => ScriptedSource.File(closed, "nov-closed");
        await Refresher().RefreshAsync(Feed("cost", new ClosedMonthsOptions { From = "2026-11" }), default);
        var closedId = Row("cost:2026-11").CurrentSnapshotId;

        _source.Next = _ => ScriptedSource.File(Fixture.Bytes("cost-2026-11.json"), "latest-open");
        await Refresher().RefreshAsync(Feed("cost"), default);

        Assert.Equal(closedId, Row("cost:2026-11").CurrentSnapshotId);
        Assert.True(Row("cost:2026-11").Complete);
    }

    [Fact]
    public async Task A_closed_month_file_for_another_month_is_rejected()
    {
        _source.Next = _ => FetchResult.Unchanged(null);
        _source.ListedMonths = ["2026-09"];
        _source.Months["2026-09"] = () => ScriptedSource.File(Fixture.Bytes("cost-2026-10.json"), "wrong");

        await Refresher().RefreshAsync(Feed("cost", new ClosedMonthsOptions { From = "2026-09" }), default);

        Assert.Null(Row("cost:2026-09").CurrentSnapshotId);
        Assert.Contains("2026-09", Row("cost:2026-09").LastError);
    }

    [Fact]
    public async Task A_month_that_is_not_published_yet_is_skipped_silently()
    {
        _source.Next = _ => FetchResult.Unchanged(null);
        _source.ListedMonths = ["2026-10"];
        _source.Months["2026-10"] = () => throw new InventoryFetchException("not there", HttpStatusCode.NotFound);

        await Refresher().RefreshAsync(Feed("cost", new ClosedMonthsOptions { From = "2026-10" }), default);

        Assert.False(_db.InventoryFeeds.Any(f => f.FeedKey == "cost:2026-10"));
    }

    [Fact]
    public async Task Rebuild_restores_rows_from_the_raw_copy()
    {
        _source.Next = _ => ScriptedSource.File(Fixture.Bytes("cost-2026-10.json"), "v1");
        await Refresher().RefreshAsync(Feed("cost"), default);
        var expected = _db.InventoryCostServices.Count();
        _db.InventoryCostServices.RemoveRange(_db.InventoryCostServices);
        _db.InventoryCostMonths.RemoveRange(_db.InventoryCostMonths);
        await _db.SaveChangesAsync();

        var rebuilt = await Refresher().RebuildAsync(force: true, default);

        Assert.Equal(1, rebuilt);
        Assert.Equal(expected, _db.InventoryCostServices.Count());
        Assert.NotEmpty(_db.InventoryCostMonths);
    }
}

public class InventoryRetentionTests
{
    [Fact]
    public async Task Status_keeps_a_week_in_full_then_one_a_day_for_ninety_days_and_never_the_current_copy()
    {
        var db = Db.InMemory();
        var now = new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);
        Guid Add(DateTimeOffset at)
        {
            var id = Guid.NewGuid();
            db.InventorySnapshots.Add(new InventorySnapshot
            {
                Id = id, FeedName = "status", FeedType = "status", SourceKind = "test", SourceLocation = "x",
                SourceVersion = "v", ContentSha256 = "sha256:" + id, FetchedAt = at, ContentGzip = [1],
            });
            return id;
        }

        var recent = Enumerable.Range(0, 6).Select(h => Add(now.AddHours(-h * 4))).ToList();           // within 7 days
        var dayMorning = Add(now.AddDays(-10).Date.AddHours(8));                                       // day -10, earlier
        var dayEvening = Add(now.AddDays(-10).Date.AddHours(20));                                      // day -10, last of day
        var ancient = Add(now.AddDays(-120));                                                          // past 90 days
        var ancientButCurrent = Add(now.AddDays(-200));
        db.InventoryFeeds.Add(new InventoryFeed { FeedKey = "status", FeedName = "status", FeedType = "status", CurrentSnapshotId = ancientButCurrent });
        await db.SaveChangesAsync();

        var (dropped, _) = await new InventoryRetention(db, new ManualClock(now)).RunAsync(default);

        var left = db.InventorySnapshots.Select(s => s.Id).ToHashSet();
        Assert.All(recent, id => Assert.Contains(id, left));
        Assert.Contains(dayEvening, left);
        Assert.DoesNotContain(dayMorning, left);
        Assert.DoesNotContain(ancient, left);
        Assert.Contains(ancientButCurrent, left);
        Assert.Equal(2, dropped);
    }

    [Fact]
    public async Task Rows_of_snapshots_no_feed_points_to_are_removed()
    {
        var db = Db.InMemory();
        var current = Guid.NewGuid();
        var old = Guid.NewGuid();
        db.InventoryFeeds.Add(new InventoryFeed { FeedKey = "catalog", FeedName = "catalog", FeedType = "catalog", CurrentSnapshotId = current });
        db.InventoryServices.AddRange(
            new InventoryService { SnapshotId = current, ServiceId = "a" },
            new InventoryService { SnapshotId = old, ServiceId = "a" });
        await db.SaveChangesAsync();

        var (_, rows) = await new InventoryRetention(db, TimeProvider.System).RunAsync(default);

        Assert.Equal(1, rows);
        Assert.Equal(current, db.InventoryServices.Single().SnapshotId);
    }
}
