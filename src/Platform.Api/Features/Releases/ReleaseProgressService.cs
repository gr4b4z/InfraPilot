using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Platform.Api.Features.Deployments;
using Platform.Api.Features.Deployments.Models;
using Platform.Api.Features.Migrations.Models;
using Platform.Api.Features.Promotions;
using Platform.Api.Features.Promotions.Models;
using Platform.Api.Features.Releases.Models;
using Platform.Api.Infrastructure.Persistence;

namespace Platform.Api.Features.Releases;

/// <summary>
/// What the portal's own records say about a release: per service, the newest build of the line on
/// staging, the promotion carrying it to production, what production runs and the migrations of that
/// version — and from those, the four checks the release steps carry (<see cref="ReleaseChecks"/>).
/// Computed on every read, never stored: deploy events, promotions and migration reports keep
/// arriving (and get backfilled) after a release is written down.
///
/// <para><b>Which services.</b> Every service of the release's products — each one present on its
/// staging or production environment by the end of the window — unless the release manager left it out
/// (<see cref="Release.ExcludedServicesJson"/>, chosen from this same list). Left-out services are
/// listed with <c>Excluded</c> but not counted by the checks. Retired services are not services.</para>
///
/// <para><b>Which records count.</b> For a service with anything on the line (see <see cref="OnLine"/>):
/// its deploys to staging and production and its promotions to production of the line, up to the end of
/// the window. For a service on a version scheme of its own (5.1.x while the train is 5.10): everything
/// it deployed and promoted inside the window. Migrations are matched to the version production runs,
/// whenever they were reported.</para>
///
/// <para><b>The window</b> runs from the release's start (<see cref="Release.StartedAt"/>, else creation)
/// to completion (else now), so a closed release goes on describing what shipped that day after the
/// line's next hotfix. The lower bound only applies to services off the line: nothing is on the line
/// before the line exists. Promotions are read only up to the moment the approval step was marked
/// done, when it is: a promotion raised after the approval round is the service's next change.</para>
/// </summary>
public partial class ReleaseProgressService
{
    private readonly PlatformDbContext _db;
    private readonly PromotionService _promotions;

    public ReleaseProgressService(PlatformDbContext db, PromotionService promotions)
    {
        _db = db;
        _promotions = promotions;
    }

    public async Task<ReleaseProgressDto?> GetAsync(Guid id, CancellationToken ct)
    {
        var release = await _db.Releases.AsNoTracking().Include(r => r.Steps).FirstOrDefaultAsync(r => r.Id == id, ct);
        return release is null ? null : await ComputeAsync(release, DateTimeOffset.UtcNow, ct);
    }

    private sealed record Ev(
        Guid Id, string Product, string Service, string Environment, string Version, string Status, DateTimeOffset DeployedAt,
        string? RunJson)
    {
        public DeployRun? Run => ParseRun(RunJson);
    }

    private sealed record Cand(
        Guid Id, string Product, string Service, string SourceEnv, string Version, PromotionStatus Status,
        DateTimeOffset CreatedAt, DateTimeOffset? ApprovedAt, DateTimeOffset? DeployedAt, string? ExternalRunUrl);

    private sealed record ServiceName(string? Product, string Service)
    {
        public bool Matches(string product, string service) =>
            string.Equals(Service, service, StringComparison.OrdinalIgnoreCase)
            && (Product is null || string.Equals(Product, product, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>"svc" or "product/svc" as the release manager typed it.</summary>
    private static ServiceName ParseServiceName(string value)
    {
        var slash = value.IndexOf('/');
        return slash > 0 && slash < value.Length - 1
            ? new ServiceName(value[..slash].Trim(), value[(slash + 1)..].Trim())
            : new ServiceName(null, value.Trim());
    }

    /// <summary>
    /// A version of the line: "5.10", "5.10.27", "5.10.27-g425981a8", with or without a leading "v".
    /// "5.100.1" is not on "5.10" — the line is matched up to its dot.
    /// </summary>
    public static bool OnLine(string? version, string line)
    {
        if (string.IsNullOrEmpty(version)) return false;
        var v = version.StartsWith('v') || version.StartsWith('V') ? version[1..] : version;
        return v == line || v.StartsWith(line + ".", StringComparison.Ordinal) || v.StartsWith(line + "-", StringComparison.Ordinal);
    }

    public async Task<ReleaseProgressDto> ComputeAsync(Release release, DateTimeOffset now, CancellationToken ct)
    {
        var products = ReleaseService.ParseList(release.ProductsJson);
        var excluded = ReleaseService.ParseList(release.ExcludedServicesJson).Select(ParseServiceName).ToList();
        var staging = release.StagingEnvironment;
        var production = release.ProductionEnvironment;
        var line = release.Version;
        var from = release.StartedAt ?? release.CreatedAt;
        var to = release.CompletedAt ?? release.CancelledAt ?? now;
        if (to < from) to = from;
        // Once the release manager has closed the approval round, a promotion raised after it is the
        // next change of that service — a hotfix, a fresh build — not this release's.
        var promotionsClosedAt = release.Steps
            .FirstOrDefault(s => s.Check == ReleaseChecks.ProductionPromotions && s.Status == ReleaseStepStatuses.Done)
            ?.CompletedAt;
        var promotionsTo = promotionsClosedAt is { } closedAt && closedAt < to ? closedAt : to;

        // The prefixes are matched again in memory by OnLine; the query only has to narrow to them.
        var dot = line + ".";
        var dash = line + "-";
        var vLine = "v" + line;
        var vDot = "v" + dot;
        var vDash = "v" + dash;
        var envs = new List<string> { staging, production };

        // Every service of the products: each one present on staging or production by the end of the
        // window — its newest event there is not its removal. Retired services are not services.
        var cells = await _db.DeployEvents.AsNoTracking()
            .ExcludingDeletedServices(_db)
            .Where(e => products.Contains(e.Product) && envs.Contains(e.Environment) && e.DeployedAt <= to)
            .GroupBy(e => new { e.Product, e.Service, e.Environment })
            .Select(g => new { g.Key.Product, g.Key.Service, g.Key.Environment, Latest = g.Max(e => e.DeployedAt) })
            .ToListAsync(ct);
        var removedAt = (await _db.DeployEvents.AsNoTracking()
                .Where(e => products.Contains(e.Product) && envs.Contains(e.Environment) && e.DeployedAt <= to
                    && e.Status == DeployEventStatuses.Removed)
                .GroupBy(e => new { e.Product, e.Service, e.Environment })
                .Select(g => new { g.Key.Product, g.Key.Service, g.Key.Environment, RemovedAt = g.Max(e => e.DeployedAt) })
                .ToListAsync(ct))
            .ToDictionary(r => (r.Product, r.Service, r.Environment), r => r.RemovedAt);
        var present = cells
            .Where(c => !(removedAt.TryGetValue((c.Product, c.Service, c.Environment), out var removed) && removed >= c.Latest))
            .Select(c => (c.Product, c.Service))
            .ToHashSet();

        // A service's records of the line: deploys to staging and production, promotions to production.
        var events = await _db.DeployEvents.AsNoTracking()
            .ExcludingDeletedServices(_db)
            .Where(e => products.Contains(e.Product) && envs.Contains(e.Environment)
                && e.DeployedAt <= to && e.Status != DeployEventStatuses.Removed
                && (e.Version == line || e.Version.StartsWith(dot) || e.Version.StartsWith(dash)
                    || e.Version == vLine || e.Version.StartsWith(vDot) || e.Version.StartsWith(vDash)))
            .Select(e => new Ev(e.Id, e.Product, e.Service, e.Environment, e.Version, e.Status, e.DeployedAt, e.RunJson))
            .ToListAsync(ct);
        events = events.Where(e => OnLine(e.Version, line)).ToList();

        var candidates = await _db.PromotionCandidates.AsNoTracking()
            .ExcludingDeletedServices(_db)
            .Where(c => products.Contains(c.Product) && c.TargetEnv == production && c.CreatedAt <= promotionsTo
                && (c.Version == line || c.Version.StartsWith(dot) || c.Version.StartsWith(dash)
                    || c.Version == vLine || c.Version.StartsWith(vDot) || c.Version.StartsWith(vDash)))
            .Select(c => new Cand(c.Id, c.Product, c.Service, c.SourceEnv, c.Version, c.Status, c.CreatedAt, c.ApprovedAt, c.DeployedAt, c.ExternalRunUrl))
            .ToListAsync(ct);
        candidates = candidates.Where(c => OnLine(c.Version, line)).ToList();

        var onLine = events.Select(e => (e.Product, e.Service)).Concat(candidates.Select(c => (c.Product, c.Service))).ToHashSet();

        // A service on a version scheme of its own (5.1.x while the train is 5.10) has nothing on the
        // line: what it deployed and promoted during the release is what it shipped with it.
        var offLine = present.Where(k => !onLine.Contains(k)).ToHashSet();
        if (offLine.Count > 0)
        {
            var names = offLine.Select(k => k.Service).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            events.AddRange((await _db.DeployEvents.AsNoTracking()
                    .Where(e => products.Contains(e.Product) && names.Contains(e.Service) && envs.Contains(e.Environment)
                        && e.DeployedAt >= from && e.DeployedAt <= to && e.Status != DeployEventStatuses.Removed)
                    .Select(e => new Ev(e.Id, e.Product, e.Service, e.Environment, e.Version, e.Status, e.DeployedAt, e.RunJson))
                    .ToListAsync(ct))
                .Where(e => offLine.Contains((e.Product, e.Service))));
            candidates.AddRange((await _db.PromotionCandidates.AsNoTracking()
                    .Where(c => products.Contains(c.Product) && names.Contains(c.Service) && c.TargetEnv == production
                        && c.CreatedAt >= from && c.CreatedAt <= promotionsTo)
                    .Select(c => new Cand(c.Id, c.Product, c.Service, c.SourceEnv, c.Version, c.Status, c.CreatedAt, c.ApprovedAt, c.DeployedAt, c.ExternalRunUrl))
                    .ToListAsync(ct))
                .Where(c => offLine.Contains((c.Product, c.Service))));
        }

        bool IsExcluded((string Product, string Service) k) => excluded.Any(x => x.Matches(k.Product, k.Service));

        var keys = present.Concat(onLine)
            .Distinct()
            .OrderBy(k => k.Product, StringComparer.Ordinal)
            .ThenBy(k => k.Service, StringComparer.Ordinal)
            .ToList();

        var eventsByKey = events.ToLookup(e => (e.Product, e.Service));
        var candidatesByKey = candidates.ToLookup(c => (c.Product, c.Service));

        // What production runs of the release, per service — the migrations to look for are that version's.
        var productionNow = new Dictionary<(string, string), Ev>();
        foreach (var key in keys)
        {
            var prod = eventsByKey[key].Where(e => e.Environment == production).OrderByDescending(e => e.DeployedAt).ToList();
            var succeeded = prod.FirstOrDefault(e => e.Status == "succeeded");
            if (succeeded is not null) productionNow[key] = succeeded;
        }
        var deployedVersions = productionNow.Values.Select(e => e.Version).Distinct().ToList();
        var deployedServices = productionNow.Values.Select(e => e.Service).Distinct().ToList();
        var migrationRows = deployedVersions.Count == 0
            ? []
            : await _db.MigrationRuns.AsNoTracking()
                .Where(m => m.Environment == production && products.Contains(m.Product)
                    && deployedServices.Contains(m.Service) && deployedVersions.Contains(m.Version))
                .ToListAsync(ct);
        var migrationsByKey = migrationRows.ToLookup(m => (m.Product, m.Service));

        // The promotion that carries each service to production: the newest one still in play.
        var effective = new Dictionary<(string, string), Cand>();
        foreach (var key in keys)
        {
            var cands = candidatesByKey[key].OrderByDescending(c => c.CreatedAt).ToList();
            var chosen = cands.FirstOrDefault(c => c.Status != PromotionStatus.Superseded) ?? cands.FirstOrDefault();
            if (chosen is not null) effective[key] = chosen;
        }
        var qaStep = await QaStepAsync(ct);
        var gates = await GateInfoAsync(effective.Values.ToList(), qaStep, ct);
        var builds = await BuildsAsync(products, line, keys, offLine, from, to, ct);

        var services = new List<ReleaseServiceDto>();
        // The pipeline runs behind each measurement, as (run, service, outcome) hits grouped later.
        var stagingRuns = new List<RunHit>();
        var productionRuns = new List<RunHit>();
        var migrationRuns = new List<RunHit>();
        var promotionRuns = new List<RunHit>();
        foreach (var key in keys)
        {
            var isExcluded = IsExcluded(key);
            var mine = eventsByKey[key].OrderByDescending(e => e.DeployedAt).ToList();
            var stagingLatest = mine.FirstOrDefault(e => e.Environment == staging);
            var prodEvents = mine.Where(e => e.Environment == production).ToList();
            productionNow.TryGetValue(key, out var prodSucceeded);
            var prodShown = prodSucceeded ?? prodEvents.FirstOrDefault();

            effective.TryGetValue(key, out var promotion);

            if (!isExcluded)
            {
                if (stagingLatest is not null) stagingRuns.Add(new RunHit(stagingLatest.Run, key.Service, stagingLatest.Status, stagingLatest.DeployedAt));
                foreach (var e in prodEvents) productionRuns.Add(new RunHit(e.Run, key.Service, e.Status, e.DeployedAt));
                if (promotion?.ExternalRunUrl is { Length: > 0 } dispatched)
                    promotionRuns.Add(new RunHit(new DeployRun(RunUrl: dispatched), key.Service, "succeeded", promotion.ApprovedAt ?? promotion.CreatedAt));
            }

            var migrations = new List<ReleaseMigrationDto>();
            if (prodSucceeded is not null)
            {
                var runs = migrationsByKey[key].Where(m => m.Version == prodSucceeded.Version).ToList();
                // Schema runs belong to one deploy; a version deployed twice has one per deploy.
                var schema = runs.Where(m => m.Kind == MigrationKinds.Schema)
                    .OrderByDescending(m => m.DeployEventId == prodSucceeded.Id)
                    .ThenByDescending(m => m.QueuedAt)
                    .FirstOrDefault();
                var data = runs.Where(m => m.Kind == MigrationKinds.Data && m.Status != MigrationRunStatus.Superseded)
                    .OrderByDescending(m => m.QueuedAt)
                    .FirstOrDefault();
                foreach (var m in new[] { schema, data })
                {
                    if (m is null) continue;
                    var run = ParseRun(m.RunJson);
                    migrations.Add(new ReleaseMigrationDto(
                        m.Id, m.Kind, m.Version, m.Status, m.QueuedAt, m.CompletedAt, m.AppliedCount, m.FailedCount, m.FailureReason,
                        RunRef(run)));
                    if (!isExcluded) migrationRuns.Add(new RunHit(run, key.Service, m.Status, m.CompletedAt ?? m.QueuedAt));
                }
            }

            services.Add(new ReleaseServiceDto(
                key.Product, key.Service,
                isExcluded,
                onLine.Contains(key),
                builds.GetValueOrDefault(key),
                stagingLatest is null ? null : ToDeploy(stagingLatest),
                promotion is null ? null : ToPromotion(promotion, gates.GetValueOrDefault(promotion.Id)),
                prodShown is null ? null : ToDeploy(prodShown),
                prodEvents.Count(e => e.Status == "failed"),
                migrations));
        }

        // The checks measure the services that ship; the ones left out are listed, not counted.
        var shipping = services.Where(s => !s.Excluded).ToList();
        var shippingKeys = shipping.Select(s => (s.Product, s.Service)).ToHashSet();
        var shippingEvents = events.Where(e => shippingKeys.Contains((e.Product, e.Service))).ToLookup(e => (e.Product, e.Service));
        var context = new CheckContext(line, staging, production, shipping, shippingEvents);
        var checks = new List<ReleaseCheckDto>
        {
            StagingBuildsCheck(context) with { Runs = Aggregate(stagingRuns) },
            QaCheck(context, qaStep),
            PromotionsCheck(context) with { Runs = Aggregate(promotionRuns) },
            DeploysCheck(context) with { Runs = Aggregate(productionRuns) },
            MigrationsCheck(context) with { Runs = Aggregate(migrationRuns) },
        };

        return new ReleaseProgressDto(release.Id, release.Version, qaStep, now, from, to, services, checks);
    }

    // ── Gates, work items and builds ─────────────────────────────────────────

    private async Task<string> QaStepAsync(CancellationToken ct)
    {
        var value = await _db.PlatformSettings.AsNoTracking()
            .Where(s => s.Key == ReleaseService.QaStepSettingKey).Select(s => s.Value).FirstOrDefaultAsync(ct);
        return string.IsNullOrWhiteSpace(value) ? ReleaseService.DefaultQaStep : value.Trim();
    }

    private sealed record GateInfo(
        List<string> OutstandingSteps, bool WorkItemsOutstanding, ReleaseWorkItemsDto? WorkItems,
        string QaReview, DateTimeOffset? QaApprovedAt, string? QaApprovedBy);

    private static readonly JsonSerializerOptions SnapshotJson = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Per promotion: the approval steps it is still short of (the promotions page's own answer,
    /// <see cref="PromotionService.GetGateStatusesAsync"/>), its work items and how far their sign-off
    /// has got, and where it stands on the QA gate — the approval step named <paramref name="qaStep"/>.
    /// </summary>
    private async Task<Dictionary<Guid, GateInfo>> GateInfoAsync(List<Cand> cands, string qaStep, CancellationToken ct)
    {
        var result = new Dictionary<Guid, GateInfo>();
        if (cands.Count == 0) return result;
        var ids = cands.Select(c => c.Id).ToList();

        var entities = await _db.PromotionCandidates.AsNoTracking().Where(c => ids.Contains(c.Id)).ToListAsync(ct);
        // A promotion without a policy snapshot (imported, or older than snapshots) has no gate to read.
        var withSnapshot = entities.Where(c => !string.IsNullOrEmpty(c.ResolvedPolicyJson)).ToList();
        var statuses = await _promotions.GetGateStatusesAsync(withSnapshot, ct);
        var hasQaStep = withSnapshot.ToDictionary(c => c.Id, c =>
        {
            try
            {
                var snapshot = JsonSerializer.Deserialize<ResolvedPolicySnapshot>(c.ResolvedPolicyJson!, SnapshotJson);
                return snapshot?.ApprovalSteps.Any(step => step.Requirements.Count > 0
                    && string.Equals((step.Name ?? "").Trim(), qaStep, StringComparison.OrdinalIgnoreCase)) ?? false;
            }
            catch (JsonException) { return false; }
        });

        var qaApprovals = (await _db.PromotionApprovals.AsNoTracking()
                .Where(a => ids.Contains(a.CandidateId) && a.Decision == PromotionDecision.Approved && a.StepName != null)
                .Select(a => new { a.CandidateId, a.StepName, a.ApproverName, a.CreatedAt })
                .ToListAsync(ct))
            .Where(a => string.Equals((a.StepName ?? "").Trim(), qaStep, StringComparison.OrdinalIgnoreCase))
            .GroupBy(a => a.CandidateId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(a => a.CreatedAt).First());

        var items = await _db.PromotionWorkItems.AsNoTracking()
            .Where(w => ids.Contains(w.CandidateId))
            .Select(w => new { w.CandidateId, w.WorkItemKey, w.Product, w.Service, w.TargetEnv })
            .ToListAsync(ct);
        var itemKeys = items.Select(w => w.WorkItemKey).Distinct().ToList();
        var decisions = itemKeys.Count == 0
            ? new()
            : (await _db.WorkItemApprovals.AsNoTracking()
                    .Where(a => itemKeys.Contains(a.WorkItemKey))
                    .Select(a => new { a.WorkItemKey, a.Product, a.Service, a.TargetEnv, a.Decision })
                    .ToListAsync(ct))
                .ToLookup(a => (a.WorkItemKey, a.Product, a.Service, a.TargetEnv), a => a.Decision)
                .ToDictionary(g => g.Key, g => g.ToList());

        foreach (var c in cands)
        {
            var status = statuses.GetValueOrDefault(c.Id) ?? PromotionGateStatus.None;
            ReleaseWorkItemsDto? workItems = null;
            var mine = items.Where(w => w.CandidateId == c.Id).DistinctBy(w => w.WorkItemKey).ToList();
            if (mine.Count > 0)
            {
                int approved = 0, issues = 0, blocked = 0;
                foreach (var w in mine)
                {
                    // Judged per instance, the way the promotion's own work-item gate judges them.
                    var rows = decisions.GetValueOrDefault((w.WorkItemKey, w.Product, w.Service, w.TargetEnv)) ?? [];
                    switch (WorkItemOverallStatus.StateOf(rows))
                    {
                        case WorkItemInstanceState.Approved: approved++; break;
                        case WorkItemInstanceState.Issue: issues++; break;
                        case WorkItemInstanceState.Blocked: blocked++; break;
                    }
                }
                workItems = new ReleaseWorkItemsDto(mine.Count, approved, issues, blocked);
            }

            qaApprovals.TryGetValue(c.Id, out var qaApproval);
            var qa = c.Status switch
            {
                PromotionStatus.Approved or PromotionStatus.Deploying or PromotionStatus.Deployed => "approved",
                PromotionStatus.Rejected => "rejected",
                PromotionStatus.Pending when hasQaStep.TryGetValue(c.Id, out var has) && !has => "not-required",
                PromotionStatus.Pending when hasQaStep.ContainsKey(c.Id)
                    && !status.OutstandingSteps.Any(x => string.Equals(x, qaStep, StringComparison.OrdinalIgnoreCase)) => "approved",
                _ => qaApproval is not null && c.Status == PromotionStatus.Pending ? "approved" : "pending",
            };
            result[c.Id] = new GateInfo(
                status.OutstandingSteps.ToList(), status.WorkItemsOutstanding, workItems, qa,
                qaApproval?.CreatedAt ?? (qa == "approved" ? c.ApprovedAt : null), qaApproval?.ApproverName);
        }
        return result;
    }

    private static ReleasePromotionDto ToPromotion(Cand c, GateInfo? gate) => new(
        c.Id, c.SourceEnv, c.Version, c.Status.ToString(), c.CreatedAt, c.ApprovedAt, c.DeployedAt, c.ExternalRunUrl,
        gate?.OutstandingSteps ?? [], gate?.WorkItemsOutstanding ?? false, gate?.WorkItems,
        gate?.QaReview ?? "pending", gate?.QaApprovedAt, gate?.QaApprovedBy);

    /// <summary>
    /// The newest registered build of the release per service: of the line, or — for a service on a
    /// version scheme of its own — inside the window.
    /// </summary>
    private async Task<Dictionary<(string, string), ReleaseBuildDto>> BuildsAsync(
        List<string> products, string line, List<(string Product, string Service)> keys,
        HashSet<(string Product, string Service)> offLine, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        var dot = line + ".";
        var dash = line + "-";
        var vDot = "v" + dot;
        var services = keys.Select(k => k.Service).Distinct().ToList();
        var rows = await _db.Builds.AsNoTracking()
            .Where(b => products.Contains(b.Product) && services.Contains(b.Service) && b.CreatedAt <= to)
            .Where(b => b.Version == line || b.Version.StartsWith(dot) || b.Version.StartsWith(dash) || b.Version.StartsWith(vDot)
                || b.CreatedAt >= from)
            .Select(b => new { b.Id, b.Product, b.Service, b.Version, b.Branch, b.BuildId, b.BuildUrl, b.CreatedAt })
            .ToListAsync(ct);
        var wanted = keys.ToHashSet();
        return rows
            .Where(b => wanted.Contains((b.Product, b.Service)))
            .Where(b => offLine.Contains((b.Product, b.Service)) ? b.CreatedAt >= from : OnLine(b.Version, line))
            .GroupBy(b => (b.Product, b.Service))
            .ToDictionary(
                g => g.Key,
                g => g.OrderByDescending(b => b.CreatedAt)
                    .Select(b => new ReleaseBuildDto(b.Id, b.Version, b.Branch, b.BuildId, b.BuildUrl, b.CreatedAt))
                    .First());
    }

    private static ReleaseDeployDto ToDeploy(Ev e) => new(e.Id, e.Version, e.Status, e.DeployedAt, RunRef(e.Run));

    // ── Pipeline runs ────────────────────────────────────────────────────────

    private static readonly JsonSerializerOptions RunJsonOptions = new(JsonSerializerDefaults.Web);

    private static DeployRun? ParseRun(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<DeployRun>(json, RunJsonOptions); }
        catch (JsonException) { return null; }
    }

    private sealed record RunHit(DeployRun? Run, string Service, string Status, DateTimeOffset At);

    /// <summary>The whole run — what a check groups by.</summary>
    private static string? RunUrl(DeployRun? run) =>
        WholeRun(!string.IsNullOrWhiteSpace(run?.RunUrl) ? run.RunUrl
            : !string.IsNullOrWhiteSpace(run?.JobUrl) ? run.JobUrl
            : null);

    [GeneratedRegex(@"^(https?://github\.com/[^/]+/[^/]+/actions/runs/\d+)")]
    private static partial Regex GitHubRunPrefix();

    /// <summary>
    /// The address of the whole run, whatever part of it a producer linked: a GitHub Actions job page
    /// becomes its run, an Azure DevOps result page loses its view and job parameters. Two services
    /// deployed by one run must land on one row.
    /// </summary>
    internal static string? WholeRun(string? url)
    {
        if (url is null) return null;
        var gh = GitHubRunPrefix().Match(url);
        if (gh.Success) return gh.Groups[1].Value;
        var build = AzureBuildId().Match(url);
        if (build.Success && Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return $"{uri.Scheme}://{uri.Authority}{uri.AbsolutePath}?buildId={build.Groups[1].Value}";
        return url;
    }

    /// <summary>The run as a person names it — straight to the service's job when the producer sent one.</summary>
    private static ReleaseRunRefDto? RunRef(DeployRun? run)
    {
        if (run is null) return null;
        var url = !string.IsNullOrWhiteSpace(run.JobUrl) ? run.JobUrl : RunUrl(run);
        return url is null ? null : new ReleaseRunRefDto(run.Provider, RunLabel(run, RunUrl(run) ?? url), url);
    }

    [GeneratedRegex(@"[?&]buildId=(\d+)")]
    private static partial Regex AzureBuildId();

    [GeneratedRegex(@"github\.com/[^/]+/([^/]+)/actions/runs/(\d+)")]
    private static partial Regex GitHubRun();

    /// <summary>
    /// "marketplace-sdp-prod 8971132" for Azure DevOps (the build id is what people quote; its run
    /// number is a long build name), "Reconcile Prod #75" for GitHub Actions, and a name read off the
    /// address when the producer sent nothing but a link.
    /// </summary>
    internal static string RunLabel(DeployRun run, string url)
    {
        var name = string.IsNullOrWhiteSpace(run.WorkflowName) ? null : run.WorkflowName.Trim();
        var buildId = AzureBuildId().Match(url);
        if (run.Provider == "azure-devops" || buildId.Success)
        {
            var id = run.RunId ?? (buildId.Success ? buildId.Groups[1].Value : null);
            return (name ?? "Azure DevOps run") + (id is null ? "" : $" {id}");
        }
        var gh = GitHubRun().Match(url);
        if (gh.Success)
            return name is null ? $"{gh.Groups[1].Value} run {gh.Groups[2].Value}" : $"{name} #{run.RunNumber ?? gh.Groups[2].Value}";
        if (name is not null) return run.RunNumber is null ? name : $"{name} #{run.RunNumber}";
        return "Pipeline run";
    }

    private static List<ReleaseRunDto> Aggregate(IEnumerable<RunHit> hits) =>
        hits.Select(h => (Hit: h, Url: RunUrl(h.Run)))
            .Where(x => x.Url is not null)
            .GroupBy(x => x.Url!)
            .Select(g =>
            {
                var run = g.First().Hit.Run!;
                return new ReleaseRunDto(
                    run.Provider, RunLabel(run, g.Key), g.Key,
                    g.Select(x => x.Hit.Service).Distinct().Count(),
                    g.Count(x => x.Hit.Status is "succeeded" or MigrationRunStatus.Skipped),
                    g.Count(x => x.Hit.Status == "failed"),
                    g.Min(x => x.Hit.At),
                    g.Max(x => x.Hit.At));
            })
            .OrderBy(r => r.FirstAt)
            .ToList();

    private sealed record CheckContext(
        string Line, string Staging, string Production, List<ReleaseServiceDto> Services, ILookup<(string, string), Ev> Events);

    private const string Waiting = "waiting";
    private const string InProgress = "in_progress";
    private const string Complete = "complete";
    private const string Attention = "attention";

    private static bool Approved(ReleasePromotionDto? p) =>
        p is not null && p.Status is nameof(PromotionStatus.Approved) or nameof(PromotionStatus.Deploying) or nameof(PromotionStatus.Deployed);

    private static bool Succeeded(ReleaseDeployDto? d) => d is not null && d.Status == "succeeded";

    private static string Plural(int n, string one, string many) => n == 1 ? one : many;

    /// <summary>Services running a build of the line on staging (or already past it, in production).</summary>
    private static ReleaseCheckDto StagingBuildsCheck(CheckContext c)
    {
        var total = c.Services.Count;
        var issues = new List<ReleaseCheckIssueDto>();
        var done = 0;
        var failed = false;
        foreach (var s in c.Services)
        {
            if (Succeeded(s.Staging) || HasSucceeded(c, s, c.Staging) || Succeeded(s.Production)) done++;
            if (s.Staging is { Status: "failed" })
            {
                failed = true;
                issues.Add(new(s.Product, s.Service, $"The last staging deploy ({s.Staging.Version}) failed"));
            }
            else if (s.Staging is null && !Succeeded(s.Production))
            {
                issues.Add(new(s.Product, s.Service, $"No {c.Line} build on {c.Staging} yet"));
            }
        }
        var stagingSucceeded = c.Events.SelectMany(g => g).Where(e => e.Environment == c.Staging && e.Status == "succeeded").ToList();
        var summary = total == 0
            ? $"No service runs a {c.Line} build on {c.Staging} yet."
            : $"{done} of {total} {Plural(total, "service runs", "services run")} a {c.Line} build on {c.Staging}.";
        return new ReleaseCheckDto(
            ReleaseChecks.StagingBuilds,
            total == 0 ? Waiting : failed ? Attention : done == total ? Complete : InProgress,
            done, total, summary,
            stagingSucceeded.Count == 0 ? null : stagingSucceeded.Min(e => e.DeployedAt),
            stagingSucceeded.Count == 0 ? null : stagingSucceeded.Max(e => e.DeployedAt),
            issues,
            []);
    }

    /// <summary>
    /// Every service's production promotion through the QA gate — the approval step named
    /// <paramref name="qaStep"/>. A promotion whose gate has no such step has nothing to wait for; an
    /// approved one is past it.
    /// </summary>
    private static ReleaseCheckDto QaCheck(CheckContext c, string qaStep)
    {
        var total = c.Services.Count;
        var issues = new List<ReleaseCheckIssueDto>();
        int done = 0, waiting = 0, rejected = 0, missing = 0;
        var approvedAt = new List<DateTimeOffset>();
        foreach (var s in c.Services)
        {
            var p = s.Promotion;
            var alreadyShipped = Succeeded(s.Production) && (s.Staging is null || s.Staging.Version == s.Production!.Version);
            if (p is null)
            {
                if (alreadyShipped) { done++; continue; }
                missing++;
                issues.Add(new(s.Product, s.Service, $"No promotion to {c.Production} raised yet"));
                continue;
            }
            switch (p.QaReview)
            {
                case "approved":
                case "not-required":
                    done++;
                    if (p.QaApprovedAt is { } at) approvedAt.Add(at);
                    break;
                case "rejected":
                    rejected++;
                    issues.Add(new(s.Product, s.Service, $"The promotion of {p.Version} was rejected"));
                    break;
                default:
                    waiting++;
                    var progress = p.WorkItems is { } w ? $" ({w.Approved + w.Issues} of {w.Total} work items signed off)" : "";
                    issues.Add(new(s.Product, s.Service, $"{qaStep} not signed for {p.Version}{progress}"));
                    break;
            }
        }
        var parts = new List<string>();
        if (waiting > 0) parts.Add($"{waiting} waiting for {qaStep}");
        if (rejected > 0) parts.Add($"{rejected} rejected");
        if (missing > 0) parts.Add($"{missing} not raised");
        var summary = total == 0
            ? $"No promotion of {c.Line} to {c.Production} yet."
            : $"{done} of {total} passed {qaStep}" + (parts.Count > 0 ? $"; {string.Join(", ", parts)}." : ".");
        return new ReleaseCheckDto(
            ReleaseChecks.StagingQa,
            total == 0 ? Waiting : rejected > 0 ? Attention : done == total ? Complete : InProgress,
            done, total, summary,
            approvedAt.Count == 0 ? null : approvedAt.Min(),
            approvedAt.Count == 0 ? null : approvedAt.Max(),
            issues,
            []);
    }

    private static bool HasSucceeded(CheckContext c, ReleaseServiceDto s, string env) =>
        c.Events[(s.Product, s.Service)].Any(e => e.Environment == env && e.Status == "succeeded");

    /// <summary>
    /// Every service's way into production approved. A service production already runs at the version
    /// staging has needs no promotion, however it got there.
    /// </summary>
    private static ReleaseCheckDto PromotionsCheck(CheckContext c)
    {
        var total = c.Services.Count;
        var issues = new List<ReleaseCheckIssueDto>();
        int done = 0, pending = 0, rejected = 0, missing = 0;
        var approvedAt = new List<DateTimeOffset>();
        foreach (var s in c.Services)
        {
            var p = s.Promotion;
            var alreadyShipped = Succeeded(s.Production) && (s.Staging is null || s.Staging.Version == s.Production!.Version);
            if (Approved(p))
            {
                done++;
                if (p!.ApprovedAt is { } at) approvedAt.Add(at);
            }
            else if (alreadyShipped && (p is null || p.Status != nameof(PromotionStatus.Pending)))
            {
                done++;
            }
            else if (p is null)
            {
                missing++;
                issues.Add(new(s.Product, s.Service, $"No promotion to {c.Production} raised yet"));
            }
            else if (p.Status == nameof(PromotionStatus.Rejected))
            {
                rejected++;
                issues.Add(new(s.Product, s.Service, $"The promotion of {p.Version} was rejected"));
            }
            else
            {
                pending++;
                issues.Add(new(s.Product, s.Service, $"{p.Version} is waiting for approval"));
            }
        }
        var parts = new List<string>();
        if (pending > 0) parts.Add($"{pending} waiting for approval");
        if (rejected > 0) parts.Add($"{rejected} rejected");
        if (missing > 0) parts.Add($"{missing} not raised");
        var summary = total == 0
            ? $"No promotion of {c.Line} to {c.Production} yet."
            : $"{done} of {total} approved for {c.Production}" + (parts.Count > 0 ? $"; {string.Join(", ", parts)}." : ".");
        return new ReleaseCheckDto(
            ReleaseChecks.ProductionPromotions,
            total == 0 ? Waiting : rejected > 0 ? Attention : done == total ? Complete : InProgress,
            done, total, summary,
            approvedAt.Count == 0 ? null : approvedAt.Min(),
            approvedAt.Count == 0 ? null : approvedAt.Max(),
            issues,
            []);
    }

    /// <summary>
    /// Every service deployed to production at the line — and at the version its promotion approved,
    /// when that is newer than what production runs.
    /// </summary>
    private static ReleaseCheckDto DeploysCheck(CheckContext c)
    {
        var total = c.Services.Count;
        var issues = new List<ReleaseCheckIssueDto>();
        int done = 0, failedAttempts = 0, attempted = 0;
        var attention = false;
        var deployedAt = new List<DateTimeOffset>();
        foreach (var s in c.Services)
        {
            failedAttempts += s.ProductionFailures;
            var prodEvents = c.Events[(s.Product, s.Service)]
                .Where(e => e.Environment == c.Production).OrderByDescending(e => e.DeployedAt).ToList();
            if (prodEvents.Count > 0) attempted++;
            var latest = prodEvents.FirstOrDefault();
            var deployed = Succeeded(s.Production);
            var awaitingApproved = deployed && s.Promotion is { } p
                && (p.Status == nameof(PromotionStatus.Approved) || p.Status == nameof(PromotionStatus.Deploying))
                && p.Version != s.Production!.Version;

            if (deployed && !awaitingApproved)
            {
                done++;
                deployedAt.Add(s.Production!.DeployedAt);
            }

            if (latest is { Status: "failed" })
            {
                attention = true;
                issues.Add(new(s.Product, s.Service, $"The last deploy of {latest.Version} failed"
                    + (s.ProductionFailures > 1 ? $" ({s.ProductionFailures} failed attempts)" : "")));
            }
            else if (latest is { Status: "in_progress" })
            {
                issues.Add(new(s.Product, s.Service, $"Deploying {latest.Version}"));
            }
            else if (awaitingApproved)
            {
                issues.Add(new(s.Product, s.Service, $"{s.Promotion!.Version} is approved but not deployed yet"));
            }
            else if (!deployed)
            {
                issues.Add(new(s.Product, s.Service, $"Not deployed to {c.Production} yet"));
            }
        }
        var summary = total == 0
            ? $"Nothing of {c.Line} is in {c.Production} yet."
            : $"{done} of {total} deployed to {c.Production}"
              + (failedAttempts > 0 ? $"; {failedAttempts} failed {Plural(failedAttempts, "attempt", "attempts")} on the way." : ".");
        return new ReleaseCheckDto(
            ReleaseChecks.ProductionDeploys,
            total == 0 || attempted == 0 ? Waiting : attention ? Attention : done == total ? Complete : InProgress,
            done, total, summary,
            deployedAt.Count == 0 ? null : deployedAt.Min(),
            deployedAt.Count == 0 ? null : deployedAt.Max(),
            issues,
            []);
    }

    /// <summary>
    /// The migrations of every deployed version finished. Services that reported none have nothing to
    /// wait for; the check completes once every service is deployed and nothing is pending, running
    /// or failed.
    /// </summary>
    private static ReleaseCheckDto MigrationsCheck(CheckContext c)
    {
        var deployed = c.Services.Where(s => Succeeded(s.Production)).ToList();
        var runs = deployed.SelectMany(s => s.Migrations.Select(m => (Service: s, Run: m))).ToList();
        var issues = new List<ReleaseCheckIssueDto>();
        int done = 0, open = 0, failed = 0, applied = 0;
        foreach (var (s, m) in runs)
        {
            applied += m.AppliedCount;
            var kind = m.Kind == MigrationKinds.Schema ? "Schema" : "Data";
            switch (m.Status)
            {
                case MigrationRunStatus.Succeeded:
                case MigrationRunStatus.Skipped:
                case MigrationRunStatus.Superseded:
                    done++;
                    break;
                case MigrationRunStatus.Failed:
                    failed++;
                    issues.Add(new(s.Product, s.Service, $"{kind} migration of {m.Version} failed"
                        + (string.IsNullOrWhiteSpace(m.FailureReason) ? "" : $": {Truncate(m.FailureReason, 160)}")));
                    break;
                default:
                    open++;
                    issues.Add(new(s.Product, s.Service, $"{kind} migration of {m.Version} is {m.Status}"));
                    break;
            }
        }
        var notDeployed = c.Services.Count - deployed.Count;
        var summary = deployed.Count == 0
            ? "Waiting for the first production deploy."
            : runs.Count == 0
                ? $"No migrations reported for the {deployed.Count} deployed {Plural(deployed.Count, "service", "services")}."
                : $"{done} of {runs.Count} {Plural(runs.Count, "migration run", "migration runs")} finished, "
                  + $"{applied} {Plural(applied, "migration", "migrations")} applied"
                  + (notDeployed > 0 ? $"; {notDeployed} {Plural(notDeployed, "service", "services")} still to deploy." : ".");
        var finishedAt = runs.Where(r => r.Run.CompletedAt is not null).Select(r => r.Run.CompletedAt!.Value).ToList();
        return new ReleaseCheckDto(
            ReleaseChecks.ProductionMigrations,
            deployed.Count == 0 ? Waiting : failed > 0 ? Attention : open > 0 || notDeployed > 0 ? InProgress : Complete,
            done, runs.Count, summary,
            runs.Count == 0 ? null : runs.Min(r => r.Run.QueuedAt),
            open == 0 && finishedAt.Count > 0 ? finishedAt.Max() : null,
            issues,
            []);
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..(max - 1)] + "…";
}
