# Declared, applied and observed versions — implementation plan

> **Status:** draft for review, 2026-11-22. Decision D-29 of the "deployments and releases" brief:
> requirement W-16, logic L-21, API P-17, screen E-22. Delivered **after** stage 1
> (`docs/plans/service-list-and-catalog-product.md`). Governed by
> `docs/adr/0004-the-portal-reads-published-artifacts.md` and
> `docs/adr/0005-the-catalog-is-the-source-of-a-services-product.md`. Written against the service
> catalog's change spec "Declared, applied and observed versions in `status.json`" (in the catalog
> repository, `docs/specs/`), which fixes the published shape and its two phases.

**Goal:** For every service and environment, show three versions — the one that is **declared**, the
one that was **applied** and the one that is **observed** running — and mark the two differences
between them: a rollout that is still pending, and drift. Today the portal shows one version per
cell and cannot tell either.

**Architecture:** The service catalog's reconciler is the only place that compares the three
versions. It publishes the values and the two comparisons in a `versions` block of its status
artifact, which the portal already fetches and stores as the `status` feed of the service inventory
(`src/Platform.Api/Features/ServiceInventory`). This plan reads that optional block, joins it to the
portal's cells by service name and environment when a page is read, puts the portal's own applied
version and the promotion that delivers the declared version beside it, and displays it. Nothing new
is fetched, no table is added, and nothing in the delivery flow reads the result.

**Tech stack:** ASP.NET Core net10.0 Minimal API, xUnit + NSubstitute; React 19 + TypeScript. No
schema change, so no migration.

**Spec:** the task brief (D-29; D-19 "the portal gates nothing on catalog data"), ADR 0004, ADR 0005,
and the catalog spec named above.

**Not in this plan:** the read-only **Services** page for every user (D-28, E-21) is Task 8a of the
stage 1 plan; this plan adds markers and a filter to it. Infrastructure state (namespaces,
identities, databases) — the other axis of desired / applied / observed — is already shown by the
**Service inventory** section and is not touched.

## The three versions, in the portal's words

| Term | Meaning | Where it comes from | Who reads it |
|---|---|---|---|
| **Declared version** (`desired` in the artifact) | The version the environment is *supposed* to run: what the release repository says for that service and environment (`mkp-release` in the push model, `ops-release` in the pull model). | Files in the release repository | the catalog's reconciler |
| **Applied version** | The version the deployer last *installed successfully*. Two records exist: the release repository's own deployment status (push model only), and the portal's deploy events (both models). | Release repository deployment status; the portal's deploy events | the reconciler (the first); the portal (its own events) |
| **Observed version** | The version actually *running* in the cluster. | Workloads in the cluster | the catalog's reconciler |

| Difference | Published as | Meaning | Typical cause |
|---|---|---|---|
| **Rollout pending** | `desired_vs_applied: pending` | A change is declared and has not been installed yet. | An approved promotion whose deploy has not finished; a commit to the release repository outside a promotion; a stopped or suspended deployer |
| **Drift** | `applied_vs_observed: drift` | What runs is not what was last installed. | A manual change in the cluster; an install that failed half-way; workloads on several versions |

Each comparison carries `since`: when that state was first seen. Besides `in_sync`, a comparison can
be **`unknown`** (a side could not be read, or has no source in this deployment model) or **`none`**
(the sources were read and nothing is declared and nothing applied there). A value has the same
three-way `state`: `known`, `none`, `unknown`. Unknown is never "in sync" and never "absent"; `none`
is a fact, not a failure.

### What the portal shows today

- One version per service and environment: the **newest deploy event** of that cell, whatever became
  of it (`DeploymentService.LatestStateEvents`). A failed or still-running deploy is shown with its
  own version, so the cell is "what was last tried", not "what is applied". A cell whose newest event
  is `removed` is dropped.
- It knows only what was reported to it. In the pull model it records `HelmRelease` install outcomes
  and drops everything else Flux sends (`notes/integrations/flux.md`: "Everything else … is answered
  `202` and dropped"), so a suspended release, a reconciliation that silently restored a changed
  release, and a change made directly in the cluster or in the release repository leave no trace.
- The only "something is on its way" signal is the promotion chip: `ProductDeploymentsPage` marks a
  cell with a **Pending** promotion (awaiting approval), and `ServiceDetailPage` lists Pending and
  Approved promotions. A promotion that is `Approved` or `Deploying` is not marked on the matrix.
- The status file the portal reads today carries **no version data**: `StatusService` has only
  `stale`, `unverified`, `warnings`, `onboarding` and `freshness`.

### What the catalog will publish, and when

| | Push model (`mkp-release`) | Pull model (`ops-release`) |
|---|---|---|
| **Phase A** (release repositories) | declared and applied `known`; `desired_vs_applied` is `in_sync` or `pending` | declared `known`; applied **`unknown`** (Flux writes nothing back; the applied record is the portal's deploy event, which the reconciler does not read); `desired_vs_applied` `unknown` |
| **Phase B** (cluster read access, not available yet) | adds observed and `applied_vs_observed` | adds observed; `applied_vs_observed` stays `unknown` while applied is |

Until phase B, observed is `unknown` and `applied_vs_observed` is `unknown` **for every entry**: the
Drift marker cannot appear. The block lists **catalog services only**, in the environments each
declares; a service the catalog does not know (359 of 361 today) has no published comparison at all.

## Global constraints

- **The portal never reads a cluster or a release repository.** No new outbound call, no new feed, no
  new credential. Everything comes through the existing `status` feed and its rules (ADR 0004).
- **The reconciler's comparison is displayed, not recomputed.** The portal does not compare the
  published versions, does not derive `pending` or `drift` — not even where the reconciler says
  `unknown` and the portal holds an applied version of its own — and does not start its own clock for
  the age of a difference. One exception: the portal shows **its own applied version** — from its
  deploy events — next to the reconciler's, **says which is which**, and flags when the two differ.
  That flag is not a marker and is not filterable as one.
- **Unknown is shown as unknown, none as none.** An `unknown` value or comparison, a state value the
  portal does not recognise, and a service or environment the block does not cover are each shown as
  such — never as in sync, never as "not deployed". `none` is shown as "nothing declared" / "nothing
  applied", without a marker and distinct from unknown.
- **Nothing is gated on these values.** No promotion, approval gate, freeze, release check, rollback
  or webhook reads them (D-19). The reader is referenced only by read endpoints and MCP read tools.
- **Works only where the service inventory is on** (`features.serviceInventory`) and the status file
  carries the block (the reconciler publishes it only when run with its versions flag). Otherwise
  every page and response is exactly today's: no marker, no placeholder, no error.
- **Derived copy.** The block is read from the stored raw status snapshot; nothing derived from it is
  written to delivery tables. Dropping the inventory's storage loses nothing (ADR 0004).
- **Wording.** In the UI the `pending` comparison is labelled **Rollout pending**, not "Pending":
  **Pending** already means a promotion awaiting approval (`GLOSSARY.md`, `PendingPromotionChip`).
  The API keeps the published value.
- Environments are the portal's canonical keys (`staging`); the catalog's `stage` reaches them
  through the environment alias that stage 1's rollout adds in **Settings → Environments**.
- Public repository: fixtures, examples and tests use the placeholder vocabulary only (`mkp`,
  `mkp-extension-adobe`, `mkp-spotlight`, `mkp-release`, `ops-release`).
- The web app has no test runner; web tasks are verified by `npx tsc -b`, the build and the manual
  checklist in Task 6.

## Decisions

1. **No new table; the stored status snapshot is the store.** Each accepted status file is already
   kept raw (`InventorySnapshot`) and its parsed form is cached in memory per snapshot id
   (`InventoryReadService.SnapshotDocumentAsync`). The matrix is already assembled in memory, so the
   join is a dictionary lookup. `InventoryProjector` keeps returning `ProjectedRows.Empty` for a
   status file and `InventoryProjector.Version` does not change.
2. **No history in the portal.** The age of a difference comes from the comparison's published
   `since`. Raw status snapshots are kept as today (`InventoryRetention`: all for 7 days, then the
   last of each day for 90 days). A trend or history view is Open point 3.
3. **The join is computed when read**, by service name and canonical environment. It is not written
   onto `service_list` or onto deploy events.
4. **The portal's applied version is the newest *succeeded* deploy event of the cell**, not the cell's
   newest event: a failed or running deploy applied nothing.
5. **The matrix stays one version per cell.** A marker is added; the three versions are one click
   away.
6. **Five outcomes, four displays.** `in_sync` and `none` show no marker (the panel tells them
   apart); `pending` and `drift` show their marker; `unknown` shows a muted "?". A service the block
   does not cover is a sixth case — **not compared** — with no marker and a page-level count.
7. **Drift UI ships with phase B.** The Drift marker, filter option and column are rendered only once
   the block carries observed values; until then the Observed row reads "not observed yet" and no
   "?" is shown for the second comparison (see "Screens").

## Review focus

1. **Unknown never reads as in sync, and none never reads as unknown.** Missing block, service not in
   the block, environment not in the service's entry, `unknown`, `none`, an unrecognised state value,
   a null version and a failed feed refresh each have a test and a defined display. → Tasks 1, 2, 4.
2. **Nothing recomputed.** No code path compares declared, applied and observed to produce a marker.
   In particular a pull-model cell with declared `2.0.1`, published applied `unknown` and the portal's
   own applied `2.0.0` shows **no** Rollout pending marker. The only comparison the portal makes is its
   own applied version against a `known` published one. → Task 2 tests.
3. **Nothing gated.** `VersionComparisonReader` is injected only into `DeploymentEndpoints`,
   `ServiceInventoryEndpoints` and `Mcp/DeploymentTools`; nothing under `Features/Promotions`,
   `Features/Releases` or `Features/Rollbacks` references it.
4. **Backward compatibility.** `GET /api/deployments/state` (both views) and
   `GET /api/deployments/services/{product}/{serviceName}` return today's rows plus one field that is
   `null` when the feature is unavailable. A status file without the block is accepted exactly as
   today; `schemaVersion` stays 1. → Tasks 1, 3.
5. **Explicit nulls.** The reader runs with `RespectNullableAnnotations`; the artifact publishes
   `"version": null` and omits `since`, `source`, `path`, `detail`, `workloads`, `mixed` freely. Every
   one of them must be nullable, or the first real file is rejected whole. → Task 1 tests use the
   catalog spec's own example.
6. **A broken block must not be half-read.** The rule in "Reading the block" (structural errors reject
   the file; unknown state values degrade to unknown) is deliberate; check it against ADR 0004.
7. **The join.** `stage` → `staging`; a name that differs only by case, `.` or `_`; a retired
   environment; a service stored under two products; a catalog service the portal has never seen.
   → Task 2 tests.
8. **Cost of the matrix read.** At most two extra queries per request (succeeded versions for the
   cells whose newest event did not succeed; promotions for cells with a declared version that is not
   in sync); no per-cell query.

---

## Design

### The status block

As the catalog spec publishes it (field names are the producer's, snake_case). Top-level, optional,
present only when the reconciler ran with its versions flag:

```jsonc
"versions": {
  "sources": {                                   // one per release repository; phase B adds one per cluster
    "mkp-release": { "read_at": "2026-11-22T10:07:03Z", "commit": "9f2c1ab04d" },
    "ops-release": { "read_at": "2026-11-21T22:37:01Z", "commit": "41d07e6c2a" }  // older than generated_at: not read in the last run
  },
  "services": [                                  // catalog services only, by catalog id
    { "id": "mkp-extension-adobe",
      "environments": [
        { "environment": "stage",                // the catalog's key
          "desired":  { "state": "known", "version": "1.4.0", "since": "…", "source": "mkp-release", "path": "staging/mkp-extension-adobe/build-metadata.yaml" },
          "applied":  { "state": "known", "version": "1.3.2", "since": "…", "source": "mkp-release", "path": "…/deployment-status.yaml" },
          "observed": { "state": "unknown", "version": null, "detail": "not observed yet" },
          "desired_vs_applied":  { "state": "pending", "since": "2026-11-22T09:37:02Z" },
          "applied_vs_observed": { "state": "unknown", "since": "…" } } ] } ],
  "undeclared": [                                // release folders for an environment the service does not declare
    { "source": "ops-release", "name": "mkp-spotlight", "service": "mkp-spotlight", "environment": "stage", "path": "apps/mkp-spotlight/staging" } ]
}
```

- A value: `state` (`known` | `none` | `unknown`), `version` (null unless `known`; published as
  read, not normalised), `since`, `source`, `path`, `detail` — all but `state` optional. `observed`
  also has `workloads[] { name, kind, version }` and `mixed` (phase B): with workloads on several
  versions `version` is null, `mixed` is true and the comparison is `drift`.
- A comparison: `state` (`in_sync` | `pending` or `drift` | `unknown` | `none`) and `since`.
- Both comparisons are separate, so rollout pending and drift can hold at once.
- There is no single state, no per-entry freshness and no flat entry list. **Age** is the
  comparison's `since`; **staleness** is `sources.<name>.read_at` against the file's `generated_at`.

### Reading the block

`Documents/Documents.cs`: `StatusDocument` gains `Versions` (`StatusVersions?`) with records
`StatusVersionSource`, `StatusVersionService`, `StatusVersionEnvironment`, `StatusVersionValue`
(with `Workloads` and `Mixed`), `StatusVersionComparison` and `StatusVersionUndeclared`. Only `id`
and `environment` are non-nullable; every other field is nullable (review focus 5).

`DocumentReader.ReadStatus`:

- **Absent block** → `Versions` is null; the file is accepted as today. `SupportedVersions` is
  unchanged.
- **Structurally invalid block** (a null entry in `services`, `environments`, `workloads` or
  `undeclared`; a service without `id`; an environment without `environment`) → the file is
  **rejected whole**, like any other list with a null entry. The last good copy keeps being served
  and the feed shows the error. Preferred over dropping only the block: a silently dropped block
  would turn "broken" into "not published".
- **Unrecognised `state`** on a value or a comparison → kept, and read as `unknown`. Producers may add
  values; a new value must not become "in sync" and must not reject the file. (This also covers the
  catalog folding `none` into `unknown`, or adding a state for a rollout in progress.)
- A missing value or comparison object inside an environment reads as `unknown`.
- Unknown fields are ignored, as everywhere in the reader.

### What is stored, and how large it gets

Nothing new. The block travels inside the status snapshot the refresher already stores
(`InventoryRefresher`, `InventorySnapshot.ContentGzip`).

Size, on the assumptions of `docs/plans/service-inventory.md` ("Size after one year": 500 services,
8:1 compression) and the catalog spec's ~0.5 KB per service and environment: 500 × 4 × 0.5 KB ≈ 1 MB,
so a status file grows from ~870 KB to ~1.9 MB. Kept copies stay (7 × 48 + 90) = 426, so stored
status grows from ~46 MB to ~100 MB a year — once the catalog holds that many services; with today's
two the block is nearly empty. The file stays far below `ServiceInventoryOptions.MaxResponseBytes`
(20 MB). One parsed document per current snapshot is held in memory for an hour, as today.

Rejected: a projection table (one row per service × environment of the current snapshot). The catalog
spec notes rows are needed only for a cross-service list; here every such list (the matrix, the
Services page, `/versions`) is served from the cached document. It becomes worth its two migrations
only with a history view (Open point 3).

### Joining a cell to the block

New `Features/ServiceInventory/VersionComparisonReader.cs` (scoped). One call per request:

```
Task<VersionComparisons> ForAsync(IReadOnlyCollection<(string Product, string Service, string Environment)> cells, ct)
```

1. Flag off, no status snapshot, or `Versions` null → `VersionComparisons.Unavailable` (callers emit
   `versions: null`).
2. Index `services[].environments[]` by (service, environment):
   - **service**: the catalog `id` against the portal's service name in normal form. Stage 1
     introduces `CatalogName.Normalize` (trim, lower-case, `.` and `_` → `-`) for exactly this match
     and this plan uses it. (Today the inventory compares by trim and lower-case only —
     `ServiceInventoryEndpoints.Key`.) The name is the identity (ADR 0005), so the product is not part
     of the key: a service stored under two products gets the same entry in both.
   - **environment**: `EnvironmentAliasMap.Resolve` of the catalog key, so `stage` lands on `staging`.
     Entries whose environment resolves to a retired one (`EnvironmentAliasResolver.RetiredKeysAsync`)
     are left out, as on every current-state read.
3. Each cell gets one of: its entry; **not covered — service** (the service has no `services[]` entry:
   it is not in the catalog); **not covered — environment** (the service is in the block but does not
   declare this environment; if `undeclared[]` names it, that record is attached).

Catalog entries that match **no** portal cell — the catalog declares the environment and the release
repository may hold a version, but the portal has no deploy event — are not added to the state matrix
response (its rows are deploy events and carry an event id). They are returned by the versions
endpoint with `hasDeployment: false`; their product is `service_list.Product` (stage 1), which every
catalog service has.

### The portal's own applied version

For each matched cell the reader adds `portalApplied`:

- newest event `succeeded` → that event (already loaded, no query);
- newest event `failed` or `in_progress` → the cell's newest `succeeded` event, read in **one** query
  for all such cells of the request (new `DeploymentService.LastSucceededVersionsAsync`, same index
  as `LatestStateEvents`; events older than the cell's last `removed` do not count);
- none → null ("the portal has no successful deploy on record").

How it is shown depends on the published applied value:

| Published `applied.state` | Shown |
|---|---|
| `known`, same version as the portal's | one **Applied** row |
| `known`, different | two rows — **Applied — release repository** and **Applied — portal's deploy events** (with the event's time and a link) — and `appliedDiffers: true` |
| `unknown` (every pull-model service; a push-model file that could not be read) | one row, **Applied — portal's deploy events**, and the published `detail` as the reason the catalog has none. **No marker**: the comparison is the reconciler's and it says `unknown` |
| `none` | two rows: **Applied — release repository: nothing applied** and the portal's |

"Same version" uses the reconciler's own, documented equality — trim, and one leading `v` dropped
when a digit follows — so `v1.4.0` and `1.4.0` do not show as different. It is the only comparison
the portal makes; it changes labels and a flag, never a marker. `applied.source` names where the
published value came from; should the catalog later take the pull-model applied record from the
portal's own deploy events, that source will say so and the portal's row is then not repeated (Open
point 1).

### Rollout pending and the promotion that delivers it

For cells whose declared value is `known` and whose `desired_vs_applied` is `pending` or `unknown`,
the reader looks up — one query for the request — `PromotionCandidate`s with the same product,
service, `TargetEnv` = the cell's environment, `Version` = the declared version and `Status`
`Approved` or `Deploying`: the match the promotion hook and the Flux endpoint use to find the
candidate a deploy delivers. The newest is returned as `promotionId`.

- `pending` with a link → "Rollout pending — promotion approved, waiting for the deploy".
- `pending` without → "Rollout pending — no promotion in the portal delivers this version". That is
  the case W-16 exists for: a change declared outside the portal.
- `unknown` with a link (the pull model) → no marker; the panel says "An approved promotion of this
  version is waiting for its deploy".

The link is context and never changes the marker.

### Age and staleness

- **Age of a difference**: the comparison's `since`, shown as the published time and as the duration
  `generated_at − since` — the reconciler's definition — with "as of <generated_at>". The portal does
  not count from now and does not use its own first sight of the difference.
- **A value's own `since`** (when this version landed) is shown in the panel, so a new declaration
  can be told from an old one.
- **A source that was not read**: `sources.<value.source>.read_at` older than the file's
  `generated_at` means the last run could not read that source and kept the previous values. Those
  values are shown with "last read <time>" and the marker in the muted "may be out of date" style.
  A value without `source` (an `unknown` one) has nothing to attribute; its `detail` is shown.
- **The portal's copy**: the existing `FeedFreshness` of the status feed
  (`InventoryReadService.Freshness`), shown once per page as the **Service inventory** section does.

### API (P-17)

All additions are optional fields or new routes; nothing existing changes shape.

**`GET /api/deployments/state`** (both views) and **`GET /api/deployments/services/{product}/{serviceName}`**:
each row (`DeploymentStateDto`, `DeploymentStateSummaryDto`, and so `ServiceDetailDto.Environments`)
gains a trailing optional `Versions` (`VersionComparisonDto`, camelCase like the rest of the row):

```jsonc
"versions": {                       // null: inventory off, block not published, or the cell is not covered
  "declared": { "state": "known", "version": "1.4.0", "since": "…", "source": "mkp-release", "path": "…", "detail": null, "sourceReadAt": "…" },
  "applied":  { "state": "known", "version": "1.3.2", … },
  "observed": { "state": "unknown", "version": null, "detail": "not observed yet", "mixed": null, "workloads": null },
  "declaredVsApplied": { "state": "pending", "since": "2026-11-22T09:37:02Z" },
  "appliedVsObserved": { "state": "unknown", "since": "…" },
  "portalApplied": { "version": "1.3.2", "deployedAt": "…", "eventId": "…" },   // null = none on record
  "appliedDiffers": false,          // only ever true when applied.state is known
  "promotionId": null,              // Approved or Deploying promotion of the declared version
  "generatedAt": "2026-11-22T10:07:05Z"
}
```

`version` on the row keeps today's meaning (the newest event's version). Why a row's `versions` is
null is answered by the versions endpoint.

**`GET /api/service-inventory/versions?product=&service=&environment=&difference=`** (new; behind the
inventory flag like the rest of the group; signed-in user or API key):

```jsonc
{
  "published": true,                // false: the status file carries no versions block
  "observedPublished": false,       // true once any entry's observed value is not unknown (phase B)
  "generatedAt": "…",
  "sources": { "mkp-release": { "readAt": "…", "commit": "…", "readInLastRun": true } },
  "entries": [ { "service": "…", "product": "mkp", "environment": "staging", "hasDeployment": true,
                 /* the versions object above */ } ],
  "undeclared": [ { "service": "…", "environment": "staging", "source": "ops-release", "path": "…" } ],
  "coverage": { "compared": 2, "of": 361 },   // services in the block / services on the service list (for the product filter, when given)
  "freshness": { … }                // the status feed's, as on every inventory response
}
```

`difference` accepts `pending`, `drift`, `unknown`, comma-separated, and matches either comparison.
`product` filters through `service_list`. With `published: false` the lists are empty and the status
is still `200`.

**`GET /api/service-inventory/services/{serviceName}/observed`** gains
`versions: { environments: […], sources: {…}, undeclared: […] }` for that service, **passed through
with the published field names** like `onboarding` (this is the contract the catalog spec states for
this endpoint); `null` while the block is absent or the service is not in it.

**MCP** (`Mcp/DeploymentTools.cs`): no new tool. `get_deployment_state` and `get_service` add, per
environment and only when present, `declared`, `observed`, `rollout` (`pending` | `in_sync` |
`unknown` | `none`, with `since`), `drift` (likewise) and `appliedDiffers`; the tool descriptions say
that `version` is the newest deploy event and that a missing `rollout` means "not compared", not "in
sync".

### Screens (E-22)

The service × environment matrix is the **State** tab of `ProductDeploymentsPage` (`MatrixCell`);
`DeploymentsPage` is the product overview and is not changed.

**Matrix cell — compact form.** The cell keeps showing one version, status and age exactly as today
(the newest deploy event). Added, to the right of the version:

| Published | Cell | From |
|---|---|---|
| `desired_vs_applied: pending` | marker **Rollout pending** (↑) and the declared version in small type: `1.3.2 ↑ 1.4.0` | phase A |
| `desired_vs_applied: unknown` | muted **?** | phase A |
| `applied_vs_observed: drift` | marker **Drift** (≠), warning colour; with pending, both, drift first | phase B |
| `applied_vs_observed: unknown` | muted **?** — only once `observedPublished` is true | phase B |
| `in_sync`, `none` | nothing | |
| not covered / block not published / inventory off | nothing — today's cell | |
| `appliedDiffers` | a dot on the version; no text in the cell | phase A |

The marker is a button that opens a panel: the three values with state, version, when each landed,
source and path; each comparison with its age; the promotion link; both applied rows where the rules
above call for them; for `mixed`, the workloads and their versions; for a cell that is not covered,
why ("not in the service catalog" / "the catalog does not declare this environment for the service",
plus the release folder from `undeclared` when there is one). The same text is appended to the
cell's keyboard label (`useKeyboardListRow`), so nothing is hover-only. The existing promotion chip
and freeze cue keep their places.

In phase A every pull-model cell carries the muted **?**: its comparison is `unknown` by
construction, and the panel says so in the catalog's words. That is deliberate — it is the honest
reading — and it is why the count line below names it.

An entry with `hasDeployment: false`, in an environment the matrix already has as a column, fills the
empty cell with the declared version in muted type and "declared, no deploy reported"; it opens the
panel, not a deploy event. Entries in environments the product has no column for add no column.

**Above the matrix**: "Versions compared for N of M services · K without an applied record · status
read <age> ago", with the feed's refresh error when there is one and a note per source that was not
read in the last run. And a filter **Show only**: **Rollout pending** / **Unknown** — and **Drift**
once `observedPublished` (multi-select, URL parameter `diff`, client-side over the loaded matrix,
like the page's service search). It keeps a service row when any of its cells matches; services that
are not compared never match, and the count line says how many those are, so a short filtered list
is not read as "everything else is fine". With no block published, neither is rendered.

**Service page — Environments section** (`ServiceDetailPage`, `EnvironmentCard`): each card gains the
rows **Declared**, **Applied**, **Observed**, the markers with "since <time> (<duration>)" and a
"last read" line when a source is behind. `unknown` reads "unknown — <detail>"; `none` reads "nothing
declared" / "nothing applied"; neither is a dash. Until `observedPublished`, **Observed** is one
muted line, "not observed yet", without a "?" . A service that is not in the block gets one line for
the section — "Not compared: this service is not in the service catalog." — not one per card; an
environment the catalog does not declare says so on its card.

**Service page — Service inventory section** (`ServiceInventorySection`, card **Runs in**): its rows
are the catalog's environments. A catalog environment that has **no** deployment card gets the same
three rows there (same component), so a declared version with no deploy reported is visible on the
service page too; environments that have a card get a marker only, pointing at the card. The catalog
spec places all of it in **Runs in**; E-22 names the Environments section, and that is where the
portal's own applied version already is, so the detail lives there.

**Services page** (E-21, `app/services/ServicesPage.tsx`): its per-environment version cells reuse
the marker and panel, and the page gets the same **Show only** filter and count line. Read through
`GET /api/service-inventory/versions`.

**Without the inventory** (flag off, or status feed never fetched): every page is today's. No marker,
no filter, no note, no extra request — the web skips `getInventoryVersions` unless
`useFeatureFlag(FeatureFlag.ServiceInventory)` is on.

### What can ship first

| Step | Needs | What users see |
|---|---|---|
| 0 (optional) | nothing | promotion chip also for approved promotions (below) |
| 1 — Tasks 1–3, dark | nothing; the block is absent | nothing: fields are `null`, `published: false` |
| 2 — Tasks 4–8 | catalog phase A on the test environment, and stage 1's step 1a | declared and applied, **Rollout pending**, unknown, not compared; for pull-model services declared plus the portal's applied, no marker |
| 3 — Task 9 | catalog phase B | observed, **Drift**, its filter and column |

Honest limits of step 2, to be said in its What's new entry and on the page: it covers only services
that are in the catalog; it can mark a pending rollout only in the push model; it says nothing about
what actually runs.

**A "pending" marker derived only from the portal's own promotions should not ship as this
feature.** It would cover only changes that went through the portal — the half the portal already
knows — and miss every change made outside it, which is what W-16 is for. Unmarked cells would be read
as "in sync", and "show only pending" would return a list that looks complete and is not. The same
reasoning is why the portal does not fill the pull-model gap itself in step 2.

**Step 0** is honest and useful on its own: extend the existing promotion chip on the matrix to
promotions that are `Approved` or `Deploying`, worded as a promotion fact — "Approved, waiting for
deploy". It makes no claim about versions, has no filter and no "in sync" counterpart, and stays when
the comparison arrives. For pull-model services it remains the only "on its way" signal until the
catalog has an applied record for them.

Recommendation: ship steps 0 and 1 now; step 2 with catalog phase A; step 3 with phase B.

### Dependencies and order

1. Stage 1, step 1a (`docs/plans/service-list-and-catalog-product.md`): `CatalogName.Normalize`, the
   `service_list` table (product of an entry without a deployment; the "of M" in the count), the
   `stage` alias, and the Services page (Task 8a). Step 1 above needs only `CatalogName.Normalize`
   and the alias; if stage 1 has not merged, it uses `ServiceInventoryEndpoints.Key` and switches in
   stage 1's PR.
2. The catalog publishes the block (its phase A), then observed values (its phase B). Both sides can
   ship in any order: `schemaVersion` stays 1 and the portal ignores a block it does not read yet.
3. Coverage grows only as services are registered in the catalog; that is outside this plan.

---

## Tasks

Tests first in every API task. Test names follow the repository's sentence style
(`Observed_state_keeps_unresolved_and_unknown_as_published`).

### Task 0 (optional, independent): chip for approved promotions

**Files:** `src/Platform.Web/src/app/deployments/ProductDeploymentsPage.tsx` (the `listPromotions`
call reads `Pending`, `Approved` and `Deploying`; `PendingPromotionChip` and the cell label word the
three), `ServiceDetailPage.tsx` (its chip, same wording).

**Accept:** a cell with an Approved or Deploying promotion shows "Approved, waiting for deploy" and
links to it; a Pending one reads as today. Checked in the dev server.

### Task 1: Read the block (TDD)

**Files:** `Features/ServiceInventory/Documents/Documents.cs`, `Documents/DocumentReader.cs`;
fixtures `tests/Platform.Api.Tests/Features/ServiceInventory/Fixtures/status-versions-phase-a.json`
and `status-versions-phase-b.json` — the catalog spec's two examples with placeholder names (the
existing `status.json` stays without the block).

**Tests** (`DocumentAndProjectorTests`): a file without the block is accepted and `Versions` is
null; the phase A fixture is read — `known`, `none` and `unknown` values, explicit `"version": null`,
omitted `since` / `source` / `path`, `sources`, `undeclared`; the phase B fixture — `workloads`,
`mixed`, a cluster source; an unrecognised value state and an unrecognised comparison state read as
unknown; a missing comparison object reads as unknown; a null entry in each of the four lists, a
service without `id` and an environment without `environment` reject the file with a reason; unknown
fields are ignored; the projector still returns no rows for a status file.

**Accept:** the existing inventory tests pass unchanged; `SupportedVersions` unchanged.

### Task 2: `VersionComparisonReader` (TDD)

**Files:** new `Features/ServiceInventory/VersionComparisonReader.cs`,
`ServiceInventoryRegistration.cs`; `Features/Deployments/DeploymentService.cs`
(`LastSucceededVersionsAsync`); `Features/Deployments/Models/DeploymentDtos.cs`
(`VersionComparisonDto` and its parts).

**Tests** (new `tests/Platform.Api.Tests/Features/ServiceInventory/VersionComparisonReaderTests.cs`):

- availability: flag off, no snapshot and no block each give `Unavailable`;
- join: `stage` joins `staging`; a name differing by case, `.` or `_` joins; a retired environment's
  entry is left out; a service under two products gets the entry twice; a service not in the block
  is "not covered — service"; an environment the service does not declare is "not covered —
  environment" and carries its `undeclared` record;
- pass-through: both comparisons, every `since`, `detail`, `path`, `workloads` and `mixed` are
  unchanged — including an entry whose versions are equal but whose published comparison is `pending`
  (the portal does not correct it) and one with `pending` and `drift` together;
- no derivation: a pull-model entry (declared `known`, applied `unknown`) whose portal applied
  version differs from the declared one has `declaredVsApplied: unknown` and `appliedDiffers: false`;
- `portalApplied`: the newest succeeded event when the newest failed; null when nothing succeeded;
  events before a `removed` ignored;
- `appliedDiffers`: true only for a `known` published applied that differs; `v1.4.0` against `1.4.0`
  is not a difference, `1.4.0+abc` against `1.4.0` is; never for `unknown` or `none`;
- `sourceReadAt` and `readInLastRun` from `sources` and `generated_at`; none for a value without
  `source`;
- `promotionId`: for an Approved and for a Deploying candidate of the declared version, with the
  comparison `pending` and with it `unknown`; none for Pending, Deployed, Superseded, Rejected,
  another version, or a comparison that is `in_sync` or `none`;
- cost: at most one succeeded-versions query and one promotions query per request.

### Task 3: Endpoints and MCP (TDD)

**Files:** `Features/Deployments/DeploymentService.cs` (`GetState`, `GetStateSummary`,
`GetServiceDetail` attach `Versions`), `DeploymentEndpoints.cs`,
`Features/ServiceInventory/ServiceInventoryEndpoints.cs` (`/versions`, `/observed`),
`Mcp/DeploymentTools.cs`.

**Tests:** `tests/Platform.Integration.Tests/DeploymentStateReadPathTests.cs` — with the flag off and
with a status file without the block, both views of `/state` and the service detail equal today's
plus `versions: null`; with the block, a covered cell carries it and an uncovered one is null.
`ServiceInventoryEndpointTests.cs` — `/versions` filters by product, environment and `difference`;
reports `published: false` with `200`; `observedPublished` false for the phase A fixture and true for
phase B; marks `hasDeployment`; returns `undeclared` and `coverage`; is `404` with the flag off.
`/observed` carries `versions` with the published field names, and `null` for a service outside the
block. `McpServerTests.cs` — `get_deployment_state` and `get_service` carry the fields when present
and omit them otherwise.

**Accept:** review focus 3 checked by reading the constructor graph; no change to any write path.

### Task 4: Web — types, marker, matrix, filter (phase A)

**Files:** `src/Platform.Web/src/lib/types.ts` (`VersionComparison` on `DeploymentStateEntry`),
`lib/api.ts` (`getInventoryVersions`), `app/service-inventory/inventoryTypes.ts`
(`InventoryObservedResponse.versions`), new `app/deployments/VersionMarker.tsx` (markers, panel,
label text — shared by Tasks 4, 5 and 9), `app/deployments/ProductDeploymentsPage.tsx` (`MatrixCell`,
the count line, the **Show only** filter with **Rollout pending** and **Unknown**, cells for entries
without a deployment).

### Task 5: Web — service page and Services page (phase A)

**Files:** `app/deployments/ServiceDetailPage.tsx` (`EnvironmentCard`, the section note),
`app/service-inventory/ServiceInventorySection.tsx` (**Runs in** rows; environment keys through the
existing `resolveEnvKey`), `app/services/ServicesPage.tsx` (marker in the version cells, filter,
count line).

### Task 6: Manual checklist (dev server, against the two fixtures)

- push model: in sync, rollout pending with and without a promotion, `none`, applied `unknown` with
  its `detail`, `appliedDiffers`, a source not read in the last run;
- pull model: declared shown, applied labelled as the portal's, muted "?", **no** Rollout pending
  marker even when the two versions differ on screen, the approved-promotion line in the panel;
- not compared: a service outside the catalog; an environment the catalog does not declare, with and
  without an `undeclared` record; a catalog environment without a deployment (matrix cell and
  **Runs in** row);
- phase A fixture: no Drift marker, filter option, column or second "?" anywhere; Observed reads
  "not observed yet";
- phase B fixture (Task 9): drift, pending and drift together, `mixed` with its workloads, observed
  `none`, a cluster that was not read;
- the panel opens by keyboard and the cell's spoken label carries the same text;
- the filter with each option and combined; the count line; a failed status refresh;
- a status file without the block, and the inventory flag off: today's pages and no request to
  `/service-inventory/versions`;
- a product with 100+ services: the matrix is still readable and no slower to open.

### Task 7: Docs

- `notes/service-inventory-api.md`: `GET /versions`; `versions` on `/observed`; the states of a value
  and of a comparison; "unknown is not in sync, none is not unknown, not covered is neither"; what
  each catalog phase fills.
- `notes/deployment-ingest-api.md` ("Reading current state"): the optional `versions` field on
  `/state` and the service detail, and that `version` remains the newest event.
- `notes/mcp-server.md` and `.claude/skills/infrapilot-api/SKILL.md`: the new fields of
  `get_deployment_state` / `get_service`, the versions endpoint, and how to read a missing value.
- `notes/integrations/flux.md`: one paragraph — the portal's deploy event is the applied record of
  the pull model; what Flux does not report reaches the portal only through the status feed.
- `GLOSSARY.md`, under "Service inventory": **Declared version** (_Avoid_: desired version, target
  version), **Applied version**, **Observed version**, **Rollout pending** (_Avoid_: pending, out of
  date) and **Drift** (_Avoid_: mismatch, out of sync), with the relationship "the service catalog's
  reconciler compares them; the portal displays the result and compares nothing itself".
- `docs/plans/service-inventory.md`: a line under "What reads what" that the status snapshot also
  feeds the deployments pages.

### Task 8: What's new entry (phase A)

In the PR that ships Tasks 4–5, dated the day it merges:
`src/Platform.Web/src/app/whats-new/entries/YYYY-MM-DD-declared-and-applied-versions.md`

```markdown
---
title: See a change that is declared but not deployed yet
date: YYYY-MM-DD
audience: everyone
link: /deployments
linkLabel: Deployments
---
A product's **State** tab and each service's **Environments** now show the version that is declared
for an environment next to the one that was applied. **Rollout pending** marks a change that is
declared but not installed yet, and says since when — also when the change was made outside the
portal. Use **Show only** to list just those.

The comparison comes from the service catalog, so it appears for services the catalog knows; a value
it could not read is shown as unknown. An admin turns it on with **ServiceInventory** in
**Settings → Feature Flags**.
```

Task 0, if shipped separately, gets its own short entry ("See promotions that are approved and
waiting for their deploy").

### Task 9: Drift (phase B)

When the catalog publishes observed values. **Files:** `VersionMarker.tsx` (Drift marker, workloads
in the panel), `ProductDeploymentsPage.tsx` and `ServicesPage.tsx` (the **Drift** filter option, the
second "?"), `ServiceDetailPage.tsx` (the Observed row). No API change: the fields exist since Task 3
and `observedPublished` switches the UI. Checked with the phase B part of Task 6. What's new:
`YYYY-MM-DD-observed-version-and-drift.md` —

```markdown
---
title: See when what runs differs from what was deployed
date: YYYY-MM-DD
audience: everyone
link: /deployments
linkLabel: Deployments
---
Next to the declared and applied versions, the portal now shows the version that is actually running.
**Drift** marks an environment where it differs from the last install, and says since when. **Show
only** → **Drift** lists them.
```

### Task 10: Verify

`dotnet test` for both test projects; `npx tsc -b` and `npm run build` in `src/Platform.Web`; the
web dev server with `/whats-new` open; Task 6's checklist; on the test environment, with the catalog's
phase A running: one catalog service of `mkp` promoted through `mkp-release` (rollout pending appears
after approval and clears after the deploy), one change committed to `mkp-release` outside a
promotion (pending without a promotion link), and one service on `ops-release` (declared shown, no
marker), each compared with what the pages show.

## Open points

1. **Catalog-side points that change what the portal shows.** They are open in the catalog spec and
   are decided there; the portal's reader tolerates each outcome, the display does not look the same:
   - *The applied record of the pull model.* As published, every pull-model service has applied and
     both comparisons `unknown` — in phase B too — so neither marker can ever appear for it. If the
     catalog later reads the portal's deploy events, `applied.source` will name the portal and the
     portal's own row is not repeated; the value of that `source` has to be agreed then. The portal
     comparing by itself is excluded by the global constraints and would need a decision on both
     sides.
   - *Environment naming of the pull-model repository.* Its `staging` folder may not correspond to
     the catalog's `stage`. Until that is settled, such a service can appear with `none` in an
     environment where the portal has a deploy, and with its real folder under `undeclared`. The
     portal shows exactly that (two applied rows; "the catalog does not declare this environment"),
     which is correct and will confuse; step 2 should not go to production for pull-model products
     before it is answered.
   - *The `none` state.* If it is folded into `unknown`, "nothing declared here" becomes a muted "?"
     and the filter **Unknown** grows accordingly; no portal change is needed.
2. **A rollout in progress in phase B** is published as `mixed` and therefore `drift` for a run or
   two. The portal shows Drift with a young `since`. Whether the Drift marker should be visually
   quieter below some age is a display choice that would have the portal interpret the age; not
   planned.
3. **History retention.** This plan keeps no history beyond the raw status snapshots (7 days full,
   90 days daily). "How long was this service drifting last month" or a trend chart needs a
   projection table and a retention rule. Not planned until asked for.
