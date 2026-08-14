# Implementation Plan: Build Registry & Feature-Branch Deployments

**Status:** Draft / for review
**Author:** (planning session, 2026-08-14)
**Repos affected:** InfraPortal (this repo), `ops-build-templates-aks-releases`, `mpt-release` — plus a one-line condition change in each consumer app pipeline (see §4.2.2).

---

## 1. Goal & motivation

Developers cannot release a feature-branch build to DEV or TEST today. The publish
stage of every app pipeline is gated to `master` / `release/*`, and the only path
from a build to an environment runs through the `mpt-release` git repository —
which we do **not** want to pollute with a commit per feature-branch build.

At the same time, the current main-branch flow has structural weaknesses this plan
removes:

- InfraPortal has **no build entity**: a build exists only as a version string on a
  `DeployEvent`. "What builds exist?" and "which branch produced this?" are not
  queryable.
- The build → DEV hand-off is a direct ADO-to-ADO pipeline trigger
  (`import-build`, pipeline 5617) that commits **every** main build's manifest to
  `mpt-release` regardless of whether it ever ships, and bypasses InfraPortal
  entirely.
- Build manifests live in a blob container (`nltapp0762sda/changelogs`) with its
  own credentials and its own (nonexistent) retention story, disconnected from the
  images/charts they describe.

**New model:** every published build (main, release, *and* feature) is registered
in InfraPortal's new **build registry**. Deployment to any environment — including
main → DEV, which is automatic today — becomes a **promotion from the synthetic
source env `build`**, governed by promotion policies. `mpt-release` commits a
manifest only when it actually deploys it (*commit-on-deploy*), so its git history
becomes a pure deployment ledger. Manifests move from blob storage into ACR as OCI
artifacts, next to the images and chart they describe.

### End-state flow

```
main build:
  publish to ACR (images + chart + manifest)                 [templates repo]
    → POST /api/builds (manifest inline + OCI ref/digest)    [templates → InfraPortal]
    → policy `build → dev` matches refs/heads/main
    → candidate auto-created + auto-approved                 [InfraPortal]
    → promotion.approved repository_dispatch                 [InfraPortal → mpt-release]
    → workflow: oras pull manifest, commit to dev/<svc>/, deploy
    → POST /api/deployments/events closes the candidate      [mpt-release → InfraPortal]

feature build (manually queued pipeline run):
  same publish + registration, then STOPS.
  A developer picks the build in the InfraPortal UI (or POSTs /api/promotions)
  → candidate on `build → dev` (or `build → test`) → approval per policy → same
  dispatch/deploy/close path as above.
```

The `stable` landing zone (env in InfraPortal, `stable/` + `main/` directories in
`mpt-release`) is retired: the build registry is a strictly better record of
"latest good main build" — it keeps *all* builds, with branch and manifest.
Promotion edges collapse to `build → dev`, `build → test`, `build → staging`,
`staging → production`.

---

## 2. Current-state references

| Concern | Location |
|---|---|
| Publish steps (ACR push, metadata upload, import-build trigger) | `ops-build-templates-aks-releases` → `aks-docker-templates/publish-steps.yaml` (branch `releases/v15`) |
| Branch gate (NOT in the template) | each app pipeline's `publish` job condition, e.g. `mpt-spotlight/pipelines/build.yml:206` — `master` OR `release/4` OR `System.Debug` |
| Manifest upload target | blob `nltapp0762sda/changelogs/<repo>/<buildNumber>/build-metadata.yaml`, SPN `CPA (Test and Development)` (distinct from the ACR push SPN `CPA (Infrastructure)`) |
| Hand-off to mpt-release | PowerShell step triggers pipeline 5617 (`mpt-release.import-build`) with a single `metadataUrl` template parameter; `continueOnError: true`; skipped for `Build.Reason == PullRequest` |
| Manifest schema | `BuildMetadata` v1-beta: `spec.service/version`, `references.{repository{branch,revision},pipeline,pull-request,work-item}`, `artifacts[]` — already carries branch + Jira key |
| Manifest ingestion into mpt-release | `mpt-release/pipelines/metadata-import/scripts/Import-BuildMetadata.ps1` → commits `dev/<component>/build-metadata.yaml` + `archive/` |
| InfraPortal deploy ingest | `POST /api/deployments/events` → `DeploymentService.IngestEventWithResult` (`src/Platform.Api/Features/Deployments/DeploymentService.cs:128`); X-Api-Key + product scope + rate limit |
| Manual Deployment (NOT reused by this plan) | `DeploymentService.CreateManualEventAsync` (`:67`) — ledger entry, requires predecessor, causes nothing |
| Promotion creation | `PromotionService.CreateExternalCandidateAsync` (`src/Platform.Api/Features/Promotions/PromotionService.cs:112`) — external/push-only per D19 of `external-promotion-creation.md` |
| Source-not-an-environment lever | `PromotionPolicy.SourceRequiresDeploy = false` — documented for "landing zone / release track that never receives deploy events" |
| Dev-ring noise lever | `PromotionPolicy.TracksWorkItems = false` |
| Deploy trigger | `promotion.approved` → GitHub `repository_dispatch` (`WebhookRequestBuilder.BuildGitHub`, `src/Platform.Api/Features/Webhooks/WebhookRequestBuilder.cs:128`); delayed + cancellable via `ApprovedWebhookOptions` |
| Completion reconciliation | `PromotionService.AssessAgainstDeployHistoryAsync` (`:2091`); admin sweep `POST /api/promotions/admin/candidates/reconcile-completions` |
| Version ordering | `PromotionVersionOrder.TryCompare` — requires ≥2-component numeric prefix; unorderable ⇒ fails open (no auto-supersede) |
| Staging/prod state sync (unaffected) | `scripts/sync-mpt-versions.ps1` reads deployed-state `versions.json` manifests — not the build-time metadata |

---

## 3. Target design

### 3.1 InfraPortal — build registry

**New table `builds`** (entity `Build`):

| Column | Notes |
|---|---|
| `Id` | PK |
| `Product`, `Service` | same normalization as deploy events |
| `Version` | build number, e.g. `5.0.347-g495d92f0` |
| `Branch` | full ref, e.g. `refs/heads/feature/MPT-1234-x` — **indexed** |
| `CommitSha`, `BuildId`, `BuildUrl` | provenance |
| `ManifestJson` | the full `BuildMetadata` document, inline |
| `ArtifactRef`, `ArtifactDigest` | OCI reference + digest of the manifest artifact in ACR |
| `CreatedAt` | |

Indexes: `(Product, Service, CreatedAt)`, `(Product, Service, Version)` unique
(replay-safe upsert), `Branch`.

**New endpoint `POST /api/builds`** — mirrors `/api/deployments/events`: X-Api-Key
auth, product scope from `allowed_product` claims, rate limiting, idempotent on
`(Product, Service, Version)` (re-POST updates in place, returns 200 vs 201).
Payload: the fields above with `ManifestJson` inline — **InfraPortal never fetches
from ACR or storage; it is a pure recipient** (consistent with deploy ingest).
Distinct scope `build:register` on the API key (least privilege, same pattern as
`promotion:create`, D16 of the previous plan).

**Read surface:** `GET /api/builds?product=&service=&branch=` for the UI picker;
`GET /api/builds/{id}` including manifest. Retention: feature-branch rows
prunable after a configurable window (see §6/OQ3).

### 3.2 InfraPortal — promotions from `build`

**Policy extension** — `PromotionPolicy` gains one nullable field:

- `AutoCreateFromBranchesJson` (`string?`, JSON array of branch patterns, e.g.
  `["refs/heads/main", "refs/heads/master"]`). Meaningful only on edges whose
  `SourceEnv == "build"`. Snapshotted into `ResolvedPolicyJson` like every other
  policy field, so candidates stay auditable.

**Seeded edges per product** (config/seed, not schema):

| Edge | SourceRequiresDeploy | TracksWorkItems | Approval | AutoCreateFromBranches |
|---|---|---|---|---|
| `build → dev` | false | false | auto-approve | `main`/`master` |
| `build → test` | false | false | 1 step (or auto — per product) | — |
| `build → staging` | false | **true** | existing staging policy | — |
| `staging → production` | unchanged | unchanged | unchanged | — |

**New `BuildIngestHook`** (twin of `PromotionIngestHook`): on build registration,
resolve all policies with `SourceEnv == "build"` for the product/service; for each
whose branch pattern matches, call `CreateExternalCandidateAsync` with:
`SourceEnv = "build"`, the build's version, `ToRevision = CommitSha`, and
**references copied from the manifest** (`repository`, `pipeline`, `work-item`,
plus a `build-manifest` reference carrying the OCI ref/digest). Copying manifest
references means the `build → staging` edge gets `PromotionWorkItem` gating
populated with zero extra plumbing. Non-matching branches create nothing.

**UI** — "Deploy a build" on the Service Details / Deployments page: picker over
`GET /api/builds` for the service (newest first, branch badge prominent), target
env selector limited to edges with `SourceEnv == "build"` that resolve to a
policy. Submits the same create-candidate path. Manual Deployment stays exactly
what it is (a ledger backfill tool) — it is **not** part of this feature.

**Webhook payload** — `DispatchWebhookAsync` already sends the candidate's
references; the `build-manifest` reference now carries the ACR OCI ref + digest
instead of a blob URL. `ApprovedWebhookOptions` delay set to ~0 for the
auto-approved `build → dev` edge (a cancellation window on automatic main deploys
is pure latency).

**Environment display** — DEV/TEST env views must surface the source branch
prominently (from the candidate/manifest references) so nobody mistakes a DEV
running a feature build for main. Being overwritten by the next main auto-deploy
is the intended lifecycle, and the deploy-event history records who put what
there.

### 3.3 `ops-build-templates-aks-releases` — publish-steps.yaml

1. **Manifest to ACR instead of blob.** Replace the `az storage blob upload` step
   with an `oras` push using the **same SPN as the image push** (`CPA
   (Infrastructure)`), deleting the `CPA (Test and Development)` storage
   dependency. Preferred shape: `oras attach` making the manifest a *referrer* of
   the helm chart (the chart is the unit mpt-release deploys; the manifest then
   travels and is garbage-collected with it, discoverable via `oras discover`).
   Fallback if referrer tooling proves awkward in ADO agents: sibling repo path
   `<acr>/<repo>/build-metadata:<buildNumber>`. Capture the pushed digest.
   **During transition, dual-publish** (ACR + existing blob) — see §5.
2. **New registration step**: `POST /api/builds` with manifest inline + OCI
   ref/digest. **This step must fail the pipeline loudly** — no
   `continueOnError`. Once the registry is the only path to a DEV deploy, an
   unregistered build silently never deploys. Skipped for
   `Build.Reason == PullRequest` (same exclusion as today's import-build
   trigger). API key + URL via variable group / service connection.
3. **Retire the import-build trigger** (pipeline 5617 step): behind a template
   parameter `useBuildRegistry` (default `false`) so consumer repos cut over
   individually; the parameter also gates old-vs-new manifest publishing. Removed
   entirely in cleanup (§6).

### 3.4 Consumer app pipelines (~8 repos, one line each)

The branch gate lives in each app repo's `publish` job condition. New condition:

```yaml
condition: or(
  eq(variables['Build.SourceBranch'], 'refs/heads/master'),
  startsWith(variables['Build.SourceBranch'], 'refs/heads/release/'),
  eq(variables['Build.Reason'], 'Manual'))
```

No `publishBuild` parameter: **manually queueing the pipeline on a feature branch
is the publish intent**. CI-triggered feature pushes and PR validation builds
still do not publish (verify each repo's `trigger:` block during rollout — OQ2).

Known consumers: `mpt-spotlight`, `mpt-notifications`, `mpt-public-catalog`,
`mpt-audit`, `mpt-currency`, `mpt-billing`, `mpt-tasks`, `swo-pyraproxy`.

### 3.5 `mpt-release` — deploy on dispatch, commit-on-deploy

**New/extended workflow** handling the `promotion.approved`
`repository_dispatch`:

1. Read the `build-manifest` reference (OCI ref + digest) from `client_payload`.
2. `oras pull` the manifest by **digest** (credentials already exist — the
   workflows pull charts/images from the same ACR).
3. Commit the manifest to `<targetEnv>/<service>/build-metadata.yaml` (+ archive)
   **as part of executing the deployment** — the commit and the deploy are one
   unit; nothing is committed for builds that never ship.
4. Call `MarkDeployingAsync` (`ExternalRunUrl`), deploy via the existing helm
   path, report `POST /api/deployments/events` (which closes the candidate via
   the existing ingest hook).

The existing promotion-sync scripts (`Publish-InfraPortal-PromotionCandidates.ps1`,
`Sync-InfraPortalPromotions.ps1`) and the staging deploy workflow must switch any
read of `stable/` / `main/` to the registry/candidate references before those
directories are removed (inventory in Phase 0).

---

## 4. Decision log

### 4.1 Resolved

| # | Decision | Resolution | Where |
|---|---|---|---|
| D1 | How InfraPortal learns about builds | **New build registry** — `builds` table + `POST /api/builds`; ALL published builds registered (main, release, feature) | §3.1 |
| D2 | Feature-branch publish opt-in | **No parameter** — publish condition is `master OR release/* OR Build.Reason == Manual`; queueing the pipeline *is* the intent. PR builds never publish | §3.4 |
| D3 | "Deploy this build" mechanism | **PromotionCandidate from synthetic source env `build`** — NOT Manual Deployment (which records *was released*; a candidate records *should be released* and brings approval/supersede/reconciliation for free) | §3.2 |
| D4 | Source-env semantics | Reuse `SourceRequiresDeploy = false` + `TracksWorkItems = false` — both documented for exactly this scenario; no new concepts | §3.2 |
| D5 | Auto-deploy of main to DEV | **Policy-driven**: `PromotionPolicy.AutoCreateFromBranchesJson` on the `build → dev` edge + `BuildIngestHook`; feature branches require an explicitly created promotion | §3.2 |
| D6 | Manifest storage | **ACR OCI artifact** (prefer `oras attach` referrer on the helm chart; fallback sibling repo), digest-pinned; blob upload dual-published during transition only | §3.3 |
| D7 | How InfraPortal gets the manifest | **Inline in the registration POST** — InfraPortal holds its own copy, needs no ACR/storage credentials | §3.1 |
| D8 | `import-build` (pipeline 5617) | **Retired** — replaced end-to-end by the `build → dev` promotion policy | §3.3, §6 |
| D9 | mpt-release git semantics | **Commit-on-deploy** — manifest committed by the deploying workflow; git history = deployment ledger only | §3.5 |
| D10 | `stable` landing zone | **Retired** — env removed from InfraPortal, `stable/` + `main/` directories removed from mpt-release; edges become `build → {dev,test,staging}`, `staging → production` | §1, §6 |
| D11 | Registration failure mode | **Fail the pipeline** (no `continueOnError`) — an unregistered build silently never deploys | §3.3 |
| D12 | Dispatch latency for auto edges | `ApprovedWebhookOptions` delay ≈ 0 for auto-approved `build → dev` | §3.2 |
| D13 | Candidate references | **Copied from the manifest at creation** — gives `build → staging` work-item gating without extra plumbing | §3.2 |

### 4.2 Open questions

| # | Question | Notes |
|---|---|---|
| OQ1 | Version ordering across branches | `PromotionVersionOrder` orders `5.0.347-g<sha>` fine, but main and feature builds sharing one counter sequence can supersede each other's candidates on the same edge in surprising order. Options: accept (DEV is ephemeral), or add a branch-identifying token to feature build numbers (must keep the numeric prefix parseable, e.g. `5.0.347-mpt1234-g<sha>`). Decide before Phase 3. |
| OQ2 | Which app repos have CI triggers on feature branches | Inventory during Phase 0; D2's condition assumes feature publishes are manual-queue only. |
| OQ3 | Retention windows | Feature-branch `builds` rows (suggest 30–60 days), ACR `acr purge` tag filter for feature tags, and whether pruning cascades to open candidates (suggest: superseding/closing any open candidate whose build is pruned). |
| OQ4 | Other consumers of the `changelogs` blob container | Grep mpt-release + reporting tooling for `nltapp0762sda` / `changelogs` before removing the blob upload (Phase 0 inventory; blocks §6 cleanup, not rollout). |
| OQ5 | Recovery when InfraPortal or a dispatch is down mid-release | InfraPortal is now on the critical path for DEV deploys. Existing pieces: webhook delivery retries + records, `reconcile-completions` sweep. Confirm/re-fire of a candidate's `promotion.approved` must be a supported admin action. |
| OQ6 | `build → test` approval ceremony | Auto-approve vs one approval step — per-product decision at seed time. |

---

## 5. Rollout & transition plan

Each phase is independently shippable and reversible; the old path keeps working
until Phase E per product.

**Phase 0 — Inventory (no code).**
Grep mpt-release + tooling for `changelogs`/`nltapp0762sda` (OQ4) and for readers
of `stable/`/`main/` directories; check `trigger:` blocks of the 8 app repos
(OQ2); confirm feature build-number scheme vs `PromotionVersionOrder` (OQ1).

**Phase A — InfraPortal build registry** *(InfraPortal repo)*.
`builds` table, `POST /api/builds` + `build:register` scope, read endpoints,
minimal list UI. No behavior change anywhere else. Useful alone: answers "what
builds exist, from which branch".

**Phase B — Templates publish + register** *(templates repo; non-breaking)*.
ORAS push (dual-publish with blob), registration POST (fail-loud), all behind
existing flow — import-build still fires. App repos add `Build.Reason == Manual`
to their publish condition (can trail; enables feature registration per repo).
Exit criterion: every main build appears in the registry with a valid digest.

**Phase C — InfraPortal promotion surface** *(InfraPortal repo)*.
`AutoCreateFromBranchesJson` + `BuildIngestHook`, seeded `build → *` policies
(auto-create initially **disabled**), UI picker, webhook payload carries OCI
ref/digest, `ApprovedWebhookOptions` per-edge delay. Feature flag
`features.promotions` on for pilot products.

**Phase D — mpt-release dispatch workflow** *(mpt-release repo)*.
Deploy-from-dispatch with commit-on-deploy, validated on one pilot
product/service via a *manually created* `build → dev` candidate while
import-build still runs for everything else. Parallel-run: verify the two paths
produce identical dev-directory commits and deploy events.

**Phase E — Cutover (per product).**
Enable `AutoCreateFromBranches` on `build → dev`; flip `useBuildRegistry: true`
in the product's pipelines (stops the import-build trigger); switch any
`stable/` readers for that product to registry/candidate references. Watch one
full main-merge cycle. Rollback = flip the parameter back.

**Transition invariants:** dual-publish keeps the blob path alive for anything
not yet inventoried; import-build and the dispatch path never run for the same
product simultaneously (the `useBuildRegistry` parameter is the single switch).

---

## 6. Post-transition cleanup plan

Run after **all** products are cut over and one release cycle has passed with no
fallback. Tracked as its own ticket batch — this is the part that otherwise never
happens.

**Templates repo:**
- Remove the blob-upload branch of the dual-publish (after OQ4 confirms no
  remaining consumers) and the `CPA (Test and Development)` service-connection
  reference from `publish-steps.yaml`.
- Remove the import-build trigger step and the `useBuildRegistry` parameter
  (registry path becomes the only path).

**mpt-release repo:**
- Delete the `import-build` pipeline (5617), `pipelines/metadata-import/`
  (`Import-BuildMetadata.ps1` et al.).
- Delete `stable/` and `main/` directories (archive history stays in git).
- Remove/simplify `Publish-InfraPortal-PromotionCandidates.ps1` /
  `Sync-InfraPortalPromotions.ps1` where their job is now done by
  `BuildIngestHook` + manifest-copied references.

**InfraPortal repo:**
- Remove the `stable` environment from env display config, seeds, and any
  policies/dashboards referencing it; delete or archive historical `stable`
  deploy events per retention policy (decide: keep as history vs purge).
- Remove any transition-only compatibility (e.g. accepting blob URLs in
  `build-manifest` references for new events).
- Turn on retention pruning for feature-branch `builds` rows (OQ3).

**Azure:**
- `acr purge` schedule for feature-branch tags + manifests.
- Decommission the `changelogs` container (after OQ4) and the storage-account
  role assignment held by the build SPN.

**Docs:** update `notes/deployment-ingest-api.md` (build registry section),
`README.md` (sync script scope), and the promotion docs to describe the `build`
source env.

---

## 7. Acceptance scenarios

1. **Main auto-deploy parity:** merge to main on a pilot repo → build publishes,
   registers, candidate auto-created/approved on `build → dev`, mpt-release
   deploys + commits manifest, deploy event closes the candidate. Dev directory
   commit content identical to what import-build would have produced.
2. **Feature build, happy path:** manually queue a feature-branch build → build
   registered, **nothing deploys**. Pick it in the UI for DEV → candidate →
   dispatch → deployed; DEV view shows the feature branch badge. Next main merge
   auto-deploys and overwrites; the feature candidate closes as
   Deployed-then-superseded per existing reconciliation.
3. **PR build:** PR validation run publishes nothing and registers nothing.
4. **Registration outage:** InfraPortal down during a main build → publish stage
   **fails visibly** (D11); re-run of the stage after recovery registers and
   deploys (idempotent re-POST).
5. **Staging gate:** `build → staging` candidate carries work items copied from
   the manifest; gating behaves as with today's staging flow.
