using Microsoft.EntityFrameworkCore;
using Platform.Api.Features.Analytics.Models;
using Platform.Api.Features.Promotions.Models;
using Platform.Api.Features.Settings;
using Platform.Api.Infrastructure.Persistence;

namespace Platform.Api.Features.Analytics;

/// <summary>
/// Read-only aggregations over deploy events and promotion candidates. Everything here is
/// computed on demand from the transactional tables — there is no analytics store, no snapshot;
/// numbers are as fresh as the last ingest. Rows are filtered to the reporting window in SQL
/// (riding the (Product, Service, Environment, DeployedAt) index), then bucketing and percentile
/// math run in memory — see <see cref="Percentiles"/> for why.
///
/// <para>Definitional choices (medians over averages, rollbacks counted apart from changes,
/// coverage reported beside every ticket-derived number) are deliberate and documented on the
/// response DTOs — change them only together with the `definition` blocks the responses echo.</para>
/// </summary>
public class AnalyticsService
{
    private readonly PlatformDbContext _db;
    private readonly AppSettingsService _settings;

    private static readonly PromotionStatus[] OpenStatuses =
        [PromotionStatus.Pending, PromotionStatus.Approved, PromotionStatus.Deploying];

    public AnalyticsService(PlatformDbContext db, AppSettingsService settings)
    {
        _db = db;
        _settings = settings;
    }

    // --- Deployment frequency -------------------------------------------------------------

    public async Task<FrequencyResponseDto> GetDeploymentFrequency(
        string? product, string? serviceName, string? environment,
        DateTimeOffset? from, DateTimeOffset? to,
        string bucket, string groupBy, TimeZoneInfo tz,
        bool includeRollbacks, bool includeRedeploys,
        CancellationToken ct)
    {
        var (rangeFrom, rangeTo) = ResolveRange(from, to);
        var span = rangeTo - rangeFrom;
        var prevFrom = rangeFrom - span;

        var query = _db.DeployEvents.AsNoTracking()
            .Where(e => e.DeployedAt >= prevFrom && e.DeployedAt < rangeTo);
        if (!string.IsNullOrWhiteSpace(product)) query = query.Where(e => e.Product == product);
        if (!string.IsNullOrWhiteSpace(serviceName)) query = query.Where(e => e.Service == serviceName);
        if (!string.IsNullOrWhiteSpace(environment)) query = query.Where(e => e.Environment == environment);

        var events = await query
            .Select(e => new EventRow(e.Id, e.Product, e.Service, e.Environment,
                e.Version, e.PreviousVersion, e.IsRollback, e.Status, e.DeployedAt))
            .ToListAsync(ct);

        var current = events.Where(e => e.DeployedAt >= rangeFrom).ToList();
        var previous = events.Where(e => e.DeployedAt < rangeFrom).ToList();

        // Work items per counted deploy — one query for the whole window, grouped in memory.
        var currentIds = current.Select(e => e.Id).ToList();
        var batchByEvent = (await _db.DeployEventWorkItems.AsNoTracking()
                .Where(w => currentIds.Contains(w.DeployEventId))
                .Select(w => w.DeployEventId)
                .ToListAsync(ct))
            .GroupBy(id => id)
            .ToDictionary(g => g.Key, g => g.Count());

        Func<EventRow, FrequencySeriesKeyDto> keyOf = groupBy switch
        {
            "service" => e => new FrequencySeriesKeyDto(e.Product, e.Service, environment.NullIfBlank()),
            "environment" => e => new FrequencySeriesKeyDto(product.NullIfBlank(), serviceName.NullIfBlank(), e.Environment),
            "product" => e => new FrequencySeriesKeyDto(e.Product, null, environment.NullIfBlank()),
            _ => _ => new FrequencySeriesKeyDto(product.NullIfBlank(), serviceName.NullIfBlank(), environment.NullIfBlank()),
        };

        var series = new List<FrequencySeriesDto>();
        var seenKeys = new HashSet<FrequencySeriesKeyDto>();
        foreach (var group in current.GroupBy(keyOf).OrderBy(g => g.Key.Product).ThenBy(g => g.Key.ServiceName).ThenBy(g => g.Key.Environment))
        {
            var buckets = BuildBuckets(rangeFrom, rangeTo, bucket, tz);
            var countedTimes = new List<DateTimeOffset>();
            var counted = 0; var failed = 0; var rollbacks = 0;
            var batchSizes = new List<double>();
            DateTimeOffset? lastDeployedAt = null;

            foreach (var e in group)
            {
                var b = buckets[BucketStart(e.DeployedAt, bucket, tz)];
                var kind = Classify(e, includeRollbacks, includeRedeploys);
                switch (kind)
                {
                    case EventKind.Counted:
                        b.Count++; counted++;
                        countedTimes.Add(e.DeployedAt);
                        batchSizes.Add(batchByEvent.GetValueOrDefault(e.Id));
                        if (lastDeployedAt is null || e.DeployedAt > lastDeployedAt) lastDeployedAt = e.DeployedAt;
                        break;
                    case EventKind.Failed:
                        b.Failed++; failed++;
                        break;
                    case EventKind.Rollback:
                        b.Rollbacks++; rollbacks++;
                        break;
                    case EventKind.RollbackCounted:
                        b.Rollbacks++; rollbacks++;
                        b.Count++; counted++;
                        countedTimes.Add(e.DeployedAt);
                        if (lastDeployedAt is null || e.DeployedAt > lastDeployedAt) lastDeployedAt = e.DeployedAt;
                        break;
                    case EventKind.Skipped:
                        break;
                }
            }

            var prevTotal = previous.Where(e => Equals(keyOf(e), group.Key))
                .Count(e => Classify(e, includeRollbacks, includeRedeploys) is EventKind.Counted or EventKind.RollbackCounted);

            countedTimes.Sort();
            var intervals = new List<double>();
            for (var i = 1; i < countedTimes.Count; i++)
                intervals.Add((countedTimes[i] - countedTimes[i - 1]).TotalHours);

            var attempts = counted + failed;
            seenKeys.Add(group.Key);
            series.Add(new FrequencySeriesDto(
                group.Key,
                buckets.Values.Select(b => (FrequencyBucketDto)b).ToList(),
                new FrequencySummaryDto(
                    Total: counted,
                    PerWeek: span.TotalDays > 0 ? Math.Round(counted / span.TotalDays * 7, 2) : 0,
                    MedianIntervalHours: Round(Percentiles.Median(intervals)),
                    LongestGapHours: intervals.Count > 0 ? Round(intervals.Max()) : null,
                    LastDeployedAt: lastDeployedAt,
                    ChangeFailureRate: attempts > 0 ? Math.Round((failed + rollbacks) / (double)attempts, 3) : null,
                    PreviousPeriodTotal: prevTotal,
                    BatchSizeP50: Round(Percentiles.Median(batchSizes)))));
        }

        // Stale services are the alarm this report exists to ring, and a plain GROUP BY silently
        // drops them: a service with no deploys in the window has no rows to group. When grouping
        // by service, emit an explicit zero series (with its true all-time last deploy) for every
        // service that has ever deployed under the current filters but didn't in this window.
        if (groupBy == "service")
        {
            var staleQuery = _db.DeployEvents.AsNoTracking().Where(e => e.DeployedAt < rangeTo);
            if (!string.IsNullOrWhiteSpace(product)) staleQuery = staleQuery.Where(e => e.Product == product);
            if (!string.IsNullOrWhiteSpace(serviceName)) staleQuery = staleQuery.Where(e => e.Service == serviceName);
            if (!string.IsNullOrWhiteSpace(environment)) staleQuery = staleQuery.Where(e => e.Environment == environment);

            var lastByService = await staleQuery
                .GroupBy(e => new { e.Product, e.Service })
                .Select(g => new { g.Key.Product, g.Key.Service, Last = g.Max(e => e.DeployedAt) })
                .ToListAsync(ct);

            foreach (var svc in lastByService.OrderBy(s => s.Product).ThenBy(s => s.Service))
            {
                var key = new FrequencySeriesKeyDto(svc.Product, svc.Service, environment.NullIfBlank());
                if (seenKeys.Contains(key)) continue;
                series.Add(new FrequencySeriesDto(
                    key,
                    BuildBuckets(rangeFrom, rangeTo, bucket, tz).Values.Select(b => (FrequencyBucketDto)b).ToList(),
                    new FrequencySummaryDto(
                        Total: 0, PerWeek: 0,
                        MedianIntervalHours: null, LongestGapHours: null,
                        LastDeployedAt: svc.Last,
                        ChangeFailureRate: null,
                        PreviousPeriodTotal: previous.Count(e =>
                            e.Product == svc.Product && e.Service == svc.Service
                            && Classify(e, includeRollbacks, includeRedeploys) is EventKind.Counted or EventKind.RollbackCounted),
                        BatchSizeP50: null)));
            }
        }

        return new FrequencyResponseDto(
            new FrequencyDefinitionDto(
                Bucket: bucket,
                GroupBy: groupBy,
                Tz: tz.Id,
                IncludeRollbacks: includeRollbacks,
                IncludeRedeploys: includeRedeploys,
                ChangeFailureRate: "(failed + rollbacks) / (succeeded + failed) within the window"),
            new AnalyticsRangeDto(rangeFrom, rangeTo),
            series);
    }

    // --- Work-item × environment matrix ---------------------------------------------------

    public async Task<MatrixResponseDto> GetWorkItemMatrix(
        string product, string? notYetOnEnv, string? reachedEnv,
        DateTimeOffset? from, DateTimeOffset? to,
        int limit, int offset, CancellationToken ct)
    {
        var (rangeFrom, rangeTo) = ResolveRange(from, to);

        // Full deploy history for the product's tickets — checkmarks show complete state,
        // the window only selects which stories appear.
        var deployRows = await _db.DeployEventWorkItems.AsNoTracking()
            .Where(w => w.Product == product)
            .Join(_db.DeployEvents.AsNoTracking(), w => w.DeployEventId, e => e.Id,
                (w, e) => new { w.WorkItemKey, w.Title, w.Url, e.Environment, e.Version, e.Status, e.DeployedAt, EventId = e.Id })
            .ToListAsync(ct);

        var candidateRows = await _db.PromotionWorkItems.AsNoTracking()
            .Where(w => w.Product == product)
            .Join(_db.PromotionCandidates.AsNoTracking(), w => w.CandidateId, c => c.Id,
                (w, c) => new { w.WorkItemKey, w.Title, w.Url, c.TargetEnv, c.Status, c.Version, c.CreatedAt, c.ApprovedAt, CandidateId = c.Id })
            .Where(x => OpenStatuses.Contains(x.Status))
            .ToListAsync(ct);

        var envUniverse = await _db.DeployEvents.AsNoTracking()
            .Where(e => e.Product == product)
            .Select(e => e.Environment)
            .Distinct()
            .ToListAsync(ct);
        foreach (var env in candidateRows.Select(c => c.TargetEnv))
            if (!envUniverse.Contains(env, StringComparer.OrdinalIgnoreCase)) envUniverse.Add(env);

        var environments = await OrderEnvironments(envUniverse, ct);
        var envRank = environments.Select((e, i) => (e, i))
            .ToDictionary(x => x.e, x => x.i, StringComparer.OrdinalIgnoreCase);

        var items = new Dictionary<string, MatrixBuilder>(StringComparer.OrdinalIgnoreCase);
        MatrixBuilder Get(string key) => items.TryGetValue(key, out var b) ? b : items[key] = new MatrixBuilder(key);

        foreach (var r in deployRows)
        {
            var b = Get(r.WorkItemKey);
            b.Observe(r.Title, r.Url, r.DeployedAt);
            if (!IsSucceeded(r.Status)) continue;
            b.DeployedInWindow |= r.DeployedAt >= rangeFrom && r.DeployedAt < rangeTo;
            if (!b.Deployed.TryGetValue(r.Environment, out var cell) || r.DeployedAt > cell.At)
                b.Deployed[r.Environment] = (r.Version, r.DeployedAt, r.EventId);
            if (!b.FirstDeployed.TryGetValue(r.Environment, out var first) || r.DeployedAt < first)
                b.FirstDeployed[r.Environment] = r.DeployedAt;
        }

        foreach (var r in candidateRows)
        {
            var b = Get(r.WorkItemKey);
            b.Observe(r.Title, r.Url, r.ApprovedAt ?? r.CreatedAt);
            b.HasOpenCandidate = true;
            // Latest candidate wins per env; deployed cells win over any candidate state.
            if (!b.Candidates.TryGetValue(r.TargetEnv, out var cell) || r.CreatedAt > cell.CreatedAt)
                b.Candidates[r.TargetEnv] = (r.Status, r.Version, r.CreatedAt, r.ApprovedAt, r.CandidateId);
        }

        // Selection: any activity in the window, or a currently open candidate.
        var selected = items.Values
            .Where(b => b.DeployedInWindow || b.HasOpenCandidate
                || (b.LastActivityAt >= rangeFrom && b.LastActivityAt < rangeTo))
            .ToList();

        if (!string.IsNullOrWhiteSpace(notYetOnEnv))
            selected = selected.Where(b => !b.Deployed.ContainsKey(notYetOnEnv)).ToList();

        if (!string.IsNullOrWhiteSpace(reachedEnv))
            selected = selected
                .Where(b => b.FirstDeployed.TryGetValue(reachedEnv, out var first)
                    && first >= rangeFrom && first < rangeTo)
                .ToList();

        var totals = environments.ToDictionary(e => e,
            e => selected.Count(b => b.Deployed.ContainsKey(e)), StringComparer.OrdinalIgnoreCase);

        var page = selected
            .OrderByDescending(b => b.LastActivityAt)
            .Skip(offset).Take(limit)
            .Select(b => b.Build(environments, envRank))
            .ToList();

        // Coverage over the product's deployments inside the window.
        var windowEvents = await _db.DeployEvents.AsNoTracking()
            .Where(e => e.Product == product && e.DeployedAt >= rangeFrom && e.DeployedAt < rangeTo)
            .Select(e => e.Id)
            .ToListAsync(ct);
        var withWorkItem = await _db.DeployEventWorkItems.AsNoTracking()
            .Where(w => windowEvents.Contains(w.DeployEventId))
            .Select(w => w.DeployEventId)
            .Distinct()
            .CountAsync(ct);
        var coverage = new MatrixCoverageDto(
            Deployments: windowEvents.Count,
            WithoutWorkItem: windowEvents.Count - withWorkItem,
            Ratio: windowEvents.Count > 0 ? Math.Round(withWorkItem / (double)windowEvents.Count, 3) : 0);

        return new MatrixResponseDto(environments, coverage, totals, selected.Count, page,
            new AnalyticsRangeDto(rangeFrom, rangeTo));
    }

    // --- Promotion queue --------------------------------------------------------------------

    public async Task<QueueResponseDto> GetPromotionQueue(
        string? product, DateTimeOffset? from, DateTimeOffset? to, CancellationToken ct)
    {
        var (rangeFrom, rangeTo) = ResolveRange(from, to);
        var now = DateTimeOffset.UtcNow;

        var openQuery = _db.PromotionCandidates.AsNoTracking()
            .Where(c => OpenStatuses.Contains(c.Status));
        if (!string.IsNullOrWhiteSpace(product)) openQuery = openQuery.Where(c => c.Product == product);

        var open = await openQuery
            .Select(c => new { c.Product, c.TargetEnv, c.Status, c.CreatedAt, c.ApprovedAt })
            .ToListAsync(ct);

        var envOrder = await OrderEnvironments(open.Select(c => c.TargetEnv).Distinct().ToList(), ct);
        var envRank = envOrder.Select((e, i) => (e, i)).ToDictionary(x => x.e, x => x.i, StringComparer.OrdinalIgnoreCase);

        var edges = open
            .GroupBy(c => (c.Product, c.TargetEnv))
            .Select(g =>
            {
                var pending = g.Where(c => c.Status == PromotionStatus.Pending).ToList();
                var awaiting = g.Where(c => c.Status is PromotionStatus.Approved or PromotionStatus.Deploying).ToList();
                return new QueueEdgeDto(
                    g.Key.Product, g.Key.TargetEnv,
                    Pending: pending.Count,
                    AwaitingDeploy: awaiting.Count,
                    OldestPendingHours: pending.Count > 0
                        ? Round((now - pending.Min(c => c.CreatedAt)).TotalHours) : null,
                    OldestAwaitingDeployHours: awaiting.Count > 0
                        ? Round((now - awaiting.Min(c => c.ApprovedAt ?? c.CreatedAt)).TotalHours) : null);
            })
            .OrderBy(e => e.Product)
            .ThenBy(e => envRank.GetValueOrDefault(e.TargetEnv, int.MaxValue))
            .ToList();

        // Latencies over the window — includes closed candidates, so query separately.
        var latencyQuery = _db.PromotionCandidates.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(product)) latencyQuery = latencyQuery.Where(c => c.Product == product);

        var approvedInWindow = await latencyQuery
            .Where(c => c.ApprovedAt != null && c.ApprovedAt >= rangeFrom && c.ApprovedAt < rangeTo)
            .Select(c => new { c.CreatedAt, c.ApprovedAt })
            .ToListAsync(ct);
        var deployedInWindow = await latencyQuery
            .Where(c => c.DeployedAt != null && c.ApprovedAt != null
                && c.DeployedAt >= rangeFrom && c.DeployedAt < rangeTo)
            .Select(c => new { c.ApprovedAt, c.DeployedAt })
            .ToListAsync(ct);

        var approvalHours = approvedInWindow.Select(c => (c.ApprovedAt!.Value - c.CreatedAt).TotalHours).ToList();
        var deployHours = deployedInWindow.Select(c => (c.DeployedAt!.Value - c.ApprovedAt!.Value).TotalHours).ToList();

        return new QueueResponseDto(
            edges,
            new LatencyStatsDto(approvalHours.Count,
                Round(Percentiles.Median(approvalHours)), Round(Percentiles.Compute(approvalHours, 0.9))),
            new LatencyStatsDto(deployHours.Count,
                Round(Percentiles.Median(deployHours)), Round(Percentiles.Compute(deployHours, 0.9))),
            new AnalyticsRangeDto(rangeFrom, rangeTo));
    }

    // --- Lead time ---------------------------------------------------------------------------

    public async Task<LeadTimeResponseDto> GetLeadTime(
        string? product, string? serviceName, string? environment,
        DateTimeOffset? from, DateTimeOffset? to,
        string bucket, TimeZoneInfo tz, CancellationToken ct)
    {
        var (rangeFrom, rangeTo) = ResolveRange(from, to);

        var query = _db.DeployEventWorkItems.AsNoTracking()
            .Join(_db.DeployEvents.AsNoTracking(), w => w.DeployEventId, e => e.Id,
                (w, e) => new { w.WorkItemKey, w.CommittedAt, e.Product, e.Service, e.Environment, e.Status, e.DeployedAt, EventId = e.Id });
        if (!string.IsNullOrWhiteSpace(product)) query = query.Where(x => x.Product == product);
        if (!string.IsNullOrWhiteSpace(serviceName)) query = query.Where(x => x.Service == serviceName);
        if (!string.IsNullOrWhiteSpace(environment)) query = query.Where(x => x.Environment == environment);

        // Grain: first successful deploy per (work item, environment). Load the full history for
        // matching rows so "first" is genuinely first, then keep grains that landed in the window.
        var rows = (await query.Where(x => x.DeployedAt < rangeTo).ToListAsync(ct))
            .Where(x => IsSucceeded(x.Status))
            .GroupBy(x => (Key: x.WorkItemKey, Env: x.Environment),
                comparer: TupleKeyComparer.Instance)
            .Select(g =>
            {
                var first = g.OrderBy(x => x.DeployedAt).First();
                return new { g.Key.Key, g.Key.Env, first.DeployedAt, first.CommittedAt, first.EventId };
            })
            .Where(x => x.DeployedAt >= rangeFrom)
            .ToList();

        var measurable = rows.Where(x => x.CommittedAt is not null).ToList();
        var distinctItems = rows.Select(x => x.Key).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        var distinctWithStart = measurable.Select(x => x.Key).Distinct(StringComparer.OrdinalIgnoreCase).Count();

        var environments = await OrderEnvironments(
            rows.Select(x => x.Env).Distinct(StringComparer.OrdinalIgnoreCase).ToList(), ct);

        var byEnv = environments
            .Select(env =>
            {
                var hours = measurable.Where(x => string.Equals(x.Env, env, StringComparison.OrdinalIgnoreCase))
                    .Select(x => (x.DeployedAt - x.CommittedAt!.Value).TotalHours).ToList();
                return new LeadTimeEnvStatsDto(env, hours.Count,
                    Round(Percentiles.Median(hours)),
                    Round(Percentiles.Compute(hours, 0.75)),
                    Round(Percentiles.Compute(hours, 0.9)));
            })
            .ToList();

        var buckets = measurable
            .GroupBy(x => (Start: BucketStart(x.DeployedAt, bucket, tz), x.Env))
            .OrderBy(g => g.Key.Start).ThenBy(g => g.Key.Env)
            .Select(g =>
            {
                var hours = g.Select(x => (x.DeployedAt - x.CommittedAt!.Value).TotalHours).ToList();
                return new LeadTimeBucketDto(g.Key.Start, g.Key.Env, hours.Count, Round(Percentiles.Median(hours)));
            })
            .ToList();

        var slowest = measurable
            .Select(x => new LeadTimeSlowestDto(x.Key, x.Env,
                Math.Round((x.DeployedAt - x.CommittedAt!.Value).TotalHours, 1), x.EventId))
            .OrderByDescending(x => x.Hours)
            .Take(10)
            .ToList();

        return new LeadTimeResponseDto(
            new LeadTimeDefinitionDto(
                ClockStart: "pull-request.occurredAt",
                ClockStartFallback: "commit.occurredAt",
                ClockStop: "deployEvent.deployedAt (first successful deploy per environment)",
                Grain: "workItem × environment, cumulative from commit"),
            new LeadTimeCoverageDto(distinctItems, distinctWithStart,
                distinctItems > 0 ? Math.Round(distinctWithStart / (double)distinctItems, 3) : 0),
            byEnv, buckets, slowest,
            new AnalyticsRangeDto(rangeFrom, rangeTo));
    }

    // --- Shared helpers ----------------------------------------------------------------------

    private static (DateTimeOffset From, DateTimeOffset To) ResolveRange(DateTimeOffset? from, DateTimeOffset? to)
    {
        var resolvedTo = to ?? DateTimeOffset.UtcNow;
        var resolvedFrom = from ?? resolvedTo.AddDays(-14);
        return (resolvedFrom, resolvedTo);
    }

    private async Task<List<string>> OrderEnvironments(List<string> universe, CancellationToken ct)
    {
        var settings = await _settings.GetSettings(ct);
        var configured = settings.Environments.Select(e => e.Key).ToList();
        var known = configured
            .Where(k => universe.Contains(k, StringComparer.OrdinalIgnoreCase))
            .ToList();
        var unknown = universe
            .Where(k => !configured.Contains(k, StringComparer.OrdinalIgnoreCase))
            .OrderBy(k => k, StringComparer.OrdinalIgnoreCase);
        known.AddRange(unknown);
        return known;
    }

    private static bool IsSucceeded(string status)
        => string.Equals(status, "succeeded", StringComparison.OrdinalIgnoreCase);

    private enum EventKind { Counted, Failed, Rollback, RollbackCounted, Skipped }

    private static EventKind Classify(EventRow e, bool includeRollbacks, bool includeRedeploys)
    {
        if (e.IsRollback)
            return includeRollbacks && IsSucceeded(e.Status) ? EventKind.RollbackCounted : EventKind.Rollback;
        if (!IsSucceeded(e.Status)) return EventKind.Failed;
        var isRedeploy = e.PreviousVersion is not null && e.Version == e.PreviousVersion;
        if (isRedeploy && !includeRedeploys) return EventKind.Skipped;
        return EventKind.Counted;
    }

    /// <summary>All buckets in [from, to) pre-created so charts get explicit zeros, keyed by label.</summary>
    private static Dictionary<string, FrequencyBucketMutable> BuildBuckets(
        DateTimeOffset from, DateTimeOffset to, string bucket, TimeZoneInfo tz)
    {
        var result = new Dictionary<string, FrequencyBucketMutable>();
        var cursor = LocalBucketDate(from, bucket, tz);
        var endLocal = TimeZoneInfo.ConvertTime(to, tz).Date;
        var step = bucket == "week" ? 7 : 1;
        while (cursor <= endLocal)
        {
            var label = cursor.ToString("yyyy-MM-dd");
            result[label] = new FrequencyBucketMutable(label);
            cursor = cursor.AddDays(step);
        }
        return result;
    }

    private static string BucketStart(DateTimeOffset at, string bucket, TimeZoneInfo tz)
        => LocalBucketDate(at, bucket, tz).ToString("yyyy-MM-dd");

    private static DateTime LocalBucketDate(DateTimeOffset at, string bucket, TimeZoneInfo tz)
    {
        var date = TimeZoneInfo.ConvertTime(at, tz).Date;
        if (bucket == "week")
            date = date.AddDays(-(((int)date.DayOfWeek + 6) % 7)); // back to Monday
        return date;
    }

    private static double? Round(double? value) => value is null ? null : Math.Round(value.Value, 1);

    private sealed record EventRow(
        Guid Id, string Product, string Service, string Environment,
        string Version, string? PreviousVersion, bool IsRollback, string Status, DateTimeOffset DeployedAt);

    /// <summary>Mutable bucket accumulator; converted to the immutable DTO implicitly.</summary>
    private sealed class FrequencyBucketMutable
    {
        public FrequencyBucketMutable(string start) => Start = start;
        public string Start { get; }
        public int Count { get; set; }
        public int Failed { get; set; }
        public int Rollbacks { get; set; }
        public static implicit operator FrequencyBucketDto(FrequencyBucketMutable b)
            => new(b.Start, b.Count, b.Failed, b.Rollbacks);
    }

    private sealed class TupleKeyComparer : IEqualityComparer<(string Key, string Env)>
    {
        public static readonly TupleKeyComparer Instance = new();
        public bool Equals((string Key, string Env) x, (string Key, string Env) y)
            => string.Equals(x.Key, y.Key, StringComparison.OrdinalIgnoreCase)
            && string.Equals(x.Env, y.Env, StringComparison.OrdinalIgnoreCase);
        public int GetHashCode((string Key, string Env) obj)
            => HashCode.Combine(
                obj.Key.ToUpperInvariant(),
                obj.Env.ToUpperInvariant());
    }

    /// <summary>Accumulates one matrix row across deploy history and open candidates.</summary>
    private sealed class MatrixBuilder
    {
        public MatrixBuilder(string key) => Key = key;

        public string Key { get; }
        public string? Title { get; private set; }
        public string? Url { get; private set; }
        public DateTimeOffset LastActivityAt { get; private set; }
        public bool DeployedInWindow { get; set; }
        public bool HasOpenCandidate { get; set; }
        public Dictionary<string, (string Version, DateTimeOffset At, Guid EventId)> Deployed { get; } =
            new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, DateTimeOffset> FirstDeployed { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, (PromotionStatus Status, string Version, DateTimeOffset CreatedAt, DateTimeOffset? ApprovedAt, Guid CandidateId)> Candidates { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        public void Observe(string? title, string? url, DateTimeOffset activityAt)
        {
            if (!string.IsNullOrWhiteSpace(title)) Title = title;
            if (!string.IsNullOrWhiteSpace(url)) Url = url;
            if (activityAt > LastActivityAt) LastActivityAt = activityAt;
        }

        public MatrixItemDto Build(List<string> environments, Dictionary<string, int> envRank)
        {
            var envs = new Dictionary<string, MatrixCellDto>(StringComparer.OrdinalIgnoreCase);
            string? furthest = null;
            var furthestRank = -1;

            foreach (var env in environments)
            {
                if (Deployed.TryGetValue(env, out var d))
                {
                    envs[env] = new MatrixCellDto("deployed", d.Version, d.At, DeployEventId: d.EventId);
                    var rank = envRank.GetValueOrDefault(env, -1);
                    if (rank > furthestRank) { furthestRank = rank; furthest = env; }
                }
                else if (Candidates.TryGetValue(env, out var c))
                {
                    var state = c.Status == PromotionStatus.Pending ? "awaiting-approval" : "approved-awaiting-deploy";
                    envs[env] = new MatrixCellDto(state, c.Version, c.ApprovedAt ?? c.CreatedAt, CandidateId: c.CandidateId);
                }
                else
                {
                    envs[env] = new MatrixCellDto("absent");
                }
            }

            return new MatrixItemDto(Key, Title, Url, furthest, envs, LastActivityAt);
        }
    }
}

internal static class StringAnalyticsExtensions
{
    public static string? NullIfBlank(this string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value;
}
