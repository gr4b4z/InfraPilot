# Incidents

Production incidents, as far as delivery metrics need them. The portal keeps a **pointer**, not the
incident: where (product, service, environment), when it started, when service was restored, which
deployment caused it when known, and a link to the record in your own incident tooling. Title,
timeline, severity narrative, responders and postmortem stay where they are. A field is stored only
if a calculation or the correlation with deploys reads it.

Incidents feed two DORA metrics on the Delivery metrics report (`notes/analytics-api.md`,
`/delivery`): **change failure rate** (deploys to production with an attributed incident ÷ deploys
to production) and **time to restore service** (`resolvedAt − startedAt`).

## Access

Records are **admin-only**, by decision: the aggregates they feed are visible to everyone with
analytics access; the records themselves (which outage, which service) are not. All endpoints live
under `/api/incidents` with the CatalogAdmin policy — a signed-in admin, or an Admin personal token.
A pipeline API key is a producer, not an admin, and is refused. Machine ingest from alerting tools is
a later, separately gated route; the `(source, externalId)` key is already in place for it.

In the portal: **System → Incidents** (list, record, edit, delete) and **Record incident** on a
deployment's page, which pre-fills product, service, environment, version and the deploy event —
the strongest attribution there is.

## Record

```jsonc
{
  "id": "…",
  "product": "mpt", "service": "orders", "environment": "production",
  "startedAt": "2026-10-20T09:12:00Z",     // detection / alert time
  "resolvedAt": "2026-10-20T10:03:00Z",    // null while open
  "durationHours": 0.85,                    // derived
  "version": "7.4.0",                       // the version that was running, when known
  "causedByDeployEventId": "…",            // the deploy that caused it, when picked
  "source": "pagerduty",                    // "manual" for incidents recorded in the portal
  "externalId": "P123",                     // with source: the identity of the real incident
  "url": "https://…",
  "createdBy": "…", "createdByName": "…", "createdAt": "…", "updatedBy": null, "updatedAt": "…"
}
```

## Endpoints

| Method | Path | Notes |
|---|---|---|
| `GET` | `/api/incidents` | `product?`, `environment?` (alias-resolved), `open?` (true = unresolved only), `from?`/`to?` on `startedAt`, `limit?` (default 100, max 500), `offset?`. Newest first; answers `{ items, total }`. |
| `GET` | `/api/incidents/{id}` | |
| `POST` | `/api/incidents` | Body: `product`, `service`, `environment`, `startedAt` (required); `resolvedAt?`, `version?`, `causedByDeployEventId?`, `source?` (default `manual`, lower-cased), `externalId?`, `url?` (absolute http(s)). `201` with the record. |
| `PUT` | `/api/incidents/{id}` | Same body; replaces the record. |
| `DELETE` | `/api/incidents/{id}` | `204`. |

Validation, every write path alike:

- `resolvedAt` must not be before `startedAt`.
- `environment` resolves through the configured aliases, like deploy ingest — `prod` lands on
  `production` when that alias is set up.
- `causedByDeployEventId` must name a known deploy event of the **same** product, service and
  environment; it fills in `version` when the body left it blank. `400` otherwise.
- `(source, externalId)`, when `externalId` is given, is unique: a second record for the same
  external incident is `409` with `existingId`. Uniqueness is enforced by the service, not an index
  — the providers disagree on how a unique index treats NULL external ids.

Every write leaves an audit entry (`incidents` module: `incident.recorded`, `incident.updated`,
`incident.deleted`).

## How an incident finds its deploy

Attribution is computed **at query time** by the delivery report, never stored, so a late deploy
event or a backfill cannot leave a stale link behind. In order:

1. `causedByDeployEventId` — explicit.
2. `version` — the first successful deploy of that version for the service and environment at or
   before `startedAt` (explicit).
3. Else the deploy that was **live** when the incident started: the most recent successful,
   non-rollback deploy of the service in the environment at or before `startedAt`, within the
   rollout tolerance (inferred).
4. Else unattributed: it still counts for time to restore, never against a deploy.

A deploy after `startedAt` is a candidate remediation, never a cause. See the delivery notes for the
attribution window and rollout tolerance settings, and for what the report echoes about each rule.
