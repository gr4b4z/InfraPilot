# Implementation Plan: Flux Integration

**Status:** Draft / for review
**Author:** (planning session, 2026-11-18)
**Repos affected:** InfraPortal (this repo), the **GitOps repository** Flux reads (manifests per
cluster and environment), the **infrastructure repository** that installs Flux on the clusters, and
the **release repository** whose `Reconcile <env>` workflow deploys today.

---

## 1. Goal & motivation

Services move from pipeline-driven `helm upgrade` to **Flux**: a cluster reconciles itself from a
GitOps repository, and a deployment is a commit to that repository. InfraPortal
must keep showing those deployments exactly as it does today — one deploy event per deployment,
with its commits, pull requests and work items, closing the promotion that asked for it.

Today the release repository's `Reconcile <env>` workflow does everything in one job: it diffs the previous and the
new `build-metadata.yaml`, runs `helm upgrade`, and posts one deploy event with the references and
the result. With Flux that job splits in two:

- the **promotion pipeline** still knows *what* changes (it computes the commit diff), but it no
  longer installs anything, so it does not know the result;
- **Flux** knows the result, but only in its own event format, and it cannot compute a diff.

Two gaps follow:

1. Flux cannot post to `POST /api/deployments/events`: its notification payload has a different
   shape (`involvedObject`, `reason`, `message`, `metadata`).
2. A second deploy event for the same version does not complete the first one. A replay
   (same natural key) refreshes only `run` and `logs`; a different `deployedAt` inserts a new row
   (§2). Posting `in_progress` from the pipeline and the result from Flux would leave two rows,
   with the references on the one that never finished.

**New model:** InfraPortal gets one **integration endpoint**, `POST /api/integrations/flux/events`,
that only maps a Flux event onto the existing deploy-event model. When the promotion pipeline has
already posted the deployment as `in_progress`, the endpoint **completes that row** (status, logs);
otherwise it ingests a minimal event. The core ingest API and its current senders do not change.

The portal stays the decision plane (`knowledge/webhook-catalogue.yaml`): promotions, approvals,
sign-offs and rollbacks keep working as they do today. Flux replaces the delivery step only.

### End-state flow

```
promotion test → staging:
  Flux (test) installs the version, posts UpgradeSucceeded
    → POST /api/integrations/flux/events                        [Flux → InfraPortal]
    → deployment.created webhook → repository_dispatch          [InfraPortal → GitOps repo]
    → workflow computes the net change set (compare, PRs, Jira keys)
    → POST /api/promotions with references                      [GitOps repo → InfraPortal]
  sign-offs, approval                                           [InfraPortal]
    → promotion.approved → repository_dispatch                  [InfraPortal → GitOps repo]
    → workflow commits the version to apps/<service>/staging/
    → GET /api/promotions/{id}, then POST /api/deployments/events
      status in_progress, references copied from the promotion  [GitOps repo → InfraPortal]
  Flux (staging) pulls the commit (≤ 5 min) and upgrades the release
    → POST /api/integrations/flux/events (UpgradeSucceeded | UpgradeFailed)
    → the in_progress row becomes succeeded | failed            [InfraPortal]
    → succeeded closes the promotion as Deployed (existing hook)
```

---

## 2. Current-state references

| Concern | Location |
|---|---|
| Deploy ingest route (API key, rate limit, product scope) | `DeploymentEndpoints.MapDeploymentEndpoints` → `src/Platform.Api/Features/Deployments/DeploymentEndpoints.cs:14` |
| Ingest + idempotent replay | `DeploymentService.IngestEventWithResult` → `src/Platform.Api/Features/Deployments/DeploymentService.cs:156` |
| Replay branch (refreshes `Run` + logs only, never `Status` or references) | same file `:183` |
| Insert of a new row | same file `:291` |
| Promotion close on ingest | `PromotionIngestHook.OnIngestedAsync` → `src/Platform.Api/Features/Promotions/PromotionIngestHook.cs:74`, called from `DeploymentService.cs:207` (replay) and `:325` (insert) |
| Natural key | `(Product, Service, Environment, Version, DeployedAt, Source)` — `DeploymentService.cs:144` (doc comment), `:993` (`DuplicateKey`) |
| Ingest payload | `CreateDeployEventDto` → `src/Platform.Api/Features/Deployments/Models/DeploymentDtos.cs:7` |
| Status values | `DeployEventStatuses` → `src/Platform.Api/Features/Deployments/Models/DeployEvent.cs:68` |
| API-key policy and scopes | `ApiKeyAuthHandler.PolicyName` → `src/Platform.Api/Infrastructure/Auth/ApiKeyAuthHandler.cs:14`; `ApiKeyScopes` → `:174` |
| Route groups | `src/Platform.Api/Program.cs:506` (`/api/deployments`, group-level `CanApprove`) |
| `deployment.created` webhook | `src/Platform.Api/Features/Webhooks/NotificationTemplates.cs:34`; catalogue `knowledge/webhook-catalogue.yaml` |
| Real-deploy sources for analytics | `knowledge/deploy-event-sources.yaml` (`helm-deploy`, `run.workflowName` discriminator) |

There is no route group for adapters of third-party formats today. `/api/webhooks` is the admin
surface for *outgoing* webhook subscriptions and is not the place for this.

---

## 3. Target design

### 3.1 InfraPortal — `/api/integrations` route group

```
src/Platform.Api/Features/Integrations/
  IntegrationEndpoints.cs      MapIntegrationEndpoints(): mounts every adapter
  Flux/FluxEndpoints.cs        MapPost("/flux/events", ...)
  Flux/FluxEventDto.cs         Flux notification payload
  Flux/FluxEventMapper.cs      FluxEventDto → FluxDeployOutcome (pure, unit-tested)
```

`Program.cs`:

```csharp
app.MapGroup("/api/integrations").MapIntegrationEndpoints();
```

No group-level `CanApprove`: integrations are called by systems, not users. Each adapter declares
`.RequireAuthorization(ApiKeyAuthHandler.PolicyName)` and
`.RequireRateLimiting(DeploymentIngestionRateLimit.PolicyName)`, as `/api/deployments/events` does.
The adapter never writes to the database itself: it maps, then calls `DeploymentService`.

### 3.2 Input — Flux notification (`type: generic` provider)

```jsonc
{
  "involvedObject": { "kind": "HelmRelease", "namespace": "checkout", "name": "checkout-api" },
  "severity": "info",                       // "info" | "error"
  "timestamp": "2026-11-18T12:20:00Z",
  "message": "Helm upgrade succeeded for release checkout/checkout-api.v12 with chart checkout-api@1.3.0",
  "reason": "UpgradeSucceeded",
  "reportingController": "helm-controller",
  "metadata": {
    "revision": "1.3.0",                     // chart version
    "environment": "staging",               // Alert.spec.eventMetadata
    "cluster": "staging-weu-1",              // Alert.spec.eventMetadata
    "product": "checkout",                   // annotation event.toolkit.fluxcd.io/product
    "service": "checkout-api",               // annotation event.toolkit.fluxcd.io/service
    "app-version": "1.3.0"                   // annotation event.toolkit.fluxcd.io/app-version (optional)
  }
}
```

### 3.3 Mapping

| Deploy event | Source in the Flux event |
|---|---|
| `product` | `metadata.product` — required; `400` when missing |
| `service` | `metadata.service`, else `involvedObject.name` |
| `environment` | `metadata.environment` — required; resolved through environment aliases like any ingest |
| `version` | `metadata.app-version`, else `metadata.revision` |
| `status` | `InstallSucceeded`, `UpgradeSucceeded` → `succeeded`; `InstallFailed`, `UpgradeFailed`, `RollbackFailed`, `UninstallFailed` → `failed`; `UninstallSucceeded` → `removed` |
| `source` | `helm-deploy` (counts as a real deployment in analytics, see D5) |
| `deployedAt` | `timestamp` (only for a newly ingested event, see §3.4) |
| `run` | `{ provider: "flux", workflowName: "Flux <metadata.cluster>" }` (only for a newly ingested event) |
| `logs` | one block `{ name: "flux event", source: <reportingController>, content: <message> }` |
| `metadata.flux` | `{ kind, namespace, name, reason, revision, cluster, timestamp }` |

Events that are not `HelmRelease`, or whose `reason` is not in the table, are accepted and ignored
(`202`). Flux does not need to filter perfectly; the `Alert` only reduces traffic.

### 3.4 Completing an in-progress event

New `DeploymentService.CompleteInProgressAsync(FluxDeployOutcome outcome, CancellationToken ct)`:

1. Resolve product (service→product override) and environment (aliases) exactly as
   `IngestEventWithResult` does, so both paths agree on the key.
2. Find the newest event with that `(Product, Service, Environment, Version)`, `Status == in_progress`
   and `CreatedAt` within `Integrations:Flux:InProgressLookbackHours` (default 24).
3. **Found:** set `Status`; append the `flux event` log block; merge `metadata.flux` into
   `MetadataJson`. `References`, `Participants`, `Run`, `DeployedAt` and `Source` stay as the
   pipeline sent them. Run the promotion hook (`OnIngestedAsync`) so a `succeeded` closes the
   candidate. Return `updated`.
4. **Not found:** build a `CreateDeployEventDto` from the mapping and call `IngestEventWithResult`.
   Its replay guard makes Flux retries of the same event harmless (same `deployedAt` = Flux
   `timestamp`). Return `created` or `replayed`.

`IngestEventWithResult` and the existing replay semantics are untouched.

### 3.5 Responses

| Status | When | Body |
|---|---|---|
| `200 OK` | an `in_progress` row was completed, or a replay | `{ "id", "status", "updated": true }` / `{ "id", "status", "replayed": true }` |
| `201 Created` | no `in_progress` row; a new event was ingested | `{ "id", "version", "previousVersion" }` |
| `202 Accepted` | event kind or reason not handled | `{ "ignored": true, "reason": "..." }` |
| `400 Bad Request` | missing `product` / `environment`, unparseable payload | `{ "errors": [ ... ] }` |
| `403 Forbidden` | product outside the key's `AllowedProducts`, or a scoped key without `integration:flux` | — |
| `404 Not Found` | `Integrations:Flux:Enabled` is `false` | — |

### 3.6 Configuration & auth

- New scope `ApiKeyScopes.IntegrationFlux = "integration:flux"`. A key **with** a `Scopes` list
  needs it; a key without one stays unrestricted, as for the other scopes. Cluster keys should be
  least-privilege:

  ```jsonc
  { "Name": "flux-staging-weu-1", "KeyHash": "<sha256-hex>", "AllowedProducts": ["checkout"], "Scopes": ["integration:flux"] }
  ```

- `appsettings`: `Integrations:Flux:Enabled` (default `false`) and
  `Integrations:Flux:InProgressLookbackHours` (default `24`). Configuration rather than a
  `FeatureFlagKeys` entry: those flags gate portal UI sections, this gates an ingest path, like
  `Deployments:Enrichment:Enabled`.

### 3.7 GitOps repository — what Flux sends

Per cluster, in `clusters/<cluster>/`:

```yaml
apiVersion: notification.toolkit.fluxcd.io/v1beta3
kind: Provider
metadata: { name: infraportal, namespace: flux-system }
spec:
  type: generic
  secretRef: { name: infraportal-webhook }   # address + headers (X-Api-Key), created from the secret store, not in Git
---
apiVersion: notification.toolkit.fluxcd.io/v1beta3
kind: Alert
metadata: { name: infraportal, namespace: flux-system }
spec:
  providerRef: { name: infraportal }
  eventSeverity: info
  eventSources: [ { kind: HelmRelease, name: "*", namespace: "*" } ]
  eventMetadata: { environment: staging, cluster: <cluster> }
```

Every `HelmRelease` carries `event.toolkit.fluxcd.io/product` and `event.toolkit.fluxcd.io/service`
annotations. Change sets never go into annotations: they are computed per promotion and can be
tens of kilobytes (D3).

### 3.8 GitOps repository — promotion pipeline

GitHub Actions, replacing the delivery part of `Reconcile <env>`:

- **Create promotion** — `repository_dispatch` from `deployment.created` (a `succeeded` event on the
  source environment) → compute the net change set → `POST /api/promotions`.
- **Promote** — `promotion.approved` / `rollback.approved` → commit the version to
  `apps/<service>/<env>/` → read the candidate's references (`GET /api/promotions/{id}`, `full`
  view) → `POST /api/deployments/events` with `status: in_progress`.
- **First environment** — a new build → commit the version to `apps/<service>/dev/` → compute
  the change set against the version on dev → `in_progress`.

The change-set computation is the existing `Reconcile` logic (compare, PRs, Jira keys) moved into a
shared action; the payload format does not change.

---

## 4. Decision log

### 4.1 Resolved

- **D1** — One adapter endpoint that only maps; no SCM or Jira calls in it. Change sets come from
  the pipeline, titles from the existing Jira enrichment.
- **D2** — Adapters live under `/api/integrations/<provider>/...`, separate from the core API,
  so the canonical payloads (`/events`, `/promotions`) stay the only documented formats.
- **D3** — References are computed once, at promotion creation, and copied into the `in_progress`
  event. Flux never carries them.
- **D4** — Completion updates the existing `in_progress` row instead of inserting a second one;
  references, participants, run and `deployedAt` stay the pipeline's.
- **D5** — `source: helm-deploy`, so Flux deployments count in analytics like today's reconciles;
  `run.workflowName: "Flux <cluster>"` tells them apart for events the adapter ingests itself.
- **D6** — Dedicated scope `integration:flux`; a cluster key cannot create promotions or register
  builds.
- **D7** — Disabled by default (`Integrations:Flux:Enabled`).
- **D8** — No What's new entry: the change is API-only and nobody meets it in the portal
  (`AGENTS.md`).

### 4.2 Open questions

1. **Completion and `deployment.created`.** Completing a row fires no webhook today
   (only `deployment.created` exists). The *Create promotion* workflow needs a signal when a
   version succeeds on the source environment. Options: fire `deployment.created` on completion,
   or add `deployment.completed`. Leaning towards a new event, so existing subscribers do not see
   the same deployment twice.
2. **`deployedAt` of a completed row.** It keeps the pipeline's timestamp (commit time), not
   Flux's install time. Analytics lead time is measured to `deployedAt`. Store the Flux time in
   `metadata.flux.timestamp` only, or also move `deployedAt`?
3. **Stale `in_progress` rows.** A commit Flux never applies leaves a row `in_progress` forever.
   Mark them `failed` after the lookback window (background sweep), or leave them?
4. **Replay filling references.** Should a replay of `/api/deployments/events` add references to a
   row that has none (the pipeline was late and the adapter created a minimal row first)?
5. **Network.** The endpoint must be reachable from every cluster's egress (NAT prefix per cluster).
6. **Flux header support.** The `headers` key in the provider secret is assumed to work with the
   `microsoft.flux` extension's notification-controller; verify on the first test, fall back to
   `generic-hmac` with signature verification if not.

---

## 5. Work breakdown (suggested order)

**Global constraints:** no change to `IngestEventWithResult` or `CreateDeployEventDto`; no commits on
`main`, no push, no PR without the user's consent.

### Task 1: Scope and configuration

**Files:** `Infrastructure/Auth/ApiKeyAuthHandler.cs` (`ApiKeyScopes.IntegrationFlux`), `appsettings.json`
(`Integrations:Flux`), options class under `Features/Integrations/Flux/`.

### Task 2: DTO and mapper (TDD)

**Files:** `FluxEventDto.cs`, `FluxEventMapper.cs`.
Tests: every reason in §3.3; non-`HelmRelease` → ignored; `app-version` wins over `revision`;
`service` falls back to the object name; missing `product` / `environment` → validation errors.

### Task 3: `CompleteInProgressAsync` (TDD)

**Files:** `DeploymentService.cs`.
Tests: completes the newest `in_progress` row and keeps its references; ignores rows older than the
lookback; resolves aliases and service→product overrides the same way as ingest; inserts a minimal
event when nothing matches; a repeated Flux event is a replay; `succeeded` closes a pending promotion
candidate on the target environment.

### Task 4: Endpoint and integration tests

**Files:** `IntegrationEndpoints.cs`, `FluxEndpoints.cs`, `Program.cs`.
Tests: `200` / `201` / `202` / `400` / `403` / `404`; rate limit applies; a user session cannot call it.

### Task 5: Docs

`notes/integrations/flux.md` (payload, mapping, responses, `Provider`/`Alert` example);
`notes/deployment-ingest-api.md` (pointer to integrations); `knowledge/deploy-event-sources.yaml`
(`run.workflowName: "Flux <cluster>"`); `AGENTS.md` (list the new note); `GLOSSARY.md` if the term
*Integration* is adopted.

### Task 6: Verify

`dotnet test`; post the sample from §3.2 to a local instance with and without a matching
`in_progress` row.

---

## 6. Rollout

1. Merge with `Integrations:Flux:Enabled = false`.
2. Enable on the instance, provision a `flux-<cluster>` key, add the `Provider`/`Alert` to
   `clusters/<cluster>/` and deploy a sample app (podinfo). Check the events in the portal.
3. Move one real service on one environment from `Reconcile <env>` to the GitOps repository,
   disabling its `helm upgrade` in the release repository in the same change. Compare its events with the old path.
4. Roll out per service with a checklist; then per cluster.

---

## 7. Acceptance scenarios

1. **Promotion through Flux.** A test → staging candidate with work items is signed off and
   approved; the pipeline commits the version and posts `in_progress`; Flux upgrades the release.
   The portal shows **one** staging deploy event with the candidate's references and status
   `succeeded`, and the candidate is `Deployed`.
2. **Failed upgrade.** Same, but the chart fails its readiness wait. The row becomes `failed` with
   the Helm message in the `flux event` log block; the candidate is not `Deployed`.
3. **No pipeline event.** Someone commits a version straight to the GitOps repository. Flux installs it;
   the adapter ingests a minimal `succeeded` event without references; a pending candidate for that
   version and edge still closes as `Deployed`, as for any out-of-band deploy.
4. **Retry.** Flux re-sends the same event; the response is a replay or an update with no second
   row.
5. **Removal.** A service is removed from `clusters/<cluster>/`; prune uninstalls it and the portal
   records `removed`.
