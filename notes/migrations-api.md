# Migrations API

Database migrations of deployed versions, read-only in the portal. Two kinds:

| Kind | When it runs | How it reaches the portal |
|---|---|---|
| `schema` | Inside the deployment, as the Helm `pre-install,pre-upgrade` hook job `{chart}-migrations` (`migrate --schema`). The new pods never start on a schema they don't know, so a failed schema migration is a failed deploy. | **Read from the deploy event's own logs** — nothing extra to send. See [Schema migrations from deploy logs](#schema-migrations-from-deploy-logs). A producer may also report one explicitly. |
| `data` | After the deployment, as a separate job (`migrate --data`), once the APIs it depends on are out. mpt-release queues it and runs it from the **Data Migration** workflow; marketplace SDP runs it in the `execute_data_migrations` job. | **Reported** through `POST /api/migrations/runs`: `pending` when the deploy queues it, `running` when the job starts, `succeeded` / `failed` / `skipped` when it ends. |

Like promotions, **a newer data migration supersedes an older one**: only the newest version's data
migration of a (product, service, environment) is ever run, so when one is reported every older
`pending` or `failed` one there becomes `superseded`, pointing at it. Schema migrations never supersede
— each belongs to the deployment that ran it.

The individual migrations inside a run — which applied, which failed — are read from the output the
MPT migration library prints, unless the producer lists them itself.

## Report a migration — `POST /api/migrations/runs`

API key (`X-Api-Key`), same rate limit and product scope as deploy ingest. Upserts on
`(kind, product, service, environment, version, runKey)`, so post the same body again as the
migration moves on.

```jsonc
{
  // ── Required ───────────────────────────────────────────────
  "kind": "data",                        // schema | data
  "product": "mpt-extensions",
  "service": "mpt-extensions",
  "environment": "staging",              // canonicalised and alias-resolved like deploy ingest
  "version": "5.10.15-gc4fc6322",
  "status": "succeeded",                 // pending | running | succeeded | failed | skipped
  "source": "mpt-release",

  // ── Optional ───────────────────────────────────────────────
  "runKey": null,                        // see "Identity" below; max 100 chars; "deploy:" is reserved
  "deployEventId": null,                 // when the producer has it; otherwise the server links it
  "queuedAt": "2026-11-02T09:18:06Z",    // when it became due (the deploy finishing)
  "startedAt": "2026-11-02T09:52:24Z",   // a new startedAt is a new attempt
  "completedAt": "2026-11-02T09:53:43Z",
  "skipReason": null,                    // with status skipped: why nothing ran
  "run": {                               // same shape as a deploy event's `run`
    "provider": "github-actions", "runId": "41424111715", "runNumber": "3",
    "workflowName": "Data Migration", "jobName": "Migrate mpt-extensions",
    "runUrl": "https://github.com/…/actions/runs/41424111715",
    "jobUrl": "https://github.com/…/actions/runs/41424111715/job/125206614727",
    "failureReason": null                // the one line that explains a failure
  },
  "migrations": null,                    // optional explicit list, see below
  "logs": [                              // same shape and cap as deploy event logs
    { "name": "data migration job output", "source": "kubectl", "content": "…pod log…" }
  ]
}
```

Response: `201 Created` for a new migration, `200 OK` for an update —
`{ id, status, deployEventId, supersededById, appliedCount, failedCount, totalCount }`. `status` is
what the portal stored, which can differ from what was sent (see *Status rules*). `400` lists every
validation error.

### Identity

- **Data migrations:** omit `runKey`. One row per version per environment; a retry after a failure
  updates the same row (`attempts` counts the starts).
- **Schema migrations:** one per deployment. Read from deploy logs they get `runKey = deploy:{eventId}`.
  A producer reporting them itself sends its own `runKey` (e.g. the deploying run's id) so a redeploy of
  the same version is a separate row.

### Status rules

- `pending` only opens a migration: re-sending it never reopens one that ran.
- A `superseded` migration stays superseded unless it is actually run again (`running`/`succeeded`).
- `superseded` itself is the server's call and cannot be sent.
- "Older" for superseding means an earlier `queuedAt`, not a lower version string. A report that
  arrives out of order (a backfill) is superseded on arrival when a newer one already exists.

### Linking to the deployment

`deployEventId` when given. Otherwise the deploy of the same product/service/environment/version that
started no later than the migration did (within an hour), latest first, succeeded only for data
migrations. A migration reported before its deploy event exists is linked when the event arrives.

### `migrations[]` — explicit list

`{ name, status, durationMs?, error?, order?, appliedAt? }`, `status` one of `applied`, `failed`,
`rolled-back`, `running`, `pending` (never started), `skipped`. When omitted the server reads them from
`logs`. Sending it replaces the stored list.

## What the server reads from logs

The MPT migration library (`Mpt.Framework.Cli.Migrations`) prints a status table to stdout and
re-prints it after each migration; the last row per migration is its outcome:

```
[✓] 4640 AddOrderIndex      1m 2026-11-02T09:14:03     1s
[x] 4650 AlterAccountName   1m 2026-11-02T09:14:04     0s Invalid column name 'DisplayName'.
[<] 4630 AddAuditTable      1m 2026-11-02T09:14:04
[.] 4660 AddOrderTotals     1m                   -
The following migrations have failed:
- AlterAccountName: Invalid column name 'DisplayName'.
```

`✓` applied, `x` failed, `<` rolled back (its transactional batch failed), `r` running, `.` not run.
Also understood: `No migrations to run.`, `Schema/Data migrations completed.`, Serilog console error
lines (`[09:58:09 ERR] …` — the only trace of a run that failed before its table, e.g. a bad connection
string), EF Core's `Applying migration '…'`. The library never re-runs an applied migration and doesn't
mention it again, so a run with nothing pending names no migrations.

### Schema migrations from deploy logs

On every deploy event ingest (new or replayed) the server looks in the event's log blocks for:

| Block | What is taken |
|---|---|
| `helm hook logs` (mpt-release) | Each `=====Logs from hook pod/X (job Y-migrations)=====` … `=====End logs…=====` section whose job name contains `migration`. |
| `failure diagnostics` (both pipelines) | Each `--- Logs for pod: X ---` / `--- Logs for job pod: X ---` section whose pod name contains `migration`, up to the next `---`/`===` header — never the `kubectl describe` output around it, which prints container environments. |
| any block named `schema migration…` | Its hook pod sections (same banners as `helm hook logs`), or the whole block when it has none. The way for a producer to send a successful schema migration's output on purpose — marketplace SDP does. |
| any block | Helm's `* job X-migrations failed: …` line. |

Data migration jobs (`…data-migration…`) are never taken as schema migrations. A deploy that
succeeded had a schema migration that succeeded (Helm only proceeds once its pre-hooks do); a failed
deploy is a failed schema migration when the job is reported failed, the output shows it, or the
deploy's `run.failureReason` says a hook failed.

## Reading

All under the `CanApprove` policy, like `/api/deployments`.

| Endpoint | What |
|---|---|
| `GET /api/migrations/runs` | List, newest due first. Filters: `product`, `service`, `environment` (alias-resolved), `kind`, `status` (`attention` = pending/running/failed, `all`, or a comma list), `version`, `deployEventId`, `from`/`to` (on `queuedAt`), `limit` (≤500), `offset`. Returns `{ items, total }`. |
| `GET /api/migrations/runs/{id}` | One migration: `run`, `ciRun`, `migrations[]`, `logs` (summaries), `deployment`, `supersededBy`, `supersedes[]`, `history[]` (same kind, service, environment). |
| `GET /api/migrations/runs/{id}/logs/{logId}` | One log block's text. |
| `GET /api/migrations/flags` | Pending, running and failed migrations (`product`, `service`, `environment`) — what the deployment views flag. |
| `GET /api/migrations/search?q=` | "Has migration X run there?" Partial, case-insensitive, word-order- and typo-tolerant match over every migration name on record, or the script's order number when `q` is a number. Per matching name, one hit per (product, service, environment): `applied` when any run there applied it, else the latest run's word for it. Filters `product`, `environment`, `kind`; `limit` names (≤100). |
| `GET /api/deployments/events/{id}` | Now also carries `migrations[]`: the deployment's schema and data migrations. |

## Admin — `POST /api/migrations/admin/derive?since=&product=`

`CatalogAdmin`. Re-reads stored deploy logs for schema migrations (default: the last 7 days). Run once
after this ships so earlier deployments show their migrations; idempotent. Returns
`{ examined, created, updated, unchanged }`.

## Realtime

Every change publishes `entityChanged` for `migration` (with id, product, service, environment) and
`deployment` (with the linked deploy event id), so open pages refresh.
