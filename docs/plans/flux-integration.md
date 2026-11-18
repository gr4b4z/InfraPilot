# Implementation Plan: Flux Integration

**Status:** Draft / for review
**Author:** (planning session, 2026-11-18)
**Repos affected:** InfraPortal (this repo), the **GitOps repository** Flux reads (manifests per
cluster and environment), the **infrastructure repository** that installs Flux on the clusters, and
the **release repository** whose `Reconcile <env>` workflow deploys today.

---

## 1. Goal & motivation

Services move from pipeline-driven `helm upgrade` to **Flux**: a cluster reconciles itself from a
GitOps repository, and a deployment is a commit to that repository. InfraPortal must keep showing
those deployments exactly as it does today — one deploy event per deployment, with its commits,
pull requests and work items, closing the promotion that asked for it.

Today `Reconcile <env>` posts **one** deploy event after `helm upgrade` has finished, with the final
status and with references it computes itself: it diffs the previous and the current
`build-metadata.yaml` of that environment, resolves the commit range from the SCM and extracts the
Jira keys. The promotion that triggered the deploy was created earlier by a separate script, which
computed its own diff (target environment's current version → promoted version) with the same
modules. In the ordinary case the two lists are the same.

With Flux, the install happens in the cluster and Flux reports the result in its own notification
format (`involvedObject`, `reason`, `message`, `metadata`). Flux knows nothing about commits,
pull requests or tickets, and cannot post to `POST /api/deployments/events`.

**New model:** InfraPortal gets one **integration endpoint**, `POST /api/integrations/flux/events`.
Flux calls it **once, when the install has finished**. The endpoint maps the event onto a deploy
event and **copies the references of the open promotion candidate** for the same service, version
and target environment — the change set people already reviewed and signed off — then ingests it
through the existing `IngestEventWithResult`. Everything downstream of ingest (webhooks, promotion
and rollback completion, migrations, analytics, release notes) runs unchanged.

The portal stays the decision plane (`knowledge/webhook-catalogue.yaml`): promotions, approvals,
sign-offs and rollbacks keep working as they do today. Flux replaces the delivery step only.

### End-state flow

```
promotion test → staging:
  Flux (test) installs the version
    → POST /api/integrations/flux/events (UpgradeSucceeded)     [Flux → InfraPortal]
    → deploy event on test (references from the test candidate)
    → deployment.created webhook → repository_dispatch          [InfraPortal → GitOps repo]
    → workflow computes the net change set (compare, PRs, Jira keys)
    → POST /api/promotions with references                      [GitOps repo → InfraPortal]
  sign-offs, approval                                           [InfraPortal]
    → promotion.approved → repository_dispatch                  [InfraPortal → GitOps repo]
    → workflow commits the version to apps/<service>/staging/
  Flux (staging) pulls the commit (≤ 5 min) and upgrades the release
    → POST /api/integrations/flux/events (UpgradeSucceeded | UpgradeFailed)
    → adapter finds the open staging candidate, copies its references
    → IngestEventWithResult: one deploy event, deployment.created,
      succeeded closes the candidate as Deployed (existing hook)
```

---

## 2. Current-state references

| Concern | Location |
|---|---|
| Deploy ingest route (API key, rate limit, product scope) | `DeploymentEndpoints.MapDeploymentEndpoints` → `src/Platform.Api/Features/Deployments/DeploymentEndpoints.cs:14` |
| Ingest + idempotent replay | `DeploymentService.IngestEventWithResult` → `src/Platform.Api/Features/Deployments/DeploymentService.cs:156` |
| Environment resolution (normalisation + aliases), private today | `DeploymentService.ResolveEnvironmentAsync` → same file `:134`; `EnvironmentAliasResolver` → `src/Platform.Api/Features/Settings/EnvironmentAliasResolver.cs:22` |
| Product resolution (service→product override) | `ServiceProductOverrideService.ResolveProductAsync` → `src/Platform.Api/Features/Deployments/ServiceProductOverrideService.cs:101` |
| `deployment.created` webhook (insert only) | `DeploymentService.cs:301`; template `src/Platform.Api/Features/Webhooks/NotificationTemplates.cs:34` |
| Promotion close on ingest | `PromotionIngestHook.OnIngestedAsync` → `src/Platform.Api/Features/Promotions/PromotionIngestHook.cs:74`; matching rule `MatchCompletionAsync` → `:116` |
| Candidate change set | `PromotionCandidate.ReferencesJson` → `src/Platform.Api/Features/Promotions/Models/PromotionCandidate.cs:78`; statuses `PromotionStatus` → `:115` |
| Migrations derived from deploy logs (`helm hook logs`) | `MigrationService.OnDeployIngestedAsync` / `DeriveFromDeployAsync` → `src/Platform.Api/Features/Migrations/MigrationService.cs:392` |
| Ingest payload | `CreateDeployEventDto` → `src/Platform.Api/Features/Deployments/Models/DeploymentDtos.cs:7` |
| API-key policy and scopes | `ApiKeyAuthHandler.PolicyName` → `src/Platform.Api/Infrastructure/Auth/ApiKeyAuthHandler.cs:14`; `ApiKeyScopes` → `:174` |
| Route groups | `src/Platform.Api/Program.cs:506` (`/api/deployments`, group-level `CanApprove`) |
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
  Flux/FluxEventMapper.cs      FluxEventDto → CreateDeployEventDto (pure, unit-tested)
  Flux/FluxIngestService.cs    resolve key → find candidate → copy references → ingest
```

`Program.cs`:

```csharp
app.MapGroup("/api/integrations").MapIntegrationEndpoints();
```

No group-level `CanApprove`: integrations are called by systems, not users. Each adapter declares
`.RequireAuthorization(ApiKeyAuthHandler.PolicyName)` and
`.RequireRateLimiting(DeploymentIngestionRateLimit.PolicyName)`, as `/api/deployments/events` does.
The adapter writes nothing itself: the deploy event is stored by `IngestEventWithResult`.

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

| Deploy event | Source |
|---|---|
| `product` | `metadata.product` — required; `400` when missing |
| `service` | `metadata.service`, else `involvedObject.name` |
| `environment` | `metadata.environment` — required |
| `version` | `metadata.app-version`, else `metadata.revision` |
| `status` | `InstallSucceeded`, `UpgradeSucceeded` → `succeeded`; `InstallFailed`, `UpgradeFailed`, `RollbackFailed`, `UninstallFailed` → `failed`; `UninstallSucceeded` → `removed` |
| `source` | `helm-deploy` (counts as a real deployment in analytics, see D5) |
| `deployedAt` | `timestamp` — the time Flux finished, so a retry of the same event is a replay |
| `run` | `{ provider: "flux", workflowName: "Flux <metadata.cluster>", completedAt: <timestamp>, failureReason: <message> on failure }` |
| `references` | copied from the matched candidate (§3.4); empty when none matches |
| `participants` | the matched candidate's event-level participants (e.g. `triggered-by`); empty when none matches |
| `logs` | one block `{ name: "flux event", source: <reportingController>, content: <message> }` |
| `metadata` | `{ flux: { kind, namespace, name, reason, revision, cluster }, promotionId: <candidate id> }` |

Events that are not `HelmRelease`, or whose `reason` is not in the table, are accepted and ignored
(`202`). The `Alert` only reduces traffic; the endpoint is the filter.

### 3.4 Copying references from the promotion candidate

`FluxIngestService.IngestAsync(FluxEventDto evt, CancellationToken ct)`:

1. **Resolve the key** the way ingest does. Extract the product/environment resolution at the top of
   `IngestEventWithResult` into a public `DeploymentService.ResolveKeyAsync(product, service,
   environment)` and call it from both places, so the candidate lookup and the stored row can never
   disagree on the product (service→product override) or the environment (aliases).
2. **Find the candidate** with the same `(Product, Service, TargetEnv, Version)` and a status other
   than `Deployed` and `Superseded` — the same set `MatchCompletionAsync` would close. When several
   match (different source environments), prefer `Deploying`, then `Approved`, `Pending`,
   `Rejected`; within a status the newest `CreatedAt`.
3. **Copy** the candidate's `References` and event-level `Participants` into the
   `CreateDeployEventDto`, and its id into `metadata.promotionId`.
4. **Ingest** with `IngestEventWithResult`. Its post-insert steps run as for any sender:
   `deployment.created` with the final status, the promotion hook (a `succeeded` closes the
   candidate the references came from), the rollback hook, migrations.

The lookup happens before ingest, so the candidate is still open when its references are read;
the hook closes it right after. A `failed` event copies the references too: the failed attempt
belongs to that change set, and the candidate stays open for the next attempt.

No candidate (a commit made outside the promotion flow, the very first deploy of a service, a
rollback — see §4.2) means an event without references, as any out-of-band deploy today.

### 3.5 Responses

| Status | When | Body |
|---|---|---|
| `201 Created` | event ingested | `{ "id", "version", "previousVersion", "promotionId" }` (`promotionId` null when no candidate matched) |
| `200 OK` | replay of an event already ingested | same body, `"replayed": true` |
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

- `appsettings`: `Integrations:Flux:Enabled` (default `false`). Configuration rather than a
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
annotations. Change sets never go into annotations (D3).

### 3.8 GitOps repository — promotion pipeline

GitHub Actions, replacing the delivery part of `Reconcile <env>`:

- **Create promotion** — `repository_dispatch` from `deployment.created` with `status: succeeded`
  on the source environment → compute the net change set → `POST /api/promotions`. The
  change-set computation is the existing logic (commit diff, PRs, Jira keys) moved into a shared
  action; the payload format does not change.
- **Promote** — `promotion.approved` / `rollback.approved` → commit the version to
  `apps/<service>/<env>/`. Nothing is posted to InfraPortal: the deploy event comes from Flux.
- **First environment** — new builds reach the first environment as promotions from the `build`
  source (`docs/plans/feature-branch-builds.md`), so the dev deploy also finds a candidate.

---

## 4. Decision log

### 4.1 Resolved

- **D1** — One adapter endpoint; no SCM or Jira calls in it. It reads only InfraPortal's own data
  (the promotion candidate). Ticket titles come from the existing Jira enrichment.
- **D2** — Adapters live under `/api/integrations/<provider>/...`, separate from the core API, so
  the canonical payloads (`/events`, `/promotions`) stay the only documented formats.
- **D3** — References are computed once, when the promotion is created, and copied into the deploy
  event. Flux never carries them. This differs from `Reconcile <env>`, which recomputes the diff at
  deploy time: the two lists differ only when something else reached the target environment
  between candidate creation and the deploy, and in that case the overtaken candidate is
  `Superseded` and is not matched.
- **D4** — Flux sends one event per deployment, after the install. No `in_progress` events, no
  update of an existing row: ingest, webhooks, analytics and release notes behave as today.
- **D5** — `source: helm-deploy`, so Flux deployments count in analytics like today's reconciles;
  `run.workflowName: "Flux <cluster>"` tells them apart.
- **D6** — Dedicated scope `integration:flux`; a cluster key cannot create promotions or register
  builds.
- **D7** — Disabled by default (`Integrations:Flux:Enabled`).
- **D8** — No What's new entry: the change is API-only and nobody meets it in the portal
  (`AGENTS.md`).

### 4.2 Open questions

1. **Migrations.** `MigrationService` derives schema migrations from the `helm hook logs` block a
   deploy event carries. A Flux event carries only Helm's one-line message, so services deployed by
   Flux get no entries on the migrations page. Options: collect the migration job's pod logs from
   the cluster and post them (`POST /api/migrations/runs`), or accept the gap per service.
2. **Rollbacks.** `rollback.approved` redeploys an earlier version; there is no promotion candidate
   for it, and `RollbackRequest` holds no change set (`Features/Rollbacks/Models/RollbackRequest.cs`),
   so the event has no references. Today `Reconcile` computes the diff of a rollback too. Have the
   rollback workflow compute it and store it on the request, or accept rollbacks without references?
3. **Visibility while installing.** The deployments view shows nothing until Flux finishes (as
   today). The promotion's `Deploying` state is the only in-progress signal. Is that enough?
4. **Network.** The endpoint must be reachable from every cluster's egress (NAT prefix per cluster).
5. **Flux header support.** The `headers` key in the provider secret is assumed to work with the
   `microsoft.flux` extension's notification-controller; verify on the first test, fall back to
   `generic-hmac` with signature verification if not.

---

## 5. Work breakdown (suggested order)

**Global constraints:** no change to `CreateDeployEventDto` or to what `IngestEventWithResult`
stores; no commits on `main`, no push, no PR without the user's consent.

### Task 1: Scope and configuration

**Files:** `Infrastructure/Auth/ApiKeyAuthHandler.cs` (`ApiKeyScopes.IntegrationFlux`), `appsettings.json`
(`Integrations:Flux:Enabled`), options class under `Features/Integrations/Flux/`.

### Task 2: DTO and mapper (TDD)

**Files:** `FluxEventDto.cs`, `FluxEventMapper.cs`.
Tests: every reason in §3.3; non-`HelmRelease` → ignored; `app-version` wins over `revision`;
`service` falls back to the object name; missing `product` / `environment` → validation errors;
`failureReason` set only on failure.

### Task 3: Key resolution and candidate lookup (TDD)

**Files:** `DeploymentService.cs` (`ResolveKeyAsync`, used by `IngestEventWithResult` and the adapter),
`FluxIngestService.cs`.
Tests: aliases and service→product overrides resolve the same as ingest; the candidate is matched on
the resolved key; `Deployed` and `Superseded` are never matched; preference order across several
candidates; references and participants copied verbatim (nested participants, `commits`,
`content`); no candidate → empty references.

### Task 4: Endpoint and integration tests

**Files:** `IntegrationEndpoints.cs`, `FluxEndpoints.cs`, `Program.cs`.
Tests: `201` / `200` replay / `202` / `400` / `403` / `404`; a `succeeded` event closes the candidate
its references came from and dispatches `deployment.created`; a `failed` event leaves the candidate
open; rate limit applies; a user session cannot call it.

### Task 5: Docs

`notes/integrations/flux.md` (payload, mapping, candidate matching, responses, `Provider`/`Alert`
example); `notes/deployment-ingest-api.md` (pointer to integrations);
`knowledge/deploy-event-sources.yaml` (`run.workflowName: "Flux <cluster>"`); `AGENTS.md` (list the
new note); `GLOSSARY.md` if the term *Integration* is adopted.

### Task 6: Verify

`dotnet test`; post the sample from §3.2 to a local instance with and without a matching candidate.

---

## 6. Rollout

1. Merge with `Integrations:Flux:Enabled = false`.
2. Enable on the instance, provision a `flux-<cluster>` key, add the `Provider`/`Alert` to
   `clusters/<cluster>/` and deploy a sample app (podinfo). Check the events in the portal.
3. Move one real service on one environment from `Reconcile <env>` to the GitOps repository,
   disabling its `helm upgrade` in the release repository in the same change. Compare its events
   with the old path, references included.
4. Roll out per service with a checklist; then per cluster.

---

## 7. Acceptance scenarios

1. **Promotion through Flux.** A test → staging candidate with work items is signed off and
   approved; the pipeline commits the version; Flux upgrades the release. The portal shows **one**
   staging deploy event with the candidate's references and status `succeeded`, the candidate is
   `Deployed`, and `deployment.created` went out once.
2. **Failed upgrade.** Same, but the chart fails its readiness wait. One `failed` event with the
   candidate's references and Helm's message in the `flux event` log block; the candidate stays
   open.
3. **No candidate.** Someone commits a version straight to the GitOps repository. Flux installs it;
   the event has no references; a pending candidate for another version is superseded by the
   existing hook if this version overtakes it.
4. **Retry.** Flux re-sends the same event; the response is a replay, with no second row.
5. **Removal.** A service is removed from `clusters/<cluster>/`; prune uninstalls it and the portal
   records `removed`.
