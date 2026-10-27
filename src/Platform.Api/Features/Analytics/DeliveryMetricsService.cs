using Microsoft.EntityFrameworkCore;
using Platform.Api.Features.Analytics.Models;
using Platform.Api.Features.Deployments;
using Platform.Api.Features.Deployments.Models;
using Platform.Api.Features.Settings;
using Platform.Api.Features.Settings.Models;
using Platform.Api.Infrastructure.Persistence;

namespace Platform.Api.Features.Analytics;

/// <summary>
/// The Delivery metrics report: DORA's four plus the flow, quality, cadence and data-quality
/// numbers that make them readable, over a product selection pooled as one population. Products
/// are a filter here, never a grouping — counts sum and percentiles run over the pooled work items,
/// because per-product medians cannot be averaged into a portfolio median.
///
/// <para>Production is resolved on the server (<see cref="EnvironmentOrdering.ResolveProduction"/>)
/// so every number in one response measures the same environments, and the response says which.
/// Like the rest of analytics there is no snapshot: numbers are as fresh as the last ingest, and
/// <c>asOf</c> says when they were computed.</para>
///
/// <para>Rows are filtered to the two windows (current and the equal-length one before) in SQL;
/// work-item grains are aggregated in SQL to their first deploy per (product, service, work item,
/// environment) so the lead-time path never loads a product's whole history. Percentile math runs
/// in memory — see <see cref="Percentiles"/>.</para>
/// </summary>
public class DeliveryMetricsService
{
    private readonly PlatformDbContext _db;
    private readonly AppSettingsService _settings;

    public DeliveryMetricsService(PlatformDbContext db, AppSettingsService settings)
    {
        _db = db;
        _settings = settings;
    }

    /// <param name="includeIncidents">Whether to list the incidents behind the figures — admins only.</param>
    public async Task<DeliveryMetricsResponseDto> Get(
        IReadOnlyList<string> products, DateTimeOffset? from, DateTimeOffset? to,
        string bucket, TimeZoneInfo tz, bool includeIncidents, CancellationToken ct)
    {
        var asOf = DateTimeOffset.UtcNow;
        var rangeTo = to ?? asOf;
        var rangeFrom = from ?? rangeTo.AddDays(-14);
        var span = rangeTo - rangeFrom;
        var prevFrom = rangeFrom - span;

        var settings = await _settings.GetSettings(ct);
        var analytics = (settings.Analytics ?? AnalyticsSettingsDto.Default).Clean();
        var tolerance = TimeSpan.FromMinutes(analytics.RolloutToleranceMinutes);
        var window = analytics.AttributionWindowDays > 0 ? TimeSpan.FromDays(analytics.AttributionWindowDays) : (TimeSpan?)null;
        var retired = AppSettingsService.RetiredEnvironmentKeys(settings.Environments);
        var configuredKeys = settings.Environments.Select(e => e.Key).ToList();
        var filter = products.Count > 0 ? products.Distinct(StringComparer.OrdinalIgnoreCase).ToList() : null;

        // ── Loads ────────────────────────────────────────────────────────────────────────────
        // Retired environments are out of everything here: a report measures what still runs.

        var eventQuery = _db.DeployEvents.AsNoTracking()
            .ExcludingRetiredEnvironments(retired)
            .Where(e => e.DeployedAt >= prevFrom && e.DeployedAt < rangeTo)
            .Where(e => e.Status != DeployEventStatuses.Removed);
        if (filter is not null) eventQuery = eventQuery.Where(e => EF.Parameter(filter).Contains(e.Product));
        var events = await eventQuery
            .Select(e => new EventRow(e.Id, e.Product, e.Service, e.Environment,
                e.Version, e.PreviousVersion, e.IsRollback, e.Status, e.DeployedAt))
            .ToListAsync(ct);

        // Work items per deploy in the current window, grouped in SQL — one join, no id list.
        var batchQuery = _db.DeployEventWorkItems.AsNoTracking()
            .Join(_db.DeployEvents.AsNoTracking().Where(e => e.DeployedAt >= rangeFrom && e.DeployedAt < rangeTo),
                w => w.DeployEventId, e => e.Id, (w, e) => new { e.Id, e.Product });
        if (filter is not null) batchQuery = batchQuery.Where(x => EF.Parameter(filter).Contains(x.Product));
        var batchByEvent = (await batchQuery
                .GroupBy(x => x.Id)
                .Select(g => new { Id = g.Key, Count = g.Count() })
                .ToListAsync(ct))
            .ToDictionary(x => x.Id, x => x.Count);

        // Grain: first successful deploy per (product, service, work item, environment). Aggregated
        // in SQL so "first" is genuinely first without loading the history; the window then keeps
        // the grains that landed in either window.
        var grainQuery = _db.DeployEventWorkItems.AsNoTracking()
            .Join(_db.DeployEvents.AsNoTracking().ExcludingRetiredEnvironments(retired)
                    .Where(e => e.Status == "succeeded" && e.DeployedAt < rangeTo),
                w => w.DeployEventId, e => e.Id,
                (w, e) => new { e.Product, e.Service, w.WorkItemKey, e.Environment, e.DeployedAt, w.CommittedAt });
        if (filter is not null) grainQuery = grainQuery.Where(x => EF.Parameter(filter).Contains(x.Product));
        var grains = (await grainQuery
                .GroupBy(x => new { x.Product, x.Service, x.WorkItemKey, x.Environment })
                .Select(g => new
                {
                    g.Key.Product, g.Key.Service, g.Key.WorkItemKey, g.Key.Environment,
                    First = g.Min(x => x.DeployedAt),
                    CommittedAt = g.Min(x => x.CommittedAt),
                })
                .Where(g => g.First >= prevFrom)
                .ToListAsync(ct))
            .Select(g => new Grain(g.Product, g.Service, g.WorkItemKey, g.Environment, g.First, g.CommittedAt))
            .ToList();

        var candidateQuery = _db.PromotionCandidates.AsNoTracking().AsQueryable();
        if (filter is not null) candidateQuery = candidateQuery.Where(c => EF.Parameter(filter).Contains(c.Product));
        var approved = await candidateQuery
            .Where(c => c.ApprovedAt != null && c.ApprovedAt >= prevFrom && c.ApprovedAt < rangeTo)
            .Select(c => new { c.Product, c.CreatedAt, ApprovedAt = c.ApprovedAt!.Value })
            .ToListAsync(ct);
        var deployed = await candidateQuery
            .Where(c => c.DeployedAt != null && c.ApprovedAt != null && c.DeployedAt >= prevFrom && c.DeployedAt < rangeTo)
            .Select(c => new { c.Product, ApprovedAt = c.ApprovedAt!.Value, DeployedAt = c.DeployedAt!.Value })
            .ToListAsync(ct);

        // Every service that has ever deployed under the filter, with its true last deploy — the
        // stale-service alarm needs the ones that did not deploy in the window.
        var knownQuery = _db.DeployEvents.AsNoTracking()
            .ExcludingRetiredEnvironments(retired)
            .Where(e => e.DeployedAt < rangeTo && e.Status != DeployEventStatuses.Removed);
        if (filter is not null) knownQuery = knownQuery.Where(e => EF.Parameter(filter).Contains(e.Product));
        var knownServices = await knownQuery
            .GroupBy(e => new { e.Product, e.Service })
            .Select(g => new { g.Key.Product, g.Key.Service, Last = g.Max(e => e.DeployedAt) })
            .ToListAsync(ct);

        // Incidents that can touch either window: started in it (allowing the rollout tolerance
        // before a deploy at the window start) or restored in it — a long outage can start before.
        var incidentQuery = _db.Incidents.AsNoTracking()
            .Where(i => i.StartedAt >= prevFrom - tolerance || (i.ResolvedAt != null && i.ResolvedAt >= prevFrom && i.ResolvedAt < rangeTo));
        if (filter is not null) incidentQuery = incidentQuery.Where(i => EF.Parameter(filter).Contains(i.Product));
        var incidentRows = await incidentQuery.ToListAsync(ct);

        // ── Scope ────────────────────────────────────────────────────────────────────────────

        var universe = events.Select(e => e.Environment)
            .Concat(grains.Select(g => g.Environment))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var ordered = EnvironmentOrdering.Order(universe, configuredKeys);
        var production = EnvironmentOrdering.ResolveProduction(
            ordered, settings.Environments.Select(e => (e.Key, e.IsProduction)));
        var prodSet = new HashSet<string>(production.Environments, StringComparer.OrdinalIgnoreCase);

        var incidents = await AttributeAsync(incidentRows, events, prodSet, retired, rangeTo, tolerance, window, ct);

        var productList = filter
            ?? events.Select(e => e.Product)
                .Concat(grains.Select(g => g.Product))
                .Concat(approved.Select(a => a.Product))
                .Concat(deployed.Select(d => d.Product))
                .Concat(knownServices.Select(k => k.Product))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToList();

        var labels = AnalyticsCalendar.Labels(rangeFrom, rangeTo, bucket, tz);
        var ctx = new Inputs(events, grains, batchByEvent, approved.Select(a => (a.Product, a.CreatedAt, a.ApprovedAt)).ToList(),
            deployed.Select(d => (d.Product, d.ApprovedAt, d.DeployedAt)).ToList(),
            knownServices.Select(k => (k.Product, k.Service, k.Last)).ToList(),
            incidents, prodSet, rangeFrom, rangeTo, bucket, tz, labels);

        var all = Scope.Compute(ctx, _ => true);

        // ── Metrics ──────────────────────────────────────────────────────────────────────────

        var prodLabel = production.Environments.Count switch
        {
            0 => "production (no environment resolved)",
            1 => production.Environments[0],
            _ => $"production ({string.Join(", ", production.Environments)})",
        };

        var dora = new List<DeliveryMetricDto>
        {
            Metric("deploymentFrequency", "Deployment frequency", "count", all.ProdDeploys, all.ProdDeploys, "up",
                $"Successful, non-rollback deploys to {prodLabel} in the window, every service pooled. " +
                "A redeploy of the version already running is not counted; failed attempts and rollbacks are reported apart. " +
                "The delta compares the equal-length window immediately before.",
                delta: Delta(all.ProdDeploys, all.PrevProdDeploys, all.PrevProdDeploys),
                trend: Trend(labels, l => (all.ProdDeploysByBucket.GetValueOrDefault(l), all.ProdDeploysByBucket.GetValueOrDefault(l)))),

            Metric("leadTime", "Lead time for changes", "hours", Hours(Percentiles.Median(all.LeadHours)), all.LeadHours.Count, "down",
                $"Median hours from a change being merged to its story's first successful deploy to {prodLabel} — cumulative from the " +
                "commit, so the whole path, not the last hop. Clock start is pull-request.occurredAt (fallback commit.occurredAt) as the " +
                "producer sent it; only stories with a recorded commit time are measurable — see coverage. Percentiles, never averages.",
                percentiles: Pct(all.LeadHours),
                delta: Delta(Hours(Percentiles.Median(all.LeadHours)), Hours(Percentiles.Median(all.PrevLeadHours)), all.PrevLeadHours.Count),
                trend: Trend(labels, l => (Hours(Percentiles.Median(all.LeadHoursByBucket.GetValueOrDefault(l, []))), all.LeadHoursByBucket.GetValueOrDefault(l, []).Count)),
                coverage: Coverage("stories shipped with a recorded commit time", all.StoriesWithClock, all.StoriesShipped)),

            Metric("changeFailureRate", "Change failure rate", "ratio",
                all.AnyIncidents ? Ratio(all.FailedDeploys, all.ProdDeploys) : null, all.ProdDeploys, "down",
                $"Deploys to {prodLabel} that caused at least one recorded incident ÷ successful deploys to {prodLabel} in the window — the DORA " +
                "definition. An incident is tied to the deploy it names, else to the first deploy of the version it names, else to the deploy " +
                $"that was live when it started (within {analytics.RolloutToleranceMinutes} min of rollout tolerance)" +
                (window is null ? "; no cap on how long after a deploy an incident still counts." : $"; an incident more than {analytics.AttributionWindowDays} days after a deploy no longer counts against it.") +
                " Shows — until at least one incident is recorded for the selection; see Incidents (admins) and Settings → Delivery Metrics.",
                delta: all.AnyIncidents ? Delta(Ratio(all.FailedDeploys, all.ProdDeploys), Ratio(all.PrevFailedDeploys, all.PrevProdDeploys), all.PrevProdDeploys) : null,
                trend: all.AnyIncidents ? Trend(labels, l =>
                {
                    var b = all.BucketCounts.GetValueOrDefault(l) ?? new BucketCount();
                    return (Ratio(b.FailedDeploys, b.Counted), b.Counted);
                }) : null,
                coverage: Coverage("incidents tied to a deploy", all.IncidentsAttributed, all.IncidentsStarted)),

            Metric("timeToRestore", "Time to restore service", "hours", Hours(Percentiles.Median(all.RestoreHours)), all.RestoreHours.Count, "down",
                $"Median hours from an incident starting on {prodLabel} to service being restored, for incidents restored inside the window. " +
                "Open incidents are not counted until they close — see coverage. Percentiles, never averages; p90 in percentiles.",
                percentiles: Pct(all.RestoreHours),
                delta: Delta(Hours(Percentiles.Median(all.RestoreHours)), Hours(Percentiles.Median(all.PrevRestoreHours)), all.PrevRestoreHours.Count),
                trend: Trend(labels, l => (Hours(Percentiles.Median(all.RestoreHoursByBucket.GetValueOrDefault(l, []))), all.RestoreHoursByBucket.GetValueOrDefault(l, []).Count)),
                coverage: Coverage("incidents with a restore time (open ones excluded)", all.RestoreHours.Count, all.RestoreHours.Count + all.IncidentsOpen)),
        };

        var flow = new List<DeliveryMetricDto>
        {
            Metric("approvalLatency", "Approval latency", "hours", Hours(Percentiles.Median(all.ApprovalHours)), all.ApprovalHours.Count, "down",
                "Median hours from a promotion candidate being created to it being approved, for candidates approved inside the window. " +
                "Waiting on a human, not on the pipeline. p90 in percentiles.",
                percentiles: Pct(all.ApprovalHours),
                delta: Delta(Hours(Percentiles.Median(all.ApprovalHours)), Hours(Percentiles.Median(all.PrevApprovalHours)), all.PrevApprovalHours.Count)),

            Metric("deployLatency", "Deploy latency", "hours", Hours(Percentiles.Median(all.DeployLatencyHours)), all.DeployLatencyHours.Count, "down",
                "Median hours from a promotion candidate being approved to it being deployed, for candidates deployed inside the window. " +
                "Waiting on the pipeline. p90 in percentiles.",
                percentiles: Pct(all.DeployLatencyHours),
                delta: Delta(Hours(Percentiles.Median(all.DeployLatencyHours)), Hours(Percentiles.Median(all.PrevDeployLatencyHours)), all.PrevDeployLatencyHours.Count)),

            Metric("storiesShipped", "Stories shipped", "count", all.StoriesShipped, all.StoriesShipped, "up",
                $"Stories whose first successful deploy to {prodLabel} fell in the window, counted once per product. Undercounts by the " +
                "share of deploys that carry no story reference — see coverage.",
                delta: Delta(all.StoriesShipped, all.PrevStoriesShipped, all.PrevStoriesShipped),
                trend: Trend(labels, l => (all.StoriesByBucket.GetValueOrDefault(l), all.StoriesByBucket.GetValueOrDefault(l))),
                coverage: Coverage("deploys carrying a story reference", all.DeploysWithStory, all.Deployments)),
        };

        var quality = new List<DeliveryMetricDto>
        {
            Metric("deployFailureRate", "Deploy failure rate", "ratio", Ratio(all.ProdFailed + all.ProdRollbacks, all.ProdDeploys + all.ProdFailed), all.ProdDeploys + all.ProdFailed, "down",
                $"(failed attempts + rollbacks) ÷ (succeeded + failed attempts) on {prodLabel} in the window. Attempt-based: a pipeline that " +
                "failed before reaching users still counts, and a bad deploy fixed forward without a rollback does not — this is not " +
                "DORA's change failure rate, which needs incident data.",
                delta: Delta(Ratio(all.ProdFailed + all.ProdRollbacks, all.ProdDeploys + all.ProdFailed),
                    Ratio(all.PrevProdFailed + all.PrevProdRollbacks, all.PrevProdDeploys + all.PrevProdFailed), all.PrevProdDeploys + all.PrevProdFailed),
                trend: Trend(labels, l =>
                {
                    var b = all.BucketCounts.GetValueOrDefault(l) ?? new BucketCount();
                    return (Ratio(b.Failed + b.Rollbacks, b.Counted + b.Failed), b.Counted + b.Failed);
                })),

            Metric("rollbackRate", "Rollback rate", "ratio", Ratio(all.ProdRollbacks, all.ProdDeploys), all.ProdDeploys, "down",
                $"Rollback deploys ÷ successful deploys on {prodLabel} in the window.",
                delta: Delta(Ratio(all.ProdRollbacks, all.ProdDeploys), Ratio(all.PrevProdRollbacks, all.PrevProdDeploys), all.PrevProdDeploys),
                trend: Trend(labels, l =>
                {
                    var b = all.BucketCounts.GetValueOrDefault(l) ?? new BucketCount();
                    return (Ratio(b.Rollbacks, b.Counted), b.Counted);
                })),
        };

        var cadence = new List<DeliveryMetricDto>
        {
            Metric("staleServices", "Stale services", "count", all.StaleServices, all.Services, "down",
                $"Services with deploy history but no successful deploy in the window, on any environment — of {all.Services} services known. " +
                "A service nobody deploys is where risk accumulates quietly.",
                delta: null),

            Metric("batchSize", "Batch size", "count", Round1(Percentiles.Median(all.BatchSizes)), all.BatchSizes.Count, "down",
                $"Median work items per successful deploy to {prodLabel}, zeros included — a deploy with no story counts as 0, so this doubles " +
                "as a coverage smell when it sits below 1.",
                percentiles: Pct(all.BatchSizes)),
        };

        var dataQuality = new List<DeliveryMetricDto>
        {
            Metric("storyCoverage", "Story coverage", "ratio", Ratio(all.DeploysWithStory, all.Deployments), all.Deployments, "up",
                "Deploys in the window, on any environment, carrying at least one work-item reference ÷ all deploys. The credibility of every " +
                "per-story number on this page: at 65% coverage they undercount by about a third.",
                delta: Delta(Ratio(all.DeploysWithStory, all.Deployments), null, 0)),

            Metric("incidentAttribution", "Incident attribution", "ratio", Ratio(all.IncidentsExplicit, all.IncidentsStarted), all.IncidentsStarted, "up",
                $"Incidents on {prodLabel} started in the window that name the deploy or the version that caused them ÷ all such incidents " +
                $"(explicit {all.IncidentsExplicit}, inferred from what was live {all.IncidentsInferred}, outside the attribution window {all.IncidentsOutsideWindow}, " +
                $"unattributed {all.IncidentsUnattributed}). Inferred attribution is a guess that gets worse the busier a service is — push " +
                "responders and producers to name the version.",
                coverage: Coverage("incidents tied to a deploy at all", all.IncidentsAttributed, all.IncidentsStarted)),

            Metric("leadTimeCoverage", "Lead time coverage", "ratio", Ratio(all.StoriesWithClock, all.StoriesShipped), all.StoriesShipped, "up",
                $"Stories shipped to {prodLabel} in the window whose producer sent a commit time ÷ stories shipped. Never compare lead time " +
                "across periods with materially different coverage.",
                delta: Delta(Ratio(all.StoriesWithClock, all.StoriesShipped), Ratio(all.PrevStoriesWithClock, all.PrevStoriesShipped), all.PrevStoriesShipped)),
        };

        var sections = new List<DeliverySectionDto>
        {
            new("dora", "DORA", dora),
            new("flow", "Flow", flow),
            new("quality", "Quality", quality),
            new("cadence", "Cadence", cadence),
            new("dataQuality", "Data quality", dataQuality),
        };

        // ── Breakdowns ───────────────────────────────────────────────────────────────────────

        var byProduct = productList.Select(product =>
        {
            var s = Scope.Compute(ctx, p => string.Equals(p, product, StringComparison.OrdinalIgnoreCase));
            return new DeliveryProductRowDto(
                product, s.Services, s.ProdDeploys, s.PrevProdDeploys, s.StoriesShipped,
                Hours(Percentiles.Median(s.LeadHours)),
                Ratio(s.StoriesWithClock, s.StoriesShipped),
                Ratio(s.ProdFailed + s.ProdRollbacks, s.ProdDeploys + s.ProdFailed),
                Ratio(s.ProdRollbacks, s.ProdDeploys),
                Ratio(s.DeploysWithStory, s.Deployments),
                s.StaleServices,
                s.IncidentsStarted,
                s.AnyIncidents ? Ratio(s.FailedDeploys, s.ProdDeploys) : null,
                Hours(Percentiles.Median(s.RestoreHours)));
        }).ToList();

        var byService = BuildServiceRows(ctx, span);

        var leadByEnv = ordered.Select(env =>
        {
            var hours = all.LeadHoursByEnvironment.GetValueOrDefault(env, []);
            return new DeliveryLeadTimeEnvDto(env, hours.Count,
                Hours(Percentiles.Median(hours)), Hours(Percentiles.Compute(hours, 0.75)), Hours(Percentiles.Compute(hours, 0.9)));
        }).ToList();

        var explanation = production.Source switch
        {
            "marked" => $"Marked as production stage in Settings → Environments: {string.Join(", ", production.Environments)}.",
            "default-name" => $"Recognised as production by name (default mapping; add the key in Settings → Environments to override): {string.Join(", ", production.Environments)}.",
            _ => production.Environments.Count > 0
                ? $"Last environment in order (no production stage marked): {production.Environments[0]}."
                : "No deployments in scope, so no production environment could be resolved.",
        };

        // Prod-like names the rules left out: a marked "production" beside an unconfigured "prod" is
        // the classic one, and the deploys under the loser count nowhere on this page.
        var ignoredProdLike = ordered
            .Where(k => !prodSet.Contains(k) && EnvironmentStage.IsProductionByName(k))
            .ToList();
        if (ignoredProdLike.Count > 0)
            explanation += $" Not counted although named like production: {string.Join(", ", ignoredProdLike)} — mark it as production, or make it an alias, in Settings → Environments.";

        return new DeliveryMetricsResponseDto(
            asOf,
            new AnalyticsRangeDto(rangeFrom, rangeTo),
            new AnalyticsRangeDto(prevFrom, rangeFrom),
            productList,
            new DeliveryProductionDto(production.Environments, production.Source, explanation, ignoredProdLike),
            new DeliveryDefinitionDto(
                Bucket: bucket,
                Tz: tz.Id,
                Deploy: "A deploy event with status succeeded that is not a rollback; a redeploy of the version already running is not counted. Failed attempts and rollbacks are reported apart. Events on retired environments are left out.",
                Production: explanation,
                StoryShipped: "A story is shipped when it first lands successfully on any production environment, dated by that first landing; counted once per product.",
                LeadTime: "Clock start: pull-request.occurredAt, fallback commit.occurredAt, as recorded on the work item. Clock stop: the story's first successful deploy to any production environment. Stories without a commit time are excluded and reported in coverage."),
            sections, leadByEnv, byProduct, byService,
            includeIncidents
                ? incidents
                    .Where(i => (i.StartedAt >= rangeFrom && i.StartedAt < rangeTo) || (i.ResolvedAt >= rangeFrom && i.ResolvedAt < rangeTo))
                    .OrderByDescending(i => i.StartedAt)
                    .Select(i => new DeliveryIncidentDto(i.Id, i.Product, i.Service, i.Environment, i.StartedAt, i.ResolvedAt,
                        i.ResolvedAt is null ? null : Round1((i.ResolvedAt.Value - i.StartedAt).TotalHours),
                        i.Attribution, i.DeployEventId, i.DeployedAt, i.Version, i.Url))
                    .ToList()
                : null);
    }

    // ── Incident attribution ─────────────────────────────────────────────────────────────────

    /// <summary>An incident with the deploy the report holds responsible, if any.</summary>
    /// <param name="DeployEventId">Counts against this deploy for change failure rate — null for
    /// <c>none</c> and <c>outside-window</c>, and for an explicit deploy the report could not find.</param>
    private sealed record AttributedIncident(
        Guid Id, string Product, string Service, string Environment,
        DateTimeOffset StartedAt, DateTimeOffset? ResolvedAt,
        string Attribution, Guid? DeployEventId, DateTimeOffset? DeployedAt, string? Version, string? Url);

    /// <summary>
    /// Ties each production incident to a deploy, at query time — a stored link would go stale the
    /// moment a late deploy event or a backfill arrived. In order: the deploy the incident names;
    /// the first deploy of the version it names; the deploy that was live when it started (latest
    /// successful, non-rollback deploy at or before the start plus the rollout tolerance); nothing.
    /// A deploy after the start is a candidate remediation, never a cause. The attribution window
    /// caps how long after a deploy an inferred or version match still counts; a deploy the
    /// reporter named always counts.
    /// </summary>
    private async Task<List<AttributedIncident>> AttributeAsync(
        List<Features.Incidents.Models.Incident> rows, List<EventRow> windowEvents, HashSet<string> prodSet,
        IReadOnlySet<string> retired, DateTimeOffset rangeTo, TimeSpan tolerance, TimeSpan? window, CancellationToken ct)
    {
        var incidents = rows
            .Where(i => prodSet.Contains(i.Environment) && !retired.Contains(i.Environment))
            .ToList();
        if (incidents.Count == 0) return [];

        // Candidate deploys: the full successful history of the services the incidents name — the
        // live version at an incident's start can predate the report's windows. Few services, so a
        // product/service/environment IN filter is cheap; exact triples are matched in memory.
        var products = incidents.Select(i => i.Product).Distinct().ToList();
        var services = incidents.Select(i => i.Service).Distinct().ToList();
        var environments = incidents.Select(i => i.Environment).Distinct().ToList();
        var named = incidents.Where(i => i.CausedByDeployEventId is not null).Select(i => i.CausedByDeployEventId!.Value).Distinct().ToList();
        var candidates = (await _db.DeployEvents.AsNoTracking()
                .Where(e => e.DeployedAt < rangeTo && e.Status == "succeeded")
                .Where(e => (EF.Parameter(products).Contains(e.Product) && EF.Parameter(services).Contains(e.Service) && EF.Parameter(environments).Contains(e.Environment))
                            || EF.Parameter(named).Contains(e.Id))
                .Select(e => new EventRow(e.Id, e.Product, e.Service, e.Environment, e.Version, e.PreviousVersion, e.IsRollback, e.Status, e.DeployedAt))
                .ToListAsync(ct))
            .Concat(windowEvents)
            .GroupBy(e => e.Id).Select(g => g.First())
            .ToList();
        var byId = candidates.ToDictionary(e => e.Id);
        var byTriple = candidates
            .GroupBy(e => (e.Product, e.Service, e.Environment), TripleComparer.Instance)
            .ToDictionary(g => g.Key, g => g.OrderBy(e => e.DeployedAt).ToList(), TripleComparer.Instance);

        var result = new List<AttributedIncident>(incidents.Count);
        foreach (var i in incidents)
        {
            var history = byTriple.GetValueOrDefault((i.Product, i.Service, i.Environment)) ?? [];
            var latestStart = i.StartedAt + tolerance;

            if (i.CausedByDeployEventId is { } namedId)
            {
                var evt = byId.GetValueOrDefault(namedId);
                result.Add(new AttributedIncident(i.Id, i.Product, i.Service, i.Environment, i.StartedAt, i.ResolvedAt,
                    "explicit", evt?.Id, evt?.DeployedAt, i.Version ?? evt?.Version, i.Url));
                continue;
            }

            EventRow? match = null;
            var attribution = "none";
            if (!string.IsNullOrWhiteSpace(i.Version))
            {
                match = history.FirstOrDefault(e => !e.IsRollback && e.DeployedAt <= latestStart
                    && string.Equals(e.Version, i.Version, StringComparison.OrdinalIgnoreCase));
                if (match is not null) attribution = "version";
            }
            if (match is null)
            {
                match = history.LastOrDefault(e => !e.IsRollback && e.DeployedAt <= latestStart);
                if (match is not null) attribution = "inferred";
            }
            if (match is not null && window is not null && i.StartedAt - match.DeployedAt > window.Value)
            {
                result.Add(new AttributedIncident(i.Id, i.Product, i.Service, i.Environment, i.StartedAt, i.ResolvedAt,
                    "outside-window", null, match.DeployedAt, i.Version ?? match.Version, i.Url));
                continue;
            }
            result.Add(new AttributedIncident(i.Id, i.Product, i.Service, i.Environment, i.StartedAt, i.ResolvedAt,
                attribution, match?.Id, match?.DeployedAt, i.Version ?? match?.Version, i.Url));
        }
        return result;
    }

    private sealed class TripleComparer : IEqualityComparer<(string Product, string Service, string Environment)>
    {
        public static readonly TripleComparer Instance = new();
        public bool Equals((string Product, string Service, string Environment) x, (string Product, string Service, string Environment) y)
            => string.Equals(x.Product, y.Product, StringComparison.OrdinalIgnoreCase)
            && string.Equals(x.Service, y.Service, StringComparison.OrdinalIgnoreCase)
            && string.Equals(x.Environment, y.Environment, StringComparison.OrdinalIgnoreCase);
        public int GetHashCode((string Product, string Service, string Environment) obj)
            => HashCode.Combine(obj.Product.ToUpperInvariant(), obj.Service.ToUpperInvariant(), obj.Environment.ToUpperInvariant());
    }

    // ── Per-service table ────────────────────────────────────────────────────────────────────

    private static List<DeliveryServiceRowDto> BuildServiceRows(Inputs ctx, TimeSpan span)
    {
        var rows = new List<DeliveryServiceRowDto>();
        var current = ctx.Events.Where(e => e.DeployedAt >= ctx.RangeFrom).ToList();
        var previous = ctx.Events.Where(e => e.DeployedAt < ctx.RangeFrom).ToList();

        var prevCounted = previous
            .Where(e => DeployEventRows.Classify(e, false, false) == EventKind.Counted)
            .GroupBy(e => (e.Product, e.Service), ServiceKeyComparer.Instance)
            .ToDictionary(g => g.Key, g => g.Count(), ServiceKeyComparer.Instance);

        // Lead time to production per service: the first production landing of each story through
        // this service.
        var leadByService = ctx.Grains
            .Where(g => ctx.ProdSet.Contains(g.Environment))
            .GroupBy(g => (g.Product, g.Service, g.WorkItemKey), StoryServiceKeyComparer.Instance)
            .Select(g =>
            {
                var first = g.MinBy(x => x.First)!;
                var committed = g.Where(x => x.CommittedAt is not null).Select(x => x.CommittedAt).Min();
                return (g.Key.Product, g.Key.Service, first.First, committed);
            })
            .Where(x => x.First >= ctx.RangeFrom && x.committed is not null)
            .GroupBy(x => (x.Product, x.Service), ServiceKeyComparer.Instance)
            .ToDictionary(g => g.Key, g => g.Select(x => (x.First - x.committed!.Value).TotalHours).ToList(), ServiceKeyComparer.Instance);

        var incidentsByService = ctx.Incidents
            .Where(i => i.StartedAt >= ctx.RangeFrom && i.StartedAt < ctx.RangeTo)
            .GroupBy(i => (i.Product, i.Service), ServiceKeyComparer.Instance)
            .ToDictionary(g => g.Key, g => g.Count(), ServiceKeyComparer.Instance);

        var seen = new HashSet<(string, string)>(ServiceKeyComparer.Instance);
        foreach (var group in current.GroupBy(e => (e.Product, e.Service), ServiceKeyComparer.Instance))
        {
            var times = new List<DateTimeOffset>();
            var batch = new List<double>();
            var counted = 0; var failed = 0; var rollbacks = 0;
            DateTimeOffset? last = null;
            foreach (var e in group)
            {
                switch (DeployEventRows.Classify(e, false, false))
                {
                    case EventKind.Counted:
                        counted++; times.Add(e.DeployedAt); batch.Add(ctx.BatchByEvent.GetValueOrDefault(e.Id));
                        if (last is null || e.DeployedAt > last) last = e.DeployedAt;
                        break;
                    case EventKind.Failed: failed++; break;
                    case EventKind.Rollback: rollbacks++; break;
                }
            }
            times.Sort();
            var intervals = new List<double>();
            for (var i = 1; i < times.Count; i++) intervals.Add((times[i] - times[i - 1]).TotalHours);

            // A service with only failed attempts in the window shows its true last success.
            last ??= ctx.KnownServices.FirstOrDefault(k =>
                ServiceKeyComparer.Instance.Equals((k.Product, k.Service), group.Key)).Last;

            seen.Add(group.Key);
            rows.Add(new DeliveryServiceRowDto(
                group.Key.Product, group.Key.Service, counted, prevCounted.GetValueOrDefault(group.Key),
                span.TotalDays > 0 ? Math.Round(counted / span.TotalDays * 7, 2) : 0,
                last, Round1(Percentiles.Median(intervals)), failed, rollbacks,
                Round1(Percentiles.Median(batch)),
                Hours(Percentiles.Median(leadByService.GetValueOrDefault(group.Key, []))),
                incidentsByService.GetValueOrDefault(group.Key)));
        }

        // Stale: known, nothing in the window.
        foreach (var k in ctx.KnownServices)
        {
            if (seen.Contains((k.Product, k.Service))) continue;
            rows.Add(new DeliveryServiceRowDto(
                k.Product, k.Service, 0, prevCounted.GetValueOrDefault((k.Product, k.Service)), 0,
                k.Last, null, 0, 0, null, null, incidentsByService.GetValueOrDefault((k.Product, k.Service))));
        }

        return rows
            .OrderByDescending(r => r.Deploys)
            .ThenBy(r => r.Product, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Service, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // ── Scope arithmetic ─────────────────────────────────────────────────────────────────────

    private sealed record Grain(string Product, string Service, string WorkItemKey, string Environment, DateTimeOffset First, DateTimeOffset? CommittedAt);

    private sealed record Inputs(
        List<EventRow> Events,
        List<Grain> Grains,
        Dictionary<Guid, int> BatchByEvent,
        List<(string Product, DateTimeOffset CreatedAt, DateTimeOffset ApprovedAt)> Approved,
        List<(string Product, DateTimeOffset ApprovedAt, DateTimeOffset DeployedAt)> Deployed,
        List<(string Product, string Service, DateTimeOffset Last)> KnownServices,
        List<AttributedIncident> Incidents,
        HashSet<string> ProdSet,
        DateTimeOffset RangeFrom,
        DateTimeOffset RangeTo,
        string Bucket,
        TimeZoneInfo Tz,
        List<string> Labels);

    private sealed class BucketCount
    {
        public int Counted;
        public int Failed;
        public int Rollbacks;
        // Counted deploys in this bucket with at least one attributed incident.
        public int FailedDeploys;
    }

    /// <summary>Everything the metrics read, for one product predicate — the whole selection or one product.</summary>
    private sealed class Scope
    {
        public int ProdDeploys, PrevProdDeploys, ProdFailed, PrevProdFailed, ProdRollbacks, PrevProdRollbacks;
        public int Deployments, DeploysWithStory;
        public int StoriesShipped, PrevStoriesShipped, StoriesWithClock, PrevStoriesWithClock;
        public int Services, StaleServices;
        public bool AnyIncidents;
        public int FailedDeploys, PrevFailedDeploys;
        public int IncidentsStarted, IncidentsAttributed, IncidentsExplicit, IncidentsInferred, IncidentsOutsideWindow, IncidentsUnattributed, IncidentsOpen;
        public List<double> RestoreHours = [], PrevRestoreHours = [];
        public Dictionary<string, List<double>> RestoreHoursByBucket = new();
        public List<double> LeadHours = [], PrevLeadHours = [], BatchSizes = [];
        public List<double> ApprovalHours = [], PrevApprovalHours = [], DeployLatencyHours = [], PrevDeployLatencyHours = [];
        public Dictionary<string, int> ProdDeploysByBucket = new(), StoriesByBucket = new();
        public Dictionary<string, BucketCount> BucketCounts = new();
        public Dictionary<string, List<double>> LeadHoursByBucket = new();
        public Dictionary<string, List<double>> LeadHoursByEnvironment = new(StringComparer.OrdinalIgnoreCase);

        public static Scope Compute(Inputs ctx, Func<string, bool> inScope)
        {
            var s = new Scope();
            string Bucket(DateTimeOffset at) => AnalyticsCalendar.BucketStart(at, ctx.Bucket, ctx.Tz);

            foreach (var e in ctx.Events)
            {
                if (!inScope(e.Product)) continue;
                var inWindow = e.DeployedAt >= ctx.RangeFrom;
                var kind = DeployEventRows.Classify(e, false, false);
                if (inWindow && kind != EventKind.Skipped)
                {
                    s.Deployments++;
                    if (ctx.BatchByEvent.ContainsKey(e.Id)) s.DeploysWithStory++;
                }
                if (!ctx.ProdSet.Contains(e.Environment)) continue;

                if (inWindow)
                {
                    var b = s.BucketCounts.TryGetValue(Bucket(e.DeployedAt), out var bc) ? bc : s.BucketCounts[Bucket(e.DeployedAt)] = new BucketCount();
                    switch (kind)
                    {
                        case EventKind.Counted:
                            s.ProdDeploys++; b.Counted++;
                            s.ProdDeploysByBucket[Bucket(e.DeployedAt)] = s.ProdDeploysByBucket.GetValueOrDefault(Bucket(e.DeployedAt)) + 1;
                            s.BatchSizes.Add(ctx.BatchByEvent.GetValueOrDefault(e.Id));
                            break;
                        case EventKind.Failed: s.ProdFailed++; b.Failed++; break;
                        case EventKind.Rollback: s.ProdRollbacks++; b.Rollbacks++; break;
                    }
                }
                else
                {
                    switch (kind)
                    {
                        case EventKind.Counted: s.PrevProdDeploys++; break;
                        case EventKind.Failed: s.PrevProdFailed++; break;
                        case EventKind.Rollback: s.PrevProdRollbacks++; break;
                    }
                }
            }

            // Stories: once per (product, work item); shipped at the first landing on ANY production
            // environment (one production suffices — further regions are a business decision, not a
            // backlog); clock start the earliest commit time any of those grains recorded.
            var stories = ctx.Grains
                .Where(g => inScope(g.Product) && ctx.ProdSet.Contains(g.Environment))
                .GroupBy(g => (g.Product, g.WorkItemKey), StoryKeyComparer.Instance)
                .Select(g => (
                    ShippedAt: g.Min(x => x.First),
                    CommittedAt: g.Where(x => x.CommittedAt is not null).Select(x => x.CommittedAt).Min()));
            foreach (var story in stories)
            {
                var hours = story.CommittedAt is null ? (double?)null : (story.ShippedAt - story.CommittedAt.Value).TotalHours;
                if (story.ShippedAt >= ctx.RangeFrom)
                {
                    s.StoriesShipped++;
                    var label = Bucket(story.ShippedAt);
                    s.StoriesByBucket[label] = s.StoriesByBucket.GetValueOrDefault(label) + 1;
                    if (hours is not null)
                    {
                        s.StoriesWithClock++;
                        s.LeadHours.Add(hours.Value);
                        (s.LeadHoursByBucket.TryGetValue(label, out var list) ? list : s.LeadHoursByBucket[label] = []).Add(hours.Value);
                    }
                }
                else
                {
                    s.PrevStoriesShipped++;
                    if (hours is not null) { s.PrevStoriesWithClock++; s.PrevLeadHours.Add(hours.Value); }
                }
            }

            // Lead time per environment, cumulative from commit: first landing per (product, story, env).
            foreach (var g in ctx.Grains
                         .Where(g => inScope(g.Product))
                         .GroupBy(g => (Product: g.Product, Key: g.WorkItemKey, Env: g.Environment), GrainKeyComparer.Instance))
            {
                var first = g.Min(x => x.First);
                if (first < ctx.RangeFrom) continue;
                var committed = g.Where(x => x.CommittedAt is not null).Select(x => x.CommittedAt).Min();
                if (committed is null) continue;
                (s.LeadHoursByEnvironment.TryGetValue(g.Key.Env, out var list) ? list : s.LeadHoursByEnvironment[g.Key.Env] = [])
                    .Add((first - committed.Value).TotalHours);
            }

            foreach (var a in ctx.Approved.Where(a => inScope(a.Product)))
                (a.ApprovedAt >= ctx.RangeFrom ? s.ApprovalHours : s.PrevApprovalHours).Add((a.ApprovedAt - a.CreatedAt).TotalHours);
            foreach (var d in ctx.Deployed.Where(d => inScope(d.Product)))
                (d.DeployedAt >= ctx.RangeFrom ? s.DeployLatencyHours : s.PrevDeployLatencyHours).Add((d.DeployedAt - d.ApprovedAt).TotalHours);

            var active = new HashSet<(string, string)>(
                ctx.Events.Where(e => inScope(e.Product) && e.DeployedAt >= ctx.RangeFrom
                    && DeployEventRows.Classify(e, false, false) == EventKind.Counted)
                    .Select(e => (e.Product, e.Service)),
                ServiceKeyComparer.Instance);
            var known = ctx.KnownServices.Where(k => inScope(k.Product)).ToList();
            s.Services = known.Count;
            s.StaleServices = known.Count(k => !active.Contains((k.Product, k.Service)));

            // Incidents: change failure rate counts a deploy once however many incidents it caused;
            // time to restore counts every incident that closed in the window.
            var scoped = ctx.Incidents.Where(i => inScope(i.Product)).ToList();
            s.AnyIncidents = scoped.Count > 0;
            var failedDeployIds = new HashSet<Guid>();
            var prevFailedDeployIds = new HashSet<Guid>();
            var countedInWindow = ctx.Events
                .Where(e => inScope(e.Product) && ctx.ProdSet.Contains(e.Environment)
                    && DeployEventRows.Classify(e, false, false) == EventKind.Counted)
                .ToDictionary(e => e.Id);
            foreach (var i in scoped)
            {
                var startedInWindow = i.StartedAt >= ctx.RangeFrom && i.StartedAt < ctx.RangeTo;
                if (startedInWindow)
                {
                    s.IncidentsStarted++;
                    switch (i.Attribution)
                    {
                        case "explicit": case "version": s.IncidentsExplicit++; s.IncidentsAttributed++; break;
                        case "inferred": s.IncidentsInferred++; s.IncidentsAttributed++; break;
                        case "outside-window": s.IncidentsOutsideWindow++; break;
                        default: s.IncidentsUnattributed++; break;
                    }
                    if (i.ResolvedAt is null) s.IncidentsOpen++;
                }
                if (i.ResolvedAt is { } resolved && resolved >= ctx.RangeFrom - (ctx.RangeTo - ctx.RangeFrom) && resolved < ctx.RangeTo)
                {
                    var hours = (resolved - i.StartedAt).TotalHours;
                    if (resolved >= ctx.RangeFrom)
                    {
                        s.RestoreHours.Add(hours);
                        var label = Bucket(resolved);
                        (s.RestoreHoursByBucket.TryGetValue(label, out var list) ? list : s.RestoreHoursByBucket[label] = []).Add(hours);
                    }
                    else s.PrevRestoreHours.Add(hours);
                }
                if (i.DeployEventId is { } deployId && countedInWindow.TryGetValue(deployId, out var deploy))
                {
                    if (deploy.DeployedAt >= ctx.RangeFrom)
                    {
                        if (failedDeployIds.Add(deployId))
                        {
                            var b = s.BucketCounts.TryGetValue(Bucket(deploy.DeployedAt), out var bc) ? bc : s.BucketCounts[Bucket(deploy.DeployedAt)] = new BucketCount();
                            b.FailedDeploys++;
                        }
                    }
                    else prevFailedDeployIds.Add(deployId);
                }
            }
            s.FailedDeploys = failedDeployIds.Count;
            s.PrevFailedDeploys = prevFailedDeployIds.Count;

            return s;
        }
    }

    // ── Small helpers ────────────────────────────────────────────────────────────────────────

    private static DeliveryMetricDto Metric(
        string key, string title, string unit, double? value, int n, string goodWhen, string definition,
        DeliveryPercentilesDto? percentiles = null, DeliveryDeltaDto? delta = null,
        List<DeliveryTrendPointDto>? trend = null, DeliveryCoverageDto? coverage = null)
        => new(key, title, Available: true, Reason: null, unit, value, n, percentiles, delta, trend ?? [], coverage, definition, goodWhen);

    private static DeliveryDeltaDto? Delta(double? value, double? previous, int previousN)
        => previous is null && previousN == 0 && value is null ? null
            : new DeliveryDeltaDto(previous, value is not null && previous is not null ? Math.Round(value.Value - previous.Value, 3) : null, previousN);

    private static List<DeliveryTrendPointDto> Trend(List<string> labels, Func<string, (double? Value, int N)> point)
        => labels.Select(l => { var (v, n) = point(l); return new DeliveryTrendPointDto(l, v, n); }).ToList();

    private static DeliveryCoverageDto Coverage(string label, int numerator, int denominator)
        => new(label, numerator, denominator, denominator > 0 ? Math.Round(numerator / (double)denominator, 3) : 0);

    private static DeliveryPercentilesDto? Pct(IReadOnlyList<double> values)
        => values.Count == 0 ? null : new DeliveryPercentilesDto(
            Round1(Percentiles.Median(values)), Round1(Percentiles.Compute(values, 0.75)), Round1(Percentiles.Compute(values, 0.9)));

    private static double? Hours(double? value) => Round1(value);
    private static double? Round1(double? value) => value is null ? null : Math.Round(value.Value, 1);
    private static double? Ratio(int numerator, int denominator) => denominator > 0 ? Math.Round(numerator / (double)denominator, 3) : null;

    private sealed class ServiceKeyComparer : IEqualityComparer<(string Product, string Service)>
    {
        public static readonly ServiceKeyComparer Instance = new();
        public bool Equals((string Product, string Service) x, (string Product, string Service) y)
            => string.Equals(x.Product, y.Product, StringComparison.OrdinalIgnoreCase)
            && string.Equals(x.Service, y.Service, StringComparison.OrdinalIgnoreCase);
        public int GetHashCode((string Product, string Service) obj)
            => HashCode.Combine(obj.Product.ToUpperInvariant(), obj.Service.ToUpperInvariant());
    }

    private sealed class StoryKeyComparer : IEqualityComparer<(string Product, string Key)>
    {
        public static readonly StoryKeyComparer Instance = new();
        public bool Equals((string Product, string Key) x, (string Product, string Key) y)
            => string.Equals(x.Product, y.Product, StringComparison.OrdinalIgnoreCase)
            && string.Equals(x.Key, y.Key, StringComparison.OrdinalIgnoreCase);
        public int GetHashCode((string Product, string Key) obj)
            => HashCode.Combine(obj.Product.ToUpperInvariant(), obj.Key.ToUpperInvariant());
    }

    private sealed class StoryServiceKeyComparer : IEqualityComparer<(string Product, string Service, string Key)>
    {
        public static readonly StoryServiceKeyComparer Instance = new();
        public bool Equals((string Product, string Service, string Key) x, (string Product, string Service, string Key) y)
            => string.Equals(x.Product, y.Product, StringComparison.OrdinalIgnoreCase)
            && string.Equals(x.Service, y.Service, StringComparison.OrdinalIgnoreCase)
            && string.Equals(x.Key, y.Key, StringComparison.OrdinalIgnoreCase);
        public int GetHashCode((string Product, string Service, string Key) obj)
            => HashCode.Combine(obj.Product.ToUpperInvariant(), obj.Service.ToUpperInvariant(), obj.Key.ToUpperInvariant());
    }

    private sealed class GrainKeyComparer : IEqualityComparer<(string Product, string Key, string Env)>
    {
        public static readonly GrainKeyComparer Instance = new();
        public bool Equals((string Product, string Key, string Env) x, (string Product, string Key, string Env) y)
            => string.Equals(x.Product, y.Product, StringComparison.OrdinalIgnoreCase)
            && string.Equals(x.Key, y.Key, StringComparison.OrdinalIgnoreCase)
            && string.Equals(x.Env, y.Env, StringComparison.OrdinalIgnoreCase);
        public int GetHashCode((string Product, string Key, string Env) obj)
            => HashCode.Combine(obj.Product.ToUpperInvariant(), obj.Key.ToUpperInvariant(), obj.Env.ToUpperInvariant());
    }
}
