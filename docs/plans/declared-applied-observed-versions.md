# Declared, applied and observed versions — implementation plan

> **Status:** draft for review, 2026-11-22. Decision D-29 of the "deployments and releases" brief:
> requirement W-16, logic L-21, API P-17, screen E-22. Delivered **after** stage 1
> (`docs/plans/service-list-and-catalog-product.md`). Governed by
> `docs/adr/0004-the-portal-reads-published-artifacts.md` and
> `docs/adr/0005-the-catalog-is-the-source-of-a-services-product.md`.

**Goal:** For every service and environment, show three versions — the one that is **declared**, the
one that was **applied** and the one that is **observed** running — and mark the two differences
between them: a rollout that is still pending, and drift. Today the portal shows one version per
cell and cannot tell either.

**Architecture:** The service catalog's reconciler is the only place that compares the three
versions. It publishes the result in its status artifact, which the portal already fetches and
stores as the `status` feed of the service inventory (`src/Platform.Api/Features/ServiceInventory`).
This plan reads one more, optional, block of that file, joins it to the portal's cells by service
name and environment when a page is read, puts the portal's own applied version and the promotion
that delivers the declared version beside it, and displays it. Nothing new is fetched, no table is
added, and nothing in the delivery flow reads the result.

**Tech stack:** ASP.NET Core net10.0 Minimal API, xUnit + NSubstitute; React 19 + TypeScript. No
schema change, so no migration.

**Spec:** the task brief (D-29; D-19 "the portal gates nothing on catalog data"), ADR 0004, ADR 0005.
The catalog side — which sources the reconciler reads and the exact shape it publishes — is specified
in the service catalog repository and is **not final**; this plan is written against the assumed
shape in "The status block" and must be aligned with that spec before Task 1 starts (Open point 1).

**Not in this plan:** the read-only **Services** page for every user (D-28) is planned in stage 1;
this plan only adds its columns to it. Infrastructure state (namespaces, identities, databases) —
the other axis of desired / applied / observed — is already shown by the **Service inventory**
section and is not touched.

## The three versions, in the portal's words

| Term | Meaning | Where it comes from | Who reads it |
|---|---|---|---|
| **Declared version** | The version the environment is *supposed* to run: what the release repository says for that service and environment (`mkp-release` in the push model, `ops-release` in the pull model). | Files in the release repository | the catalog's reconciler |
| **Applied version** | The version the deployer last *installed successfully*. Two records of it exist: the release repository's own deployment status, and the portal's deploy events. | Release repository deployment status; the portal's deploy events | the reconciler (both); the portal (its own events only) |
| **Observed version** | The version actually *running* in the cluster. | Helm releases and pods | the catalog's reconciler |

| Difference | Meaning | Typical cause |
|---|---|---|
| **Rollout pending** (`pending`) | declared ≠ applied: a change is declared and has not been installed yet. | An approved promotion whose deploy has not finished; a commit to the release repository outside a promotion; a deployer that is stopped or suspended |
| **Drift** (`drift`) | applied ≠ observed: what runs is not what was last installed. | A manual change in the cluster; an install that failed half-way; a change the deployer corrected or reverted without reporting it |

Both carry an age: since when the difference has existed. A value that could not be read is
**unknown** — never "in sync" and never "absent".

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
  `stale`, `unverified`, `warnings`, `onboarding` and `freshness`, and the reconciler emits an entry
  only for services that are in the catalog.

## Global constraints

- **The portal never reads a cluster or a release repository.** No new outbound call, no new feed, no
  new credential. Everything comes through the existing `status` feed and its rules (ADR 0004).
- **The reconciler's comparison is displayed, not recomputed.** The portal does not compare the
  published versions, does not derive `pending` or `drift`, and does not compute how long a
  difference has existed. One exception: the portal may show **its own applied version** — from its
  deploy events — next to the reconciler's, and when the two differ it shows both and **says which is
  which**. It still does not change the published marker.
- **Unknown is shown as unknown.** A value the reconciler could not read, a comparison it reports as
  `unknown`, a comparison value the portal does not recognise, and a service or environment the block
  does not cover are each shown as such — never as in sync, never as absent, never as "not deployed".
- **Nothing is gated on these values.** No promotion, approval gate, freeze, release check, rollback
  or webhook reads them (D-19). The reader is referenced only by read endpoints and MCP read tools.
- **Works only where the service inventory is on** (`features.serviceInventory`), and where the
  status file carries the block. Otherwise every page and response is exactly today's: no marker, no
  empty placeholder, no error.
- **Derived copy.** The block is read from the stored raw status snapshot; nothing derived from it is
  written to delivery tables. Dropping the inventory's storage loses nothing (ADR 0004).
- **Wording.** In the UI the `pending` difference is labelled **Rollout pending**, not "Pending":
  **Pending** already means a promotion awaiting approval (`GLOSSARY.md`, `PendingPromotionChip`).
  The API keeps the published value `pending`.
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
2. **No history in the portal.** The age of a difference comes from the artifact's own since-when
   timestamps, so no history is needed to show it. Raw status snapshots are kept as today
   (`InventoryRetention`: all for 7 days, then the last of each day for 90 days), which is enough to
   answer "what did it say last Tuesday" by hand. A trend or history view is Open point 3.
3. **The join is computed when read**, by service name and canonical environment. It is not written
   onto `service_list` or onto deploy events.
4. **The portal's applied version is the newest *succeeded* deploy event of the cell**, not the cell's
   newest event: a failed or running deploy applied nothing.
5. **The matrix stays one version per cell.** A marker is added; the three versions are one click
   away.
6. **Not covered is not unknown.** A service the block has no entry for gets no marker and a page-level
   note says how many services are compared; an entry the reconciler marks `unknown` gets the unknown
   marker. With 2 of 361 services in the catalog today, marking every uncovered cell would bury the
   matrix.

## Review focus

1. **Unknown never reads as in sync.** Missing block, missing entry, `unknown`, an unrecognised
   comparison value, a null version and a failed feed refresh each have a test and a distinct display.
   → Tasks 1, 2, 4.
2. **Nothing recomputed.** No code path compares `declared`, `applied` and `observed` to produce a
   marker or an age. The only comparison the portal makes is its own applied version against the
   published one, and it changes a label, not the marker. → Task 2 tests.
3. **Nothing gated.** `VersionComparisonReader` is injected only into `DeploymentEndpoints`,
   `ServiceInventoryEndpoints` and `Mcp/DeploymentTools`; nothing under `Features/Promotions`,
   `Features/Releases` or `Features/Rollbacks` references it.
4. **Backward compatibility.** `GET /api/deployments/state` (both views) and
   `GET /api/deployments/services/{product}/{serviceName}` return byte-for-byte today's rows plus one
   field that is `null` when the feature is unavailable. A status file without the block is accepted
   exactly as today. → Tasks 1, 3.
5. **A broken block must not take the rest of the status file down, and must not be half-read.** The
   rule chosen in "Reading the block" is deliberate; check it against ADR 0004's "rejected whole".
6. **The join.** `stage` → `staging`; a name that differs only by case, `.` or `_`; a retired
   environment; a service stored under two products; an entry for a service the portal has never
   seen. → Task 2 tests.
7. **Cost of the matrix read.** One extra query at most (succeeded versions for the few cells whose
   newest event did not succeed, promotions for the cells marked pending); no per-cell query.

---

## Design

### The status block (assumed shape)

Assumed until the catalog's spec is final (Open point 1). One optional top-level block, keyed by
service **name** and not nested under `services[]`, because `services[]` lists only catalog services
and the reconciler already reports things outside the catalog at the top level (`discovered`).

```jsonc
"versions": {
  "observed_at": "2026-11-22T10:30:00Z",        // when this block was last computed
  "entries": [
    {
      "service": "mkp-extension-adobe",
      "environment": "stage",                     // the catalog's key
      "desired":  { "version": "1.4.0" },         // null version = could not be read
      "applied":  { "version": "1.3.2" },
      "observed": { "version": "1.3.2" },
      "state": "pending",                         // pending | drift | in_sync | unknown
      "pending_since": "2026-11-22T08:05:00Z",    // declared ≠ applied since; null when it does not hold
      "drift_since": null,                        // applied ≠ observed since
      "freshness": {                              // when each source was last read for this entry
        "desired": "2026-11-22T10:30:00Z",
        "applied": "2026-11-22T10:30:00Z",
        "observed": "2026-11-22T09:00:00Z"        // older than observed_at: the last read failed
      }
    }
  ]
}
```

The portal calls the first state **declared**; the artifact's field is `desired`. Both differences
can hold at once (declared 1.4.0, applied 1.3.2, running 1.3.1); the two since-when fields carry
that, and `state` is the one the reconciler puts first.

### Reading the block

`Documents/Documents.cs`: `StatusDocument` gains `Versions` (`StatusVersions?`), with
`StatusVersionEntry`, `StatusVersionValue` and the freshness as typed records; every field nullable
except `service` and `environment`.

`DocumentReader.ReadStatus`:

- **Absent block** → `Versions` is null; the file is accepted as today. `schemaVersion` stays 1
  (producers add fields without a new version; `SupportedVersions` is unchanged).
- **Structurally invalid block** (not an object, `entries` not a list, a null entry, an entry with no
  `service` or `environment`) → the file is **rejected whole**, like any other list with a null entry.
  The last good copy keeps being served and the feed shows the error. This follows ADR 0004 and is
  preferred over dropping only the block: a silently dropped block would turn "broken" into "not
  published".
- **Unrecognised `state` value** → the entry is kept and reads as `unknown`. Producers may add values;
  an unknown value must not become "in sync" and must not reject the file.
- Unknown fields are ignored, as everywhere in the reader.

### What is stored, and how large it gets

Nothing new. The block travels inside the status snapshot the refresher already stores
(`InventoryRefresher`, `InventorySnapshot.ContentGzip`).

Size, on the assumptions of `docs/plans/service-inventory.md` ("Size after one year": 500 services,
8:1 compression): 500 services × 4 environments × ~350 bytes ≈ 700 KB per file, so a status file
grows from ~870 KB to ~1.6 MB. Kept copies stay (7 × 48 + 90) = 426, so stored status grows from
~46 MB to ~85 MB a year. The file stays far below `ServiceInventoryOptions.MaxResponseBytes` (20 MB).
One parsed document per current snapshot is held in memory for an hour, as today.

Rejected: a projection table (`inventory_version_states`, one row per service × environment per
current snapshot). It would allow SQL filtering, but every consumer here already holds the matrix in
memory, and it costs two migrations and a retention rule. It becomes worth it only with a history
view (Open point 3).

### Joining a cell to an entry

New `Features/ServiceInventory/VersionComparisonReader.cs` (scoped). One call per request:

```
Task<VersionComparisons> ForAsync(IReadOnlyCollection<(string Product, string Service, string Environment)> cells, ct)
```

1. Flag off, no status snapshot, or `Versions` null → `VersionComparisons.Unavailable` (callers emit
   `versions: null`).
2. Index the entries by (service name, environment):
   - **service**: the normal form of the name. Stage 1 introduces `CatalogName.Normalize` (trim,
     lower-case, `.` and `_` → `-`) and this plan uses it; it is the same rule stage 1 uses to match a
     portal service to a catalog id. (Today the inventory compares by trim and lower-case only —
     `ServiceInventoryEndpoints.Key`.) The name is the identity (ADR 0005), so the product is not part
     of the key: a service stored under two products gets the same entry in both.
   - **environment**: `EnvironmentAliasMap.Resolve` of the entry's key, so `stage` lands on `staging`.
     Entries whose environment resolves to a retired one (`EnvironmentAliasResolver.RetiredKeysAsync`)
     are left out, as on every current-state read. An environment the portal has no configuration for
     passes through unchanged and is reported, not dropped.
3. Each cell gets its entry or none.

Entries that match **no** portal cell — a service running somewhere the portal never received a
deploy event for — are not added to the state matrix response (its rows are deploy events and carry
an event id). They are returned by the new versions endpoint (below) and shown by the web as cells
of their own. Their product comes from stage 1's `service_list` (`Product`); an entry whose service
the list does not hold has no product and appears only on the unfiltered listing. This reverse
direction is the one place this plan needs the stored service list.

### The portal's own applied version

For each matched cell the reader adds `portalApplied`:

- newest event `succeeded` → that event's version (already loaded, no query);
- newest event `failed` or `in_progress` → the version of the cell's newest `succeeded` event, read in
  **one** query for all such cells of the request (new `DeploymentService.LastSucceededVersionsAsync`,
  same index as `LatestStateEvents`; events older than the cell's last `removed` do not count);
- none → null ("the portal has no successful deploy on record").

`appliedDiffers` is true when `portalApplied` and the published applied version are both known and
are not the same string (ordinal, after trim). It is the only comparison the portal makes. When true,
the UI shows both, labelled **Applied — portal's deploy events** and **Applied — release repository**,
with the deploy event's time beside the first. The usual causes are a deploy that was never reported
to the portal and a deploy event posted for an install the release repository does not record; either
is worth seeing, and the portal does not decide which is right.

Whether the reconciler's applied record in the pull model is itself built from the portal's deploy
events is Open point 2; if it is, the two can never differ there and the second line is not shown.

### Rollout pending and the promotion that delivers it

For cells whose entry has `pending_since` set, the reader looks up — one query for the request —
`PromotionCandidate`s with the same product, service, `TargetEnv` = the cell's environment,
`Version` = the declared version and `Status` `Approved` or `Deploying`. This is the same match the
promotion hook and the Flux endpoint use to find the candidate a deploy delivers. The newest match is
returned as `promotionId`.

- A link exists → "Rollout pending — promotion approved, waiting for the deploy", linked to the
  promotion.
- No link → "Rollout pending — no promotion in the portal delivers this version". That is the case
  W-16 exists for: a change declared outside the portal.

The link is context. It never changes the marker, and a promotion that is `Approved` while the
reconciler reports `in_sync` or `unknown` adds nothing here (it keeps its own chip, see "What can
ship first").

### Age and staleness

- **Age of a difference**: `pending_since` / `drift_since` as published, rendered relative ("for
  3 h"). The portal never starts its own clock: its first sight of a difference is the first fetch
  after it, not the moment it began.
- **A source that was not read**: an entry's `freshness.<source>` older than the block's
  `observed_at` means the reconciler's last run could not read that source and kept the previous
  value. The version is shown with "last read <age> ago" and the marker gets the muted "may be out of
  date" style. This compares two published timestamps; it does not touch the versions.
- **The portal's copy**: the existing `FeedFreshness` of the status feed
  (`InventoryReadService.Freshness`) — when the copy was fetched and whether the last refresh failed —
  shown once per page, as the **Service inventory** section already does.

### API (P-17)

All additions are optional fields or new routes; nothing existing changes shape.

**`GET /api/deployments/state`** (both views) and **`GET /api/deployments/services/{product}/{serviceName}`**:
each row (`DeploymentStateDto`, `DeploymentStateSummaryDto`, and so `ServiceDetailDto.Environments`)
gains a trailing optional `Versions`:

```jsonc
"versions": {                       // null: inventory off, block not published, or no entry for this cell
  "declared": "1.4.0",              // null = unknown
  "applied": "1.3.2",               // as published by the reconciler
  "observed": "1.3.2",
  "state": "pending",               // pending | drift | in_sync | unknown, as published
  "pendingSince": "2026-11-22T08:05:00Z",
  "driftSince": null,
  "portalApplied": "1.3.2",         // the portal's newest succeeded deploy event; null = none on record
  "appliedDiffers": false,
  "promotionId": "…",               // Approved or Deploying promotion of the declared version, or null
  "sourceReadAt": { "declared": "…", "applied": "…", "observed": "…" },
  "computedAt": "2026-11-22T10:30:00Z"
}
```

`version` on the row keeps today's meaning (the newest event's version). Responses do not say *why*
`versions` is null per row; `GET /api/service-inventory/versions` does.

**`GET /api/service-inventory/versions?product=&service=&environment=&state=`** (new; behind the
inventory flag like the rest of the group; signed-in user or API key):

```jsonc
{
  "published": true,                // false: the status file carries no versions block
  "computedAt": "…", "generatedAt": "…",
  "entries": [ { "service": "…", "product": "mkp", "environment": "staging", "hasDeployment": true,
                 /* the fields above */ } ],
  "freshness": { … }                // the status feed's, as on every inventory response
}
```

`state` accepts `pending`, `drift`, `unknown`, `in_sync`, comma-separated; `pending` and `drift`
match on the since-when fields, so an entry with both is found by either. `product` filters through
`service_list`. `hasDeployment: false` marks an entry with no portal cell. With `published: false`
the list is empty and the status is still `200`.

**`GET /api/service-inventory/services/{serviceName}/observed`** gains `versions`: that service's
entries (one per environment, same fields without `portalApplied` and `promotionId`, which need a
product), or `null` when the block is not published. `404` stays "the status file has no entry for
the service" only when neither `services[]` nor the versions block knows it.

**MCP** (`Mcp/DeploymentTools.cs`): no new tool. `get_deployment_state` and `get_service` add, per
environment and only when present, `declared`, `observed`, `versionState`, `pendingSince`,
`driftSince` and `appliedDiffers`; the tool descriptions say that `version` is the newest deploy
event and that a missing `versionState` means "not compared", not "in sync".

### Screens (E-22)

The service × environment matrix is the **State** tab of `ProductDeploymentsPage` (`MatrixCell`);
`DeploymentsPage` is the product overview and is not changed.

**Matrix cell — compact form.** The cell keeps showing one version, status and age exactly as today
(the newest deploy event). Added, to the right of the version:

| Published | Cell |
|---|---|
| `in_sync` | nothing |
| `pending_since` set | marker **Rollout pending** (an upward arrow) and the declared version in small type: `1.3.2 ↑ 1.4.0` |
| `drift_since` set | marker **Drift** (≠), warning colour |
| both | both markers, drift first |
| `unknown` | muted **?** |
| no entry / block not published / inventory off | nothing — today's cell |
| `appliedDiffers` | a dot on the version; no text in the cell |

The marker is a button that opens a small panel with the three versions, their ages and sources, the
age of the difference, the promotion link and, when they differ, both applied versions with their
labels. The same text is appended to the cell's keyboard label (`useKeyboardListRow`), so it is not
hover-only. The existing promotion chip and freeze cue keep their places.

An entry with `hasDeployment: false` in an environment the matrix already has as a column fills the
empty cell with the observed version in muted type and "not reported to the portal"; it opens the
panel, not a deploy event. Entries in environments the product has no column for are not turned into
columns.

**Above the matrix**: a line "Versions compared for N of M services · status read <age> ago", with
the feed's refresh error when there is one; and a filter **Show only**: **Rollout pending** /
**Drift** / **Unknown** (multi-select, URL parameter `diff`, client-side over the loaded matrix, like
the page's service search). It keeps a service row when any of its cells matches. With no block
published, neither the line nor the filter is rendered.

**Service page — Environments section** (`ServiceDetailPage`, `EnvironmentCard`): each card gains
three labelled rows — **Declared**, **Applied**, **Observed** — with the marker and "for <age>" under
them, and a line naming the sources' last read when one is behind. When `appliedDiffers`, **Applied**
becomes two rows with the labels from "The portal's own applied version". Unknown values read
"unknown — could not be read", never a dash. A card without an entry reads "Not compared: the service
catalog publishes no versions for this service here." once per section, not per card.

**Services page** (D-28, stage 1): two columns, **Rollout pending** and **Drift**, each the count of
environments with its oldest age, sortable; the same **Show only** filter. Added only if that page
has shipped; otherwise it follows it.

**Without the inventory** (flag off, or status feed never fetched): every page is today's. No marker,
no filter, no note, no extra request — the web skips `getInventoryVersions` unless
`useFeatureFlag(FeatureFlag.ServiceInventory)` is on.

### What can ship first

The reconciler does not publish versions yet (`scripts/lib/reconcile-core.ts` in the catalog emits
onboarding steps, warnings, freshness and the cloud scan — no version data).

- **Tasks 1–3 can ship before it does**, dark: the reader accepts the block when it comes, the API
  fields are `null`, `published` is `false`. No user-visible change, no What's new entry.
- **A "pending" marker derived only from the portal's own promotions should not ship as this
  feature.** It would cover only changes that went through the portal — which is the half the portal
  already knows — and miss every change made outside it, which is what W-16 is for. A matrix where
  some cells say "pending" and the others say nothing would be read as "the others are in sync",
  and the filter "show only pending" would return a list that looks complete and is not.
- **What is honest and useful on its own** (Task 0, optional, independent of everything else): extend
  the existing promotion chip on the matrix to promotions that are `Approved` or `Deploying`, worded
  as a promotion fact — "Approved, waiting for deploy" — next to today's "awaiting approval". It makes
  no claim about versions, has no filter and no "in sync" counterpart, and is replaced by nothing
  later: when the comparison arrives, the chip and the marker sit side by side and the panel links
  them.

Recommendation: ship Task 0 and Tasks 1–3 now; Tasks 4–8 when the catalog publishes versions for at
least one product on the test environment.

### Dependencies and order

1. Stage 1, step 1a (`docs/plans/service-list-and-catalog-product.md`): `CatalogName.Normalize`, the
   `service_list` table (product of an entry without a portal cell), the `stage` alias, and the
   Services page for its two columns. Tasks 1–3 need only `CatalogName.Normalize` and the alias; if
   stage 1 has not merged, they use `ServiceInventoryEndpoints.Key` and switch in stage 1's PR.
2. The catalog publishes the versions block in its status artifact (its own change spec).
3. This plan's Tasks 4–8.

---

## Tasks

Tests first in every API task. Test names follow the repository's sentence style
(`Observed_state_keeps_unresolved_and_unknown_as_published`).

### Task 0 (optional, independent): chip for approved promotions

**Files:** `src/Platform.Web/src/app/deployments/ProductDeploymentsPage.tsx` (the `listPromotions`
call reads `Pending`, `Approved` and `Deploying`; `PendingPromotionChip` and the cell label word the
three), `ServiceDetailPage.tsx` (its chip, same wording).

**Accept:** a cell with an Approved or Deploying promotion shows "Approved, waiting for deploy" and
links to it; a Pending one reads as today; no other change. Checked in the dev server.

### Task 1: Read the block (TDD)

**Files:** `Features/ServiceInventory/Documents/Documents.cs`, `Documents/DocumentReader.cs`;
fixtures `tests/Platform.Api.Tests/Features/ServiceInventory/Fixtures/status-with-versions.json`
(the existing `status.json` stays without the block).

**Tests** (`DocumentAndProjectorTests`): a status file without the block is accepted and
`Versions` is null; with the block, entries and timestamps are read; an unrecognised `state` reads as
unknown; a null version stays null; a null entry, a non-list `entries` and an entry without
`service` reject the file with a reason; unknown fields inside an entry are ignored; the projector
still returns no rows for a status file.

**Accept:** the existing inventory tests pass unchanged; `SupportedVersions` unchanged.

### Task 2: `VersionComparisonReader` (TDD)

**Files:** new `Features/ServiceInventory/VersionComparisonReader.cs`,
`ServiceInventoryRegistration.cs`; `Features/Deployments/DeploymentService.cs`
(`LastSucceededVersionsAsync`); `Features/Deployments/Models/DeploymentDtos.cs`
(`VersionComparisonDto`).

**Tests** (new `tests/Platform.Api.Tests/Features/ServiceInventory/VersionComparisonReaderTests.cs`):
flag off, no snapshot and no block each give `Unavailable`; `stage` joins `staging`; a name differing
by case, `.` or `_` joins; a retired environment's entry is left out; a service under two products
gets the entry twice; a cell without an entry gets none; `state`, both since-when values and the
source times are passed through unchanged — including an entry whose versions are equal but whose
published state is `drift` (the portal does not correct it); `portalApplied` is the newest succeeded
version when the newest event failed, null when nothing succeeded, and ignores events before a
`removed`; `appliedDiffers` only when both are known; `promotionId` for an Approved and for a
Deploying candidate of the declared version, none for Pending, Deployed, Superseded, Rejected or
another version; the request issues at most one succeeded-versions query and one promotions query.

### Task 3: Endpoints and MCP (TDD)

**Files:** `Features/Deployments/DeploymentService.cs` (`GetState`, `GetStateSummary`,
`GetServiceDetail` attach `Versions`), `DeploymentEndpoints.cs`,
`Features/ServiceInventory/ServiceInventoryEndpoints.cs` (`/versions`, `/observed`),
`Mcp/DeploymentTools.cs`.

**Tests:** `tests/Platform.Integration.Tests/DeploymentStateReadPathTests.cs` — with the flag off and
with a status file without the block, both views of `/state` and the service detail equal today's
plus `versions: null`; with the block, a cell carries it. `ServiceInventoryEndpointTests.cs` —
`/versions` filters by product, environment and state, reports `published: false` with `200`, marks
`hasDeployment`, is `404` with the flag off; `/observed` carries `versions` and is found for a service
only the versions block knows. `McpServerTests.cs` — `get_deployment_state` and `get_service` carry
the fields when present and omit them otherwise.

**Accept:** review focus 3 checked by reading the constructor graph; no change to any write path.

### Task 4: Web — types, matrix marker, filter

**Files:** `src/Platform.Web/src/lib/types.ts` (`VersionComparison` on `DeploymentStateEntry`),
`lib/api.ts` (`getInventoryVersions`), `app/service-inventory/inventoryTypes.ts`,
new `app/deployments/VersionMarker.tsx` (marker, panel, label text — shared with Task 5),
`app/deployments/ProductDeploymentsPage.tsx` (`MatrixCell`, the note, the **Show only** filter, cells
for entries without a deployment).

### Task 5: Web — service page and Services page

**Files:** `app/deployments/ServiceDetailPage.tsx` (`EnvironmentCard`, the section note); the
Services page from stage 1 (two columns and the filter) (E-21, Task 8a of `docs/plans/service-list-and-catalog-product.md`).

### Task 6: Manual checklist (dev server, against a status fixture with the block)

- in sync, rollout pending with and without a promotion, drift, both at once, unknown, a source not
  read in the last run, `appliedDiffers`, an entry without a deployment — each on the matrix and on
  the service page;
- the panel opens by keyboard and the cell's spoken label carries the same text;
- the filter with each option and combined; the count line; a failed status refresh;
- a status file without the block, and the inventory flag off: pixel-for-pixel today's pages and no
  request to `/service-inventory/versions`;
- a product with 100+ services: the matrix is still readable and no slower to open.

### Task 7: Docs

- `notes/service-inventory-api.md`: `GET /versions`, `versions` on `/observed`, the assumed block and
  the rule "unknown is not in sync; not covered is not unknown".
- `notes/deployment-ingest-api.md` ("Reading current state"): the optional `versions` field on
  `/state` and the service detail, and that `version` remains the newest event.
- `notes/mcp-server.md` and `.claude/skills/infrapilot-api/SKILL.md`: the new fields of
  `get_deployment_state` / `get_service`, the versions endpoint, and how to read a missing value.
- `notes/integrations/flux.md`: one paragraph — what Flux does not report reaches the portal through
  the status feed, not through this endpoint.
- `GLOSSARY.md`, under "Service inventory": **Declared version**, **Applied version**, **Observed
  version**, **Rollout pending** (_Avoid_: pending, out of date) and **Drift** (_Avoid_: mismatch,
  out of sync), with the relationship "the service catalog's reconciler compares them; the portal
  displays the result".
- `docs/plans/service-inventory.md`: a line under "What reads what" that the status snapshot also
  feeds the deployments pages.

### Task 8: What's new entry

In the PR that ships Tasks 4–5, dated the day it merges:
`src/Platform.Web/src/app/whats-new/entries/YYYY-MM-DD-declared-applied-observed-versions.md`

```markdown
---
title: See when what runs differs from what was deployed
date: YYYY-MM-DD
audience: everyone
link: /deployments
linkLabel: Deployments
---
A product's **State** tab and each service's **Environments** now show three versions: the one that
is declared, the one that was applied and the one actually running. **Rollout pending** marks a
change that is declared but not installed yet, **Drift** marks a running version that differs from
the last install, and both say for how long. Use **Show only** to list just those.

The comparison comes from the service catalog and is shown with its age. A value that could not be
read is shown as unknown. It appears for services the catalog covers, once an admin has turned on
**ServiceInventory** in **Settings → Feature Flags**.
```

Task 0, if shipped separately, gets its own short entry ("See promotions that are approved and
waiting for their deploy").

### Task 9: Verify

`dotnet test` for both test projects; `npx tsc -b` and `npm run build` in `src/Platform.Web`; the
web dev server with `/whats-new` open; Task 6's checklist; on the test environment, one service of
`mkp` taken through declared → applied → observed in the push model (`mkp-release`) and the pull
model (`ops-release`), and one change made outside a promotion, each compared with what the pages
show.

## Open points

1. **Field names and shape of the versions block.** This plan assumes the block in "The status
   block": top-level `versions.entries[]` keyed by service name and environment, `desired` /
   `applied` / `observed`, one `state`, two since-when fields, per-source freshness. It must be
   aligned with the catalog's change spec before Task 1. Three points to settle there, not here:
   whether the block covers services that are **not in the catalog** (if it hangs under `services[]`
   it covers 2 of 361 today); whether rollout pending and drift can be reported **at once**; and
   whether the artifact's environment key stays `stage`.
2. **Is the pull-model applied record the portal's deploy event?** The catalog's decision lists two
   applied records on the version axis: the release repository's deployment status and the portal's
   deploy events. If the reconciler takes "applied" for `ops-release` services from the portal's
   events, the portal's own applied version is the same data and `appliedDiffers` is meaningless
   there; and the reconciler then needs to read the portal, which is a dependency in the opposite
   direction of ADR 0004's "producers do not depend on the portal". Needs a decision on the catalog
   side; the portal side only hides the second **Applied** row when told the source is its own events
   (a `source` field on `applied` would do).
3. **History retention.** This plan keeps no history beyond the raw status snapshots (7 days full,
   90 days daily). If "how long was this service drifting last month" or a trend chart is wanted, a
   projection table and a retention rule are needed, and it has to be decided how long to keep it.
   Not planned until asked for.
