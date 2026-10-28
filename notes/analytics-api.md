# Analytics API

Read-only aggregations over deploy events and promotion candidates, powering the Analytics page.
All endpoints live under `/api/analytics` (CanApprove policy) and compute on demand from the
transactional tables — there is no snapshot store; numbers are as fresh as the last ingest.

Two conventions every endpoint follows:

- **Responses describe their own counting.** Aggregates echo a `definition` block (what was
  counted, in which timezone, with which exclusions). Two numbers are only comparable when their
  definitions match; a chart pasted into a report stays self-explaining.
- **Coverage is first-class.** Any number derived from work-item references is accompanied by how
  many deployments carried no work item at all. With real-world coverage around two-thirds, a
  story count without this qualifier misleads.

Durations are reported as percentiles (p50/p75/p90), never averages — lead-time and latency
distributions are tail-heavy and the mean systematically flatters them.

Common query parameters: `from`/`to` (ISO-8601; window is half-open `[from, to)`; default: last
14 days), `tz` (IANA id, default UTC — buckets are cut at local midnights), `bucket`
(`day`|`week`|`month`; weeks start Monday, months on the 1st).

Two kinds of endpoint live here. The first four are **operational**: one product (or one filter) at a
time, feeding the Analytics page. `/delivery` is the **report**: several products pooled as one
population, every metric in one envelope, feeding the Delivery metrics page.

---

## GET /deployments/frequency

How often things change, per series.

Query: `product?`, `serviceName?`, `environment?`, `from?`, `to?`, `bucket?` (default `day`),
`groupBy?` (`none`|`service`|`environment`|`product`, default `none`), `tz?`,
`includeRollbacks?` (default false), `includeRedeploys?` (default false; a redeploy is
`version == previousVersion`), `summaryOnly?` (default false — when true, every series'
`buckets` array is empty; use for tables that read summaries alone, where at hundreds of
services the zero-filled buckets would dominate the payload).

Two `groupBy=service` specifics:

- **Stale services are reported, not dropped.** A service with deploy history under the current
  filters but no deploys in the window gets an explicit zero series (`total: 0`, its true
  all-time `lastDeployedAt`, buckets all zero). A plain GROUP BY would silently omit it — and a
  service nobody deploys is the alarm this report exists to ring.
- Combined with `summaryOnly=true` this is the intended feed for a per-service cadence table.

Counting rules (echoed in `definition`):

- `count` — succeeded, non-rollback deployments (rollbacks/redeploys only when the flags say so).
- `failed` — non-succeeded, non-rollback attempts. Always reported.
- `rollbacks` — rollback events. Always reported apart; never silently mixed into `count`.
- `changeFailureRate` = `(failed + rollbacks) / (succeeded + failed)` within the window.

```jsonc
{
  "definition": { "bucket": "day", "groupBy": "service", "tz": "Europe/Warsaw",
                  "includeRollbacks": false, "includeRedeploys": false,
                  "changeFailureRate": "(failed + rollbacks) / (succeeded + failed) within the window" },
  "range": { "from": "…", "to": "…" },
  "series": [
    {
      "key": { "product": "mpt", "serviceName": "mpt-audit", "environment": null },
      "buckets": [ { "start": "2026-08-11", "count": 2, "failed": 0, "rollbacks": 0 } ],
      "summary": {
        "total": 14, "perWeek": 3.2,
        "medianIntervalHours": 41.5, "longestGapHours": 168.0,
        "lastDeployedAt": "2026-08-11T09:14:00Z",
        "changeFailureRate": 0.067,
        "previousPeriodTotal": 11,       // same-length window immediately before `from`
        "batchSizeP50": 1.0              // work items per counted deploy, zeros included
      }
    }
  ]
}
```

Buckets are pre-filled with zeros across the whole window, so charts need no gap handling.

## GET /work-items/matrix

Which stories are where — the story × environment checkmark matrix.

Query: `product` (**required**), `environment?` (keep only stories **not yet deployed** on that
env), `reachedEnv?` (the "shipped this period" filter — see below), `from?`, `to?`, `limit?`
(default 100, max 500), `offset?`.

**`reachedEnv` semantics.** A single env keeps only stories whose **first** successful deploy to
it falls inside the window. A comma-separated set (`reachedEnv=prod-eu,prod-us` — multi-region
production) uses **ANY semantics**: the story qualifies once it has landed on *at least one*
listed environment, dated by the FIRST landing among them. One production means real users have
it; which further regions ever receive the change is a business decision, not an obligation —
so a story that first reached any listed env before the window is not "shipped this period",
and the per-environment rollout state lives in the matrix cells.

Selection vs. state: the window selects **which stories appear** (any deploy or candidate
activity inside it, or a currently open candidate). The cells always show **full state**,
including deploys from before the window — a story deployed to dev three weeks ago and to test
yesterday shows both checkmarks.

Cell `state` is one of `deployed` | `approved-awaiting-deploy` | `awaiting-approval` | `absent`
(the two pending states map to the candidate's `PromotionStatus`; `Deploying` reads as
`approved-awaiting-deploy`). Deployed cells carry `version`, `at`, `deployEventId`; pending
cells carry `candidateId`. A deployed cell always wins over any candidate state.

`environments` is settings-ordered (the `Environments` list in app settings); keys the settings
don't know are appended at the end, never dropped. `furthestEnv` is the furthest environment in
that order with a successful deploy. An environment marked **retired** in settings is the one
exception: it is dropped from the matrix (column, cells, and the deploys and open candidates that
would select a story), from lead time, and from frequency `groupBy=environment` series — other
groupings still count its deploys, since they genuinely happened in the window.

**Which environments are "production"?** Resolution order (the tiles' ⓘ popovers state which
rule fired):

1. Environments **marked** `isProduction` in app settings (Settings → Environments, "Prod"
   column; several may be marked — multi-region). The executive strip aggregates over ALL marked
   environments present in scope: deploy counts sum, failure counts sum before dividing,
   "shipped" means landed on at least one of them, "in flight" means deployed to none.
2. Else, unconfigured keys whose **name** reads as production — `prod`, `production`, `prd`,
   `live`, alone or suffixed (`prod-eu`); `preprod` never matches. This is the **default
   mapping**: it holds until an admin adds the key to settings, which then always wins.
3. Else the historical convention: the last environment in order is the end of the pipeline.

The same default name mapping also orders unconfigured keys (dev-like < test-like <
staging-like < unrecognised < prod-like) so an unconfigured `prod` never sorts before `test`
alphabetically. Implemented in `EnvironmentStage` (API) and `lib/envStage.ts` (web) — change
them together. Environment keys in settings are **not** validated unique (a deliberate
choice — operators own their data): be aware that every lookup (display name, colour, order,
production flag) resolves by FIRST match, so with duplicate keys the first row wins everywhere.

Products that never reach a production environment (a dev/test-only pipeline) report on their
own last stage — the rules narrow the choice, they never force an environment a product doesn't
use.

```jsonc
{
  "environments": ["dev", "test", "prod"],
  "coverage": { "deployments": 51, "withoutWorkItem": 18, "ratio": 0.647 },
  "totals": { "dev": 31, "test": 20, "prod": 12 },   // of the selected stories, deployed per env
  "totalItems": 28,
  "items": [
    {
      "key": "MPT-14053",
      "title": "fix: handle duplicate JSON keys in Delta as 400",
      "url": "https://…/browse/MPT-14053",
      "furthestEnv": "test",
      "envs": {
        "dev":  { "state": "deployed", "version": "5.0.41", "at": "…", "deployEventId": "…" },
        "test": { "state": "deployed", "version": "5.0.41", "at": "…", "deployEventId": "…" },
        "prod": { "state": "awaiting-approval", "version": "5.0.41", "at": "…", "candidateId": "…" }
      },
      "lastActivityAt": "…"
    }
  ],
  "range": { "from": "…", "to": "…" }
}
```

## GET /promotions/queue

What is waiting right now, and how long the process took for candidates that closed in the window.

Query: `product?`, `from?`, `to?`.

- `edges[]` — current open candidates per `(product, targetEnv)`: `pending` (Pending),
  `awaitingDeploy` (Approved + Deploying), and the age in hours of the oldest of each.
- `approvalLatency` — `createdAt → approvedAt` for candidates **approved** inside the window
  (p50/p90 hours, n).
- `deployLatency` — `approvedAt → deployedAt` for candidates **deployed** inside the window.

The two latencies split "waiting for a human" from "waiting for the pipeline".

## GET /lead-time

Commit → environment, the DORA-style series. Requires producers to send `occurredAt` on
`pull-request` (preferred) or `commit` references — see `deployment-ingest-api.md`. Until they
do, the endpoint returns empty stats with `coverage.ratio: 0`; it never 404s.

Query: `product?`, `serviceName?`, `environment?`, `from?`, `to?`, `bucket?` (default `week`),
`tz?`.

Grain: **work item × environment**, clock stop at the **first successful deploy** of the ticket
to that environment (a later re-deploy doesn't move it). Clock start is the ticket's
`CommittedAt` (resolved at ingest from `pull-request.occurredAt`, fallback `commit.occurredAt`).
Environments are cumulative from commit — `prod` measures the full path, not the test→prod hop.
The window selects grains by when the first deploy landed.

```jsonc
{
  "definition": {
    "clockStart": "pull-request.occurredAt",
    "clockStartFallback": "commit.occurredAt",
    "clockStop": "deployEvent.deployedAt (first successful deploy per environment)",
    "grain": "workItem × environment, cumulative from commit"
  },
  "coverage": { "workItems": 120, "withClockStart": 94, "ratio": 0.783 },
  "byEnvironment": [
    { "environment": "dev",  "n": 94, "p50Hours": 4.1,  "p75Hours": 9.0,  "p90Hours": 26.5 },
    { "environment": "prod", "n": 38, "p50Hours": 41.2, "p75Hours": 96.0, "p90Hours": 210.5 }
  ],
  "buckets": [ { "start": "2026-08-03", "environment": "prod", "n": 12, "p50Hours": 38.0 } ],
  "slowest": [ { "workItemKey": "MPT-14053", "environment": "prod", "hours": 512.3, "deployEventId": "…" } ],
  "range": { "from": "…", "to": "…" }
}
```

**Never compare lead-time figures across periods with materially different coverage** — a
backfill that reaches deeper into one quarter than another manufactures a trend. The `coverage`
block exists so that check is always possible.

## GET /delivery

The Delivery metrics report — DORA's four plus the flow, quality, cadence and data-quality numbers
that make them readable — over a product selection pooled as **one population**.

Query: `products?` (comma-separated; empty = every product; at most 100), `from?`, `to?`,
`bucket?` (`day`|`week`|`month`, default `week` — the trend grain), `tz?`.

Three rules that differ from the endpoints above:

- **Products are a filter, never a grouping.** Counts sum across the selection; percentiles run over
  the pooled work items. Per-product medians are not averaged into a portfolio median — they cannot
  be. The per-product table carries the split.
- **Production is resolved on the server** and echoed in `production`, so every number in one
  response measures the same environments: environments **marked** `isProduction` in settings; else
  unconfigured keys whose **name** reads as production (`prod`, `production`, `prd`, `live`, alone or
  suffixed; `preprod` never); else the **last environment in order**. `production.source` says which
  rule fired (`marked` | `default-name` | `convention`), `production.explanation` says it in words.
  Retired environments are left out of everything here.
- **A story is shipped once per product**, when it first lands successfully on **any** production
  environment (ANY semantics, like the matrix's `reachedEnv`), dated by that first landing. The lead
  time clock stops there. The same ticket key under two products is two stories — the grain carries
  the product.

### Envelope

Every metric has the same shape, so a reader (or the page) treats them alike:

```jsonc
{
  "asOf": "2026-10-26T12:00:00Z",             // computed when — numbers are live, a backfill moves them
  "range": { "from": "…", "to": "…" },
  "previousRange": { "from": "…", "to": "…" }, // the equal-length window before, for deltas
  "products": ["mpt", "extra"],                // the selection, or every product seen
  "production": { "environments": ["prod-eu", "prod-us"], "source": "marked",
                  "explanation": "Marked as production stage in Settings → Environments: prod-eu, prod-us." },
  "definition": { "bucket": "week", "tz": "UTC", "deploy": "…", "production": "…", "storyShipped": "…", "leadTime": "…" },
  "sections": [
    { "key": "dora", "title": "DORA", "metrics": [
      { "key": "deploymentFrequency", "title": "Deployment frequency",
        "available": true, "reason": null,
        "unit": "count",                       // count | hours | ratio
        "value": 42, "n": 42,
        "percentiles": null,                   // { p50, p75, p90 } for duration metrics
        "delta": { "previous": 37, "change": 5, "previousN": 37 },
        "trend": [ { "start": "2026-08-03", "value": 9, "n": 9 } ],
        "coverage": null,                      // { label, numerator, denominator, ratio } where a number depends on ticket data
        "definition": "Successful, non-rollback deploys to prod-eu, prod-us in the window …",
        "goodWhen": "up" },                    // up | down | none — which movement reads as improvement
      { "key": "changeFailureRate", "available": false,
        "reason": "Needs production incidents linked to the deployments that caused them. …", "value": null, "n": 0, … }
    ] },
    { "key": "flow", … }, { "key": "quality", … }, { "key": "cadence", … }, { "key": "dataQuality", … }
  ],
  "leadTimeByEnvironment": [ { "environment": "test", "n": 30, "p50Hours": 9.0, "p75Hours": 20.1, "p90Hours": 41.0 } ],
  "byProduct": [ { "product": "mpt", "services": 8, "deploys": 30, "previousDeploys": 25, "storiesShipped": 18,
                   "leadTimeP50Hours": 41.2, "leadTimeCoverage": 0.78, "deployFailureRate": 0.06,
                   "rollbackRate": 0.03, "storyCoverage": 0.65, "staleServices": 1 } ],
  "byService": [ { "product": "mpt", "service": "orders", "deploys": 9, "previousDeploys": 7, "perWeek": 4.5,
                   "lastDeployedAt": "…", "medianIntervalHours": 31.0, "failed": 1, "rollbacks": 0,
                   "batchSizeP50": 2.0, "leadTimeP50Hours": 38.0 } ]
}
```

Every metric also carries `previousTrend` — the same series over the equal-length window before,
**aligned by position** (bucket *i* of the previous window sits under bucket *i* of this one), which is
what the report draws as the dashed ghost line. Trend points carry `coverage` where a bucket can be
thin in a way the value hides (lead time: stories with a commit time ÷ shipped; change failure rate:
incidents tied to a deploy ÷ started); the report draws such points hollow below 50%. Each
`byProduct` row carries `trends` — the DORA four over that product alone, per bucket of the current
window — for the per-product overlay.

`available: false` means the installation cannot measure the metric at all — `reason` says what data
would make it measurable. An available metric with nothing in the window has `n: 0` and a null
`value`: no data, not "not a feature".

### Metrics

| Section | Key | Value | Notes |
|---|---|---|---|
| dora | `deploymentFrequency` | successful, non-rollback deploys to production | redeploys of the running version skipped; trend per bucket |
| dora | `leadTime` | p50 hours commit → first production deploy per story | `percentiles` p50/p75/p90; `coverage` = stories with a recorded commit time; needs `occurredAt` on `pull-request`/`commit` references |
| dora | `changeFailureRate` | deploys to production with ≥1 attributed incident ÷ successful deploys to production | the DORA definition; `coverage` = incidents tied to a deploy ÷ incidents started in the window; value is null until at least one incident is recorded for the selection |
| dora | `timeToRestore` | p50 hours `resolvedAt − startedAt` for production incidents **restored** in the window | open incidents wait until they close (`coverage` says how many are open); p90 in `percentiles` |
| flow | `approvalLatency` | p50 hours created → approved, candidates approved in the window | waiting on a human; p90 in `percentiles` |
| flow | `deployLatency` | p50 hours approved → deployed, candidates deployed in the window | waiting on the pipeline |
| flow | `storiesShipped` | stories whose first production landing fell in the window | `coverage` = deploys carrying a story |
| quality | `deployFailureRate` | `(failed + rollbacks) / (succeeded + failed)` on production | attempt-based — **not** DORA's change failure rate; the same figure the frequency endpoint calls `changeFailureRate` |
| quality | `rollbackRate` | `rollbacks / succeeded` on production | |
| cadence | `staleServices` | services with history but no successful deploy in the window, any environment | `n` = services known |
| cadence | `batchSize` | p50 work items per successful production deploy, zeros included | below 1 is a coverage smell |
| dataQuality | `storyCoverage` | deploys (any environment) carrying ≥1 work-item reference ÷ all deploys | the credibility of every per-story number |
| dataQuality | `leadTimeCoverage` | stories shipped with a commit time ÷ stories shipped | never compare lead time across periods where this differs materially |
| dataQuality | `incidentAttribution` | incidents that name the deploy or version that caused them ÷ incidents started in the window | the definition lists explicit / inferred / outside-window / unattributed counts |

`leadTimeByEnvironment` is cumulative from commit per environment (first landing per story and
environment), settings-ordered — it shows **where** the time goes (`prod` minus `test` ≈ approval
plus deploy wait). `byService` lists every service known under the selection, stale ones included
with their true last deploy, ordered by deploys.

### Incidents → change failure rate and time to restore

Incidents (`notes/incidents.md`) are tied to deploys **at query time**, never stored, so a late deploy
event or a backfill cannot leave a stale link. Only incidents on the resolved production set count.
Per incident, in order:

1. **explicit** — the incident names the deploy (`causedByDeployEventId`). Always counts, however
   old the deploy.
2. **version** — the first successful deploy of the version it names, at or before the start (plus
   the rollout tolerance).
3. **inferred** — the deploy that was live when it started: the latest successful, non-rollback
   deploy at or before the start plus the rollout tolerance. A deploy *after* the start is a
   candidate remediation, never a cause; a rollback is never a cause.
4. **none** — no prior deploy. Counts for time to restore, never against a deploy.

Two admin settings (Settings → Delivery Metrics, stored with the app settings as `analytics`)
shape 2 and 3 and are echoed in the metric definitions:

- `attributionWindowDays` (default 7, `0` = no cap): a version or inferred match older than this is
  reported as **outside-window** and does not count as a change failure.
- `rolloutToleranceMinutes` (default 15): a deploy's time is usually the pipeline's finish; an
  incident that starts this many minutes earlier is blamed on it rather than on the previous version.

Change failure rate counts a deploy **once** however many incidents it caused; time to restore
counts every incident that closed in the window. The per-product rows carry `incidents`,
`changeFailureRate` and `timeToRestoreP50Hours`; the per-service rows carry `incidents`.

**Admin drill-down.** For callers with the admin role the response also carries `incidents`: every
production incident started or restored in the window with its `attribution`, `deployEventId`,
`deployedAt`, `version`, `durationHours` and `url`. For everyone else it is `null` — the aggregates
are for all, the records are not.

**Scale.** Events are filtered to the two windows in SQL; work-item grains are aggregated in SQL to
their first deploy per (product, service, work item, environment) with the window applied as a
`HAVING`, so the lead-time path does not load a product's history. Percentile math runs in memory
over the window's rows.

