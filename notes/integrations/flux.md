# Flux Integration

Endpoint for Flux's notification-controller to report the outcome of a Helm install. Each event
becomes a deploy event, as if a pipeline had posted it to `POST /api/deployments/events`.

```
POST /api/integrations/flux/{client}/events
```

Flux reports what it installed but knows nothing about the change: no commits, pull requests or work
items. The endpoint takes them from the **promotion candidate the deploy delivers** — the one with
the same product, service, version and target environment that is not yet `Deployed` or
`Superseded`. That is the change set people reviewed and signed off when the promotion was created.
A rollback has no candidate; its change set comes from the deploy history (see below). Everything
after that is ordinary ingest: `deployment.created`, the promotion hook (a `succeeded` event closes
the candidate), the rollback hook, analytics and release notes.

The route exists only when `Integrations:Flux:Enabled` is `true` (read at startup). Otherwise it is
`404`.

## Authentication

Flux signs the request with its **`generic-hmac`** provider type:

```
X-Signature: sha256=<hex>
```

`<hex>` is the HMAC of the raw request body, keyed with the `token` of the provider's secret.
`sha256`, `sha384` and `sha512` are accepted; Flux sends `sha256`.

Each sending cluster is a **client**, named in the URL, with its own secret:

```jsonc
// appsettings — the secret comes from the secret store, never from the repository
"Integrations": {
  "Flux": {
    "Enabled": true,
    "Clients": [
      { "Name": "staging-weu-1", "Secret": "<shared secret>", "AllowedProducts": ["checkout"] }
    ]
  }
}
```

| Field | Meaning |
|---|---|
| `Name` | The `{client}` path segment, usually the cluster name. Case-insensitive. |
| `Secret` | The same value as the `token` key of the Flux provider secret. Stored in clear: an HMAC can only be checked with the key. |
| `AllowedProducts` | Products this client may report for. Empty means all, as for API keys. Checked against the product the event names, before any service→product override. |
| `Revoked` | `true` stops the client without removing it. |

A client secret works only for this endpoint; an API key or a signed-in session cannot call it.
Requests are rate-limited like deploy ingest, per client. The body is read for verification up to
1 MiB; larger requests are rejected.

## What Flux sends

The notification-controller's event, as posted by a `generic-hmac` provider. The endpoint reads:

```jsonc
{
  "involvedObject": { "kind": "HelmRelease", "namespace": "checkout", "name": "checkout-api" },
  "timestamp": "2026-11-18T12:20:00Z",
  "message": "Helm upgrade succeeded for release checkout/checkout-api.v12 with chart checkout-api@1.3.0",
  "reason": "UpgradeSucceeded",
  "reportingController": "helm-controller",
  "metadata": {
    "revision": "1.3.0",          // chart version (Flux)
    "environment": "staging",     // Alert eventMetadata
    "cluster": "staging-weu-1",   // Alert eventMetadata
    "product": "checkout",        // HelmRelease annotation event.toolkit.fluxcd.io/product
    "service": "checkout-api",    // HelmRelease annotation event.toolkit.fluxcd.io/service
    "app-version": "1.3.0"        // optional: HelmRelease annotation event.toolkit.fluxcd.io/app-version
  }
}
```

## Mapping

| Deploy event | From |
|---|---|
| `product` | `metadata.product` — required |
| `service` | `metadata.service`, else `involvedObject.name` |
| `environment` | `metadata.environment` — required; resolved through environment aliases like any ingest |
| `version` | `metadata.app-version`, else `metadata.revision` |
| `status` | `InstallSucceeded`, `UpgradeSucceeded` → `succeeded`; `InstallFailed`, `UpgradeFailed`, `RollbackFailed`, `UninstallFailed` → `failed`; `UninstallSucceeded` → `removed` |
| `source` | `helm-deploy` |
| `deployedAt` | `timestamp`, so a retry of the same event is a replay |
| `run` | `provider: flux`, `workflowName: "Flux <cluster>"`, `jobName: "HelmRelease/<namespace>/<name>"`, `completedAt`, and `failureReason` (the message) on failure |
| `logs` | one block named `flux event` with the message |
| `references`, `participants` | copied from the matched promotion candidate; for a rollback, the references of the versions it undoes (no participants); otherwise empty |
| `isRollback` | `true` when the event is a rollback (below) |
| `metadata` | `flux` (kind, namespace, name, reason, revision, cluster, client), plus `promotionId` when a candidate matched or `rolledBackVersions` for a rollback |

Only `HelmRelease` events with a reason from the table are recorded. Everything else (progress,
dependency waits, test results, other kinds) is answered `202` and dropped, so Flux does not retry.

### Which candidate

The match is the one the promotion hook would close: same resolved product, service, target
environment and version, status not `Deployed` or `Superseded`. When several match (the version is
promoted into the environment from more than one source), the one furthest along wins —
`Deploying`, then `Approved`, `Pending`, `Rejected` — then the newest. A `failed` event copies the
references too, and the candidate stays open for the next attempt.

### Rollbacks

With no candidate, the endpoint checks whether the version is going **back**: it was deployed
successfully to this environment before, and other versions were deployed after it. Those later
versions are the ones being undone, and the event takes their references (deduplicated by type and
key) and is stored with `isRollback: true` and `metadata.rolledBackVersions`. The references are the
change the rollback removes. Only succeeded forward deploys count: a failed attempt changed nothing,
and an earlier rollback's references already describe a removal.

A redeploy of the version that is already current is not a rollback and gets no references. Neither
does a version committed to the GitOps repository outside a promotion that this environment never
ran, as for any out-of-band deploy.

## Responses

| Status | When | Body |
|---|---|---|
| `201 Created` | event ingested | `{ "id", "version", "previousVersion", "status", "isRollback", "promotionId", "rolledBackVersions", "replayed": false }` |
| `200 OK` | the same event again (same natural key) | same, `"replayed": true` |
| `202 Accepted` | not an install outcome | `{ "ignored": true, "reason": "..." }` |
| `400 Bad Request` | product, environment, version or timestamp missing | `{ "errors": [ ... ] }` |
| `401 Unauthorized` | unknown or revoked client, missing or wrong signature, body over 1 MiB | — |
| `403 Forbidden` | product outside the client's `AllowedProducts` | — |
| `404 Not Found` | integration not enabled | — |

## Flux configuration

Per cluster, next to the cluster's other manifests in the GitOps repository:

```yaml
apiVersion: v1
kind: Secret
metadata: { name: infraportal-webhook, namespace: flux-system }
stringData:                       # created from the secret store, not committed
  address: https://<portal>/api/integrations/flux/staging-weu-1/events
  token: <shared secret>
---
apiVersion: notification.toolkit.fluxcd.io/v1beta3
kind: Provider
metadata: { name: infraportal, namespace: flux-system }
spec:
  type: generic-hmac
  secretRef: { name: infraportal-webhook }
---
apiVersion: notification.toolkit.fluxcd.io/v1beta3
kind: Alert
metadata: { name: infraportal, namespace: flux-system }
spec:
  providerRef: { name: infraportal }
  eventSeverity: info
  eventSources:
    - kind: HelmRelease
      name: "*"
      namespace: "*"
  eventMetadata:
    environment: staging
    cluster: staging-weu-1
```

and on every `HelmRelease`:

```yaml
metadata:
  annotations:
    event.toolkit.fluxcd.io/product: checkout
    event.toolkit.fluxcd.io/service: checkout-api
```

## Testing a client by hand

```bash
body='{"involvedObject":{"kind":"HelmRelease","namespace":"checkout","name":"checkout-api"},"timestamp":"2026-11-18T12:20:00Z","reason":"UpgradeSucceeded","message":"manual test","metadata":{"revision":"1.3.0","environment":"staging","product":"checkout"}}'
sig=$(printf '%s' "$body" | openssl dgst -sha256 -hmac "$FLUX_SECRET" | awk '{print $2}')
curl -sS -X POST "$DEPLOYMENTS_URL/api/integrations/flux/staging-weu-1/events" \
  -H "Content-Type: application/json" -H "X-Signature: sha256=$sig" --data "$body"
```
