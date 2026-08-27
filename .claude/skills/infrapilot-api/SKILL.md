---
name: infrapilot-api
description: Connect to the InfraPilot (InfraPortal) REST API — read deployment state/history, ingest deploy events, create/query promotions, register builds, pull analytics and release notes. Use whenever a task mentions InfraPilot, InfraPortal, deployment tracking, deploy events, promotions, or the deployments API. Connection comes from the DEPLOYMENTS_URL and DEPLOYMENTS_API_KEY environment variables.
---

# InfraPilot API

InfraPilot (also branded **InfraPortal**) is an infrastructure portal tracking deployments,
promotions between environments, builds, and release notes. This skill covers calling its REST
API directly.

## Connection

Two environment variables define the connection — always use them, never hardcode a host or key:

| Variable | Meaning |
|---|---|
| `DEPLOYMENTS_URL` | Base URL of the InfraPilot instance (origin, e.g. `https://infrapilot.example.com`). All endpoints live under `{DEPLOYMENTS_URL}/api/...`. Strip any trailing slash. |
| `DEPLOYMENTS_API_KEY` | API key sent as the `X-Api-Key` header on every request. |

Connectivity check (no auth required):

```bash
curl -s "$DEPLOYMENTS_URL/health"
```

Authenticated request template — bash:

```bash
curl -s "$DEPLOYMENTS_URL/api/deployments/products" -H "X-Api-Key: $DEPLOYMENTS_API_KEY"
```

PowerShell:

```powershell
Invoke-RestMethod "$env:DEPLOYMENTS_URL/api/deployments/products" -Headers @{ 'X-Api-Key' = $env:DEPLOYMENTS_API_KEY }
```

If either variable is missing, stop and ask the user for it — do not guess a URL or key.
Never print the key value in output; refer to it only as `$DEPLOYMENTS_API_KEY`.

## Auth model and limits

- The API key authenticates **both writes and all standard reads** (deployments, promotions,
  builds, work-items, analytics, release notes). No bearer token needed.
- Keys may be **product-scoped**: posting or creating for a product outside the key's allowed
  list returns `403 Forbidden`.
- Keys may carry **scopes**: a key with a `Scopes` list needs `build:register` for
  `POST /api/builds` and `promotion:create` for `POST /api/promotions`. Keys without a scopes
  list are unrestricted.
- **Rate limit**: 120 requests/minute per key (sliding window) → `429` when exceeded. Back off
  and retry; don't hammer.
- **Admin endpoints** (`/api/deployments/admin/*`, `/api/promotions/admin/*`, `/api/audit`,
  catalog admin) require the `InfraPortal.Admin` role — a plain API key normally cannot call
  them.
- Approve/reject endpoints authenticate with the key, but approval *eligibility* is evaluated
  against human users/groups in the promotion policy — treat approvals as human actions, not
  something to automate with the key.

## Reading deployment data

All under `{DEPLOYMENTS_URL}/api/deployments`, header `X-Api-Key` required:

| Endpoint | Returns |
|---|---|
| `GET /products` | Product summaries (the portal's landing overview). |
| `GET /state?product=&environment=&serviceName=` | Current version matrix — what is deployed where right now. All params optional filters. |
| `GET /services/search?q=&limit=` | Cross-product service search (case-insensitive substring; `q` required). |
| `GET /services/{product}/{serviceName}?versionsLimit=` | Service detail: state per environment, recent distinct versions, promotions. |
| `GET /history/{product}/{serviceName}?environment=&limit=` | Deployment history for one service (default limit 50). |
| `GET /recent/{product}?since=&limit=` | Recent deploys across environments (`since` ISO-8601, defaults to today UTC). |
| `GET /recent/{product}/{environment}?since=` | Recent deploys for one environment. |
| `GET /versions?product=&environment=&serviceName=&limit=` | Versions ever deployed to an environment (`product` + `environment` required). |
| `GET /events/{id}?historyLimit=` | One deploy event's full detail. |

Environment and role strings are canonicalised to kebab-case server-side (`Production` →
`production`), so filters match regardless of casing.

## Ingesting a deploy event

`POST /api/deployments/events` — how pipelines (or scripts) record a deployment.

Required fields: `product`, `service`, `environment`, `version`, `source`, `deployedAt`
(ISO-8601 UTC). Optional: `status` (`succeeded` default | `failed` | `in_progress`),
`isRollback` (bool), `references[]`, `participants[]`, `metadata{}`.

```bash
curl -s -X POST "$DEPLOYMENTS_URL/api/deployments/events" \
  -H "X-Api-Key: $DEPLOYMENTS_API_KEY" \
  -H "Content-Type: application/json" \
  -d '{
    "product": "marketplace",
    "service": "search-api",
    "environment": "staging",
    "version": "1.0.42",
    "source": "github-actions",
    "deployedAt": "2026-08-19T14:00:00Z"
  }'
```

Responses: `201` `{ id, version, previousVersion }` on create; `200` with `replayed: true` when
the same natural key is re-posted (safe to retry). `previousVersion` is derived server-side —
don't compute it yourself unless asserting drift.

Reference `type` vocabulary the UI/analytics understand: `repository` (add `url` + `revision`
for commit deep-links), `pipeline`, `build`, `pull-request`, `commit`, `branch`, `work-item`.
References may carry their own `participants[]` (a PR's author/reviewer, a ticket's QA) and
`occurredAt` (feeds lead-time analytics: PR merge time preferred, commit committer-date
fallback). Canonical participant roles: `triggered-by`, `author`, `reviewer`, `qa`.

An `in_progress` event can be finalised later by re-posting the same version with a final
status. Rollbacks: post the old version with `isRollback: true`.

`POST /api/deployments/manual` records a manual (non-CI) deploy — requires a `note`, and the
server stamps `source: "manual"` with the key as the actor.

## Promotions

A promotion candidate = "service X version V moves sourceEnv → targetEnv" under an approval
gate. Lifecycle: `Pending → Approved → Deploying → Deployed`, with `Rejected` / `Superseded`
off-ramps. A succeeded deploy of the version on the target env auto-closes the candidate.

- `POST /api/promotions` — create (API-key auth). Required: `product`, `service`, `sourceEnv`,
  `targetEnv`, `version`. Optional: `fromRevision`, `toRevision`, `references[]` (only
  `work-item` references feed the approval gate), `participants[]`.
  `422` means: no policy for that edge, OR no succeeded deploy of `version` in `sourceEnv`, OR
  the target is already at `version`. Idempotent on `(product, service, sourceEnv, targetEnv,
  version)`; a newer version on the same edge supersedes the pending older candidate.
- `GET /api/promotions?status=&product=&service=&targetEnv=&reference=` — list.
- `GET /api/promotions/{id}` — detail incl. `approvalProgress` and comments.
  Candidates carry both `fromVersion` (what the target ran when the promotion was created — the
  "from" side of the change, frozen) and `targetCurrentVersion` (what it runs now). They differ
  once the promotion lands, so read history off `fromVersion`.
- `POST /api/promotions/{id}/approve` / `/reject` — human actions (body `{ "comment"?: ... }`).
- Work-item sign-off: `POST /api/work-items/{key}/approvals` | `/issues` | `/blocks` with body
  `{ product, targetEnv, comment? }`.

## Build registry

- `POST /api/builds` — register a published build. Required: `product`, `service`, `version`,
  `branch` (full git ref). Optional: `commitSha`, `buildId`, `buildUrl`, inline `manifest`,
  `artifactRef`, `artifactDigest`. Idempotent on `(product, service, version)` — replay returns
  `200` with `replayed: true`.
- `GET /api/builds?product=&service=&branch=&limit=` — newest first, `branch` substring match.
- `GET /api/builds/{id}` — one build incl. manifest.
- `POST /api/promotions/from-build` `{ buildId, targetEnv }` — promote a registered build
  (targets discoverable via `GET /api/promotions/build-targets?product=&service=`).

## Analytics (read-only)

Under `/api/analytics`. Common params: `from`/`to` (ISO-8601, half-open window, default last
14 days), `tz` (IANA id, default UTC), `bucket` (`day`|`week`).

| Endpoint | Answers |
|---|---|
| `GET /deployments/frequency?product=&serviceName=&environment=&groupBy=&summaryOnly=` | Deploy cadence, failure/rollback counts, change-failure rate per series. |
| `GET /work-items/matrix?product=&environment=&reachedEnv=&limit=` | Story × environment rollout matrix (`product` required). |
| `GET /promotions/queue?product=` | Open candidates per edge + approval/deploy latency percentiles. |
| `GET /lead-time?product=&environment=` | DORA-style commit→env lead time (percentiles, needs `occurredAt` coverage). |

Durations are percentiles (p50/p75/p90), never averages. Every response echoes a `definition`
block and a `coverage` block — quote them when reporting numbers, and never compare figures
across windows with materially different coverage.

## Release notes

Feature-flagged (off by default). `GET /api/release-notes/preview?product=&environment=&from=&to=`
renders a draft; `POST /api/release-notes/generate` persists it and fires the
`release_note.generated` webhook; `GET /api/release-notes` / `/{id}` list and fetch.

## Error handling

| Status | Meaning | What to do |
|---|---|---|
| `400` | Validation — body has `{ "errors": [...] }` | Fix the payload; the messages are specific. |
| `401` | Missing/invalid `X-Api-Key` | Check `DEPLOYMENTS_API_KEY` is set and not revoked. |
| `403` | Key not scoped for this product, or missing scope | Report which product/scope the key lacks; don't retry. |
| `404` | Resource not found | Verify ids/product/service spelling (kebab-case). |
| `422` | Promotion precondition failed — body `{ "error": "..." }` | Explains which precondition; surface it verbatim. |
| `429` | Per-key rate limit (120/min) | Back off and retry after a pause. |

## Deeper reference

Full payload shapes and semantics live in the InfraPilot repo under `notes/`:
`deployment-ingest-api.md`, `promotions-api.md`, `build-registry-api.md`, `analytics-api.md`,
`release-notes.md`. Consult them when a field's exact behaviour matters.
