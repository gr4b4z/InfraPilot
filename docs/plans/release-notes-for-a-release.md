# Release notes for a release, release webhooks, analytics by train and team — implementation plan

> **Status:** specification agreed 2026-11-22 (stage 3 of "Deployments and releases in the portal").
> Not started. Depends on stage 2 (`docs/plans/release-trains.md`: trains, the completion record
> `release_shipped_services` with the union of the promotions' change sets) and, for team and
> product-at-the-time, on stage 1 (`docs/plans/service-list-and-catalog-product.md`: the stored
> service list `service_list`, the never-rewritten `ProductAtEvent` column on deploy events). Items: L-19, L-20, P-13, P-14, P-15, E-16, E-17, E-18.

**Goal:** A release manager publishes one release note for a release, built from what its promotions
shipped; receivers can be told when a release starts and completes; analytics can be filtered and
grouped by train, team and the product a deploy belonged to when it happened; the service page calls
its timeline what it is.

**Architecture:** A release note gains an optional release and a list of products; everything a
product note does today stays on the same code path. The content of a release note is assembled per
service from the release's deployed promotions (`PromotionCandidate.References`), and from deploy
events (today's `ReleaseNoteService` logic) for a service that shipped without a promotion. Webhook
events learn two more things about themselves — a set of products and a train — and two new events
are raised where `ReleaseService` today only writes the audit log. Analytics resolves a train or a
team to a set of service names and filters by that set; the product-at-the-time is a second basis
for the product filter and grouping, not a second series key.

**Tech stack:** ASP.NET Core net10.0 Minimal API + EF Core (Postgres and SQL Server migration sets),
Handlebars.Net, xUnit + NSubstitute, React 19 + TypeScript.

**Spec:** the agreed specification of 2026-11-22 (summarised here), ADR-0005, ADR-0006, ADR-0007
(`docs/adr/0007-a-release-train-is-saved-release-settings.md`: the completion record; a Release
gates nothing), `notes/release-notes.md`, `notes/releases-api.md`, `notes/analytics-api.md`.

## Global constraints

- **Nothing that works today changes.** `GET /api/release-notes/preview[/raw]` and
  `POST /api/release-notes/generate` with `product` + `environment` (+ window) answer exactly as now,
  including the automatic "since the last note" window and the `no_services` refusal. Existing
  `release_note.generated` payload fields keep their names and types; new fields are only added.
- Nothing that passes through the API today starts being rejected without a warning period. A renamed
  response field is sent under both names first.
- A release gates nothing (D-19). Generating, or failing to generate, a note never blocks a release
  step, a promotion or the completion.
- Portable SQL only: no JSON functions in queries, `""` instead of NULL in keys (ADR-0003). Every
  schema change ships a Postgres and a SQL Server migration.
- Stage-3 behaviour is behind stage 2's feature flag `features.releaseTrains` and
  `features.releaseNotes`, checked on the server for the **new** request shapes only. Team filters
  additionally need `features.serviceInventory`.
- Public repository: fixtures and examples use the placeholder names only (`mkp`, `mkp-extensions`,
  `mkp-extension-adobe`, `mkp-spotlight`, org `acmetrix`).
- Names of stage-1 and stage-2 tables, columns and flags are the ones those plans introduce. Where
  this plan speaks by role it means: "the stored service list" = `service_list` (team in
  `CatalogTeam`), read through stage 2's `IServiceDirectory`; "the product-at-the-time column" =
  `ProductAtEvent`; "the completion record" = `release_shipped_services` (`Outcome`, `Version`,
  `ReferencesJson`, `ChangeSetSource`) with `Release.RecordedAt`, read through
  `GET /api/releases/{id}/shipped`; "the release's train" = `Release.TrainId` / `TrainName`, a row
  of `release_trains`; a train's scope and the release's scope = `ReleaseTrainMembership` and
  `ReleaseScope`. Stage 2's flag is `features.releaseTrains`; stage 1's is `features.serviceList`.

## Decisions

Numbered locally (1–12); the specification's decisions are cited as D-n.

| # | Question | Answer |
|---|---|---|
| 1 | One note per release, or one per product of the release? | One note per release, covering every product. A release is announced once. After stage 2 moves extensions and support tools into `mkp`, most releases have one product anyway. |
| 2 | Model change | `ReleaseNote.ReleaseId` (nullable) plus a child table `release_note_products`. `Product` stays required and is `""` on a release note. See "The model". |
| 3 | What a release note is built from | Per service: the release's Deployed promotions into its production environment; a service with none falls back to deploy events, with today's rule. For a completed release, the completion record of stage 2. |
| 4 | Template for a release note | Its own scope: train → all releases → built-in. Product templates are not used. See "Template resolution". |
| 5 | Same webhook event or a new one for a release note? | The same two, `release_note.generated` and `release_note.generated.html`, with added fields. A receiver filtered to a product gets the note of every release that includes that product. |
| 6 | Release lifecycle events | `release.started` and `release.completed`. Cancel, reopen and edits stay audit-only. |
| 7 | Filter dimensions of release events | Products (any of the release's), environment (its production environment), train (a new subscription dimension). |
| 8 | Who may generate a release note | Whoever may generate a product note today (any signed-in caller). It is a read of recorded facts. |
| 9 | Team in analytics | The service's team now, from the stored service list. Offered only while the service inventory is enabled. |
| 10 | Train in analytics | The services in the train's scope now (D-14). |
| 11 | Product-at-the-time | A parameter, `productBasis=current\|at-event`, default `current`. It changes which product column the filter and the product grouping read; it never splits a service into two series. |
| 12 | The two change failure rates | The attempt-based figure of the frequency endpoint is renamed **deploy failure rate**, the name the delivery report already uses for the same formula. **Change failure rate** means only the incident-based DORA figure. |

## Review focus

1. A product note generated through today's calls is byte-for-byte what it was: same window default,
   same services, same payload fields. The automatic window must ignore release notes. → Task 3 tests.
2. A service that shipped without a promotion is in the release note, from deploy events, and is
   marked as such; a service that ended the release on the version it started on is left out, by the
   same state-diff rule as today. → Task 2 tests.
3. A receiver filtered to one product gets a release note exactly once when the release has two of
   its products, and not at all when the release has none. → Task 4 tests.
4. A deploy before a service's move counts under the old product with `productBasis=at-event` and
   under the new one with `current`; with `groupBy=service` the service is one series under both.
   → Task 7 tests.
5. `release.started` fires once per release however the start was recorded (first step, create,
   edit); a reopened and re-completed release fires `release.completed` again. → Task 5 tests.
6. Where the specification and the code disagree, the code was followed:
   - The specification says the release, release-note and analytics APIs have no server-side flag
     check. True (`Program.cs:520`, `:537`, `:540`), and unchanged for today's calls; only the new
     request shapes are checked. No plan adds a check to today's calls; whether to is open point 5 of
     `docs/plans/release-trains.md`.
   - `ReleaseNoteService.GetVersionsLiveBeforeAsync` and `MapService` are private and take one
     product. They are widened, not copied (Task 2).
   - `WebhookEventFilters` carries one product. A release has several, so the filter grows a set
     (Task 4); dispatching once per product would deliver twice to a receiver of two products.
   - The frequency endpoint's series key is `(Product, ServiceName, Environment)` in
     `FrequencySeriesKeyDto`; the delivery report keys service rows by `(Product, Service)`
     (`ServiceKeyComparer`). Both change to the service name (Task 7).
   - `AnalyticsEndpoints.GroupBys` and `AnalyticsTools.GroupBys` are two copies of one list.
   - The web app has no test runner; web tasks are verified by `npx tsc -b`, the build and by hand.

---

## The model

Today a `ReleaseNote` (`Features/ReleaseNotes/Models/ReleaseNote.cs`) is one product, one
environment and a window. Product is read in four places: the list filter and the hidden-products
filter in `ReleaseNoteEndpoints`, the automatic window of `/generate`, the index
`(Product, Environment, GeneratedAt)`, and the web routes `/release-notes/:product/...`.

Change:

- `ReleaseNote.ReleaseId : Guid?`, indexed, no cascade. Null is a product note. A release that is
  later deleted leaves its notes; they were sent out. The note's `RawJson` keeps the version and title.
- New table `release_note_products (ReleaseNoteId, Product)`, primary key both, index on `Product`.
  The migration fills it for every existing note from its `Product`. A product note has one row; a
  release note one row per product of the release at generation time.
- `ReleaseNote.Product` stays `varchar(200) NOT NULL`. A release note stores `""`.
- `Environment` is the release's `ProductionEnvironment`; `From`/`To` are the release's evidence
  window (`StartedAt ?? CreatedAt` to `CompletedAt ?? now`, as `ReleaseProgressService.ComputeAsync`
  reads it; bounded by stage 2's `WindowFrom` / `WindowTo` on a release that has them).

Why this shape and not the alternatives:

- **`Product` nullable or a `ProductsJson` column only.** The list filter `?product=` and the
  hidden-products rule must be SQL (they sit ahead of `Count` and paging), and JSON functions are not
  allowed. A child table is the portable way to ask "notes that cover product X".
- **Storing the single product in `Product` for a one-product release.** Then the automatic window of
  the pipelines' product notes would move whenever a release note is published. With `""` every
  existing read by `Product` keeps seeing product notes only, without a second condition to forget.
- **One note per product.** Rejected by decision 1.

`release_note_products` holds a product without a service, and it is a record of what was announced.
A service move (ADR-0006) does not rewrite it.

Reads:

| Read | Today | After |
|---|---|---|
| `GET /api/release-notes?product=X` | `r.Product == X` | a row in `release_note_products` for X |
| Hidden products | `!hidden.Contains(r.Product)` | at least one of the note's products is not hidden |
| Automatic window of `/generate` | last note of `(Product, Environment)` | unchanged; release notes have `Product = ""` and never match |
| `GET /api/release-notes?releaseId=` | — | `r.ReleaseId == id` |

DTOs: `ReleaseNoteListItemDto`, `ReleaseNoteFeedItemDto` and `ReleaseNoteDetailDto` gain `Products`
(list), `ReleaseId` and `ReleaseVersion` (both null on a product note). Their `Product` is the stored
value for a product note and the products joined with `", "` for a release note, so anything that
prints it stays readable.

## What a release note contains

`ReleaseNoteService.GetReleasePreview(release, now, ct)` returns the same `RawPreviewDto` with added
members, so `TemplateEngine`, the stored `RawJson` and the detail page keep one shape.

1. **Scope.** The services of the release that are not left out: for a completed release the
   completion record of stage 2 (`release_shipped_services`: shipped versions, services that did
   not ship); otherwise the live scope the release page shows (`ReleaseProgressService.ComputeAsync`,
   through stage 2's `ReleaseScope`). A release completed before stage 2 has no record until
   `POST /api/releases/{id}/record` writes one; until then it is read live.
2. **From promotions.** For each service, its promotions of the release into the production
   environment with status `Deployed` — the same selection the progress view makes (on the version
   line, or inside the window for a service on a scheme of its own and for an ad hoc release without
   a line). One entry per service:
   - `currentVersion`: the version of the newest deployed promotion; `previousVersion`: `FromVersion`
     of the oldest; `deployedAt`: the newest `DeployedAt`.
   - work items, pull requests, pipelines: the union of the promotions' `References`, built by the
     rule of stage 2's completion record — oldest promotion first, de-duplicated by
     `(type, provider, key)`, by `(type, url)` when there is no key, keeping the newest title. For a
     completed release this is the union the completion record already stores (`ReferencesJson`).
   - participants: the promotions' `Participants` and the references' own, de-duplicated as today.
   - `source: "promotions"`.
3. **Fallback to deploy events.** A service in scope with no deployed promotion but a succeeded
   deploy to the production environment in the window: today's logic for that one service — the
   newest succeeded event, its references — with `source: "deploy-events"`.
4. **State-diff rule, for both sources.** A service whose production version at the end of the
   window is the one that was live before it (`GetVersionsLiveBeforeAsync`) is left out.
5. **Not shipped.** Services in scope with nothing deployed are listed by name in `notShipped`; the
   template may print them.
6. **Empty guard.** No service shipped and no `renderedContent`: `400` with `code: "no_services"`,
   as today.

`RawPreviewDto` additions (all optional, so stored notes still deserialise): `Products`, `Release`
(`Id`, `Version`, `Title`, `Train`, `TargetDate`, `StartedAt`, `CompletedAt`), `NotShipped`.
`ServiceReleaseDto` additions: `Product`, `Source`.

A note is a snapshot. Generating again makes a new row; the release page links the newest.

## Template resolution

`ReleaseNoteTemplateService` stores templates under `release-notes.template.{product}[.{environment}]`
with `release-notes.template.default` as the fallback. A release note resolves:

```
release-notes.template.@release.{train}   ← this train's releases
release-notes.template.@release           ← every release
(built-in DefaultReleaseTemplate)         ← fallback
```

`@` cannot occur in a product name (D-24), so the scope cannot collide with a product's.
Product templates are skipped on purpose: they are written for one product, and a release note that
silently changed its look with the number of products would be hard to explain. The settings editor
pre-fills an empty release scope with the built-in release template.

The context is a superset of today's (`TemplateEngine.ToContext`): `product` (the products joined),
`environment`, `from`, `to`, `date`, `services` (all services, flat) stay; added are `release`
(`version`, `title`, `train`, `targetDate`), `products` (each with its own `services`), `notShipped`,
and per service `product` and `source`. The built-in release template prints a release header, then
one block per product when there are several, with the per-service lines of today's default.

## API (P-13)

| Call | Change |
|---|---|
| `GET /api/release-notes/preview/raw`, `/preview` | Accept `releaseId` instead of `product`, `environment`, `from`, `to`. Both shapes at once: `400`. |
| `POST /api/release-notes/generate` | Body accepts `releaseId` (+ optional `renderedContent`). `product` and `environment` are required only without it. |
| `GET /api/release-notes` | New filter `releaseId`; `product` also finds release notes covering it. |
| `GET`/`PUT /api/release-notes/template` | New query/body field `scope=release` with optional `train`. |

`releaseId` of a cancelled release: `400`. Unknown: `404`. With `features.releaseNotes` or
`features.releaseTrains` off, a request carrying `releaseId` answers `404`; requests without it are not checked, as today.

## Webhooks

### `release_note.generated` for a release note

Same two events. Filters: `Products` = the release's products, `Environment` = its production
environment, `Train` = its train (when it has one). Payload, added fields in **bold**:

```jsonc
{
  "id": "…", "product": "mkp, mkp-extensions", "environment": "prod",
  "from": "…", "to": "…", "generatedAt": "…",
  "renderedContent": "…",
  "services": [ { "service": "mkp-extension-adobe", "product": "mkp-extensions", "source": "promotions", /* as today */ } ],
  "products": ["mkp", "mkp-extensions"],
  "release": { "id": "…", "version": "5.10", "title": "Release 5.10", "train": "…" },
  "notShipped": [ { "product": "mkp", "service": "mkp-spotlight" } ]
}
```

A product note carries `products: ["<product>"]` and `release: null`; nothing else changes for it.

What existing consumers get: a subscription filtered to product `mkp` receives the release note of
every release that includes `mkp`, once, with all the release's services. The default message
template (`NotificationTemplates`, "Release notes — {{data.product}} / {{data.environment}}") keeps
rendering. A consumer that wants only one kind tells them apart by `release`. `release.version` is
null for an ad hoc release without a line.

This is what answers open point 1 of `docs/plans/release-trains.md` (release-note channels of the
extension services): a channel whose subscription sets the **Trains** filter to the Extensions train
receives that train's release notes and not the Core train's. Product notes carry no train and no
service, so this stage does not route them per service or per train; such a channel still receives
its product's product notes.

### `release.started`, `release.completed` (P-14)

Named like the audit actions `ReleaseService` already writes (`release.completed`) and like
`promotion.deployed`, `rollback.deployed`.

| Event | Raised when | Where |
|---|---|---|
| `release.started` | `Release.StartedAt` goes from empty to a value | `UpdateStepAsync` (the first step that starts), `CreateAsync` and `UpdateAsync` (start given by hand) |
| `release.completed` | the release manager completes the release | `CompleteAsync`, after the completion record is written |

Payload: `id`, `version`, `title`, `train` (id and name, or null), `products`, `stagingEnvironment`,
`productionEnvironment`, `targetDate`, `status`, `startedAt`, `completedAt`, `recordedAt`, `actor`,
`url` (the portal path `/releases/{id}`). `release.completed` adds, from the completion record,
`shipped` (`product`, `service`, `version`), `notShipped` (`product`, `service`) and their counts. The
change sets are not in the payload; a consumer that wants them reads the release note.

Filters: products (any of), environment (production), train. No service: a subscription's service
filter does not apply (the existing rule for a dimension the event does not carry).

`ReleaseService` already publishes the realtime `release` entity change itself (`PublishAsync`);
`RealtimeNotifyingWebhookDispatcher` has no `release` mapping and must not get one, or pages would
refresh twice.

### Filter changes

- `WebhookEventFilters` gains `Products` (a set; matches when the subscription's product filter is
  empty or names any of them) and `Train`, both after `Service` so positional call sites compile.
- `WebhookSubscription` gains `FilterTrainsJson` (`"[]"` = any), tested only when the event carries a
  train — so no existing subscription changes what it receives. `WebhookSubscriptionFilters.Matches`
  is the one place that changes; it is the stage-2 version (service patterns and
  `FilterExcludedServicesJson` narrow a mandatory product filter). Stage 2 removes the copy in
  `DiagnosticsService`, which calls this matcher from then on, so there is no second place to change.

## Analytics (L-20, P-15, E-18)

### Resolving train and team

Both resolve, once per request, to a set of service names (service names are unique system-wide,
D-7): a team through the stored service list (`service_list.CatalogTeam`), a train through its scope
(stage 2's `ReleaseTrainMembership`). Queries then add
`services.Contains(x.Service)` with the list passed as one parameter (`EF.Parameter`, as
`DeliveryMetricsService` already does for products). An unknown team or train is `400`; a team
filter with the inventory off is `400` with `code: "team_unavailable"`.

Both are **as of now**: the portal keeps no history of team or train membership. Every `definition`
block says so.

### Product-at-the-time

`productBasis=at-event` makes the product filter and `groupBy=product` read the never-rewritten
column `DeployEvent.ProductAtEvent` instead of `Product`. It applies to figures counted from deploy
events. Stage 1 also puts `ProductAtEvent` on promotion candidates and incidents (not on builds);
this plan still counts figures from promotion candidates (queue, approval and deploy latency) and
incidents under the current product (see Open points); the `definition` names which basis each used.

### Queries that change

`AnalyticsService`:

| Method | Query | Change |
|---|---|---|
| `GetDeploymentFrequency` | `query` over `DeployEvents` | service-set filter; product filter on the chosen basis; `EventRow` gains the product-at-the-time |
| | `staleQuery` (zero series for stale services) | same filters; grouped by service, not `(Product, Service)` |
| | `keyOf` | `service` keys by service name, labelled with the current product; new `team`, `train` |
| `GetPromotionQueue` | `openQuery`, `latencyQuery` over `PromotionCandidates` | service-set filter |
| `GetLeadTime` | the work-item × deploy join | service-set filter; product filter on the chosen basis |
| `GetWorkItemMatrix` | — | unchanged (one product, by definition) |

`DeliveryMetricsService.Get`: `eventQuery`, `batchQuery`, `grainQuery`, `candidateQuery`,
`knownQuery` and `incidentQuery` each take the service-set filter next to the product filter; the
first three and `knownQuery` read the chosen product basis. `Scope.Compute` takes a predicate over
`(product, service)` instead of the product alone, which gives `byTeam` and `byTrain` rows from the
loads already made. `BuildServiceRows` and `knownServices` key by service name.

### The series key and a moved service

A service is identified by its name (D-7). Series and rows per service are keyed by the name alone;
`product` on them is a label, the service's current product. So a moved service is one series before
and after the move on both bases: under `current` because the move rewrote its rows (ADR-0006),
under `at-event` because the basis is not part of the key. With `groupBy=product` and `at-event` its
deploys are split between the old and the new product at the date of the move, which is the point.

`groupBy=train` counts a deploy in every train whose scope holds the service; when trains overlap,
the series do not add up to the total, and the `definition` says so.

### API

| Endpoint | New parameters | Response |
|---|---|---|
| `GET /deployments/frequency` | `train`, `team`, `productBasis`; `groupBy` adds `team`, `train` | series key adds `team`, `train`; `definition` adds `productBasis`, `team`, `train`, `membership: "as of now"` |
| `GET /lead-time`, `GET /promotions/queue` | `train`, `team` (`productBasis` on lead time) | `definition` / echo of the filter |
| `GET /delivery` | `trains`, `teams` (comma lists, like `products`), `productBasis` | adds `byTeam`, `byTrain` (same columns as `byProduct`); `definition` as above |

### The two change failure rates

`/deployments/frequency` computes `(failed + rollbacks) / (succeeded + failed)` and calls it
`changeFailureRate`; `/delivery` calls the same formula `deployFailureRate` and keeps
`changeFailureRate` for deploys with an attributed incident. Fix: the frequency response adds
`summary.deployFailureRate` and `definition.deployFailureRate`; `changeFailureRate` stays beside them
with the same value for one warning period and is listed under `definition.deprecated`. The
Analytics page, the MCP tool description and the notes use the new name at once. Removal of the old
field is a separate change after the warning period.

### MCP (`Mcp/AnalyticsTools.cs`)

`get_deployment_frequency`: `train`, `team`, `productBasis`, `groupBy` with `team` and `train`;
description says "deploy failure rate". `get_lead_time`, `get_promotion_queue`: `train`, `team`.
`get_delivery_metrics`: `trains`, `teams`, `productBasis`. Train names resolve as in the stage-2
release tools; a team that cannot be resolved is an `McpException` naming the known teams.

## Screens

- **Release page (E-16)** `app/releases/ReleaseDetailPage.tsx`: a **Release notes** action — **Preview
  release notes** opens the draft (`/releases/:id/release-notes/new`, the existing
  `ReleaseNoteDraftPage` given a release instead of a product and window: editable markdown, live
  preview, **Publish**); published notes of the release are listed with their date and open at
  `/releases/:id/release-notes/:noteId` (`ReleaseNoteDetailPage`). The per-product "Release notes ·
  {product}" links in `LinksSection` stay. Shown when `features.releaseNotes` is on.
- **Release Notes** `app/release-notes/ReleaseNotesPage.tsx`: a product's list also shows release
  notes that cover it, marked with the release's title and linking to the route above.
- **Settings → Release Notes Template**: the scope picker gains **Releases**, with an optional train.
- **Webhooks**: the two new events in the event list (`webhookTargets.ts`) with default message
  templates; a **Trains** filter next to products, services and environments.
- **Analytics (E-18)** `app/analytics/AnalyticsPage.tsx`, `app/delivery/DeliveryMetricsPage.tsx`:
  **Train** and **Team** filters, kept in the URL (`train`, `team`; `trains`, `teams` on the report);
  **Team** is absent while the inventory is off. The report gains a **By team** and a **By train**
  table and a **Product: now / at the time** switch. The card "Change failure rate" on Analytics
  becomes **Deploy failure rate**.
- **Service page (E-17)** `app/deployments/ReleaseTimeline.tsx`: heading **Version timeline**, with a
  **Service history** link to `/deployments/:product/:service/history` in the heading row. The
  component and file are renamed `VersionTimeline`.

---

## Tasks

Tests under `tests/Platform.Api.Tests/Features/…` (unit, in-memory context as
`ReleaseNoteServiceStateDiffTests`) and `tests/Platform.Integration.Tests/` (endpoints, migrations).
Each task: write the failing tests, implement, run both test projects.

### Task 1: Entity, DbContext, migrations

**Files:** `Features/ReleaseNotes/Models/ReleaseNote.cs`, new `Models/ReleaseNoteProduct.cs`,
`Infrastructure/Persistence/PlatformDbContext.cs`, migrations `AddReleaseNoteRelease` in
`Migrations/Postgres` and `Migrations/SqlServer`; `Features/Webhooks/Models/WebhookSubscription.cs`
(`FilterTrainsJson`), migration `AddWebhookTrainFilter` in both sets.

- [ ] `release_notes.ReleaseId` (nullable, indexed); `release_note_products` filled from existing
      rows by an `INSERT … SELECT Id, Product` in the migration.
- [ ] `webhook_subscriptions.FilterTrainsJson` with default `"[]"`.
- [ ] The index on `deploy_events.ProductAtEvent` that stage 1 leaves to this stage (with Task 7).

Acceptance: both migration sets apply to a database holding notes; every existing note has exactly
one product row; `Product` of existing rows is untouched.

### Task 2: Assemble a release note (TDD)

**Files:** `Features/ReleaseNotes/ReleaseNoteService.cs`, `Models/ReleaseNoteDtos.cs`; new
`tests/Platform.Api.Tests/Features/ReleaseNotes/ReleaseNoteForReleaseTests.cs`.

- [ ] Tests: two deployed promotions of one service → one entry, versions from oldest `FromVersion`
      to newest, references united without duplicates; a service with a production deploy and no
      promotion → entry with `source: "deploy-events"` equal to what `GetRawPreview` gives for it; a
      service that ended on the version live before the window → absent; excluded service → absent;
      service in scope with nothing deployed → in `NotShipped`; two products → both in `Products`,
      each service carries its product; a completed release reads the completion record and does not
      change when a later hotfix deploys; `ReleaseNoteServiceStateDiffTests` unchanged and green.
- [ ] `GetReleasePreview`; `MapService` split into a builder taking references and participants;
      `GetVersionsLiveBeforeAsync` takes `(product, service)` pairs.

Acceptance: review focus 2.

### Task 3: Endpoints and template scope (TDD)

**Files:** `ReleaseNoteEndpoints.cs`, `ReleaseNoteTemplateService.cs` (`KeyForRelease`,
`GetReleaseTemplate`, `DefaultReleaseTemplate`), `TemplateEngine.cs` (`ToContext`); new
`tests/Platform.Integration.Tests/ReleaseNoteForReleaseEndpointsTests.cs`.

- [ ] Tests: preview and generate by `releaseId`; both shapes at once → `400`; cancelled release →
      `400`; nothing shipped → `no_services`; `renderedContent` stored verbatim; note stored with
      `ReleaseId`, `Product = ""` and one product row per product; list by `product` finds it, list by
      `releaseId` finds it; a release note whose products are all hidden is not listed, one with a
      visible product is; flag off → `404` for `releaseId` requests only; template chain train →
      release → built-in; a saved product template is not used; an existing custom template renders
      against the release context without error.
- [ ] Regression tests: a product `/generate` after a release note for the same product still takes
      its window from the last **product** note; its response and stored row are unchanged.

Acceptance: review focus 1.

### Task 4: Webhook filters and the release-note payload (TDD)

**Files:** `Features/Webhooks/IWebhookDispatcher.cs` (`WebhookEventFilters`),
`WebhookSubscriptionFilters.cs`, `WebhookEndpoints.cs` (filters in create/update, `GetFilterOptions`
adds trains), `ReleaseNoteEndpoints.cs` (payload, filters), `NotificationTemplates.cs` (`SampleData`
gains the new fields); tests in `Features/Webhooks/WebhookFilterMatchingTests.cs`,
`NotificationMessageTests.cs`, `tests/Platform.Integration.Tests/WebhookIntegrationTests.cs`.

- [ ] Tests: product filter `[mkp]` against `Products [mkp, mkp-extensions]` → one delivery; filter
      `[mkp, mkp-extensions]` → one delivery; filter `[acx]` → none; empty filter → one; train filter
      against an event without a train → passes; against another train → blocked; a product note's
      payload has every field it had, plus `products` and `release: null`.

Acceptance: review focus 3.

### Task 5: `release.started`, `release.completed` (TDD)

**Files:** `Features/Releases/ReleaseService.cs` (inject `IWebhookDispatcher`),
`NotificationTemplates.cs` (defaults and samples for the `release.` family), `Program.cs` only if the
registration needs it; tests in `tests/Platform.Integration.Tests/ReleaseEndpointsTests.cs`.

- [ ] Tests: starting the first step fires `release.started` once, a second step does not; create
      with `startedAt` fires it; editing `startedAt` of a started release does not; complete fires
      `release.completed` with `shipped` and `notShipped` from the completion record; reopen then
      complete fires again; cancel fires nothing; filters carry products, production environment and
      train; no second realtime `release` event.

Acceptance: review focus 5.

### Task 6: Release notes on the screens

**Files:** `src/Platform.Web/src/lib/api.ts`, `App.tsx` (two routes under `/releases/:id`),
`app/release-notes/ReleaseNoteDraftPage.tsx`, `ReleaseNoteDetailPage.tsx`, `ReleaseNotesPage.tsx`,
`app/releases/ReleaseDetailPage.tsx`, `app/settings/ReleaseNoteTemplateSettings.tsx`,
`app/webhooks/webhookTargets.ts`, `WebhookFilterFields.tsx`, `webhookFilters.ts`,
`guides/generate-release-notes.yaml` (a step for the release page).

Acceptance: from a release with two products, **Preview release notes** shows one draft with both
products; **Publish** opens the note; the note appears on the release page and in both products'
lists; the product flow from `/release-notes/:product` is unchanged; `GuideCorpusTests` green.

### Task 7: Analytics by train, team and product-at-the-time (TDD)

**Files:** `Features/Analytics/AnalyticsService.cs`, `DeliveryMetricsService.cs`,
`AnalyticsEndpoints.cs`, `DeployEventRows.cs` (`EventRow`), `Models/AnalyticsDtos.cs`
(`FrequencySeriesKeyDto`, `FrequencyDefinitionDto`, `FrequencySummaryDto`),
`Models/DeliveryMetricsDtos.cs`; new `Features/Analytics/AnalyticsScopeResolver.cs` (team and train
to service names); tests in `tests/Platform.Integration.Tests/AnalyticsEndpointsTests.cs`,
`DeliveryMetricsEndpointsTests.cs`.

- [ ] Tests: `team=` counts only that team's services, on frequency, lead time, queue and delivery;
      `train=` likewise; inventory off + `team` → `400 team_unavailable`; `groupBy=team` puts
      services without a team under an empty key; a moved service: `productBasis=at-event` books its
      earlier deploys under the old product, `current` under the new, `groupBy=service` gives one
      series under both; stale-service zero series appear once per service; frequency returns
      `deployFailureRate` equal to `changeFailureRate`; delivery `byTeam` deploys sum to the total,
      `byTrain` is flagged non-additive when trains overlap; every existing test green without edits
      other than the series-key shape.

Acceptance: review focus 4.

### Task 8: MCP analytics tools

**Files:** `Mcp/AnalyticsTools.cs`; tests in `tests/Platform.Integration.Tests/McpServerTests.cs`.

- [ ] Tests: each tool accepts the new arguments and returns the same body as the REST call; unknown
      team → error naming the known teams; `groupBy=team` accepted.

### Task 9: Analytics filters and the version timeline on the screens

**Files:** `app/analytics/AnalyticsPage.tsx`, `app/delivery/DeliveryMetricsPage.tsx`,
`app/delivery/deliveryFormat.ts`, `lib/api.ts`; `app/deployments/ReleaseTimeline.tsx` →
`VersionTimeline.tsx`, `app/deployments/ServiceDetailPage.tsx`.

Acceptance: a filtered view survives a reload and can be shared as a link; with the inventory off
there is no **Team** control; the Analytics card reads **Deploy failure rate**; the service page shows
**Version timeline** with a working **Service history** link.

### Task 10: Docs

- `notes/release-notes.md`: release notes for a release (content, sources, endpoints, template scope
  and context, persistence, payload, UI routes).
- `notes/releases-api.md`: generating notes from a release; `release.started`, `release.completed`.
- `notes/analytics-api.md`: `train`, `team`, `productBasis`, the series key, `byTeam`/`byTrain`,
  deploy failure rate vs change failure rate and the deprecation.
- `knowledge/webhook-catalogue.yaml`: the two events, the train filter, the product set.
- `.claude/skills/infrapilot-api/SKILL.md`: the release-note call by release, the analytics
  parameters, the renamed figure.
- `notes/mcp-server.md`: the analytics tools' new arguments.
- `GLOSSARY.md`: **Release Note** (product note, release note); "deploy failure rate" under _Avoid_
  for change failure rate and the reverse.
- `docs/src/content/siteContent.ts`: the delivery-metrics and release-notes pages.

### Task 11: What's new entries

Files under `src/Platform.Web/src/app/whats-new/entries/`, dated the day each PR merges.

`YYYY-MM-DD-release-notes-for-a-release.md` (with Task 6)

```markdown
---
title: Publish release notes for a whole release
date: YYYY-MM-DD
audience: everyone
link: /releases
linkLabel: Releases
---
A release's page now has **Preview release notes**. The draft lists every service that shipped with
the release, across all its products, with the work items its promotions carried. Edit it if you
like, then **Publish**.

Notes for one product and a period work as before under **Release Notes**.
```

`YYYY-MM-DD-release-started-and-completed-notifications.md` (with Task 6)

```markdown
---
title: Get a message when a release starts or completes
date: YYYY-MM-DD
audience: admins
link: /webhooks
linkLabel: Webhooks
---
**Webhooks** has two new events, **release.started** and **release.completed**. Add them to a
notification to post in a channel when a release gets going and when it is closed, with the services
that shipped. You can narrow them to products and, with the new **Trains** filter, to a release train.
```

`YYYY-MM-DD-analytics-by-train-and-team.md` (with Task 9)

```markdown
---
title: Filter analytics by release train and team
date: YYYY-MM-DD
audience: everyone
link: /analytics
linkLabel: Analytics
---
**Analytics** and **Delivery metrics** can now be narrowed to a **Train** or a **Team**, and the
report breaks its numbers down by both. Use **Product: at the time** to count a service that changed
product under the product it belonged to when it was deployed.

The figure called "Change failure rate" on **Analytics** is now **Deploy failure rate**: it counts
failed and rolled-back deploys. **Change failure rate** on **Delivery metrics** still counts deploys
that caused an incident.
```

`YYYY-MM-DD-version-timeline.md` (with Task 9)

```markdown
---
title: Release timeline is now Version timeline
date: YYYY-MM-DD
audience: everyone
---
The timeline on a service's page shows when each version reached each environment, so it is now
called **Version timeline**. **Service history** next to it opens the full list of deployments.
```

If `docs/plans/navigation.md` has merged, the Webhooks link is `/settings/webhooks` with the label
`Settings → Webhooks`.

### Task 12: Verify

`dotnet test` for both test projects; both migration sets applied to a fresh database and to a copy
holding notes and subscriptions; `npx tsc -b` and `npm run build` in `src/Platform.Web`; open
`/whats-new` and check the four entries; by hand: a product note through the old call before and
after, compared field by field; a release note for a two-product release delivered to a test
receiver filtered to one of the products. The canonical control scenario
(`docs/plans/service-list-and-catalog-product.md`, Task 21: seven flow steps, variants (a)–(d)) still
passes unchanged; this stage adds no variant of its own.

## Open points

- A start or completion recorded after the fact (a past `startedAt` or `completedAt`) fires the
  webhook like a live one; the payload carries both the stated time and `recordedAt`. Whether such
  records should stay silent is not decided.
- A pipeline that still posts a product note after each deploy run and a release manager who
  publishes the release note announce the same change twice. The plan suppresses neither; the teams
  choose one.
- Team and train membership "as of now" misstates past periods after a service changes team or
  leaves a train. Keeping history is not planned.
- Stage 2 settles that a service may be in the scope of several trains at once
  (`docs/plans/release-trains.md`, "Membership"), so the non-additive note on `groupBy=train` stays.
- Stage 1 stores `ProductAtEvent` on promotion candidates and incidents too. Whether
  `productBasis=at-event` should also apply to the figures counted from them is not decided; this
  plan leaves them on the current product.
- How long `changeFailureRate` stays on the frequency response before it is removed.
