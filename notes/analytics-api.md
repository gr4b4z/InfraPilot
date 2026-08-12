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
(`day`|`week`, weeks start Monday).

---

## GET /deployments/frequency

How often things change, per series.

Query: `product?`, `serviceName?`, `environment?`, `from?`, `to?`, `bucket?` (default `day`),
`groupBy?` (`none`|`service`|`environment`|`product`, default `none`), `tz?`,
`includeRollbacks?` (default false), `includeRedeploys?` (default false; a redeploy is
`version == previousVersion`).

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
env), `reachedEnv?` (keep only stories whose **first** successful deploy to that env falls inside
the window — the "shipped this period" list), `from?`, `to?`, `limit?` (default 100, max 500),
`offset?`.

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
that order with a successful deploy.

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
