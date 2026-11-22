# Declared, applied and observed versions — implementation plan

> **Status:** draft for review, 2026-11-22. Decision D-29 of the "deployments and releases" brief:
> requirement W-16, logic L-21, API P-17, screen E-22. Governed by
> `docs/adr/0009-the-portal-compares-declared-applied-and-observed-versions.md` (ADR 0009), which
> amends `docs/adr/0004-the-portal-reads-published-artifacts.md`. Supersedes the earlier drafts of
> this plan, in which the service catalog's reconciler compared and the portal only displayed.

**Goal:** For every service and environment, show three versions — **declared**, **applied** and
**observed** — and mark the two differences between them: **Rollout pending** (declared ≠ applied)
and **Drift** (applied ≠ observed), each with the time it began. Today the portal shows one version
per cell and can tell neither.

**Architecture:** The portal is the one place that compares (ADR 0009).

- **Applied** is the portal's own newest succeeded deploy event, in both deployment models.
- **Observed** is pushed by a **cluster agent**: a job in each cluster that every minute lists the
  workloads of the service namespaces — with the image digests actually running — the static
  front-end sites in blob storage it is configured for, and the cluster's namespaces, and posts the
  whole list to a new ingest endpoint. The portal keeps the current list per cluster in tables.
- **The comparison is on content, not only on the version string.** The owner's main goal for
  "observed" is to detect that someone replaced something by hand: the same version with another
  image digest, or a changed file in a static site, is Drift.
- **Declared** arrives in the `versions.declared` list of the service catalog's status artifact,
  which the portal already fetches as the `status` feed of the service inventory.
- A pure comparer turns the three into two states per service and environment; a small table keeps
  since when each difference has existed. On clusters with Flux, notifications the portal drops
  today are kept as signals that explain a difference.

**Tech stack:** ASP.NET Core net10.0 Minimal API + EF Core (Postgres and SQL Server migrations),
xUnit + NSubstitute; React 19 + TypeScript; the agent is a .NET console project in a container, run
as a Kubernetes CronJob.

**Spec:** the task brief (D-29; D-19 "the portal gates nothing"), ADR 0009, and — for the declared
list only — the service catalog's change spec for versions in its status file.

**Not in this plan:** the **Services** page itself (E-21, Task 8a of
`docs/plans/service-list-and-catalog-product.md`); this plan adds markers and a filter to it.
Infrastructure state (namespaces, identities, databases) stays in the **Service inventory** section.

## The three versions, in the portal's words

| Term | Meaning | Source | Arrives by |
|---|---|---|---|
| **Declared version** | What the release repository says the environment should run (`mkp-release` in the push model, `ops-release` in the pull model). | the service catalog's status file, `versions.declared` | pull, the existing `status` feed |
| **Applied version** | What was last installed successfully. | the portal's newest **succeeded** deploy event of the service in that environment | already in the portal |
| **Observed version** | What the service's workloads actually run. | the cluster agent's last report | push, new endpoint |

| Difference | Rule | Typical cause |
|---|---|---|
| **Rollout pending** | declared ≠ applied | an approved promotion whose deploy has not finished; a commit to the release repository outside a promotion; a stopped or suspended deployer |
| **Drift — other version** | applied version ≠ observed version, outside a rollout | an install that failed half-way; a deploy that was never reported to the portal; a manual change of the image tag |
| **Drift — content changed** | same version, but the running image digest or the static site's content fingerprint is not the one that was deployed | an image pushed again under the same tag; a file replaced by hand in a storage container |

Equality is the portal's rule (ADR 0009): surrounding whitespace removed and one leading `v` or `V`
dropped when a digit follows, then an ordinal comparison. No ordering, no release line, no dropping
of build metadata: the portal says "different", never "newer".

### What the portal shows today

- One version per service and environment: the **newest deploy event** of that cell, whatever became
  of it (`DeploymentService.LatestStateEvents`). A failed or still-running deploy is shown with its
  own version, so the cell is "what was last tried", not "what is applied". A cell whose newest event
  is `removed` is dropped.
- It knows only what was reported to it. In the pull model `FluxEventMapper` records `HelmRelease`
  install outcomes and answers everything else `202` and drops it — other kinds, progress,
  dependency waits, drift detection, test results — so a suspended release, a reconciliation that
  silently restored a changed release, and a change made directly in the cluster or in the release
  repository leave no trace.
- The only "something is on its way" signal is the promotion chip: `ProductDeploymentsPage` marks a
  cell with a **Pending** promotion (awaiting approval); a promotion that is `Approved` or
  `Deploying` is not marked on the matrix.
- The status file carries no version data today and the portal has no ingest endpoint for state.

## Global constraints

- **The portal reads no cluster and no release repository**, and makes no new outbound call.
  Observed state is pushed to it; declared versions come through the existing feed (ADR 0004).
- **One comparer.** `VersionComparer` is the only code that compares versions, and the only
  equality rule is the one above. The API, the MCP tools and the web display its result; none of
  them compares.
- **Unknown is never in sync.** A cluster that has not reported recently, a declared entry the
  catalog could not read, a workload without a version and a status feed without the list each give
  **unknown** or **not compared** — never in sync, never "not deployed".
- **Nothing is gated on these values** (D-19, ADR 0009). No promotion, approval gate, freeze,
  release check, rollback or webhook reads them. The comparer is referenced only by read endpoints,
  MCP read tools and its own evaluator.
- **Current state only.** Observed workloads and difference states are not history. Dropping both
  tables loses at most a minute of observed state and the age of open differences.
- **The agent is read-only**: in the cluster `get`/`list` on `deployments`, `statefulsets`,
  `daemonsets` (`apps`), `cronjobs` (`batch`), `pods` (for the running image digest only) and
  `namespaces`; on storage, a blob data reader role on the configured accounts, through the
  cluster's workload identity. No access to `secrets`, no write verb, no exec, no logs, no account
  keys, and it never downloads a blob.
- **The agent does not decide which environment a workload is in.** The cluster and namespace are
  mapped to an environment in the portal. A static site's environment is named in the agent's
  configuration (a storage account belongs to one environment) and is accepted only when the
  reporting cluster is mapped to that environment.
- **One ingest contract.** Workloads, static sites and namespaces are item kinds of one report to
  one endpoint; the later controller uses the same.
- **Wording.** The first difference is labelled **Rollout pending**, never "Pending": **Pending**
  means a promotion awaiting approval (`GLOSSARY.md`, `PendingPromotionChip`).
- Own feature flag, `features.versionState` (`FeatureFlagKeys.VersionState`, seeded off by
  `FeatureFlagSeeder`, `Features:VersionState:DefaultEnabled`), checked server-side on the read
  endpoints. The ingest endpoint is not behind the flag (as the service list keeps filling with its
  flag off), so turning the flag on shows data at once.
- Portable SQL only; `""` instead of NULL in keys (ADR-0003); every schema change ships a Postgres
  and a SQL Server migration (`Migrations/Postgres`, `Migrations/SqlServer`).
- Public repository: fixtures, examples and tests use the placeholder vocabulary only (`mkp`,
  `mkp-extension-adobe`, `mkp-spotlight`, `mkp-release`, `ops-release`).
- The web app has no test runner; web tasks are verified by `npx tsc -b`, the build and the manual
  checklist in Task 12.

## Decisions

1. **Tables for the current observed state**, one row per cluster × namespace × workload, per
   static site and per namespace. Unlike the earlier drafts tables are needed: the data is pushed,
   belongs to no artifact, and is read on every matrix request.
2. **No history.** Each report replaces the cluster's rows. The age of a difference is kept in a
   second small table, one row per service × environment, also current only. Reasons: the questions
   this feature answers are about now; history of versions already exists as deploy events; a
   minute-by-minute log of every workload is 1,440 reports a day per cluster for no reader. Decided
   2026-11-22: the portal keeps the current state only. If a record is wanted later, an audit entry
   per drift (written by `VersionStateEvaluator` when a difference begins and when it ends, with the
   versions it already holds) can be added without changing any table planned here.
3. **A row is written only when it changes.** A report that matches the stored rows updates one row
   — the cluster's last-report time — not one per workload.
4. **`since` is the portal's**, stored when a difference first appears and cleared when it ends. It
   does not move while the difference persists, even when the versions change.
5. **Rolling out is not Drift.** While the cluster itself reports a rollout in progress, or within a
   grace period after a deploy event, a difference is shown as **Rolling out**. Defined in "The
   comparison".
6. **Environment mapping lives in the portal**, in the stored settings next to environments and
   their aliases, edited on **Settings → Environments**.
7. **The matrix stays one version per cell**; markers are added and the three versions are one click
   away.
8. **Drift does not depend on the catalog or on stage 1**; Rollout pending depends on the catalog
   publishing `versions.declared`, not on a service being in the catalog.
9. **The applied content identity travels with the deploy event**, as a new optional `artifacts`
   list. The build registry cannot supply it: its `artifactDigest` (`Build.ArtifactDigest`) is the
   digest of the build-metadata manifest artifact the deploy workflow pulls, not of a container
   image, and the portal does not interpret `ManifestJson`. Where a deploy event carries no
   artifacts, versions alone are compared and the panel says so.
10. **A kept declared version is shown, not compared.** An entry in state `unknown` that still
    carries its last version gets no marker; the version is shown muted as "last read".

## Review focus

1. **Unknown never reads as in sync.** A stale cluster, an unmapped namespace, a workload without a
   version, an environment no cluster covers, a declared entry in state `unknown`, a status feed
   without the list, the inventory flag off — each has a test and a defined display. → Tasks 3, 5, 6.
2. **Full-snapshot semantics cannot delete what was not listed.** A report removes rows only in the
   namespaces it says it listed; an agent that failed on one namespace must not empty it. An older
   report arriving late must not overwrite a newer one. → Task 2 tests.
3. **Who may post.** A key without the `observed:report` scope is refused even if it lists no
   scopes; a key is refused for a cluster outside its `AllowedClusters`; a personal access token is
   refused. → Task 2 tests.
4. **Rolling out versus Drift.** A normal deploy must not flash Drift; a manual image change must
   show Drift without waiting out a grace period. → Task 5 tests.
5. **Nothing gated.** `VersionStateReader` is injected only into `DeploymentEndpoints` and
   `Mcp/DeploymentTools`; nothing under `Features/Promotions`, `Features/Releases` or
   `Features/Rollbacks` references it or `VersionComparer`.
6. **Backward compatibility.** `GET /api/deployments/state` (both views) and the service detail
   return today's rows plus one field that is `null` with the flag off. A status file without
   `versions` is accepted as today; `schemaVersion` stays 1. Flux install outcomes are ingested
   exactly as today. → Tasks 1, 6, 8.
7. **Write load.** One report per cluster per minute must cost one read and, when nothing changed,
   one row update; it must not take a lock the matrix read waits on.
8. **Content identity.** Same version and another digest is Drift — content changed; a missing
   applied digest falls back to versions and says so, and is never reported as "content matches";
   the fingerprint of a static site is byte-for-byte the same in the pipeline and in the agent
   (shared test vectors). → Tasks 5, 9.
9. **The join.** `stage` → `staging`; the pull-model `dev`; a name that differs by case, `.` or `_`;
   a retired environment; a service running in two clusters of one environment; one name under two
   products. → Tasks 3, 5 tests.

---

## Design

### Declared versions from the status file

The fixed shape (the catalog publishes it; the block is absent until its reconciler runs with its
versions flag):

```jsonc
"versions": {
  "sources": { "mkp-release": { "read_at": "2026-11-22T10:07:03Z", "commit": "9f2c1ab04d" } },
  "declared": [
    { "name": "mkp-extension-adobe",      // component name in the release repository
      "service": "mkp-extension-adobe",   // catalog id, or null when the component is not in the catalog
      "environment": "stage",             // catalog environment key
      "source": "mkp-release", "path": "staging/mkp-extension-adobe/build-metadata.yaml",
      "state": "known",                   // known | unknown
      "version": "1.4.0",                 // null when unknown
      "since": "2026-11-22T09:37:02Z" }
  ]
}
```

One entry per component × environment for **every** component in the release repositories, in the
catalog or not, sorted by name, environment, source. The pull-model repository's `staging` folder is
published as environment `dev`; the portal takes the published key as it is. `service` is set when
the catalog matched the component by its id or by its repository name. Reading rules, as the catalog
states them:

- **No entry**, for a source that has a `sources` key: nothing is declared.
- **No `sources` key** for a repository: it has not been read yet — *not* "nothing declared".
- **A repository that could not be read** keeps its previous entries unchanged (still `known`) and
  its `read_at` and `commit` do not move. So the age of a declared value is its source's `read_at`
  against the file's `generated_at`, not the entry's state.
- **`state: unknown`**: that one file is present and could not be read or has no version. `version`
  is the last version read, or `null`; `since` is `null` when `version` is.

**Reader.** `Documents/Documents.cs`: `StatusDocument.Versions` (`StatusVersions?`) with
`StatusVersionSource` and `StatusDeclaredVersion`; only `name` and `environment` are non-nullable
(the reader runs with `RespectNullableAnnotations`, and `service`, `version` and `since` are
published as `null`). `DocumentReader.ReadStatus`: an absent block is accepted as today; a null entry in
`declared`, or an entry without `name` or `environment`, rejects the file whole like any other list
(ADR 0004); a `state` the portal does not know reads as `unknown`; unknown fields are ignored;
`SupportedVersions` unchanged. Nothing is projected: `InventoryProjector` keeps returning
`ProjectedRows.Empty` for a status file and the list is read from the parsed document cached per
snapshot id (`InventoryReadService.SnapshotDocumentAsync`).

**Join** (new `Features/VersionState/DeclaredVersions.cs`, built once per request from the cached
document):

- service: `service` when set, else `name`, in normal form — stage 1's `CatalogName.Normalize`
  (trim, lower-case, `.` and `_` → `-`); until stage 1 merges, trim and lower-case as
  `ServiceInventoryEndpoints.Key` does;
- environment: `EnvironmentAliasMap.Resolve` of the published key, so `stage` lands on `staging`;
  entries in a retired environment (`EnvironmentAliasResolver.RetiredKeysAsync`) are left out;
- **two entries for one (service, environment)** — the component is in both release repositories.
  The portal takes the entry of the repository that matches how the cell is deployed: when the
  cell's newest deploy event came through Flux (`run.provider` is `flux`,
  `FluxEventMapper.RunProvider`), the entry whose `source` is listed in
  `Deployments:VersionState:PullModelSources`; otherwise the entry whose source is not. When that
  does not leave exactly one — no deploy event, the setting empty, both on one side — both are shown
  in the panel, the rollout state is **unknown** and there is no marker;
- **a kept version** (`state: unknown` with a version): declared is `unknown`, the rollout state is
  unknown, no marker; the version is shown muted as "last read 1.3.2 — the file could not be read";
- **staleness**: a source whose `read_at` is older than the file's `generated_at` was not read in
  the last run. Its entries stay `known` and are compared, and are shown with "release repository
  last read <time>" and the marker in the muted "may be out of date" style;
- **a source without a `sources` key**: the repositories the installation expects are listed in
  `Deployments:VersionState:DeclaredSources`. While one of them has no key, a pair with no entry is
  **unknown** ("a release repository has not been read yet"), not `none`. With the setting empty the
  portal expects exactly the sources present.

Declared is **not available** (Rollout pending is not compared, anywhere) when
`features.serviceInventory` is off, no status snapshot exists, or the snapshot has no `versions`.

### The observed-state ingest

**`POST /api/deployments/observed`** — in `DeploymentEndpoints`, next to `POST /events`, with the
same `.RequireAuthorization(ApiKeyAuthHandler.PolicyName)` and
`.RequireRateLimiting(DeploymentIngestionRateLimit.PolicyName)`.

```jsonc
{
  "cluster": "staging-weu-1",
  "reportedAt": "2026-11-22T10:07:00Z",          // the agent's clock when it finished listing
  "agent": { "version": "1.0.0", "mode": "job" },  // "controller" for step 2
  "complete": {                                  // what this report is the whole truth for
    "namespaces": true,                          // the namespace list was read
    "workloadNamespaces": ["mkp-staging", "mkp-extensions-staging"],
    "staticSiteAccounts": ["mkpwebstaging"]
  },
  "items": [
    { "kind": "workload",
      "namespace": "mkp-staging", "workloadKind": "Deployment", "name": "mkp-extension-adobe-api",
      "service": "mkp-extension-adobe",          // value of the service label, null when unlabelled
      "version": "1.4.0",                        // label app.kubernetes.io/version, null when absent
      "containers": [
        { "name": "api", "image": "registry.example.com/mkp-extension-adobe:1.4.0",
          "digests": ["sha256:9c1e…"] }          // distinct imageID digests of the running pods; two during a rollout
      ],
      "replicas": 3, "updatedReplicas": 3, "readyReplicas": 3,
      "suspended": false },                      // CronJob spec.suspend; Flux releases, see below

    { "kind": "static-site",
      "account": "mkpwebstaging", "container": "mkp-spotlight",
      "service": "mkp-spotlight", "environment": "staging",   // from the agent's configuration
      "fingerprint": "sha256:5b0f…",             // see "Static sites"
      "blobCount": 412, "blobsWithoutMd5": 0,
      "lastModified": "2026-11-21T16:02:11Z" },  // newest blob

    { "kind": "namespace", "name": "mkp-staging", "labels": { "service-namespace": "true" } }
  ]
}
```

- **Auth.** `ApiKeyEntry` gains `AllowedClusters` (claim `allowed_cluster`) and `ApiKeyScopes` gains
  `ObservedReport = "observed:report"`. The endpoint requires the scope **explicitly**: a key that
  lists no scopes is refused (`403`), unlike `ApiKeyAuthHandler.HasScope`, whose "no scopes = all" rule exists for keys that
  predate scopes — this endpoint has none. The request's `cluster` must be in the key's
  `AllowedClusters` (no "empty = all"). `AllowedProducts` is not consulted: a cluster is not a
  product, and the service label decides the service. Personal access tokens are refused, as on
  `/events` (`auth_method` must be `api_key`). Recommended: one key per cluster with exactly this
  scope, so a leaked key can only misreport one cluster's versions.
- **Full snapshot, per scope.** `complete` says what the report is the whole truth for. For each
  namespace in `workloadNamespaces`, stored workloads of that cluster and namespace that are not in
  `items` are deleted — the workload is gone. The same for the static sites of each account in
  `staticSiteAccounts`, and for the namespace list when `namespaces` is true. Anything not named is
  left untouched, so an agent that could not list one namespace or one storage account omits it and
  loses nothing. An item outside the scopes the report names is a `400`. An item of a `kind` the
  portal does not know is counted and skipped, so a newer agent can talk to an older portal.
- **Idempotency and order.** `reportedAt` orders reports per cluster. A report not newer than the
  stored `LastReportedAt` is answered `200 { "applied": false, "reason": "older than the stored
  report" }` and changes nothing; a retry of the same report is therefore harmless. A `reportedAt`
  more than five minutes in the future is a `400`.
- **Limits.** Body up to 4 MiB (`413` beyond), 10,000 items, strings clipped to their column
  length, at most 20 labels per namespace. A cluster with 2,000 workloads is about 900 KB.
- **Rate.** The ingest limit is 120 requests a minute per key name
  (`DeploymentIngestionRateLimitOptions.AuthenticatedPermitLimit`, partitioned by key in
  `DeploymentIngestionRateLimit`). One report a minute per cluster with its own key uses 1 of 120.
  The step 2 controller must batch to stay under it (at most one post every few seconds).
- **Response.** `200 { "applied": true, "workloads": 212, "staticSites": 6, "namespaces": 31,
  "changed": 3, "removed": 1, "skipped": 0,
  "unmapped": { "namespaces": ["kube-tools"], "unlabelled": 4, "staticSites": [] } }` — the agent logs it, so a
  mapping gap is visible on the cluster side too. `400` with `errors[]` for a malformed report.
- **After storing**, the evaluator runs for the (service, environment) pairs whose observed value
  changed (see "The comparison").

### Storage

Five small tables, all current state only. Entities under `Features/VersionState/Models`, `DbSet`s on
`PlatformDbContext`, migration `AddVersionState` in both sets.

**`observed_clusters`** — one row per reporting cluster.

| Column | |
|---|---|
| `Cluster` PK | as sent, lower-cased |
| `LastReportedAt` | the report's `reportedAt` |
| `LastReceivedAt` | the portal's clock; staleness is judged on this one |
| `AgentVersion`, `AgentMode`, `KeyName` | who reports |
| `WorkloadCount`, `UnmappedCount` | for the admin view |

**`observed_workloads`** — one row per cluster × namespace × workload.

| Column | |
|---|---|
| `Cluster`, `Namespace`, `Kind`, `Name` | PK |
| `Service` | the service label in normal form, `""` when unlabelled |
| `Version` | the version label; else the image tag when the image is not pinned by digest; else `""` |
| `ContainersJson` | per container: name, image, image repository, the distinct running digests. Opaque to SQL, read in memory |
| `Replicas`, `UpdatedReplicas`, `ReadyReplicas`, `Suspended` | |
| `ContentSince` | when this row first had this set of digests |
| `VersionSince` | when this row first had this `Version` (the report's `reportedAt`) |
| `FirstSeenAt`, `ChangedAt` | |

Index `(Service)` for the read. The environment is **not** stored: it is resolved from the mapping
when read, so a mapping change takes effect at once and needs no rewrite.

**`observed_static_sites`** — one row per cluster × storage account × container: `Service`,
`Environment` (as configured in the agent, canonicalised), `Fingerprint`, `BlobCount`,
`BlobsWithoutMd5`, `LastModified`, `FingerprintSince`, `FirstSeenAt`, `ChangedAt`.

**`observed_namespaces`** — one row per cluster × namespace: `LabelsJson`, `FirstSeenAt`,
`ChangedAt`. Stored and exposed read-only; the portal itself uses it only to show which namespaces
of a cluster are mapped.

**`deploy_events.ArtifactsJson`** — a new nullable column: the content identities the deploy
delivered (see "Content identity").

**`version_states`** — one row per service × environment that has, or had until its last
evaluation, a difference.

| Column | |
|---|---|
| `Service`, `Environment` | PK; normal-form name, canonical environment |
| `RolloutPendingSince` | null when declared = applied |
| `ObservedDiffSince` | null when applied = observed; when a difference between them was first seen |
| `ObservedDiffKind` | `""`, `version` or `content` |
| `DeclaredVersion`, `AppliedVersion`, `ObservedVersion` | what was compared at the last evaluation, for the audit of a surprising marker |
| `EvaluatedAt` | |

Size: 361 services × 4 environments × about 1.5 workloads ≈ 2,200 rows of `observed_workloads`, a
few hundred bytes each — under 1 MB, and it does not grow with time. `version_states` is at most one
row per cell. Writes: per report one indexed read of the cluster's rows, an update of the rows that
changed (usually none), and one update of `observed_clusters`.

**Staleness.** A cluster whose `LastReceivedAt` is older than
`Deployments:ObservedState:StaleAfterMinutes` (default 5 — five missed reports) is **stale**: every
observed value from it reads as **unknown** and its rows are shown with "last reported <time>". It is
judged when read, from the clock, so no job has to run for a silent cluster to turn unknown. Rows of
a stale cluster are kept (the next report repairs them); an admin can remove a cluster that is gone
for good.

### From a workload to a service and an environment

**Service.** The agent sends the value of the first of its configured service labels the workload
carries — by default `app.kubernetes.io/part-of`, then `service`, the labels the service catalog's
tagging rule already requires. The portal normalises it and treats it as the service name. A
workload with no such label is **unlabelled**: stored, counted, listed for admins, joined to nothing.
The portal does not guess from the workload's name.

**Environment.** New stored setting on `AppSettingsDto`, optional like `Analytics`:

```jsonc
"clusters": [
  { "name": "staging-weu-1", "environment": "staging",          // default for the cluster
    "namespaces": [ { "pattern": "*-dev", "environment": "dev" } ] }   // exceptions, first match wins
]
```

Edited on **Settings → Environments** (`EnvironmentsSettings.tsx`, a **Clusters** block), stored and
validated by `AppSettingsService` (the environment must be a configured, not retired, key). A
namespace pattern is tried first, then the cluster's default. What is **unmapped**:

- a cluster with no entry — all its workloads; the report is still stored, and the cluster shows up
  on the settings block as "reporting, not mapped" so an admin can map it in one step;
- an unlabelled workload;
- a workload whose service the portal has never seen — kept, and offered on the Services page as
  "running, no deploy reported" once it is mapped to an environment.

Unmapped workloads never produce a marker. `GET /api/deployments/observed/clusters` (admin) lists
clusters with their last report, mapping, counts and the unmapped namespaces and workloads.

A service may run in several clusters of one environment (two regions); its observed value is taken
over all of them, and if any of those clusters is stale the value is unknown.

### Content identity: detecting a replacement by hand

A version string says what was meant to run; a digest says what does. The comparison therefore uses
both where both are known.

**Observed side.** For a workload the agent reports, per container, the image and the distinct
digests of the running pods, read from the pod status (`imageID`) — the digest the node actually
pulled, whatever the tag says. For a static site it reports the content fingerprint.

**Applied side.** `CreateDeployEventDto` gains an optional `artifacts` list, stored in
`DeployEvent.ArtifactsJson`:

```jsonc
"artifacts": [
  { "kind": "image", "name": "registry.example.com/mkp-extension-adobe", "digest": "sha256:9c1e…" },
  { "kind": "static-site", "name": "mkp-spotlight", "digest": "sha256:5b0f…" }
]
```

`name` is the image repository without tag, or the storage container. The deploying pipeline knows
both: it resolved the tag to a digest to deploy, or computed the fingerprint of what it uploaded.
`product`, `service`, `version` and every other field stay as they are; the field is optional, and
an event without it is today's event.

What the portal already holds does not give the image digest (verified in code): the build
registry's `artifactDigest` is the digest of the build-metadata manifest artifact in the registry,
and `ManifestJson` is stored verbatim and not interpreted. Flux notifications carry no digest
either. So for an event without `artifacts` the applied digest is **not known** (Open point 4 lists
the two ways to fill the gap without touching every pipeline).

**Rule.** After the version comparison says the versions are equal:

- every applied `image` artifact is matched to the observed containers with the same image
  repository (lower-cased, tag and digest removed). All their running digests equal the applied
  digest → content matches. Any other digest → **Drift — content changed**. An applied image with
  no observed container of that repository is ignored (an init or job image);
- no applied artifact for the service → the result is "versions match; content not compared — the
  deploy did not report a digest". It is `in_sync` with `comparedBy: "version"`, and the panel says
  so; it is never shown as "content matches".

A version difference is **Drift — other version** regardless of digests. Rolling out applies to
both kinds: two digests of one container at once is a rollout in progress, like two versions.

### Static sites in blob storage

Front-end apps are deployed as files: one storage account per environment, each app in its own
container. They have no workload, so the cluster agent cannot see them through the Kubernetes API;
the same job lists the containers it is configured for and reports each as an item of kind
`static-site`.

**Fingerprint — the exact algorithm**, so the pipeline and the agent agree:

1. List every blob of the container: current blobs only — no snapshots, no previous versions, no
   soft-deleted blobs. **Nothing is excluded**: a `version.json` or a build stamp the pipeline
   writes is part of what was deployed and is in the fingerprint on both sides.
2. For each blob take its full name (the path, exactly as stored, UTF-8) and its `Content-MD5` as
   the listing returns it (base64). A blob without a `Content-MD5` contributes `-`.
3. Sort by name, ordinal on the UTF-8 bytes.
4. Concatenate one line per blob: `<name>` TAB `<md5>` LF.
5. The fingerprint is `sha256:` followed by the lower-case hex SHA-256 of those bytes. An empty
   container has the fingerprint of the empty string.

The listing returns the MD5; nothing is downloaded. Metadata, content type, cache headers and
modification times are not part of it: re-uploading identical files does not change the
fingerprint, changing one byte or adding, removing or renaming one file does. The pipeline must
upload with content MD5 set (large files uploaded in blocks do not get one by default); while
`blobsWithoutMd5` is not zero the comparison for that site is **unknown** — "content hashes
missing" — because a changed file would not show.

**Applied.** The deploying pipeline computes the fingerprint over what it uploaded (by the same
listing, after the upload) and sends it as the `static-site` artifact of the deploy event.
`src/Platform.ClusterReporter` ships the algorithm as a small command (`fingerprint`) and as a
reference script, with test vectors, so pipelines do not reimplement it.

**Comparison.** Applied fingerprint against observed fingerprint: equal → in sync; different →
**Drift — content changed**. No applied fingerprint → not compared, with the reason. A static site
has no observed version; the Observed row shows the fingerprint (shortened), the blob count and the
newest modification time.

**An upload in progress.** While the pipeline uploads, the container no longer matches the last
deploy event and the new event has not arrived. A difference is shown as **Rolling out** while the
container's `lastModified` is younger than `Deployments:VersionState:StaticSiteGraceMinutes`
(default 2 — decided) or the applied deploy event is. Unlike a workload, a storage container gives no signal
that tells an upload from a manual change, so a file replaced by hand is also "Rolling out" for
that period and Drift after it, with `since` at the first sight of the difference. That delay is
the price of not flagging every deploy.

**Configuration.** In the agent (chart values): a list of
`{ account, container, service, environment }`, or per account `{ account, environment }` with
"every container is a site and its name is the service". The portal accepts a site only when the
reporting cluster is mapped to that environment (cluster default or any namespace pattern);
otherwise the item is stored as unmapped and compared with nothing.

**Rollout notes — prevention, not portal work.** Detection is the second line. Before or with the
first agent installation, on each of these storage accounts: (1) write access to the containers
only for the deploying pipeline's identity, with account keys and shared access signatures
disabled, so that a manual change needs a role assignment someone can see; (2) blob versioning on,
so that a detected change can be inspected and the previous content restored. The agent's identity
gets the blob data reader role only.

### Namespaces

Each report carries the cluster's namespaces with their labels (`kind: namespace`). The portal
stores them (`observed_namespaces`), shows them on **Settings → Environments** next to the mapping,
and serves them read-only. This is decided (ADR 0009): the service catalog's reconciler gets no
cluster access at all and takes its "namespace exists" onboarding observation from the portal.

**`GET /api/deployments/observed/namespaces?cluster=`** — an ordinary API key or a signed-in user
(the deployments group's read policy); no special scope; not behind `features.versionState`, since
the consumer is another system and not a portal page.

```jsonc
{
  "clusters": [
    { "cluster": "staging-weu-1",
      "reportedAt": "2026-11-22T10:07:00Z",     // the agent's clock, of the last report that listed namespaces
      "receivedAt": "2026-11-22T10:07:02Z",
      "stale": false,                            // true: no report for StaleAfterMinutes — treat every answer as unknown
      "staleAfterMinutes": 5,
      "namespaces": [ { "name": "mkp-staging", "labels": { "service-namespace": "true" }, "firstSeenAt": "…" } ] }
  ]
}
```

Without `cluster`, every reporting cluster. A cluster that never reported is absent from the list —
a consumer must read "absent" and "stale" as **unknown**, never as "the namespace does not exist";
only a namespace missing from a fresh cluster's list is absent. The namespace list is replaced only
by a report whose `complete.namespaces` is true, and `reportedAt` here is that report's, so an
agent that can post workloads but failed to list namespaces makes this list stale by itself. The
portal draws no conclusion from the namespaces and gates nothing on them.

### The comparison

`Features/VersionState/VersionComparer.cs` — pure, no I/O, the clock passed in.

**Inputs per (service, environment):**

- *declared*: `known(version, since, source, path)` · `unknown(reason)` · `none` (the list is
  published and has no entry) · `notAvailable` (no list);
- *applied*: `known(version, deployedAt, eventId, artifacts)` — the newest `succeeded` deploy event of the
  service in the environment, not older than the cell's last `removed` — or `none`. The newest event
  is used when it succeeded; otherwise one extra query for the request finds the newest succeeded
  one (new `DeploymentService.LastSucceededVersionsAsync`, same index as `LatestStateEvents`);
- *observed*, from the workloads mapped to the pair:
  - `notCovered` — no cluster is mapped to this environment, or none has ever reported;
  - `unknown` — a cluster it runs in is stale, or a workload has no version;
  - `none` — the clusters are fresh and hold no workload of the service;
  - `known(version, digests)` — every workload has the same normalised version;
  - `mixed(versions)` — several versions, or two digests of one container;
  - for a static site: `known(fingerprint, lastModified)`, or `unknown` while blobs lack an MD5;
  - plus `midRollout`: any workload with `updatedReplicas < replicas` or `readyReplicas < replicas`.

**Rollout** (declared against applied):

| Declared | Applied | State |
|---|---|---|
| `notAvailable` | any | `not_compared` |
| `unknown` | any | `unknown` |
| `known` | `known`, equal | `in_sync` |
| `known` | `known`, different, or `none` | `pending` |
| `none` | `known` | `not_declared` — installed, and the release repository declares nothing here |
| `none` | `none` | `none` |

**Drift** (applied against observed):

| Applied | Observed | State |
|---|---|---|
| any | `notCovered` | `not_compared` |
| any | `unknown` | `unknown` |
| `known` | `known`, version equal, content equal or not comparable, not `midRollout` | `in_sync` (`comparedBy`: `content` or `version`) |
| `known` | `known`, version equal, digest or fingerprint different | `rolling_out` or `drift` with kind `content`, below |
| `known` | `known` version different, `mixed`, or equal but `midRollout` | `rolling_out` or `drift` with kind `version`, below |
| `known` | `none` | `drift` — installed and nothing runs |
| `none` | `known` or `mixed` | `not_reported` — it runs and the portal has no successful deploy on record |
| `none` | `none` | `none` |

**Rolling out, not Drift.** A difference between applied and observed is `rolling_out` while either
holds, and `drift` otherwise:

1. the cluster says a rollout is in progress — `midRollout`, or several versions at once — and the
   difference was first seen less than `RolloutGraceMinutes` ago (default 15; setting
   `Deployments:VersionState:RolloutGraceMinutes`);
2. the applied deploy event is younger than the grace period — the install has just finished, or
   Helm returned before the pods rolled, and the next report or two will catch up. A cell whose
   newest event is `in_progress` counts the same way, from that event's time.

For a static site the first condition is "the container's newest blob is younger than its grace
period" (see "Static sites").

So a normal deploy shows **Rolling out** for a minute or two and then nothing. A manual change of an
image on a settled workload — no deploy event, rollout complete by the next report — is **Drift**
at once. A rollout that never finishes (a crash loop: `readyReplicas` stays short) becomes Drift
after the grace period, with `since` at its first sight, not at the moment the grace ran out.

**`since`.** `VersionStateEvaluator` maintains `version_states`: it sets `RolloutPendingSince` to now
when the rollout state first becomes `pending` and clears it when it leaves `pending`; it sets
`ObservedDiffSince` and `ObservedDiffKind` when applied and observed first differ — in version or in
content, including while rolling out — and clears them when they agree. It runs for the affected pairs after each of the three things that can change an
input: an observed report (`POST /observed`), an accepted status snapshot (`InventoryRefresher`,
where stage 1 hooks its catalog sync), and an ingested deploy event
(`DeploymentService.IngestEventWithResult`). For a pending rollout whose declared entry carries its
own `since`, the earlier of the two is shown — the catalog saw the declaration before the portal
did. Reads compute the states with `VersionComparer` from the live inputs and take only `since` from
the table, so a stale cluster reads unknown the moment it is stale, with no evaluation needed.

**Unknown, not compared, in sync** — the three are different and are displayed differently:

- *in sync*: both sides known and equal;
- *unknown*: the source exists and could not be read now (stale cluster, unreadable declared file,
  workload without a version);
- *not compared*: there is no source for this pair (no agent in the cluster, no mapping, no declared
  list published).

### Coverage

| Marker | Works for | Needs |
|---|---|---|
| **Drift — other version** | every service a cluster agent reports with a service label, in an environment the cluster is mapped to, that has a succeeded deploy event in the portal | the agent and the mapping. No catalog, no inventory flag, no stage 1 |
| **Drift — content changed** | the same, where the deploy event carries `artifacts`; every configured static site whose deploy event carries a fingerprint | additionally, pipelines sending `artifacts` |
| **Rollout pending** | every component of the release repositories — in the catalog or not — that has a declared entry | the catalog publishing `versions.declared`, and `features.serviceInventory` on |

Each page states its coverage: "Drift compared for N of M services · Rollout pending for K of M".

### The promotion that delivers a pending rollout

For pairs in `pending`, one query for the request finds `PromotionCandidate`s with the same product,
service, `TargetEnv`, `Version` = the declared version and `Status` `Approved` or `Deploying` — the
match the promotion hook and `FluxIngestService` use for the candidate a deploy delivers. With a
match: "Rollout pending — promotion approved, waiting for the deploy", linked. Without: "Rollout
pending — no promotion in the portal delivers this version", the case W-16 exists for.

### Flux: the notifications the portal drops today

`FluxEventMapper.Map` returns `Ignored` for every event that is not a `HelmRelease` with one of
seven install reasons, and `FluxEndpoints` answers those `202`. Install outcomes keep becoming deploy
events exactly as today. The rest stops being thrown away: a new `FluxSignalMapper` turns the kinds
below into a **signal** on the release, stored in `flux_release_signals` (one row per client ×
namespace × name: `Product`, `Service`, `Environment` from the same metadata the install events
carry, `Reason`, `Message` clipped, `At`, `Suspended`, `DriftDetectedAt`; current state only). The
answer stays `202`, so Flux never retries. No signal creates a deploy event, and none changes a
comparison state: they explain it in the panel.

| Notification (`HelmRelease` unless said) | Today | Contributes |
|---|---|---|
| `InstallSucceeded`, `UpgradeSucceeded`, `InstallFailed`, `UpgradeFailed`, `RollbackFailed`, `UninstallFailed`, `UninstallSucceeded` | deploy event | **applied** — unchanged |
| `RollbackSucceeded` (Flux put the previous release back after a failed upgrade) | dropped | **applied** — a deploy event of the version rolled back to, marked `isRollback` (decided 2026-11-22; see below) |
| `DriftDetected` | dropped | **observed-side** signal: "Flux detected a change in the cluster at <time>" and, with correction on, that it restored it. Shown in the panel; it is about manifests, not versions, so it sets no Drift marker by itself |
| `Progressing`, `DependencyNotReady`, `ArtifactFailed`, reconciliation failures; `Kustomization` `ReconciliationFailed` / `HealthCheckFailed` | dropped | explains **Rollout pending**: "Flux reports: dependency not ready since <time>" |
| reconciliation succeeded (`HelmRelease` or `Kustomization`) | dropped | clears the signal above; "Flux last reconciled <time>" |
| suspension | not sent as a notification, as far as the Flux documentation says (Open point 2) | **suspended** — explains a pending rollout that will not move by itself. Until verified, taken from the agent: with `fluxReleases: true` it also lists `helmreleases.helm.toolkit.fluxcd.io` (`get`/`list`) and sends `suspended` and the ready condition per release in the same report |
| test results, anything else | dropped | still dropped |

**A rollback Flux performs is a deploy event.** Without it the portal's history ends on the failed
upgrade and says nothing about what Flux did next; with it, history shows that the environment went
back, and applied names an event that describes what runs.

- **Mapper.** `FluxEventMapper.StatusByReason` gains `RollbackSucceeded` → `succeeded`, and the
  mapped event is forced to `isRollback: true`.
- **Which version.** The version rolled back to is the one the environment ran before the failed
  upgrade: the cell's newest `succeeded` event older than the notification's timestamp (not older
  than a `removed`). It is taken from the portal's history, not from the notification: the
  `release-version` annotation `FluxEventMapper` reads names what the repository declares — the
  version that just failed — and what `revision` carries on a rollback is to be confirmed on a real
  event in Task 8 (when it reliably names the restored version it is used and must agree). With no
  succeeded event on record the portal cannot name the version: the notification is kept as a signal
  and no deploy event is created.
- **`previousVersion`** is derived by ingest as always — the newest event of the cell, which is the
  failed upgrade. The event reads "rolled back from 1.4.0 to 1.3.2".
- **References and metadata.** `FluxIngestService.IngestAsync` skips the candidate lookup for a
  rollback (a rollback delivers no promotion) and goes to its rollback path. `FindRolledBackAsync`
  counts only succeeded forward deploys, and the failed upgrade is not one, so it usually finds
  nothing: the event is then stored with `isRollback: true`, no references, and
  `metadata.rolledBackVersions` holding the failed version. `metadata.flux.reason` is
  `RollbackSucceeded`.
- **Idempotency.** The natural key ingest already uses — product, service, environment, version,
  source, `deployedAt` — with `deployedAt` the notification's timestamp: a retried notification is a
  replay (`200`, `replayed: true`).
- **History.** It appears in the service's history and on the matrix like any rollback today: the
  rollback marker, source `helm-deploy`, run "Flux <cluster>", the Flux message as its log.
- **What it does to promotions, rollbacks and analytics** (existing rules, verified in code; none
  changes):
  - `PromotionIngestHook.MatchCompletionAsync` closes candidates of the *landed* version that are
    not yet Deployed or Superseded. The promotion that first delivered that version is already
    Deployed, so nothing closes. A second, still open candidate of the same version into the same
    environment would be closed as "deployed outside this promotion" — correct, the version is live.
  - The promotion of the version that **failed** matches nothing (other version) and stays
    Approved or Deploying, "open for the next attempt" as today. `SupersedeOvertakenByDeployAsync`
    supersedes only promotions the landing overtook with a newer version; a rollback lands an older
    one, so it must leave that promotion alone — a test pins this. The cell then honestly shows
    **Rollout pending**: the failed version is still declared, the older one is applied, and the
    approved promotion is linked.
  - `RollbackService.MatchCompletionAsync` completes an open rollback item whose target is the
    restored version; `SupersedeOvertakenAsync` supersedes an approved rollback request to a
    different version, as any succeeded deploy of another version does.
  - Analytics: `DeployEventRows.Classify` books it as a rollback, not as a deployment; the failed
    upgrade stays counted as a failed deploy.
- **Drift.** Applied is the restored version either way (a failed upgrade applied nothing); with the
  event, the moment of the rollback restarts the rollout grace, so the pods going back do not show
  as Drift.

What Flux actually emits for each row depends on the Flux version and on the `Alert`'s
`eventSources` and severity; the mapper matches on `kind` and `reason`, takes anything it does not
know as dropped, and `notes/integrations/flux.md` gets the `Alert` change that makes Flux send the
additional kinds. These notifications are more frequent than install outcomes; they share the
client's 120-a-minute limit, and the mapper writes a row only when reason or state changed.

### API (P-17)

All additions are optional fields or new routes.

**`GET /api/deployments/state`** (both views) and **`GET /api/deployments/services/{product}/{serviceName}`**:
each row (`DeploymentStateDto`, `DeploymentStateSummaryDto`, so `ServiceDetailDto.Environments`)
gains a trailing optional `Versions`:

```jsonc
"versions": {                        // null when features.versionState is off
  "declared": { "state": "known", "version": "1.4.0", "since": "…", "source": "mkp-release", "path": "…", "reason": null },
  "applied":  { "state": "known", "version": "1.3.2", "deployedAt": "…", "eventId": "…" },
  "observed": { "state": "known", "version": "1.3.2", "reportedAt": "…", "clusters": ["staging-weu-1"],
                "workloads": [ { "cluster": "…", "namespace": "…", "kind": "Deployment", "name": "…", "version": "1.3.2",
                                 "replicas": 3, "updatedReplicas": 3, "readyReplicas": 3 } ] },
  "rollout": { "state": "pending", "since": "2026-11-22T09:37:02Z", "promotionId": null },
  "drift":   { "state": "in_sync", "kind": null, "since": null, "comparedBy": "content" },   // kind: version | content; comparedBy: content | version
  "signals": [ { "source": "flux", "reason": "DependencyNotReady", "message": "…", "at": "…" } ]
}
```

`rollout.state`: `in_sync` | `pending` | `not_declared` | `none` | `unknown` | `not_compared`.
`drift.state`: `in_sync` | `drift` | `rolling_out` | `not_reported` | `none` | `unknown` |
`not_compared`. The summary view carries `workloads` as a count only. `version` on the row keeps
today's meaning (the newest event).

**`GET /api/deployments/versions?product=&service=&environment=&difference=`** (new, behind the
flag; signed-in user or API key): every compared pair, including those with **no deploy event**
(`hasDeployment: false`) — a declared component never deployed through the portal, a workload running
with no deploy reported. `difference` accepts `pending`, `drift`, `rolling_out`, `unknown`. Also
`coverage` (the two counts), `clusters` (name, last report, stale), `declaredPublished` and the
status feed's `freshness`. `product` filters by the product of the service's deploy events, or by
stage 1's `service_list` where a service has none.

**`POST /api/deployments/observed`**, **`GET /api/deployments/observed/clusters`** (admin),
**`GET /api/deployments/observed/namespaces`**: above.

**`POST /api/deployments/events`** (and `/manual`): the optional `artifacts` list. Additive; an
event without it is unchanged. `GET` responses that return an event carry it back.

**`GET /api/service-inventory/services/{serviceName}/observed`** is unchanged; it stays the
infrastructure view.

**MCP** (`Mcp/DeploymentTools.cs`): no new tool. `get_deployment_state` and `get_service` add per
environment, when present, `declared`, `applied`, `observed`, `rollout` and `drift` (state, kind and
`since`); the descriptions say `version` is the newest deploy event, and that `not_compared` and
`unknown` are not `in_sync`.

### Screens (E-22)

The service × environment matrix is the **State** tab of `ProductDeploymentsPage` (`MatrixCell`);
`DeploymentsPage` is the product overview and is not changed.

**Matrix cell.** One version, status and age as today. Added to the right of the version:

| State | Cell |
|---|---|
| rollout `pending` | **Rollout pending** (↑) and the declared version in small type: `1.3.2 ↑ 1.4.0` |
| drift `drift` | **Drift** (≠), warning colour; with pending, both, drift first. The panel and the label say which: **Drift — other version** or **Drift — content changed** |
| drift `rolling_out` | a quiet **Rolling out** indicator, no warning colour |
| either `unknown` | muted **?** |
| `in_sync`, `none`, `not_declared`, `not_reported`, `not_compared` | nothing in the cell; the panel says which |

The marker is a button that opens a panel: the three versions with where each came from and when;
each difference with "since <time>"; whether content was compared ("image digest matches the
deploy", "versions match; content not compared — the deploy did not report a digest"); for a content
change, the deployed and the running digest or fingerprint; the promotion link; the workloads with
their versions and replica counts; Flux signals; for unknown and not compared, the reason in words ("cluster
staging-weu-1 last reported 12 minutes ago", "no cluster agent reports for this environment", "the
service catalog publishes no declared versions yet"). The same text is appended to the cell's
keyboard label (`useKeyboardListRow`). The existing promotion chip and freeze cue keep their places.

A pair with `hasDeployment: false`, in an environment the matrix has as a column, fills the empty
cell in muted type — "1.4.0 declared, no deploy reported" or "1.3.2 running, no deploy reported" —
and opens the panel.

**Above the matrix**: the coverage line, any stale cluster by name, and a filter **Show only**:
**Rollout pending** / **Drift** / **Unknown** (multi-select, URL parameter `diff`, client-side over
the loaded matrix like the page's service search). Each option is offered only when its comparison
has coverage, so **Rollout pending** appears with the catalog's list and **Drift** with the first
mapped cluster. Rolling out is never matched by **Drift**.

**Service page — Environments section** (`ServiceDetailPage`, `EnvironmentCard`): rows **Declared**,
**Applied**, **Observed**, the markers with their age, the workloads (collapsed when they agree),
Flux signals. Unknown reads "unknown — <reason>", not compared "not compared — <reason>", neither a
dash.

**Services page** (E-21, `app/services/ServicesPage.tsx`): the marker in each version cell, two
sortable columns — **Rollout pending** and **Drift**, the count of environments and the oldest age —
and the same **Show only** filter. It reads `GET /api/deployments/versions`, so services with no
deploy event are covered.

**Settings → Environments**: the **Clusters** block (mapping, last report, the cluster's namespaces
and which are mapped, static sites, unmapped namespaces, workloads and sites, remove a cluster).

**With the flag off**: every page is today's — no marker, filter, line or extra request.

### The cluster agent

A deliverable of this plan, kept and versioned with the portal.

- **Deployed by Flux from the pull-model release repository** (decided 2026-11-22). The agent is a
  platform component: `ops-release` holds one `HelmRelease` of the chart per cluster, with the
  cluster's values (name, namespace selector, static sites) and a reference to the key's `Secret`,
  and Flux installs and upgrades it like any other component there. A new agent version is a
  version bump in that repository. **Rollout caveat:** a cluster without Flux gets no agent this
  way — it needs Flux first, or another route (the same chart installed by that cluster's own
  pipeline); until then its environments are "not compared".
- **Where the code lives: this repository** (decided; the service catalog records the same).
  `src/Platform.ClusterReporter` (a .NET console project, added to `InfraPilot.slnx`),
  tests in `tests/Platform.ClusterReporter.Tests`, the chart in `infra/cluster-reporter/chart`, its
  own `Dockerfile` beside the project. The repository has no `agents/` directory; projects live
  under `src/Platform.*` and deployment files under `infra/`, so the agent follows that. It shares
  no code with `Platform.Api` — the contract is the JSON above — so it can be built and released on
  its own (a job in `.github/workflows/release-image.yml`).
- **Step 1 — a job.** A Kubernetes `CronJob`, schedule `* * * * *`, `concurrencyPolicy: Forbid`,
  short `activeDeadlineSeconds`. One run: list the namespaces; list the four workload kinds and the pods
  (for `imageID`) in the selected namespaces through the in-cluster API with the pod's service
  account; list the blobs of the configured storage containers with the pod's workload identity and
  compute each fingerprint; build the report, post it, log the response, exit. A namespace or a
  storage account that failed to list is left out of `complete` and the run exits non-zero after
  posting the rest. No state between runs.
- **Rights.** A `ClusterRole` with `get`, `list` on `deployments`, `statefulsets`, `daemonsets`
  (`apps`), `cronjobs` (`batch`), `pods` and `namespaces`; optionally `helmreleases`
  (`helm.toolkit.fluxcd.io`) with `fluxReleases: true`. No `secrets`, no `pods/exec`, no `pods/log`,
  no write verb. On storage: the blob data reader role on the configured accounts for the workload
  identity the chart's `ServiceAccount` is annotated with — listing only, in practice; no account
  key, no shared access signature. Bound with a `ClusterRoleBinding`, or per namespace with `RoleBinding`s when the
  cluster's owner prefers (chart value).
- **Configuration** (chart values → environment variables): `portalUrl`; `apiKey` from a Kubernetes
  `Secret` the chart references and does not create; `clusterName`; the namespace selector — an
  explicit list, or a label selector such as `service-namespace=true`; `serviceLabels` (default
  `app.kubernetes.io/part-of`, `service`); `versionLabel` (default `app.kubernetes.io/version`);
  `fluxReleases`; `staticSites` (the list of "Static sites"); proxy and CA bundle settings for
  clusters behind an egress proxy.
- **Version of a workload.** The version label of the pod template; the agent also sends the image
  of the container named after the workload (else the first), and the portal falls back to its tag
  when the label is missing and the image is not pinned by digest.
- **Tests.** Unit tests against recorded API list responses: each kind, a workload with no label, a
  digest-pinned image, pods on two digests, a namespace that fails, pagination (`continue`), the
  report shape; the fingerprint against shared test vectors (empty container, one blob, names that
  sort differently by byte and by culture, a blob without MD5), also run against the reference
  script. One
  integration test posts a report to the portal's test host (`tests/Platform.Integration.Tests`).
  The chart is rendered and checked in CI (`helm template` + a schema check).
- **Step 2 — a controller (later, optional).** The same image run as a `Deployment` that watches the
  same kinds and posts on change, batched, with a full report every few minutes as the repair. Same
  endpoint and payload (`agent.mode: "controller"`); a full report is still a full snapshot.
  **Trigger for doing it:** the minute proves too slow in use — a rollout shown as Rolling out long
  after it finished, or a drift that people need within seconds — or the per-minute list becomes a
  measurable load on a large cluster's API server. Not before.

### What ships in which order

| Step | Content | Needs | Users see |
|---|---|---|---|
| 0 (optional) | promotion chip for approved promotions (Task 0) | nothing | "Approved, waiting for deploy" on the matrix |
| 1 | **Drift**: ingest, tables, mapping, comparer, API, screens, the agent (Tasks 2–7, 9–13) | an agent installed in at least one cluster | applied and observed versions, **Drift — other version**, **Rolling out**, for every labelled service in mapped clusters; the namespace list, readable by the service catalog's reconciler |
| 1b | **Content**: `artifacts` on deploy events, digests and fingerprints compared (in Tasks 2, 5, 9; pipelines change on their own schedule) | pipelines sending `artifacts`; storage accounts configured in the agent and readable by it | **Drift — content changed** for images and static sites, service by service as their pipelines start sending |
| 2 | **Rollout pending**: the declared reader and its part of the comparer and screens (Tasks 1, 3, 14) | the catalog publishing `versions.declared`; `features.serviceInventory` on | declared versions and **Rollout pending**, for every release-repository component |
| 3 | **Flux extras** (Task 8, 15) | the `Alert` change on Flux clusters | signals that explain a pending rollout or a corrected change; suspension |
| later | the controller (step 2 of the agent) | its trigger | the same, faster |

Step 1 does not wait for the catalog, the service inventory or stage 1. Task 1 (the reader) can be
merged at any time and reads nothing until the list is published. The earlier conclusion — nothing
user-visible before the catalog publishes — no longer holds.

The optional chip of step 0 stays honest on its own terms: it states a promotion fact ("Approved,
waiting for deploy"), not a version comparison.

---

## Tasks

Tests first in every API task. Test names follow the repository's sentence style
(`Observed_state_keeps_unresolved_and_unknown_as_published`).

### Task 0 (optional, independent): chip for approved promotions

**Files:** `src/Platform.Web/src/app/deployments/ProductDeploymentsPage.tsx` (the `listPromotions`
call reads `Pending`, `Approved` and `Deploying`; `PendingPromotionChip` and the cell label word the
three), `ServiceDetailPage.tsx`. **Accept:** an Approved or Deploying promotion shows "Approved,
waiting for deploy"; a Pending one reads as today.

### Task 1: Read `versions.declared` (TDD)

**Files:** `Features/ServiceInventory/Documents/Documents.cs`, `Documents/DocumentReader.cs`;
fixture `tests/Platform.Api.Tests/Features/ServiceInventory/Fixtures/status-versions.json`.

**Tests** (`DocumentAndProjectorTests`): a file without the block is accepted, `Versions` null; the
fixture is read — `service: null`, `version: null`, `state: unknown`, `sources`; an unrecognised
`state` reads as unknown; a null entry and an entry without `name` or `environment` reject the file
with a reason; unknown fields ignored; the projector returns no rows.

### Task 2: Entities, migrations, the ingest endpoint (TDD)

**Files:** new `Features/VersionState/Models/ObservedCluster.cs`, `ObservedWorkload.cs`,
`ObservedStaticSite.cs`, `ObservedNamespace.cs`, `VersionState.cs`;
`Features/Deployments/Models/DeployEvent.cs` (`ArtifactsJson`), `DeploymentDtos.cs`
(`CreateDeployEventDto.Artifacts`), `DeploymentService.cs` (store and return it); `PlatformDbContext.cs`; migrations `AddVersionState` (Postgres, SQL Server); new
`Features/VersionState/ObservedStateIngest.cs` and `ObservedReportDto.cs`;
`Features/Deployments/DeploymentEndpoints.cs` (`POST /observed`);
`Infrastructure/Auth/ApiKeyAuthHandler.cs` (`AllowedClusters`, `ApiKeyScopes.ObservedReport`, the
claim); `Infrastructure/Features/IFeatureFlags.cs` and `FeatureFlagSeeder.cs` (`VersionState`).

**Tests** (new `tests/Platform.Api.Tests/Features/VersionState/ObservedStateIngestTests.cs`,
integration `tests/Platform.Integration.Tests/ObservedStateEndpointTests.cs`): a first report stores
its rows; an identical report changes only the cluster row; a workload missing from the next report
is removed; a namespace not named keeps its rows; a workload in an unnamed namespace is `400`; an
older report is answered `applied: false`; the same report twice is a no-op; `VersionSince` moves
only when the version does; version from the label, from the image tag, empty for a digest; `413`
over the limit; `403` for a key without the scope, for a key with no scopes at all, for a cluster
outside `AllowedClusters`, for a personal access token; `401` without a key; the response counts
unmapped namespaces and unlabelled workloads; a static site is stored, replaced and — when its
account is in `complete` and the site is not in the report — removed; a site for an environment
the cluster is not mapped to is stored as unmapped; the namespace list replaces the stored one only
when `complete.namespaces` is true; an item of an unknown kind is skipped and counted; an item
outside the report's scopes is `400`; container digests are stored and `ContentSince` moves only
when they change. `DeploymentServiceArtifactsTests`: a deploy event with `artifacts` stores and
returns them; without, the event is byte-for-byte today's; a replay does not duplicate them.

### Task 3: Declared join and cluster mapping (TDD)

**Files:** new `Features/VersionState/DeclaredVersions.cs`, `ClusterEnvironmentMap.cs`;
`Features/Settings/Models/AppSettingsDtos.cs` (`ClusterConfigDto`, `AppSettingsDto.Clusters`),
`AppSettingsService` (validation).

**Tests** (`DeclaredVersionsTests`, `ClusterEnvironmentMapTests`): `stage` joins `staging`; `dev`
joins `dev`; `service` wins over `name`; a name differing by case, `.` or `_` joins; a retired
environment is left out; two entries for one pair give unknown with both paths; a source not read
in the last run is flagged; no list, no snapshot and the inventory flag off give not available.
Mapping: namespace pattern before the cluster default; first pattern wins; an unknown cluster and an
unmatched namespace without a default are unmapped; an entry naming a retired or unknown environment
is refused at save.

### Task 4: `LastSucceededVersionsAsync` (TDD)

**Files:** `Features/Deployments/DeploymentService.cs`. **Tests**
(`tests/Platform.Api.Tests/Features/Deployments/DeploymentServiceAppliedVersionTests.cs`): the
newest succeeded event when the newest failed or is in progress; none when nothing succeeded; events
before a `removed` ignored; one query for many cells.

### Task 5: Comparer, evaluator, reader (TDD)

**Files:** new `Features/VersionState/VersionComparer.cs`, `VersionStateEvaluator.cs`,
`VersionStateReader.cs`, `VersionStateRegistration.cs`; the three hooks (`ObservedStateIngest`,
`ServiceInventory/InventoryRefresher.cs`, `Deployments/DeploymentService.cs` after ingest).

**Tests** (`VersionComparerTests`, `VersionStateEvaluatorTests`, `VersionStateReaderTests`):

- content: same version and same digest is in sync, compared by content; same version and another
  digest is drift of kind `content`; no applied artifact is in sync compared by version, and the
  reader says so; an applied image with no matching container is ignored; two digests of one
  container within the grace period is rolling out; a version difference is kind `version` whatever
  the digests;
- static sites: equal fingerprints; different with a recent `lastModified` (rolling out) and an old
  one (drift, `since` at first sight); `blobsWithoutMd5` > 0 is unknown; no applied fingerprint is
  not compared;
- declared: a kept version (`unknown` with a version) gives no marker and is returned as last read;
  two entries resolved by the cell's deployment model, and unknown when that does not leave one; a
  pair without an entry while an expected source has no `sources` key is unknown, not none; a source
  not read in the last run is compared and flagged;
- equality: `v1.4.0` = `1.4.0`; ` 1.4.0 ` = `1.4.0`; `1.4.0+abc` ≠ `1.4.0`; `version1` is not
  stripped;
- every row of the two state tables in "The comparison";
- rolling out: mid-rollout within the grace period; several versions within it; a deploy event
  younger than the grace period with observed still old; an `in_progress` newest event;
- drift: a settled workload on another version with no recent deploy event, at once; a rollout that
  never finishes, after the grace period, with `since` at first sight; applied known and nothing
  running;
- unknown: a stale cluster; one of two clusters stale; a workload with no version; declared unknown;
- not compared: no cluster mapped to the environment; no declared list;
- `since`: set at first difference, unchanged while it lasts and while the versions change, cleared
  when it ends, set anew when it returns; the declared entry's earlier `since` wins for display;
- the evaluator runs for the affected pairs after a report, a status snapshot and a deploy event,
  and is idempotent;
- the promotion link: Approved and Deploying of the declared version; none for other statuses or
  versions;
- cost: a matrix read adds at most three queries (succeeded versions, workloads, promotions) plus
  the `version_states` rows.

### Task 6: Read endpoints and MCP (TDD)

**Files:** `Features/Deployments/DeploymentService.cs` (`GetState`, `GetStateSummary`,
`GetServiceDetail` attach `Versions`), `DeploymentEndpoints.cs` (`GET /versions`,
`GET /observed/clusters`, `GET /observed/namespaces`), `Features/Deployments/Models/DeploymentDtos.cs`,
`Mcp/DeploymentTools.cs`.

**Tests:** `DeploymentStateReadPathTests.cs` — flag off: today's rows plus `versions: null`; flag
on: a covered cell carries both states. `ObservedStateEndpointTests.cs` — `/versions` filters,
`hasDeployment: false` pairs, coverage counts, stale clusters named, `404` with the flag off;
`/observed/clusters` is admin-only. `/observed/namespaces`: readable with an ordinary API key
and by a signed-in user, with the flag off too; names, labels and `reportedAt` per cluster; a
cluster silent past the limit is `stale: true` and still lists its last namespaces; a report without
`complete.namespaces` does not move its `reportedAt`; a cluster that never reported is absent;
`401` without credentials. `McpServerTests.cs` — the new fields when present, absent
otherwise.

### Task 7: Settings → Environments, **Clusters**

**Files:** `src/Platform.Web/src/app/settings/EnvironmentsSettings.tsx`, `lib/api.ts`,
`lib/types.ts`. Mapping editor; reporting-but-unmapped clusters offered for mapping; last report;
unmapped namespaces and unlabelled workloads; remove a cluster.

### Task 8: Flux signals (TDD)

**Files:** new `Features/Integrations/Flux/FluxSignalMapper.cs`, `FluxReleaseSignal.cs` (entity,
migration `AddFluxReleaseSignals` in both sets), `FluxEndpoints.cs` (the `Ignored` branch asks the
signal mapper), `FluxEventMapper.cs` (`RollbackSucceeded`; `Ignored` carries kind and reason),
`FluxIngestService.cs` (the rollback path: version from history, no candidate lookup).

**Tests** (`FluxSignalMapperTests`, `FluxIntegrationTests.cs`): each row of the Flux table; an
install outcome still becomes a deploy event and no signal; an unknown reason is dropped; a signal
is `202` and creates no deploy event; an unchanged signal writes nothing; a succeeded reconciliation
clears a failure; product scope of the client applies as for install events. Rollback: a
`RollbackSucceeded` after a failed upgrade stores a succeeded deploy event of the previously applied
version with `isRollback: true`, `previousVersion` the failed version and
`metadata.rolledBackVersions`; the same notification again is a replay; with no succeeded event on
record no deploy event is created and the signal is kept; the Approved promotion of the failed
version stays Approved and is not superseded; the Deployed promotion of the restored version is
untouched; an open rollback item targeting the restored version is completed; analytics count it as
a rollback; the comparer shows no Drift and shows Rollout pending for the failed version.

### Task 9: The agent — project and tests

**Files:** new `src/Platform.ClusterReporter` (`Program.cs`, `KubernetesLister.cs`,
`BlobLister.cs`, `ContentFingerprint.cs`, `ReportBuilder.cs`, `PortalClient.cs`,
`ReporterOptions.cs`, `Dockerfile`; the `fingerprint` command and `scripts/content-fingerprint.ps1`
as the reference for pipelines),
`tests/Platform.ClusterReporter.Tests`, `InfraPilot.slnx`. Tests as listed under "The cluster agent".

### Task 10: The agent — chart and release

**Files:** new `infra/cluster-reporter/chart` (`CronJob`, `ServiceAccount`, `ClusterRole`, binding,
values and schema), `.github/workflows/ci.yml` (build, test, `helm template`),
`.github/workflows/release-image.yml` (the agent's image). The `HelmRelease` for each cluster is added in `ops-release`
(outside this repository; listed in the guide). **Accept:** installed on the test
cluster through Flux with a scoped key, the cluster appears on **Settings → Environments** within a minute; the
rendered `ClusterRole` holds no verb but `get` and `list` and no `secrets`; a configured storage
container appears as a static site.

### Task 11: Web — marker, matrix, service page, Services page

**Files:** `src/Platform.Web/src/lib/types.ts` (`VersionState` on `DeploymentStateEntry`),
`lib/api.ts` (`getDeploymentVersions`), `stores/featureFlagsStore.ts` (`VersionState`), new
`app/deployments/VersionMarker.tsx` (markers, panel, label text),
`app/deployments/ProductDeploymentsPage.tsx` (`MatrixCell`, coverage line, **Show only**),
`app/deployments/ServiceDetailPage.tsx` (`EnvironmentCard`), `app/services/ServicesPage.tsx`.

### Task 12: Manual checklist (dev server, seeded reports and a status fixture)

- drift: in sync; rolling out after a deploy; drift after a manual change; nothing running; a
  rollout that never finishes; running with no deploy reported;
- content: the same tag pushed again and restarted (Drift — content changed); a deploy without
  `artifacts` (the panel says content was not compared); a static site with one file replaced by
  hand (Rolling out, then Drift — content changed), during a pipeline upload (Rolling out, then
  nothing), with blobs lacking an MD5 (unknown);
- declared: a kept version shown muted as last read; a component in both release repositories; a
  release repository not read in the last run;
- unknown and not compared: a cluster silent for six minutes (every cell of it turns "?" with the
  cluster named); an environment with no cluster; an unmapped namespace; an unlabelled workload on
  the settings block; a service in two clusters, one stale;
- rollout: pending with and without a promotion; not declared; declared unknown; no list published
  (the **Rollout pending** option and column are absent, Drift still works);
- Flux signals in the panel; a suspended release;
- the panel opens by keyboard and the cell's spoken label carries the same text;
- the filter with each option and combined; the coverage line;
- flag off: today's pages and no request to `/deployments/versions`;
- a product with 100+ services: the matrix is still readable and no slower to open.

### Task 13: Docs (with step 1)

- New `notes/observed-state-api.md`: the ingest contract with its three item kinds, auth, limits,
  the per-scope full-snapshot rule, the mapping, staleness, the fingerprint algorithm with its test
  vectors, and `GET /observed/namespaces` for consumers outside the portal — its shape, that it
  needs only an ordinary API key, and that stale or absent means unknown; listed in `AGENTS.md` next to the other notes.
- `notes/deployment-ingest-api.md`: the optional `versions` field on `/state` and the service
  detail; `GET /versions`; that `version` remains the newest event; the optional `artifacts` list
  on a deploy event and how a pipeline obtains an image digest or a fingerprint; the `observed:report` scope and
  `AllowedClusters` in the API key section.
- `notes/mcp-server.md` and `.claude/skills/infrapilot-api/SKILL.md`: the new fields and how to read
  `unknown` and `not_compared`.
- `GLOSSARY.md`: the entries added with ADR 0009 lose "not built yet" as each ships.
- `knowledge/deploy-event-sources.yaml`: the cluster agent as a source of state, not of events.
- New `guides/` page: installing the agent (key, chart values, mapping the cluster, storage
  accounts and the reader role), with the two preventive measures for static-site storage as
  rollout notes.
- `docs/plans/service-list-and-catalog-product.md` E-3: the key view shows `AllowedClusters`.

With step 2: `notes/service-inventory-api.md` (the `versions.declared` list is read from the status
feed). With step 3: `notes/integrations/flux.md` — the Mapping table's `status` row gains
`RollbackSucceeded` → `succeeded` with `isRollback: true`; a "Rollbacks performed by Flux" section
(which version, `previousVersion`, what happens to promotions); the signals; the `Alert` change;
what is still dropped.

### Task 14: Rollout pending (step 2)

Enabled by the catalog's list: Task 1 merged, the status feed carrying it, `features.serviceInventory`
on. No code beyond Tasks 1, 3, 5 and 11 — the option, column and marker appear with coverage.
Verified on the test environment: a promotion through `mkp-release` (pending appears after approval
and clears with the deploy), a commit to `mkp-release` outside a promotion (pending without a
promotion link), a component of `ops-release` in `dev`.

### Task 15: What's new entries

Each in the PR that ships the step, dated the day it merges
(`src/Platform.Web/src/app/whats-new/entries/`).

`YYYY-MM-DD-observed-version-and-drift.md` (step 1):

```markdown
---
title: See when what runs differs from what was deployed
date: YYYY-MM-DD
audience: everyone
link: /deployments
linkLabel: Deployments
---
A product's **State** tab, each service's **Environments** and the **Services** page now show the
version that is actually running next to the one that was deployed. **Drift** marks an environment
where they differ and says since when — also when the version is the same but the content is not,
for example an image or a front-end file replaced by hand. A deploy still in progress shows as
**Rolling out**. Use **Show only** → **Drift** to list them.

It appears for clusters that report to the portal. Where a cluster has not reported for a few
minutes, the running version is shown as unknown.
```

`YYYY-MM-DD-cluster-mapping.md` (step 1, audience `admins`, link `/settings/environments`, label
`Settings → Environments`): "**Settings → Environments** has a new **Clusters** block: say which
environment each reporting cluster and namespace belongs to, and see which workloads the portal
could not place."

`YYYY-MM-DD-declared-version-and-rollout-pending.md` (step 2):

```markdown
---
title: See a change that is declared but not deployed yet
date: YYYY-MM-DD
audience: everyone
link: /deployments
linkLabel: Deployments
---
Next to the deployed and running versions, the portal now shows the version that is declared for
each environment. **Rollout pending** marks a change that is declared but not installed yet, says
since when, and links the approved promotion that delivers it — or tells you there is none. Use
**Show only** → **Rollout pending**.
```

Step 3 adds one sentence to no entry of its own unless suspension is shown as a marker; Task 0, if
shipped separately, gets a short entry ("See promotions that are approved and waiting for their
deploy").

### Task 16: Verify

`dotnet test` for all test projects; `npx tsc -b` and `npm run build` in `src/Platform.Web`; both
migration sets applied to a fresh database and to a copy with data; the web dev server with
`/whats-new` open; Task 12's checklist; on the test environment with the agent installed in the
cluster behind `mkp-release` and in one behind `ops-release`: a normal deploy (Rolling out, then
nothing), a manual image change (Drift within two minutes), the same tag
pushed again (Drift — content changed), one file changed in a static site's container (Drift —
content changed after the grace period, and restorable from the blob's previous version), the agent
stopped (unknown after five
minutes, never in sync), the agent restarted (repaired by one report).

## Open points

1. **Service labels on real workloads.** The join needs `app.kubernetes.io/part-of` or `service` on
   the workloads and `app.kubernetes.io/version` (or a tagged image). How many workloads carry them
   today is not known; unlabelled ones are listed, not guessed. Whether to add a fallback — the Helm
   release name (`app.kubernetes.io/instance`) or the workload name — is undecided: it widens
   coverage and can join the wrong service. The first agent report from each cluster answers it.
2. **Suspension and the exact Flux notification kinds.** Which reasons Flux emits for reconciliation,
   drift detection and correction depends on its version and the `Alert`; suspension appears not to
   be sent as a notification at all. Task 8 starts by recording what a real cluster sends. If
   suspension is not sent, it comes from the agent (`fluxReleases: true`), which costs `get`/`list`
   on `helmreleases` — to be accepted by the clusters' owners.
3. **Grace period and staleness defaults** (15 minutes for workloads, 5 for a silent cluster) are proposals; both are settings and should be tuned on the test environment
   before the first production cluster. The grace period for static sites is decided: 2 minutes.
4. **The applied digest where pipelines do not send `artifacts`.** Two ways to fill it without
   changing every pipeline, neither decided: the portal reads image digests out of the build
   manifest (`ManifestJson`) if the BuildMetadata document turns out to carry them — its content
   was not verified for this plan; or, for the pull model, the Flux notification is extended with a
   digest annotation. Until then such services are compared by version only.
5. **Which release repository is the pull-model one.** The rule for a component in both
   repositories needs `Deployments:VersionState:PullModelSources`. If the catalog later publishes
   the deployment model per source, the setting goes away.
