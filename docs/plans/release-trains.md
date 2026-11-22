# Release trains — implementation plan

> **Status:** plan for stage 2 of the deployments-and-releases work, written 2026-11-22 against the
> code on `main`. Decisions are in ADR-0007 (a release train is saved release settings; a Release
> gates nothing) and ADR-0008 (a freeze may be scoped to a release train). Nothing here is
> implemented yet.

**Goal:** Let a team save how it releases (a **release train**), open a release from it or for a
hand-picked set of services, and have the portal record what shipped. Let an approval policy, a
webhook filter and a freeze name *selected services* of a product, so the extension and support-tool
services can go back into product `mkp` as groups without losing their own rules.

**Architecture:** A `ReleaseTrain` row holds settings only. A `Release` copies them when it is
created and keeps computing its progress on read; completing it writes one row per service into a
record that is never recomputed. Approval policies get a selector (service names, service groups or
name patterns) in a child table, and **one** resolver answers every policy question the code asks
today in six places. Webhook service filters learn `*` and exclusions through one matcher shared
with diagnostics. The freeze key gains a train; the gate asks "product freeze, train freeze or
service freeze?" and names every one that holds.

**Tech stack:** ASP.NET Core net10.0 Minimal API + EF Core (Postgres and SQL Server migration sets),
xUnit + NSubstitute, React 19 + TypeScript, Python 3 for the cutover script.

**Spec:** the agreed specification of 2026-11-22 (summarised here): logic L-11..L-18, API P-7..P-12,
screens E-8..E-15, decisions D-8, D-9, D-13..D-19, D-23, D-26. ADRs:
`docs/adr/0007-a-release-train-is-saved-release-settings.md`,
`docs/adr/0008-freeze-may-be-scoped-to-a-release-train.md`.

**Depends on:** stage 1, `docs/plans/service-list-and-catalog-product.md` — the stored service list
with each service's product, group, team and type from the service catalog; the completed move of a
service to another product (including retired versions, pending migration runs, incidents,
service-level freezes and release exclusions); the product-at-the-time column on events. This plan
names what it needs from that list as `IServiceDirectory` (below); use the type that plan creates.

## Delivery order

| Step | Content | Tasks |
|---|---|---|
| 1 | Trains, release from a train, ad hoc release, completion record, release filters, MCP | 1–9 |
| 2 | Policy for selected services, the shared resolver, webhook patterns and exclusions | 10–16 |
| 3 | One-off move of the `mkp-extensions` and `mkp-support-tools` services into product `mkp` | 17–18 |
| 4 | Train freeze, group-change handling | 19–23 |
| — | Configuration (D-26), docs, What's new, verification | 24–27 |

Each step can ship on its own. Step 3 needs steps 1 and 2 in production and stage 1's move completed.

## Non-goals

- No approval policy on a train. A train never selects approvers (D-13, ADR-0007).
- No explicit include-list for a product-scoped train: "all services of the products minus standing
  exclusions" stays the only way to say "the product".
- No "one open release per service" rule, and no check that releases do not overlap (D-15).
- No version pinning when a release is cut; versions are recorded once, at completion (D-16).
- No hotfix release type. A hotfix is a later build on the release's line (D-17).
- A Release never holds a promotion: not on a step, not on its status, not on another service
  (D-19). Nothing in this plan adds a read of `Release` to `PromotionService.EvaluateGateAsync`.
- No scheduler. A train's cadence is text for people; nobody opens a release automatically.
- Release notes for a Release, release webhooks and analytics by train are stage 3.

## Global constraints

- **Today's call keeps working unchanged.** `POST /api/releases { version, products, targetDate }`
  returns the same release with the same scope, checks and status codes as before. Every new request
  field is optional and appended; every new response field is additive.
- **The scope rule of a product-scoped release does not change:** every service of its products
  present on its staging or production environment, minus the left-out ones
  (`ReleaseProgressService.ComputeAsync`, `ReleaseProgressService.cs:118-136`). A new service of a
  product appears by itself.
- **Releases may overlap.** A deploy is attributed to a release by its version line
  (`ReleaseProgressService.OnLine`, `ReleaseProgressService.cs:86`). The same deploy may show on two
  releases; that is reported, never rejected.
- **Feature flag.** New flag `features.releaseTrains` (`FeatureFlagKeys.ReleaseTrains`), off by
  default, seeded in `FeatureFlagSeeder`. The server checks it, not only the UI: the trains
  endpoints answer 404 while it is off (the endpoint-filter pattern of
  `ServiceInventoryEndpoints.cs:21-27`); the new request fields on releases, policies, webhooks and
  freezes answer 400 `feature_disabled`. **The flag gates writes and screens, never evaluation:** a
  stored selector policy, webhook pattern or train freeze keeps applying while the flag is off. An
  approval rule or a freeze must not disappear because someone flipped a display switch.
- **Nothing that passes through the API today starts being rejected without a warning period.**
  Where this plan tightens a rule (mandatory product filter on webhooks), the first release warns.
- **Governance rule for webhooks:** every subscription keeps a product filter; service patterns and
  exclusions only narrow it. A subscription with patterns or exclusions and no product is refused.
- **Words.** "Train" means the saved settings only; one run is a "Release". The governance rules'
  "grouped promotion" is something else — never use that term for a Release, in code, UI or docs.
- **Portable SQL.** `""` instead of NULL in unique keys (ADR-0003); no JSON functions in queries;
  every schema change ships a migration under `Migrations/Postgres` and `Migrations/SqlServer`.
- **Public repository.** Tests, fixtures, examples and the cutover script use the placeholder names
  (`mkp`, `mkp-extensions`, `mkp-support-tools`, `mkp-extension-adobe`, `mkp-extension-operations`,
  `acx-extension-mscsp`, `mkp-spotlight`, `mkp-release`, `ops-release`, "the marketplace SDP").
- No commits on `main`, no push, no PR without the user's consent.

## Decisions

| # | Question | Answer |
|---|---|---|
| D-8 | Extension services | Back into product `mkp` as a group, in step 3. |
| D-9 | `mkp-support-tools` | A group of `mkp`. |
| D-13 | Unit of approval | One service's promotion. Policy for a product or for selected services (exact name, group, pattern with `*`). Order: name, group, pattern, product. Same-level overlap is refused at save. |
| D-14 | Train scope | Products minus standing exclusions, **or** services selected by list / group / pattern. |
| D-15 | Overlap | Allowed. Attribution by version line. |
| D-16 | Completion | Records shipped versions; partial completion allowed. |
| D-17 | Hotfix | Part of its release's line. |
| D-18 | Freeze scopes | Product, train, service. A product freeze holds all its services. |
| D-19 | Release | Groups and shows; gates nothing. |
| D-23 | Approvers | Stay configured in the portal's policies. |
| D-26 | Hotfix branches | Staging policies also auto-create from the hotfix branch pattern (configuration, Task 24). |

Choices this plan makes inside those decisions (each is argued in the design section it belongs to):

| # | Choice |
|---|---|
| P-a | A train always names its products. A service selector narrows *within* them. |
| P-b | Policy selectors live in a child table, one kind per policy; a product-level policy has one sentinel row, so one unique index covers every level. |
| P-c | Uniqueness of a line is kept for product-scoped releases and per train; service-scoped and line-less releases never conflict. |
| P-d | The completion record is a child table, one row per service, exempt from the service move. |
| P-e | A train freeze is one row with `Product = ""`; membership is resolved when the gate is evaluated. |
| P-f | A release copies the train's selectors, not a resolved service list. |

## Review focus

1. **Snapshot comparison.** `PromotionService.RefreshPolicySnapshotsAsync` (`PromotionService.cs:1585`)
   and `ResolveCurrentPolicyAsync` (`PromotionService.cs:4086`) decide "did the policy change?" by
   comparing serialised snapshots. Adding `MatchedBy` to `ResolvedPolicySnapshot` makes every stored
   snapshot differ from a fresh one. Without the normalisation in Task 11 the first policy save after
   the deploy re-stamps every Pending candidate with a false "policy changed" comment, and
   cancel-approval treats every Approved candidate as approved under outdated rules. → Task 11 tests.
2. **One resolver, same answers.** The six lookup sites give the same answer for a product-level and
   a single-service policy before and after Task 12. → characterisation tests written first.
3. **A release created today's way is byte-for-byte today's release.** Same DTO fields, same
   progress, same 409 for a second release of a line and product. → Task 3, Task 5 tests.
4. **Uniqueness.** A core train and an extensions train of one product may both run `5.10`; the same
   train may not run it twice; today's product rule still fires. → Task 3 tests.
5. **The move must not change who approves.** After step 3 every Pending candidate of a moved
   service resolves a policy whose approval steps equal the ones it had, or the script stops. → Task 17.
6. **A product-scoped release of `mkp` after the move.** Extension services would join every `mkp`
   release as "open on every check". The core train excludes the groups, and the script leaves the
   moved services out of open `mkp` releases. → Task 17, verification variant 1.
7. **Freeze union over three shapes.** Lifting a train freeze leaves a product or service freeze
   holding; a service that joins a frozen train is held on its next evaluation. → Task 19 tests.
8. **A webhook filter never widens.** Patterns and exclusions without a product are refused; an
   existing subscription without a product filter keeps delivering and is reported. → Task 14 tests.

Where the specification's facts and the code disagree (the code is what this plan follows):

- The specification lists five lookup sites besides `PromotionPolicyResolver`. The code has those
  five plus `EnvironmentMergeService.MergePromotionPoliciesAsync` (`EnvironmentMergeService.cs:444-466`,
  whose conflict key is `(Product, Service, edge)`) and the admin endpoints' duplicate pre-check
  (`PromotionAdminEndpoints.cs:383`). `RollbackPolicySeeder.cs:74` reads promotion policies once, at
  start-up, to seed rollback policies; it is not a lookup and is left alone.
- `PromotionPolicyResolver.ResolveForTargetAsync` and `SnapshotForTargetAsync`
  (`PromotionPolicyResolver.cs:59-75`) have no caller; rollbacks use `RollbackPolicyResolver`. They
  are deleted, not ported.
- The policy unique index does not guard product-level rows on either engine: the comment at
  `PlatformDbContext.cs:577-579` says both treat NULLs as distinct, and the SQL Server index carries
  `HasFilter("[Service] IS NOT NULL")`. Only the endpoint pre-check stops a second product-level
  policy today.
- "Filter the releases list by product" (P-9) already exists in the API
  (`ReleaseService.ListAsync`, `ReleaseService.cs:283-288`); only the page lacks it. `train` is new.
- The diagnostics copy of the webhook filter is not a faithful copy: it compares case-insensitively
  (`DiagnosticsService.cs:451-455`) where the dispatcher compares ordinally
  (`WebhookSubscriptionFilters.cs:88-91`).
- Work-item decision webhooks carry no service (`WorkItemApprovalService.cs:382`), although the
  sign-off is keyed by service. A service filter does not narrow them today; this plan keeps that.
- `Release.cs`, `ReleaseService.cs:695`, `ReleasesPage.tsx:14` and `notes/releases-api.md` call a
  Release "a release train". Corrected in Tasks 1 and 25.
- The specification's file holds no control scenario. The one in Task 27 is derived from its facts
  and from `docs/plans/migrate-mkp-services.md`; confirm it before step 3 (Open points).
- `/settings/*` is wrapped in `AdminRoute` (`App.tsx:153`), and the freeze routes sit in the
  `/api/promotions/admin` group (`Program.cs:534`, `CatalogAdmin`). "Release managers may freeze"
  therefore needs a route and a page outside both (Tasks 20, 21).

---

## What stage 1 must provide

Stage 1 stores one row per service in `service_list` (`Features/ServiceList/Models/ServiceListEntry.cs`:
`Name`, `Product`, `CatalogGroup`, `CatalogTeam`, `CatalogType`), kept current by
`ServiceListCatalogSync` and read through `ServiceListService`. This plan reads it through one small
facade, so the resolver, the selectors and the freeze share one definition of "the services of a
product" and "a service's group":

```csharp
public interface IServiceDirectory   // implemented over ServiceListService; no second list
{
    Task<ServiceEntry?> FindAsync(string service, CancellationToken ct);
    Task<IReadOnlyList<ServiceEntry>> ListAsync(string product, CancellationToken ct);   // incl. "not in catalog"
}
public sealed record ServiceEntry(string Product, string Service, string? Group, string? Team, string? Type);
```

`Group` is the **effective group**: `CatalogGroup`, or the previous group while a group change
waits for an admin (L-18). It is null for a service the catalog does not know and for a product
without groups. Stage 1 treats groups as display-only; from step 2 of this plan they decide policies,
train membership and freezes, which is why a group change gets its own handling (Task 22).

## Design: train and release

### `ReleaseTrain`

New entity `Features/Releases/Models/ReleaseTrain.cs`, table `release_trains`. It follows `Release`:
lists as JSON string columns, environments as canonical keys.

| Column | Type | Meaning |
|---|---|---|
| `Id` | guid | |
| `Name` | string(200), unique | "Core", "Extensions" |
| `Cadence` | string(200), null | Free text: "every second Tuesday". Not parsed. |
| `Description` | string(2000), null | |
| `ProductsJson` | string(4000), `"[]"` | Products the train belongs to. At least one. |
| `ScopeKind` | string(20) | `products` or `services` |
| `ExclusionsJson` | text, `"{}"` | `ServiceSelector`: standing exclusions (scope `products`) |
| `SelectorJson` | text, `"{}"` | `ServiceSelector`: what ships (scope `services`) |
| `StagingEnvironment`, `ProductionEnvironment` | string(100) | As on `Release` |
| `StepsJson` | text, null | Checklist template; null = `ReleaseStepTemplate.Default` |
| `Archived` | bool | Hidden from the new-release form; existing releases keep it |
| `CreatedBy`, `CreatedByName`, `CreatedAt`, `UpdatedBy`, `UpdatedAt` | | As on `Release` |

`ServiceSelector` (`Features/Releases/ServiceSelector.cs`) is one small record used for a train's
scope, a train's standing exclusions and a release's copies of both:

```jsonc
{ "services": ["mkp/mkp-extension-operations"],   // exact, product/service — the form exclusions use today
  "groups":   ["extensions", "support-tools"],    // service groups of the train's products
  "patterns": ["mkp-extension-*", "acx-extension-*"] }   // service-name globs, * only
```

`ServiceSelector.Matches(ServiceEntry)` is true when any entry matches. Globs use the matcher that
branch patterns use today, extracted from `BuildPromotions.GlobMatches` (`BuildPromotions.cs:33-38`,
private) into `Infrastructure/NameGlob.cs`; `BuildPromotions.BranchMatches` delegates to it.

- **A train always names products (P-a).** With scope `services` the selector picks among the
  services of those products; a `services` entry must be `product/service` of one of them. This
  keeps three existing rules working without a new concept: who manages (release managers of the
  products), the release's `Products`, and a product filter on every webhook.
- **Standing exclusions take groups and patterns**, not only names. After step 3 the core train of
  `mkp` is "product `mkp` minus groups `extensions` and `support-tools`", and a new core service
  still joins by itself.
- **Checklist template.** `StepsJson` is a list of `ReleaseStepTemplate.StepDefinition`
  (`ReleaseStepTemplate.cs:19`) with `{version}` where the text names the line.
  `ReleaseStepTemplate.Default(version)` stays the source of the default; add
  `Build(releaseId, definitions, version, now)` next to the existing `Build`. A step's `Check` must be
  one of `ReleaseChecks.All` or null; keys are unique; at most 50 steps (the limit `AddStepAsync`
  already has).
- **Who manages.** No stored list. Administrators, and whoever is a release manager of one of the
  train's products for its production environment — the rule `ReleaseService.CanManageAsync`
  (`ReleaseService.cs:268`) applies to a release. A train has no approvers.
- **Membership** of a service in a train, used by the freeze, the product page and the Services
  page: `ReleaseTrainMembership` (`Features/Releases/ReleaseTrainMembership.cs`), depending only on
  `PlatformDbContext` and `IServiceDirectory` (so `PromotionService` can use it without a cycle
  through `ReleaseService`). Scope `products`: the service's product is one of the train's and the
  exclusions do not match. Scope `services`: the product is one of the train's and the selector
  matches. Presence on an environment is **not** required: a service that has never deployed is a
  member. A service may be in several trains.

### Release → train

New columns on `releases` (`Release.cs`):

| Column | Type | Meaning |
|---|---|---|
| `TrainId` | guid, null, FK → `release_trains` `SET NULL` | The train it was created from |
| `TrainName` | string(200), null | Copied, so the release still says where it came from after the train is renamed or deleted |
| `ScopeKind` | string(20), default `products` | `products` (today) or `services` |
| `SelectorJson` | text, `"{}"` | What ships, for scope `services` (copied from the train, or the ad hoc list) |
| `StandingExclusionsJson` | text, `"{}"` | The train's standing exclusions, copied |
| `StepTemplateJson` | text, null | The train's checklist template, copied; null = default |
| `WindowFrom`, `WindowTo` | timestamptz, null | The time window of a release without a line |
| `RecordedAt`, `RecordedBy` | | When and by whom the completion record was written |

`Version` stays non-null; `""` means "no line". The DTOs expose it as `version: string | null`.

**What a release copies at creation (L-12, P-f):** products, staging and production environment,
scope kind, selector, standing exclusions, checklist template (rendered into `release_steps` as
today, and kept in `StepTemplateJson` so `RetitleTemplateSteps`, `ReleaseService.cs:793`, can
re-render after a version correction). It copies the **selectors, not a resolved list**: a service
that joins the group next week is in the open release, exactly as a new service of a product is today.
What is fixed is fixed at completion.

**Why later train edits do not touch existing releases:** the release stores its own scope — its
products and exclusions, or its own selection by list, group or pattern — and reads only its own
columns; nothing in `ReleaseService` or `ReleaseProgressService` loads the train after creation. An open release whose scope should follow an
edit is edited itself (`PUT /api/releases/{id}`). This is the difference between a train and a
release, so it gets a test (Task 3).

`ExcludedServicesJson` keeps its meaning — the services this release's manager left out by hand, in
the **Services** dialog. A service is out of the release when the standing exclusions *or* that list
names it.

### Scope resolution in `ReleaseProgressService`

Extract the scope from `ComputeAsync` into `ReleaseScope` (`Features/Releases/ReleaseScope.cs`), used
by progress, the completion record, the L-14 warning and the promotion filters:

- `products`: unchanged. Keys = present on staging or production by the end of the window
  (`ReleaseProgressService.cs:120-136`) ∪ on the line (`:158`); excluded = `ExcludedServicesJson` ∪
  `StandingExclusionsJson`.
- `services`: candidates are the services of the release's products the selector matches
  (`IServiceDirectory.ListAsync` per product). A service named exactly in `services` is always
  listed, present or not — somebody picked it. One matched by group or pattern is listed when
  present or on the line, like a product's service. Excluded as above.

The queries keep their `products.Contains(e.Product)` narrowing and add the key set in memory, as
they do today for off-line services.

### Ad hoc release (L-13)

Hand-picked services, a time window, an optional line. No train. Stored as scope `services` with
only `selector.services` filled, `WindowFrom`/`WindowTo` set, `Version` `""` or a line.

How the existing logic behaves:

| | With a line | Without a line (`Version == ""`) |
|---|---|---|
| Records of a service | On the line, as today; a picked service with nothing on the line falls back to the window, as today (`:162-178`) | Every service is treated as off the line: everything it deployed to staging/production and promoted to production inside the window |
| Window lower bound | `WindowFrom ?? StartedAt ?? CreatedAt` — applies to off-line services only | `WindowFrom` (required) |
| Window upper bound | `CompletedAt ?? CancelledAt ?? now`, as today | `min(WindowTo, CompletedAt ?? CancelledAt ?? now)` — the window is the definition of the release, so it is a hard bound; extend it by editing the release |
| `OnLine` on each service | As today | Always `false`; the page drops the "own version scheme" hint |
| Line queries (`:139-156`, `BuildsAsync` `:421`) | As today | Skipped; only the window queries run |
| `promotionsTo` (approval round closed, `:105-108`) | As today | As today |
| Five checks | As today | Same rules per service. Texts that print the line (`StagingBuildsCheck`, `QaCheck`, `PromotionsCheck`, `DeploysCheck` summaries, `ReleaseProgressService.cs:557-765`) say "this release" through one `LineLabel(c)` helper |
| Checklist | Train or default template | Default template without `version-cut` and `code-freeze`, `{version}` rendered as the title (`ReleaseStepTemplate.AdHoc()`) |
| L-14 warning | Computed | None — there is no line to be on |

`WindowTo > WindowFrom`; both may be in the past (a release recorded after the fact) or the future.
`TargetDate` defaults to the date of `WindowTo`. `StartedAt` keeps its meaning and its
not-in-the-future rule.

### Uniqueness (P-c)

Today `EnsureUniqueAsync` (`ReleaseService.cs:698-710`) refuses a release when another release that
is not cancelled — **completed ones included** — has the same version and shares a product. The
index on `Version` is deliberately not unique (`PlatformDbContext.cs:869-871`).

New rule, still in the service:

1. **Product-scoped, with a line** (today's releases, and releases of product-scoped trains):
   unchanged, but compared only with other *product-scoped* releases of that line. Same message, same
   409 with `existingId`.
2. **Same train, same line:** refused whatever the scope — a train does not run `5.10` twice.
3. **Service-scoped** (selector trains, ad hoc) **with a line:** not compared with product-scoped
   releases or with each other beyond rule 2. A core train and an extensions train of `mkp` both
   reach `5.10`.
4. **No line:** never conflicts.

Where rules 3 and 4 let two open releases cover the same service on the same line, the create and
update responses carry `warnings: ["mkp/mkp-spotlight is also in Release 5.10 (Core) on this line"]`.
Nothing is rejected: an ad hoc release for a hotfix of two services on a line that already has a
release is exactly what D-15 and D-17 allow.

Considered: making the rule "(version, train)" only. Rejected — releases without a train are today's
releases, and their rule must not loosen silently.

### Warning: a service outside the release on its line (L-14)

*Definition.* For a release with a line: a service of the release's products that is **not shipping
with it** — left out by hand, excluded by the standing exclusions, or not matched by the selector —
and has a deploy event to the release's staging or production environment, not `removed`, with a
version on the line, at a time inside `[from, to]`.

Other products are not searched: two products may reach `5.10` independently
(`ReleaseService.cs:694-697`).

*Query.* The line query in `ComputeAsync` (`ReleaseProgressService.cs:139-147`) already returns every
on-line event of the release's products, shipping or not. Filter it in memory to keys outside the
shipping set and `DeployedAt >= from`, group by service, keep the newest event per environment. No
new database query for scope `products`; for scope `services` the same query already spans the
products.

*Shape.* `ReleaseProgressDto` gains `offRelease: [{ product, service, reason, environment, version,
status, deployedAt, eventId }]`, `reason` = `left-out` | `standing-exclusion` | `not-selected`. The
checks do not count these services. Nothing is added to the scope.

### Completion record (L-15, P-d)

New table `release_shipped_services`, one row per service in the release's scope (left-out services
included, marked), written by `ReleaseService.CompleteAsync` (`ReleaseService.cs:381`) in the same
`SaveChanges` as `CompletedAt`:

| Column | Meaning |
|---|---|
| `ReleaseId` (FK, cascade), `Product`, `Service` | Product **as it was at completion** |
| `Outcome` | `shipped` \| `not-shipped` \| `left-out` |
| `Version`, `DeployEventId`, `DeployedAt` | The newest succeeded production deploy of the release (what `ReleaseServiceDto.Production` shows when it succeeded); null when not shipped |
| `StagingVersion` | What staging ran, for a service that did not ship |
| `PromotionIdsJson` | The release's promotions of the service that reached `Deployed` |
| `ReferencesJson` | Union of those promotions' change sets |
| `ChangeSetSource` | `promotions` \| `none` (shipped without a promotion; stage 3 falls back to deploy events) |

- **Shipped** = production runs a succeeded deploy of the release for the service at `CompletedAt`
  (the `productionNow` map, `ReleaseProgressService.cs:192-198`). Everything else in scope is **not
  shipped**. Completing with services not shipped is allowed and is what "partial completion" means;
  the status stays `completed`, and the release DTO says `shipped: { recordedAt, recordedBy,
  shipped: 31, notShipped: 2, leftOut: 4 }`.
- **Union of change sets.** For each service: every promotion that `ReleaseScope` attributes to the
  release, into the production environment, with status `Deployed`. Their
  `PromotionCandidate.ReferencesJson` (`PromotionCandidate.cs:78`) entries are concatenated oldest
  first and de-duplicated by `(type, provider, key)` — by `(type, url)` when there is no key — keeping
  the newest title. A candidate's references are its authoritative net change set, so the union over
  a service's deployed promotions is what the release changed in production for that service.
- **Never recomputed.** A row is not updated after completion. `ReopenAsync` (`ReleaseService.cs:427`)
  deletes the rows (the audit entry keeps the counts); completing again writes them afresh.
  `POST /api/releases/{id}/record` writes the record for a release completed before this feature
  existed, from the state at its `CompletedAt` — needed before step 3 (below).
- **Exempt from the service move.** Stage 1's `ServiceMoveCoverageTests` fails for any entity with
  a `Service` that `ServiceMoveClassification` does not name. This table is classified there as
  *stays — history*: it says under which
  product the service shipped. The move does rewrite the `product/service` entries in
  `releases.ExcludedServicesJson` (stage 1), and Task 2 adds `releases.SelectorJson`,
  `releases.StandingExclusionsJson`, `release_trains.ExclusionsJson` and `release_trains.SelectorJson`
  to it.

*Next to the live state.* `GET /api/releases/{id}/progress` keeps computing. `GET
/api/releases/{id}/shipped` returns the record and, per row, `live: { version, deployedAt } | null`
and `differs: true` when the live production version of the release is not the recorded one — a
deploy event backfilled after completion, a service retired, or a service moved to another product.
The page shows the recorded table and marks differing rows; it never overwrites the record.

## Design: policy for selected services (L-16)

### Data model (P-b)

Today a policy is `(Product, Service?, SourceEnv, TargetEnv)` plus settings
(`PromotionPolicy.cs:16-24`), unique on those four columns where `Service` is not null.

| | (a) selector columns on the policy row | (b) child table of members |
|---|---|---|
| Shape | `MatchKind` + `MatchValue`, one row per rule | `promotion_policy_selectors (PolicyId, Kind, Value)` |
| "These five services share one approval tree" (E-13) | Five rows with copied settings; an edit must be repeated five times and can drift | One policy, five selector rows |
| No two same-level rules for one name or group | Unique index `(Product, SourceEnv, TargetEnv, MatchKind, MatchValue)` | Same index on the child table (edge columns copied onto it) |
| Product-level duplicate | Needs a sentinel value | Sentinel row `(product, "")` |
| "Which rule matched" | The row | The selector row |
| Cost | One migration, small | One more table, a join on every read |

**Chosen: (b), with one kind per policy.** The screen the owner asked for edits one policy for
several services; (a) cannot give that without a second grouping concept. One kind per policy
(`PromotionPolicy.MatchKind`: `product` | `service` | `group` | `pattern`, with one or more selector
rows of that kind) keeps "where did the policy come from" a single answer and the form a radio
choice. Mixed kinds are refused with 400.

`promotion_policy_selectors`: `Id`, `PolicyId` (FK, cascade), `Product`, `SourceEnv`, `TargetEnv`
(copied from the policy, rewritten when the policy is re-scoped), `Kind`, `Value` (string(200), `""`
for kind `product`). Unique index `(Product, SourceEnv, TargetEnv, Kind, Value)`. Every policy has at
least one row, so the index also guards product-level policies on both engines.

Migration `AddPromotionPolicySelectors`: create the table; one `service` row per existing policy with
a `Service`, one `product` row per policy without; add `MatchKind`; drop the old unique index and the
`Service` column; keep the `(Product, TargetEnv)` index. `Down()` restores `Service` from
single-service policies; a policy with any other selector has no shape in the old schema — the way
back is the feature flag, not the schema.

`PromotionPolicy.Service` stays in the **API** for old clients: on write, `service: "x"` means
`match: { kind: "service", values: ["x"] }`; on read it is the service when the policy has exactly
one `service` selector, else null.

### Resolution

Order (D-13): **exact name → group → pattern → product**, per `(product, sourceEnv, targetEnv)`.

- *Name:* a `service` selector equal to the service name.
- *Group:* a `group` selector equal to the service's **effective group** — the stored list's group,
  or the previous one while a group change waits for an admin (L-18). No group, no group match.
- *Pattern:* a `pattern` selector whose glob matches the service name (`NameGlob`, ordinal).
- *Product:* the `product` row.

**Same-level overlap is refused at save.** Names and groups cannot overlap by construction (the
unique index; a service has one group). Patterns can: saving a `pattern` policy runs every pattern of
the edge against the product's services in `IServiceDirectory` and answers 409 with the services two
rules both match and the other policy's id. A pattern must contain `*`; one without is a name and the
error says so.

**A tie can still appear later** — a new service registers whose name two stored patterns match. The
resolver then takes the older policy (`CreatedAt`, then `Id`), sets `Ambiguous` on the match, logs a
warning, and Settings → Promotions shows the conflict until one rule is changed. It does not refuse
the promotion (global constraint). ADR-0007 says there is "never a tie to break"; this is the one
case save-time checking cannot see.

### One shared resolver

`PromotionPolicyResolver` becomes the only reader of `promotion_policies` for resolution:

```csharp
public sealed record PolicyMatch(PromotionPolicy Policy, string Level, string? Value, bool Ambiguous);
// Level: "service" | "group" | "pattern" | "product"; Value: the selector that matched ("" for product)

Task<PolicyMatch?> MatchAsync(string product, string service, string sourceEnv, string targetEnv, CancellationToken ct);
Task<PromotionPolicy?> ResolveAsync(...);                 // today's signature, = MatchAsync(...)?.Policy
Task<ResolvedPolicySnapshot> SnapshotAsync(...);          // today's signature, now stamps MatchedBy
Task<IReadOnlyList<PolicyMatch>> MatchFromSourceAsync(string product, string service, string sourceEnv, CancellationToken ct);  // one per target env
Task<IReadOnlyList<PolicyMatch>> MatchIntoTargetAsync(string product, string service, string targetEnv, CancellationToken ct);  // one per source env
Task<bool> AnyIntoTargetAsync(string product, string targetEnv, CancellationToken ct);
Task<IReadOnlyList<PromotionPolicy>> ProductLevelIntoAsync(string? product, IReadOnlySet<string> targetEnvs, CancellationToken ct);
IReadOnlyList<ServiceEntry> MatchingServices(PromotionPolicy policy, IReadOnlyList<ServiceEntry> services);   // E-13 preview
```

It loads the policies of one product (a handful of rows) with their selectors once per call and
matches in memory; no per-level query.

Every lookup site, what it does today, and what it becomes:

| # | Site | Today | Honours service-level rows today? | Becomes |
|---|---|---|---|---|
| 1 | `PromotionPolicyResolver.ResolveAsync` `PromotionPolicyResolver.cs:27` — used at `PromotionService.cs:142` (external create) | Service row, else product row, exact edge | Yes | `MatchAsync` |
| 2 | `PromotionPolicyResolver.SnapshotAsync` `:50` — used at `PromotionService.cs:191` (create), `:1600` (refresh), `:4086` (cancel-approval) | Projects site 1 | Yes | `SnapshotAsync` with `MatchedBy` |
| 3 | `BuildIngestHook.ResolveBuildEdgePoliciesAsync` `BuildIngestHook.cs:87-100` | Own query: `build → *` rows for the service or product, service row first per target | Yes, by its own copy of the rule | `MatchFromSourceAsync(product, service, BuildPromotions.SourceEnv)` |
| 4 | `GET /api/promotions/build-targets` `PromotionEndpoints.cs:793-817` | Same query, third copy | Yes, own copy | `MatchFromSourceAsync` |
| 5 | MCP `get_build` `Mcp/BuildTools.cs:66-75` | Same query, fourth copy | Yes, own copy | `MatchFromSourceAsync` |
| 6 | `ReleaseService.GetManagerGatesAsync` `ReleaseService.cs:181-214` | All policies into production; per product the product-level ones, or **all** service rows when the product has none | Only as a fallback | `ProductLevelIntoAsync`; fallback = every selector policy of the product into that environment |
| 7 | `PromotionFreezeService.FreezeAsync` `PromotionFreezeService.cs:135-139` | `AnyAsync` on rows of the product into the env; for a service, a row with `Service == null` or that service | Yes, but ignores the source env | Product: `AnyIntoTargetAsync`. Service: `MatchIntoTargetAsync(...).Count > 0`. Train: any member has one |
| 8 | `EnvironmentMergeService.MergePromotionPoliciesAsync` `EnvironmentMergeService.cs:444-466` | Finds merge conflicts by `(Product, Service, edge)` | Yes | Conflict = two policies that would hold the same selector on the merged edge; selector rows' edge columns are rewritten with the policy |
| 9 | Policy CRUD `PromotionAdminEndpoints.cs:354-486` | Reads and writes rows; duplicate pre-check `:383` | — | Moves into a `PromotionPolicyAdminService` that owns validation, the overlap check and the refresh |

Not lookups, unchanged: `ReleaseService.GetGateStepNamesAsync` / `GetGateRequirementNamesAsync` /
`ProductionEnvironmentsAsync` (`ReleaseService.cs:114`, `:164`, `:216` — vocabulary lists),
`PromotionSeedData.cs:246`, `RollbackPolicySeeder.cs:74`.

Deleted: `ResolveForTargetAsync`, `SnapshotForTargetAsync` (no caller).

### Snapshot records which rule matched

`ResolvedPolicySnapshot` gains `MatchedBy { level, value }` — init-only, default null, so snapshots
written before it deserialise (the pattern every other member follows,
`ResolvedPolicySnapshot.cs:25-27`). `PromotionPolicyResolver.Project` takes the match. The promotion
page's "where the policy came from" (E-7, stage 1) reads it; null reads as "service" when the
candidate's `PolicyId` belongs to a single-service policy, else "product".

Both comparisons of a stored snapshot with a fresh one (`PromotionService.cs:1609` in
`RefreshPolicySnapshotsAsync`, `:4091` in `ResolveCurrentPolicyAsync`) go through one helper
`SnapshotEquals(storedJson, live)` that copies `live.MatchedBy` onto a stored snapshot that has none
before comparing. See Review focus 1.

### Refreshing Pending snapshots

`RefreshPolicySnapshotsAsync(product, service?, sourceEnv, targetEnv)` already re-resolves each
Pending candidate on the edge and skips those whose snapshot did not change.

- **A rule is saved, re-scoped or deleted:** call it with `service: null` for the policy's edge
  unless the policy is a single-service one (then as today). Old and new scope, as the PUT handler
  does at `PromotionAdminEndpoints.cs:467-472`.
- **A service's group changes** (L-18 executed, or applied by itself): new
  `RefreshPolicySnapshotsForServiceAsync(product, service, reason)` — the same loop over the
  service's Pending candidates on every edge. The system comment reads "The service group of
  {service} changed from {a} to {b} — the promotion has been re-gated under the policy for {b}"
  instead of "{Actor} changed the promotion policy for this edge".
- The safety rails stay: only Pending is touched; when nothing resolves any more the candidate keeps
  its snapshot (`PromotionService.cs:1603-1606`).

### Interaction with freeze and release managers

- "Needs a covering policy" (site 7) now sees group and pattern policies, so a service governed only
  by a group rule can be frozen. For a train see the freeze section.
- Release managers (site 6) keep coming from the **product-level** policy into production. A group or
  pattern policy's "Release Manager" requirement does not make someone a release manager of the
  product while a product-level policy exists — the rule service rows have today. `ReleaseManagerGateDto`
  keeps `Service` and gains `Match { kind, values }`.

## Design: webhook service patterns and exclusions (P-12)

`WebhookSubscription` gains `FilterExcludedServicesJson` (`"[]"`). `FilterServicesJson` entries may
contain `*`.

Matching of the **service** dimension, in `WebhookSubscriptionFilters` (`WebhookSubscriptionFilters.cs:88-104`):

```
event has no service            → passes            (unchanged: an absent dimension is never tested)
include list empty              → included
include list not empty          → included when an entry equals the service or its glob matches
excluded                        → when an exclusion entry equals the service or its glob matches
passes = included and not excluded
```

Products and environments stay exact. Matching stays ordinal.

- **Events without a service** (release notes, rollbacks, work-item decisions, a product freeze, a
  train freeze) pass the service filter as they do today, patterns or not, and an exclusion never
  removes them. A subscription narrowed to the extension services therefore still receives its
  product's release notes. The filter form says so under the field, and the preview shows it.
- **Product filter mandatory.**
  - Patterns or exclusions with an empty product filter: 400, from the first release. This is new
    functionality; there is nothing to break.
  - An empty product filter otherwise: setting `Webhooks:RequireProductFilter` = `warn` (default) or
    `enforce`. In `warn`, create and update succeed and the response carries `warnings: ["This
    subscription has no product filter …"]`; in `enforce` they answer 400. Stored subscriptions
    without one **keep delivering in both modes**. `GET /api/webhooks` marks them
    `productFilterMissing: true`, the list page shows a banner with their names, and the cutover
    script prints them. Switching to `enforce` is an operator's decision after the list is empty.
- **One matcher.** `DiagnosticsService.Subscribes` (`DiagnosticsService.cs:441-455`) calls
  `WebhookSubscriptionFilters.Matches(sub, new WebhookEventFilters(product, env, service))` and drops
  its private `MatchesFilter`/`ParseList`. Its answers become case-sensitive like the dispatcher's —
  a correction, noted in the task.
- **Preview** (E-12 of the webhooks screen, part of P-12): `GET /api/webhooks/matches?product=&service=&environment=&eventType=`
  returns, per active subscription listening to that event type, whether it would receive the event
  and why not (`product`, `service-not-included`, `service-excluded`, `environment`). Same matcher.

## Design: moving the extension and support-tool services into `mkp` (D-8, D-9)

One sitting, production only, by script, reversible. It moves every service filed under
`mkp-extensions` and `mkp-support-tools` to product `mkp`.

### Prerequisites

1. **Stage 1 step 1c is live:** the move covers retired versions, pending migration runs, incidents,
   service-level freezes and release exclusions, refreshes Pending snapshots, and old addresses
   redirect. The script refuses to run when the remap preview reports a kind it does not move.
2. **Steps 1 and 2 of this plan are live** with `features.releaseTrains` on.
3. **Groups.** The service catalog publishes `extensions` and `support-tools` as groups of `mkp` and
   each moved service names one. *Fallback when it does not yet:* the script's `--match patterns`
   mode writes pattern rules and selectors (`mkp-extension-*`, `acx-extension-*`, plus exact names
   for services neither pattern covers, read from the product) instead of group ones. Patterns sit
   below groups in the order, so switching to groups later is adding group rules, then deleting the
   pattern rules.
4. **Policies.** For each of the four edges the script compares the `mkp-extensions` (and
   `mkp-support-tools`) product-level policy with `mkp`'s, field by field (the list in
   `docs/plans/migrate-mkp-services.cutover.py`, `POLICY_FIELDS`, plus `steps`). Equal → nothing to
   create: the services fall through to `mkp`'s policy. Different (for example
   `autoCreateFromBranches` naming another default branch) → a group (or pattern) rule in `mkp` with
   the old product's settings. A service-level policy of an old product — production has one such
   service — is recreated in `mkp` as a service-level rule with identical settings. The old products'
   policies are left in place until the undo window closes.
5. **Webhooks.** The move changes which subscriptions see these services' events:
   - The marketplace SDP's subscription lists product `mkp` only, so it would start receiving the
     extensions' approvals again. The SDP skips what it does not own, but each event costs a no-op
     run. The script adds `excludedServices` for the moved services to that subscription.
   - Subscriptions of `mkp-release` already list both products; they keep working. The old product
     names are removed from them after the undo window, not in the sitting.
   - Subscriptions that list **only** an old product (release-note and chat channels) would fall
     silent. The script lists them; for each the operator chooses in a small mapping file: switch to
     `mkp` with a service filter, or leave. Release notes carry no service, so a release-note channel
     switched to `mkp` receives the whole product's notes — see Open points.
6. **Releases.**
   - No open release (planned, in progress) names an old product. Complete or cancel it first.
   - Every completed release that names an old product has a completion record (`POST
     /api/releases/{id}/record`). After the move its live state is empty under the old product name;
     the record is what remains. The script writes missing records in its first pass.
   - Trains exist: "Core" = product `mkp` minus groups `extensions`, `support-tools`; "Extensions" =
     `mkp`, selector group `extensions`. Open `mkp` releases created without a train get the moved
     services added to their left-out list (`PUT /api/releases/{id}/services`), so their checks do
     not grow by services that were never part of them.
7. **No promotion of a moved service is Approved or Deploying** (D-6), with the precedent's one
   exception: an Approved `build → dev` waits rather than races.
8. **Rollback policy.** `mkp` has a rollback policy covering what the old products' policies covered
   (rollback policies are per product and are not moved).

### The script

`docs/plans/release-trains.cutover.py`, in the style of
`docs/plans/migrate-extension-services.cutover.py`: `DEPLOYMENTS_URL` / `DEPLOYMENTS_API_KEY`, dry run
by default, `--apply`, `--undo`, `--services a,b` to run a subset, `--match groups|patterns`. It
talks to the API only.

Order with `--apply`:

1. Pre-checks (prerequisites 1–8). Any failure prints what to do and stops before the first write.
2. Save the *before* picture to stdout as JSON: per service — deploy, build and promotion counts
   under the old product; per Pending candidate — id, edge, the policy's approval steps and work-item
   flags; per subscription — the result of `GET /api/webhooks/matches` for one sample service and
   `promotion.approved`.
3. Policies (prerequisite 4), then webhook exclusions and mapped subscriptions (5), then release
   records and left-out lists (6).
4. Per service: `POST /api/deployments/admin/product-overrides { service, product: "mkp",
   fromProduct: null, reason }`, preview with `GET …/{id}/remap`, apply with `POST …/{id}/remap` —
   the calls the precedent uses. Builds registered by a pipeline that still sends the old product are
   filed under `mkp` by the override.
5. The *after* picture, and the comparison:
   - no deploy event, build, candidate, work-item sign-off, retired version, migration run, incident
     or freeze of a moved service is left under an old product;
   - the counts under `mkp` equal the counts before;
   - every Pending candidate resolves a policy, and its approval steps and work-item flags equal the
     ones before — or the difference was announced in step 3;
   - every moved service is a member of the Extensions train (or matches its pattern) and of no core
     train;
   - the sample service's `promotion.approved` reaches the same deployers as before, and the SDP's
     subscription does not match it.
   A failed comparison prints `--undo --apply` as the next command.

`--undo` reverses in the opposite order: overrides back to the old product and remap (then delete the
override, as the precedent does), remove the exclusions it added, delete the rules it created (found
by their selector on product `mkp`), restore the subscriptions from the mapping file. Completion
records and trains are left.

Outside the portal, not done by the script: pipelines keep sending the old product until they are
changed (`product` stays required, D-4; under enforce stage 1 authorises a key against the product the write is
stored under, and the sent product only when it is a recorded previous product of that service); release-repository annotations, namespaces and Flux key scopes keep the old product name.

## Design: train freeze (L-17) and group changes (L-18)

### Model (P-e)

`PromotionFreeze` gains `Train` (string(64), required, default `""`): the train's id as text, `""`
for a product or service freeze — the sentinel rule of ADR-0003. A train freeze has `Product = ""`
and `Service = ""`; a train may span products, so no single product can stand for it. `TrainName` is
copied for the journal. Unique index `(Product, Service, Train, TargetEnv)` replaces
`(Product, Service, TargetEnv)` (`PlatformDbContext.cs:691`). Migration `AddPromotionFreezeTrain`
fills existing rows with `""`.

`PromotionFreeze.Covers` (`PromotionFreeze.cs:35`) stays for the two existing shapes; a train freeze
is matched through `ReleaseTrainMembership`.

### Evaluation

`PromotionService.FindFreezeAsync(product, service, targetEnv)` (`PromotionService.cs:4038`) becomes a
wrapper over a new `FindFreezesAsync` that returns **all** freezes holding the candidate, widest
first — product, train, service (ADR-0008):

1. Product and service freezes: today's query.
2. Train freezes: the rows with `Train != ""` for the target environment (normally none or one);
   for each, load the train and ask `ReleaseTrainMembership.Contains(train, product, service)`.
   Membership is resolved **now**, from the train's current scope and the service's effective group,
   not from a list copied when the freeze was set. A freeze whose train no longer exists holds
   nothing and is listed in Settings → Freeze as orphaned until lifted (deleting a frozen train is
   refused, so this needs a database edit to occur).

A candidate is held when the list is not empty. No shape overrides another; lifting one leaves the
others. Callers:

| Caller | Change |
|---|---|
| `PromotionService.cs:201` (auto-approve at creation), `:1686` (bypass audit flag), `:2267` (`EvaluateGateAsync`), `:2443` (approval progress), `PromotionEndpoints.cs:203` (detail) | Use the list; the gate's reason names every freeze: "Promotions of train Extensions to production are frozen — {reason}" |
| `PromotionService.DescribeFreezeScope` `:4047` | `train {name}` for a train freeze |
| `PromotionFreezeService.GetHeldByFreezeAsync` `PromotionFreezeService.cs:71` | Loads train freezes and their trains once, matches in memory |
| `PromotionFreezeService.HeldCandidates` / `CountHeldAsync` | For a train: Pending candidates into the environment whose product is one of the train's, filtered by membership in memory |
| `PromotionFreezeService.LiftAsync` `:196` | Sweep = the train's held candidates; one still covered by another freeze stays Pending because `ReevaluateAsync` asks `FindFreezesAsync` again |

### Rules

- **Needs a covering policy.** A train freeze into an environment is refused (422,
  `NoApprovalPolicyForPairException`) unless at least one member service has a policy into it
  (`MatchIntoTargetAsync`), and (422) when the train has no member at all.
- **Who may freeze and lift** (ADR-0008): administrators, any scope. A release manager: a product or
  one of its services when they are a release manager of that product; a train only when they are a
  release manager of **every** product the train covers. `ReleaseService.IsReleaseManagerOfAsync`
  (`ReleaseService.cs:233-244`) and `CanManageAsync` must **not** be reused as they are: they answer
  true for a manager of any one of the products, which stays the rule for running a release and
  managing a train, not for freezing. Add `IsReleaseManagerOfAllAsync(products, environment)`, which
  asks `GetManagerGatesAsync` per product and needs every answer; it is asked for the production
  environment (the train's `ProductionEnvironment`; `prod` for a product or service freeze),
  whichever environment is frozen. Set and lift are symmetric. The check lives in
  `PromotionFreezeService`, so both route groups share it.
- **Audit** `promotion.frozen` / `promotion.unfrozen` payload gains `scope` (`product` | `train` |
  `service`), `train` (`{ id, name }` or null); `product` is null for a train freeze. The history and
  window parsers (`PromotionFreezeService.cs:349-410`) read entries without `scope` as before.
  `FreezeWindow` gains the train; the deployments pages' matchers (`freezeWindows.ts`) mark a deploy
  inside a train window when its service is a member **today** — membership history is not kept, and
  the tooltip says so.
- **Webhooks.** A train freeze is dispatched **once per product of the train**, with
  `WebhookEventFilters(Product: p, Environment: env)` and a payload `{ eventType, scope: "train",
  train: { id, name }, product: p, service: null, targetEnv, reason, frozenBy, frozenAt, actor, at,
  heldCandidates }` where `heldCandidates` counts that product's. A subscription filtered to one
  product gets its product's event and nothing about the others, which is what the mandatory product
  filter promises.
- **Banner** (E-14). The promotions list banner, the promotion page notice and the gate reason print
  the scope: "Product mkp", "Train Extensions", "Service mkp/mkp-spotlight" — with reason, who and
  since when; several freezes are listed widest first.

### Group change from the catalog (L-18)

A service's group decides which group rules, selectors and train freezes apply to it. When a catalog
refresh (stage 1) brings a different group for a service:

1. Compute the consequences with the new group: the policy matched on each edge, the trains the
   service is in, the train freezes that cover it.
2. **Nothing changes** → the group is applied and audited (`service.group-changed`).
3. **Something changes** → a row in `service_group_changes` (`Service` unique, `Product`, `FromGroup`,
   `ToGroup`, `CatalogVersion`, `DetectedAt`). While it exists, resolution uses `FromGroup` — the
   **effective group** — everywhere this plan reads a group. The Services page lists it under
   "Group changes to execute", next to stage 1's "Services to be moved".
4. An administrator opens the preview — policies gained and lost per edge with the Pending promotions
   whose gate would change, trains joined and left, freezes that would start or stop holding — and
   executes it. Executing deletes the row, calls `RefreshPolicySnapshotsForServiceAsync`, re-evaluates
   the service's Pending candidates, and audits `service.group-change.executed` with the preview as
   the payload. Unlike a product move it is **not** held by an Approved or Deploying promotion: those
   keep their snapshot.
5. A later refresh that returns the old group deletes the pending row.

The administrator executes the catalog's decision; there is no "reject". A change that should not
happen is fixed in the catalog.

## API

All new routes and fields need `features.releaseTrains` on. Errors use the shapes the neighbouring
endpoints use (`{ error }`, plus `existingId` on a release conflict).

### Trains — `/api/release-trains` (P-7)

Mapped in `Program.cs` next to `/api/releases` with `RequireAuthorization(AuthorizationPolicies.CanApprove)`;
`ReleaseTrainService` decides who may write, like `ReleaseService`.

| Route | Who | |
|---|---|---|
| `GET /` `?product=&includeArchived=` | signed in | `{ items: [Train], canCreate }` |
| `GET /{id}` | signed in | `Train` |
| `GET /{id}/services` | signed in | Members now: `[{ product, service, group, via }]`, `via` = `product` \| `service` \| `group` \| `pattern`; plus `excluded: [...]` |
| `POST /preview` | signed in | Same answer for an unsaved body — the form's live list |
| `GET /membership?product=` | signed in | `{ services: { "<service>": [{ id, name }] } }` — for the product page and the Services page |
| `POST /` | admin, or release manager of one of `products` | 201 `Train`; 409 on a duplicate name |
| `PUT /{id}` | admin, or release manager of the train's products before **and** after | `Train` |
| `DELETE /{id}` | admin | 204; 409 while a freeze names it. Releases keep `trainName` |

```jsonc
// POST /api/release-trains
{ "name": "Extensions", "cadence": "every second Tuesday", "products": ["mkp"],
  "scope": "services",
  "selector": { "groups": ["extensions"] },
  "exclusions": {},                       // scope "products" only
  "stagingEnvironment": "staging", "productionEnvironment": "prod",
  "steps": null }                         // null = the default checklist
// → 201
{ "id": "…", "name": "Extensions", "cadence": "…", "products": ["mkp"], "scope": "services",
  "selector": { "services": [], "groups": ["extensions"], "patterns": [] },
  "exclusions": { "services": [], "groups": [], "patterns": [] },
  "stagingEnvironment": "staging", "productionEnvironment": "prod", "steps": null,
  "archived": false, "memberCount": 14, "openReleases": 1, "canManage": true,
  "createdBy": "…", "createdAt": "…", "updatedAt": "…" }
```

### Releases (P-8, P-9)

`POST /api/releases` and `PUT /api/releases/{id}` — `CreateReleaseRequest` / `UpdateReleaseRequest`
(`ReleaseDtos.cs:111-132`) get optional members appended; `Version` and `Products` become nullable in
the record and are still required exactly when they are today:

| Body | Result |
|---|---|
| `{ version, products, targetDate? }` | Today's release. Unchanged. |
| `{ trainId, version, targetDate? }` | Products, environments, scope, exclusions and checklist copied from the train. Sending `products` as well must equal the train's (400 otherwise). |
| `{ services: ["mkp/a","mkp/b"], windowFrom, windowTo, version?, title? }` | Ad hoc. `products` derived from the entries. `title` defaults to "Release {version}" or "Ad hoc release {date of windowTo}". |

`version` is required unless `services` and a window are given. Who may create: unchanged
(`CanCreateAsync`, `EnsureInScopeAsync`, `ReleaseService.cs:265`, `:805`) — for a train or an ad hoc
release, a release manager of one of the resulting products.

`ReleaseDto` and `ReleaseSummaryDto` gain: `train: { id, name } | null`, `scope`, `selector`,
`standingExclusions`, `windowFrom`, `windowTo`, `shipped: { recordedAt, recordedBy, shipped,
notShipped, leftOut } | null`, and on create/update `warnings: string[]`. `version` is null for a
release without a line.

| Route | Change |
|---|---|
| `GET /api/releases?train={id}&product=&status=` | `train` is new; `product` exists and now also matches service-scoped releases through their products |
| `GET /api/releases/{id}/progress` | `+ offRelease[]` (L-14); `version` null without a line |
| `POST /api/releases/{id}/complete` | Body unchanged. Writes the record. Response `ReleaseDto` with `shipped` |
| `GET /api/releases/{id}/shipped` | `{ recordedAt, recordedBy, services: [{ product, service, outcome, version, deployedAt, eventId, stagingVersion, promotionIds, references, changeSetSource, live, differs }] }`; 404 when not recorded |
| `POST /api/releases/{id}/record` | Release managers of it. Writes the record of a completed release that has none; 409 when it has one |

### Promotions and work items (P-10)

| Route | Change |
|---|---|
| `GET /api/promotions?release={id}` | Only candidates `ReleaseScope` attributes to the release: service ships with it, target is its staging or production environment, version on its line — or, for a service off the line or a release without one, created inside the window. `PromotionQuery` (`PromotionService.cs:4170`) gains `ReleaseId`; applied before the Pending / resolved split in `GetAsync` (`:742`) |
| `GET /api/promotions/filter-options` | `+ releases: [{ id, title, status }]` — active ones, plus those completed in the last 30 days |
| `GET /api/work-items/me/pending?release={id}` | Same attribution on the candidates behind the queue (`WorkItemApprovalService.GetPendingForCurrentUserAsync`, `WorkItemApprovalService.cs:537`), pending and decided views |
| `GET /api/promotions/{id}` | `+ releases: [{ id, title }]` the candidate belongs to; `freezes: [...]` next to the existing `freeze` (which stays the widest) |

An unknown release id answers 400. The progress checks keep reading production promotions only; the
filter is wider on purpose, because the release's approvals on the way into staging are what people
mass-approve.

### MCP (P-11)

New `Mcp/ReleaseTools.cs`, registered in `McpServerSetup.AddInfraPortalMcp` (`WithTools<ReleaseTools>()`):

- `list_releases(status?, product?, train?, limit?)` — id, title, version, train, products, status,
  dates, steps closed / total, current step.
- `get_release(releaseId, includeServices = true)` — the release, its checks (state, done / total,
  summary, issues), off-release services, and after completion the record's counts and not-shipped
  services.

`list_promotions` (`Mcp/PromotionTools.cs:23`) gains `release`. All read-only.

### Policies

`UpsertPolicyRequest` (`PromotionAdminEndpoints.cs:677`) gains `match: { kind, values[] } | null`;
`service` stays and means a single-service match. Responses (`MapPolicy`) gain `match`, `matches`
(count of services matched now) and keep `service`.

| Route | |
|---|---|
| `POST /api/promotions/admin/policies`, `PUT …/{id}` | 400: mixed or empty values, a pattern without `*`, a group the product does not declare. 409: `{ error, conflicts: [{ policyId, kind, value, services: [...] }] }` |
| `POST /api/promotions/admin/policies/preview` `{ product, sourceEnv, targetEnv, match, exceptPolicyId? }` | `{ services: [{ service, group, overriddenBy: null \| { policyId, level } }], conflicts: [...] }` — the live "matches these services" list; `overriddenBy` marks a service a more specific rule takes |
| `GET /api/promotions/admin/policies/conflicts` | Ties that appeared after saving (resolver `Ambiguous`) |

Admin only, as today.

### Freezes

| Route | Who | |
|---|---|---|
| `POST /api/promotions/admin/freezes` | admin | Body gains `train` (id) as an alternative to `product` (+ `service`) |
| `DELETE /api/promotions/admin/freezes/trains/{trainId}/{targetEnv}` | admin | Lifts a train freeze |
| `POST /api/promotions/freezes` | admin or release manager of the scope | Same body as the admin route |
| `DELETE /api/promotions/freezes/{product}/{targetEnv}`, `…/services/{service}`, `…/trains/{trainId}/{targetEnv}` | admin or release manager of the scope | 403 otherwise |
| `GET /api/promotions/freezes`, `GET /api/promotions/admin/freezes` | as today | Each freeze gains `scope`, `train: { id, name } \| null`, `canLift`; `product` is null for a train freeze |
| `GET /api/promotions/freezes/scopes` | signed in | What the caller may freeze: `{ products: [...], trains: [...] }` — a train only when the caller is a release manager of all its products |

The admin routes stay as they are for existing callers.

### Webhooks (P-12)

`WebhookFilterDto` (`WebhookEndpoints.cs:815`) gains `excludedServices: string[]`; `services` entries
may contain `*`. Responses (`FilterResponse`, `:319`) gain `excludedServices`, and each subscription
`productFilterMissing`. Create and update responses gain `warnings`. `GET /api/webhooks/matches` as
described. Admin only, as today.

### Group changes

On stage 1's services resource (`Features/ServiceList/ServiceListEndpoints.cs`), next to
`GET`/`POST /api/services/{name}/move`; admin only.

| Route | |
|---|---|
| `GET /api/services?state=group-change` | Services with a pending group change (a new value of the existing `state` filter) |
| `GET /api/services/{name}/group-change` | Preview: policies, promotions, trains, freezes affected |
| `POST /api/services/{name}/group-change` | Applies it; audited |

## UI

No web unit tests exist in this repository; each UI task is verified with `npx tsc -b`, `npm run
build` and the manual checks listed with it. Types and calls go into `src/Platform.Web/src/lib/api.ts`;
the flag into `stores/featureFlagsStore.ts` (`FeatureFlag.ReleaseTrains`). With the flag off none of
the controls below render and the pages look as they do today.

| Screen | Files | What changes |
|---|---|---|
| **E-8** Releases → **Trains** tab | `app/releases/ReleasesPage.tsx` (tabs **Releases** \| **Trains**), new `app/releases/TrainsTab.tsx`, `app/releases/TrainForm.tsx`, `components/releases/ServiceSelectorFields.tsx`, `components/releases/StepTemplateEditor.tsx` | List of trains: name, cadence, products, scope in words ("mkp without extensions, support-tools"), members, open releases, **New release** per row. **New train** / **Edit** for admins and release managers. The form shows the member list live (`POST /preview`). |
| **E-9** New-release form | `app/releases/ReleaseForm.tsx` | The three fields stay: **Version**, **Products**, **Release date**. Above them an optional **Train** select: choosing one fills Products (read-only, with "from train Core") and nothing else needs typing. A **Hand-picked services** switch replaces Products with a service picker and Release date with **From** / **To**, and makes Version optional. Required fields: train mode 1 (version; date as today is optional), today's mode 2, ad hoc 3 — never more than today plus the window. |
| **E-10** Release page | `app/releases/ReleaseDetailPage.tsx`, new `components/releases/OffReleaseWarning.tsx`, `components/releases/CompleteReleaseDialog.tsx`, `components/releases/ShippedTable.tsx`; `app/releases/ReleaseServicesDialog.tsx` | Header shows the train. A warning lists services outside the release that deployed on its line, with **Add to release** where the service was left out by hand. **Complete** opens a dialog that lists the services that have not shipped and says they will be recorded as not shipped. After completion a **What shipped** table (service, version, deployed, changes) with not-shipped services at the top and a marker on rows where the live state differs. The Services dialog shows standing exclusions as locked rows. |
| **E-11** Releases list | `app/releases/ReleasesPage.tsx` | **Train** and **Product** filters, kept in the URL; a train column. |
| **E-12** Mass approve, Work items queue | `components/promotions/MassApproveDialog.tsx`, `app/promotions/PromotionsPage.tsx`, `app/promotions/promotionFilterParams.ts`, `app/me/MyQueuePage.tsx`, `app/me/queueFilterParams.ts` | Mass approve gets a **Release** select *inside the dialog*, next to environment and gate, default **All releases**. It is never initialised from the page — the dialog's own rule (`MassApproveDialog.tsx:38-40`); `PromotionsPage.tsx:979-985` passes no release. The Promotions page and the queue get their own **Release** filter. |
| **E-13** Settings → Promotions | `app/settings/PromotionSettings.tsx`, new `components/promotions/PolicyMatchFields.tsx` | "Applies to": **All services** \| **Selected services** \| **Service group** \| **Name pattern**. Under it "Matches these services" updates as you type (`POST …/policies/preview`), marking services a more specific rule takes. A conflict is shown before saving. The policy list groups by product and edge and prints each rule's level. |
| **E-14** Freeze | `app/settings/FreezeSettings.tsx` → content moves to new `app/promotions/FreezePage.tsx` at `/promotions/freeze`; `App.tsx` (route outside `AdminRoute`, `/settings/freeze` redirects); `app/promotions/FreezeNotice.tsx`, `app/promotions/FreezeCues.tsx`, `app/deployments/FreezeCues.tsx`, `app/deployments/freezeWindows.ts`, `lib/freezeScope.ts` | Scope select: **Product** \| **Release train** \| **Service**, offering only what the caller may freeze (`GET …/freezes/scopes`). Settings keeps a **Freeze** entry that links to the page. Banners and notices name the scope and list every freeze that holds. |
| **E-15** Product page | `app/deployments/ProductDeploymentsPage.tsx` | Filters **Group**, **Train**, **Type** above the service list, shown when the product has more than one value of each; kept in the URL. Group and type from stage 1's service list, train from `GET /api/release-trains/membership`. |
| Webhooks (P-12) | `app/webhooks/WebhookFilterFields.tsx`, `app/webhooks/webhookFilters.ts`, `app/webhooks/WebhookListPage.tsx`, `app/webhooks/WebhookDetailPage.tsx`, `app/webhooks/NotificationCreateForm.tsx` | Services field accepts `*`; new **Except services** field; both disabled with a hint until a product is chosen. Under the fields: "Receives events of N services" with the list. A **Who receives…** box on the list page (product, service, event) backed by `GET /api/webhooks/matches`. Banner for subscriptions without a product filter. |
| Settings → Services (stage 1, E-1) | `app/settings/ServicesSettings.tsx` | Section **Group changes to execute** with preview and **Execute**; a **Train** column. |

---

## Tasks

Tests are written first and named as the neighbouring ones are: unit tests under
`tests/Platform.Api.Tests/Features/<Feature>/…Tests.cs`, endpoint tests as
`tests/Platform.Integration.Tests/…Tests.cs`, methods as sentences
(`Create_FromATrain_CopiesItsSettings`).

### Step 1 — trains, releases, completion record

#### Task 1: Feature flag, `NameGlob`, `ServiceSelector`

**Files:** `Infrastructure/Features/IFeatureFlags.cs`, `FeatureFlagSeeder.cs`
(`Features:ReleaseTrains:DefaultEnabled`, default false), new `Infrastructure/NameGlob.cs`,
`Features/Builds/BuildPromotions.cs`, new `Features/Releases/ServiceSelector.cs`; web
`stores/featureFlagsStore.ts`. Reword the `Release.cs` summary and `ReleaseService.cs:694-697`: a
Release is one run, not a train.

- [ ] Tests `Infrastructure/NameGlobTests.cs`: no `*` = exact; `a*`, `*a`, `a*b*`; regex characters
  in the pattern are literal; ordinal.
- [ ] Tests `Features/Releases/ServiceSelectorTests.cs`: names, groups, patterns, empty selector
  matches nothing; a service without a group matches no group.
- [ ] `BuildPromotions.BranchMatches` delegates; existing branch-pattern tests stay green.

**Done when** the flag shows in Settings → Feature Flags and nothing else changed.

#### Task 2: `ReleaseTrain` entity, migrations, `ReleaseTrainMembership`

**Files:** `Features/Releases/Models/ReleaseTrain.cs`, `Models/ReleaseTrainDtos.cs`,
`ReleaseTrainMembership.cs`, `PlatformDbContext.cs`, migrations `AddReleaseTrains` (both sets): table
`release_trains`, the new `releases` columns, table `release_shipped_services`. In stage 1's
`ServiceMoveClassification` (`ServiceProductRemapService`): list the four selector columns as
JSON-carried references that move, and `ReleaseShippedService` as "stays — history"; its
`ServiceMoveCoverageTests` must pass.

- [ ] Tests `ReleaseTrainMembershipTests.cs`: product scope with name, group and pattern exclusions;
  service scope by list, group, pattern; a never-deployed service is a member; a service of another
  product never is; a service in two trains.
- [ ] Move test (stage 1's suite): moving a service rewrites `product/service` in train and release
  selectors; `release_shipped_services` keeps the old product.

#### Task 3: `ReleaseTrainService`, trains endpoints, release from a train

**Files:** new `ReleaseTrainService.cs`, `ReleaseTrainEndpoints.cs`, `Program.cs`;
`ReleaseService.cs` (`CleanAsync`, `Apply`, `CreateAsync`, `UpdateAsync`, `EnsureUniqueAsync`,
`ListAsync`, `ToDtoAsync`, `ToSummary`, `Snapshot`), `ReleaseStepTemplate.cs`, `ReleaseDtos.cs`,
`ReleaseEndpoints.cs`.

- [ ] Tests `ReleaseTrainEndpointsTests.cs`: 404 with the flag off; CRUD; a release manager of the
  product may create and edit, another signed-in user gets 403, only an admin deletes; duplicate name
  409; `/{id}/services` and `/preview` agree; delete refused while frozen (after Task 19).
- [ ] Tests `ReleaseEndpointsTests.cs` (extend): `Create_FromATrain_CopiesItsSettings`;
  `EditingTheTrain_DoesNotChangeAnOpenRelease`; `Create_TodaysBody_IsUnchanged` (snapshot of the DTO
  minus the additive fields); `Create_TrainAndDifferentProducts_IsRefused`; list `?train=`.
- [ ] Tests `Features/Releases/ReleaseUniquenessTests.cs`: rules 1–4 above, including "completed
  release still blocks its line for the product" and the `warnings` for a shared service.

**Done when** a release opened from a train shows the train's products and checklist, and every
existing `ReleaseEndpointsTests` test passes unmodified.

#### Task 4: `ReleaseScope` and service-scoped progress

**Files:** new `ReleaseScope.cs`; `ReleaseProgressService.cs` (`ComputeAsync` uses it; behaviour for
scope `products` identical).

- [ ] Characterisation first: run `Progress_ReadsTheLineFromDeploysPromotionsAndMigrations` and its
  siblings before the refactor; they must pass unchanged after it.
- [ ] Tests `Features/Releases/ReleaseScopeTests.cs`: standing exclusions and the hand list both
  exclude; selector by group follows a service that joins the group; an exactly named service is
  listed though never deployed; a service of the product outside the selector is not in the release.

#### Task 5: Ad hoc release

**Files:** `ReleaseService.cs` (validation of `services` + window, title default, `""` line),
`ReleaseProgressService.cs` (line-less path, `LineLabel`), `ReleaseStepTemplate.cs` (`AdHoc()`).

- [ ] Tests `ReleaseEndpointsTests.cs`: `AdHoc_WithoutALine_ReadsTheWindow` (deploys before
  `windowFrom` and after `windowTo` are not read; a completed one stops at `completedAt`);
  `AdHoc_WithALine_ReadsTheLineForItsServicesOnly`; validation (no services, window reversed, service
  of an unknown product, version missing without a window).
- [ ] Tests `Features/Releases/ReleaseVersionLineTests.cs` (extend): `OnLine` never matches an empty
  line.

#### Task 6: L-14 warning

**Files:** `ReleaseProgressService.cs`, `ReleaseDtos.cs` (`ReleaseOffReleaseDto`).

- [ ] Tests: a left-out service deploying `5.10.3` to staging is listed with `left-out`; a service of
  the product outside a group selector with `not-selected`; a service of another product on `5.10`
  is not; a deploy before the window is not; none without a line; the checks' totals do not move.

#### Task 7: Completion record

**Files:** `Features/Releases/Models/ReleaseShippedService.cs`, `ReleaseService.cs` (`CompleteAsync`,
`ReopenAsync`, new `RecordAsync`, `GetShippedAsync`), `ReleaseEndpoints.cs`, `ReleaseDtos.cs`.

- [ ] Tests `ReleaseCompletionRecordTests.cs` (integration): all shipped; partial (two services
  without a production deploy → `not-shipped`, status `completed`); left-out services recorded as
  such; references are the union over a service's Deployed promotions, de-duplicated, a Superseded
  promotion contributes nothing; a deploy event backfilled after completion sets `differs` and leaves
  the row; reopen deletes, complete rewrites; `/record` on an old completed release; 409 on a second
  `/record`.

#### Task 8: Promotions and queue `release` filter, MCP tools

**Files:** `PromotionService.cs` (`PromotionQuery`, `GetAsync`, `GetFilterOptionsAsync`),
`PromotionEndpoints.cs` (list, detail), `WorkItemApprovalService.cs`, `WorkItemEndpoints.cs`, new
`Mcp/ReleaseTools.cs`, `Mcp/McpServerSetup.cs`, `Mcp/PromotionTools.cs`.

- [ ] Tests `PromotionListReadPathTests.cs`, `WorkItemQueueReadTests.cs` (extend): only the release's
  candidates; staging-target promotions on the line are included; unknown id 400; the Pending list is
  still never clipped.
- [ ] Tests `McpServerTests.cs` (extend): both tools listed and callable; `list_promotions` with
  `release`.

#### Task 9: UI for step 1 (E-8, E-9, E-10, E-11, E-12)

**Files:** as in the UI table.

- [ ] Manual: flag off → Releases page identical to today. Flag on → create a train, open a release
  from it with a version only; open an ad hoc release; complete a release with one service not
  deployed and read the What shipped table; filter the list by train; Mass approve with a product
  filter set on the page opens with **All releases**.

### Step 2 — policies for selected services, shared resolver, webhook filters

#### Task 10: Policy selectors — model and migration

**Files:** `Features/Promotions/Models/PromotionPolicy.cs` (`MatchKind`, `Selectors`), new
`Models/PromotionPolicySelector.cs`, `PlatformDbContext.cs`, migrations `AddPromotionPolicySelectors`,
`Infrastructure/Persistence/PromotionSeedData.cs`.

- [ ] Migration test on both engines' SQL scripts: every existing policy has exactly one selector
  row; a second product-level policy for an edge now fails at the database.

#### Task 11: The resolver, `MatchedBy`, snapshot comparison

**Files:** `PromotionPolicyResolver.cs`, `Models/ResolvedPolicySnapshot.cs`, `PromotionService.cs`
(`SnapshotEquals`, both comparison sites, `RefreshPolicySnapshotsForServiceAsync`).

- [ ] Tests `PromotionPolicyResolverTests.cs` (extend): the order name → group → pattern → product on
  one edge; another edge's rules never apply; no group, no group match; tie between two patterns →
  older policy, `Ambiguous`; `MatchFromSourceAsync` returns one match per target with the right level;
  `MatchingServices`.
- [ ] Tests `PromotionPolicyReapplyTests.cs` (extend):
  `SnapshotWithoutMatchedBy_IsNotReappliedWhenNothingChanged` (no comment, no audit entry, count 0);
  `CancelApproval_OldSnapshot_IsNotTreatedAsChanged`; a new group rule re-gates the group's Pending
  candidates and leaves the others; a group change re-gates one service across edges.

#### Task 12: Move every lookup site to the resolver

**Files:** `Features/Builds/BuildIngestHook.cs`, `Features/Promotions/PromotionEndpoints.cs`
(build-targets), `Mcp/BuildTools.cs`, `Features/Releases/ReleaseService.cs`
(`GetManagerGatesAsync`), `Features/Promotions/PromotionFreezeService.cs`,
`Features/Settings/EnvironmentMergeService.cs`. Delete `ResolveForTargetAsync` /
`SnapshotForTargetAsync`.

- [ ] Characterisation tests written **before** the change and kept: for a product-level and a
  single-service policy, `BuildPromotionFlowTests`, the build-targets endpoint, `get_build`,
  `ReleaseManagers_AreTheApproversOfTheProductionGate`, `PromotionFreezeTests`,
  `EnvironmentAliasAndMergeTests` give the same results.
- [ ] New: a build of a service governed by a group rule with `autoCreateFromBranches` opens its
  candidate (site 3); build-targets and `get_build` list that target (4, 5); the service can be
  frozen (7); merging two environments with group rules reports the conflict (8).
- [ ] `grep -rn "PromotionPolicies" src/Platform.Api --include=*.cs` shows the resolver, the admin
  service, the seeders and the vocabulary lists only.

#### Task 13: Policy admin — save-time checks, preview, refresh

**Files:** new `Features/Promotions/PromotionPolicyAdminService.cs`, `PromotionAdminEndpoints.cs`
(policies section delegates), DTOs.

- [ ] Tests `PromotionPolicyGateTests.cs` / new `PromotionPolicySelectorEndpointTests.cs`: `service`
  in the body still works and reads back; `match` kinds; mixed kinds 400; pattern without `*` 400;
  two patterns matching one known service 409 with the service named; the same group twice 409;
  preview lists matches and `overriddenBy`; saving refreshes Pending snapshots of the matched services
  and reports `reappliedCandidates`; 400 `feature_disabled` for group / pattern with the flag off
  while existing rules still resolve.

#### Task 14: Webhook patterns, exclusions, mandatory product filter, shared matcher

**Files:** `Features/Webhooks/Models/WebhookSubscription.cs`, `WebhookSubscriptionFilters.cs`,
`WebhookEndpoints.cs` (`ApplyFiltersAsync`, `FilterResponse`, new `GetMatches`),
`Features/Diagnostics/DiagnosticsService.cs`, migrations `AddWebhookExcludedServices`, option
`Webhooks:RequireProductFilter`.

- [ ] Tests `WebhookFilterMatchingTests.cs` (extend): the matching table above, case by case; an
  event without a service passes patterns and exclusions; exact entries behave as before.
- [ ] Tests `WebhookIntegrationTests.cs` (extend): patterns without a product 400; no product in
  `warn` → 201 with a warning, in `enforce` → 400; a stored subscription without a product still
  receives in `enforce`; `productFilterMissing`; `/matches` agrees with what `DispatchAsync` enqueues.
- [ ] Tests `Features/Diagnostics`: `diagnose_promotion` reports a subscription as not listening when
  only an exclusion removes the service, and no longer matches a product that differs in case.

#### Task 15: UI for step 2 (E-13, webhooks)

- [ ] Manual: create a group rule and watch the list of matched services; try a second pattern that
  overlaps; open a promotion of a matched service and read where its policy came from; add `*` and an
  exception to a subscription and check **Who receives…**.

#### Task 16: `knowledge/promotion-policies.yaml`, `knowledge/webhook-catalogue.yaml`

Update the resolution order and the filter semantics the diagnosis playbooks quote.

### Step 3 — the move

#### Task 17: Cutover script

**Files:** new `docs/plans/release-trains.cutover.py`; a section "Moving the extension and
support-tool services into `mkp`" in this file's log when it has run.

- [ ] Dry run against a development instance seeded with two extension services, one support tool,
  one service-level policy, one Pending and one Approved promotion: it stops on the Approved one,
  prints every planned write, writes nothing.
- [ ] `--apply` there, then every comparison in "The script" step 5 passes; `--undo --apply` returns
  the *before* picture.
- [ ] `--match patterns` produces pattern rules and the same comparisons pass.

#### Task 18: Run it in production

Not code. In one sitting with the owners of both deployers: dry run, review the printed plan and the
subscription mapping, `--apply`, the control scenario (Task 27) for one extension service, then the
variants. Record date, counts and anything unexpected in this file.

### Step 4 — train freeze, group changes

#### Task 19: Freeze model and matching

**Files:** `Models/PromotionFreeze.cs`, `PlatformDbContext.cs`, migrations `AddPromotionFreezeTrain`,
`PromotionService.cs` (`FindFreezesAsync`, callers, `DescribeFreezeScope`), `PromotionFreezeService.cs`
(`GetHeldByFreezeAsync`, `HeldCandidates`, `GetActiveAsync`).

- [ ] Tests `PromotionFreezeTests.cs` (extend): a train freeze holds its members and no sibling; a
  product freeze still holds members of a train; product + train + service at once → all three
  listed, widest first; lifting the train freeze leaves the product one holding; a service that joins
  the train (group changes, selector edited) is held at its next evaluation and one that leaves is
  released; auto-approve at creation waits under a train freeze; bypass ignores it.

#### Task 20: Freeze, lift, permissions, webhooks, audit

**Files:** `PromotionFreezeService.cs` (`FreezeAsync`, `LiftAsync`, authorisation, per-product
dispatch, parsers), `PromotionAdminEndpoints.cs`, `PromotionEndpoints.cs` (non-admin routes,
`/freezes/scopes`), `ReleaseTrainService.cs` (delete refused while frozen).

- [ ] Tests `PromotionFreezeEndpointTests.cs` (extend): 422 for a train with no covered member and
  for an empty train; 409 twice; a release manager of the product freezes and lifts its product and its
  service, and gets 403 for another product's; a train over two products is frozen and lifted only by
  a release manager of both (403 for a manager of one) and by an admin; another signed-in user 403; admin routes
  unchanged; a train over two products dispatches two `promotion.frozen` events, each passing only
  its product's filter.
- [ ] Tests `PromotionFreezeHistoryTests.cs` (extend): entries with and without `scope` parse; a
  train window.

#### Task 21: UI (E-14)

- [ ] Manual: as a release manager without the admin role open `/promotions/freeze`, freeze a train,
  see the banner on the Promotions page name it, lift it. `/settings/freeze` redirects.

#### Task 22: Group changes (L-18)

**Files:** new `Features/ServiceList/ServiceGroupChangeService.cs`, `Models/ServiceGroupChange.cs`,
`ServiceListEndpoints.cs`, `PlatformDbContext.cs`, migrations `AddServiceGroupChanges`; the hook in
`ServiceListCatalogSync.ApplyAsync`; `PromotionPolicyResolver` and `ReleaseTrainMembership` read the
effective group through it.

- [ ] Tests `ServiceGroupChangeTests.cs`: a change with no consequence is applied and audited; one
  that changes a policy waits and the old group keeps resolving; the preview lists the edge, the
  Pending promotions, the trains and the freezes; execute re-gates and audits; the catalog reverting
  removes the pending row; an Approved promotion keeps its snapshot.

#### Task 23: UI (E-15, Services page section)

- [ ] Manual: filter a product's services by group and train; execute a pending group change from
  its preview.

### Configuration, docs, verification

#### Task 24: Hotfix branches on the staging policies (D-26)

A settings change, no code. For each product's `build → staging` policy, add the hotfix pattern to
`autoCreateFromBranches` next to the release one:

```jsonc
"autoCreateFromBranches": ["refs/heads/release/*", "refs/heads/hotfix/*"]
```

Patterns are full git refs matched with `*` (`PromotionPolicy.AutoCreateFromBranches`,
`PromotionPolicy.cs:174-183`); the governance rules name hotfix branches `hotfix/<ticket>`.

- [ ] Before: with `list_builds`, confirm one hotfix build's `branch` reads `refs/heads/hotfix/…` and
  not a short name.
- [ ] Change it in **Settings → Promotions**, or `PUT /api/promotions/admin/policies/{id}` with the
  whole policy (the body replaces every field).
- [ ] After: the next hotfix build opens a `build → staging` candidate with the same steps a release
  build gets, and belongs to its line's release (variant 4 of the verification).
- [ ] The governance repository records the pattern in its promotion policy (outside this repository).

#### Task 25: Docs

- `notes/releases-api.md`: a Release is one run; trains; scope kinds and selectors; ad hoc; the
  uniqueness rules; off-release warning; completion record; new routes and fields. Remove "a
  versioned release train" and "wasn't part of the train".
- `notes/promotions-api.md`: `release` filter; `match` on policies, resolution order, preview and
  conflicts; `matchedBy` in the snapshot. **Fix the stale freeze scope:** the access table (line 28,
  "Admins"), the gate rule (line 674, "the candidate's `(product, targetEnv)` is frozen") and the
  Promotion Freeze section (from line 679, "one product's promotions") describe two scopes and
  admin-only freezing; rewrite for three scopes, the union, release managers, the non-admin routes
  and the `freezes` list on the detail response.
- `notes/mcp-server.md`: `list_releases`, `get_release`, `release` on `list_promotions`.
- `.claude/skills/infrapilot-api/SKILL.md`: the two tools and when to use them ("what shipped in
  release X", "which release is this promotion part of").
- `knowledge/webhook-catalogue.yaml` (there is no webhooks note under `notes/`): patterns,
  exclusions, the product rule, the train freeze payload.
- `GLOSSARY.md`: check that **Release**, **Release Train**, **Service Group** and the three-scope
  **Promotion Freeze** relationships arrived with ADR-0007/0008; add **Completion record** under
  Release and "_Avoid_: grouped promotion, release train (for one run)".
- `docs/adr/0002-…` and `0003-…`: a one-line "amended by ADR-0008" note if the ADR change did not add it.
- Code comments that state the old rules: `PromotionPolicy.cs:5-9`, `PromotionPolicyResolver.cs:7-12`,
  `PromotionFreeze.cs:3-9`, `WebhookSubscriptionFilters.cs:6-22`.

#### Task 26: What's new entries

One file each under `src/Platform.Web/src/app/whats-new/entries/`, dated the day its PR merges, added
in the PR that ships the feature. The move in step 3 is a data change of one installation and gets no
entry.

| File | Audience | Ships with |
|---|---|---|
| `YYYY-MM-DD-release-trains.md` | everyone | Task 9 |
| `YYYY-MM-DD-ad-hoc-releases.md` | everyone | Task 9 |
| `YYYY-MM-DD-release-what-shipped.md` | everyone | Task 9 |
| `YYYY-MM-DD-approve-by-release.md` | approvers | Task 9 |
| `YYYY-MM-DD-policy-for-selected-services.md` | admins | Task 15 |
| `YYYY-MM-DD-webhook-service-patterns.md` | admins | Task 15 |
| `YYYY-MM-DD-freeze-a-release-train.md` | approvers | Task 21 |
| `YYYY-MM-DD-product-service-filters.md` | everyone | Task 23 |

Drafts:

```markdown
---
title: Save how you release as a release train
date: YYYY-MM-DD
audience: everyone
link: /releases
linkLabel: Releases
---
**Releases** has a new **Trains** tab. A train keeps the products, environments, services that never
ship with it and the checklist, so you open the next release by choosing the train and typing the
version. Changing a train affects the releases you open afterwards, not the ones already running.
```

```markdown
---
title: Open a release for a few hand-picked services
date: YYYY-MM-DD
audience: everyone
link: /releases
linkLabel: Releases
---
In **New release**, switch on **Hand-picked services** to follow a one-off change: choose the
services and the time window, and add a version only if there is one. The portal shows what those
services deployed and promoted in that window.
```

```markdown
---
title: See what a release shipped
date: YYYY-MM-DD
audience: everyone
link: /releases
linkLabel: Releases
---
When you **Complete** a release, the portal records the version each service shipped and the changes
that went with it. You can complete a release while some services have not shipped; they are listed
as not shipped. The release page also warns when a service outside the release deploys a version of
its line.
```

```markdown
---
title: Approve one release's promotions at a time
date: YYYY-MM-DD
audience: approvers
link: /promotions
linkLabel: Promotions
---
**Mass approve** has a **Release** choice next to the environment and the gate, so you approve only
the promotions of the release you are running. You can also filter **Promotions** and the
**Work items queue** by release.
```

```markdown
---
title: Set approval rules for a group of services
date: YYYY-MM-DD
audience: admins
link: /settings/promotions
linkLabel: Settings → Promotions
---
A promotion policy can now apply to **Selected services**, a **Service group** or a **Name pattern**
instead of a whole product. The form shows which services the rule matches as you type. The most
specific rule wins: a service's own rule, then its group, then a pattern, then the product.
```

```markdown
---
title: Narrow a webhook to services by pattern
date: YYYY-MM-DD
audience: admins
link: /webhooks
linkLabel: Webhooks
---
A webhook's service filter accepts `*`, and **Except services** leaves services out. Both narrow the
products you chose, so choose a product first. **Who receives…** on the Webhooks page shows which
subscriptions get an event of a given service.
```

```markdown
---
title: Freeze a release train
date: YYYY-MM-DD
audience: approvers
link: /promotions/freeze
linkLabel: Freeze
---
A freeze can now cover a **Release train**: every service of the train, including ones that join it
later. Release managers can set and lift freezes for their own products, and for a train when they are
release manager of all its products. A held promotion
names the freeze that holds it and who set it.
```

```markdown
---
title: Filter a product's services by group and train
date: YYYY-MM-DD
audience: everyone
link: /deployments
linkLabel: Deployments
---
On a product's page you can filter its services by **Group**, **Train** and **Type**.
```

#### Task 27: Verify

- [ ] `dotnet test` for `tests/Platform.Api.Tests` and `tests/Platform.Integration.Tests`; `npx tsc -b`
  and `npm run build` in `src/Platform.Web`; both migration sets apply to an empty and to a copied
  database.
- [ ] With `features.releaseTrains` **off** after all steps are merged: Releases, Promotions,
  Settings → Promotions, Freeze and Webhooks look and answer as before; stored rules still apply.
- [ ] `/whats-new` shows the entries without console warnings.
- [ ] **Control scenario** (development instance first, production in the Task 18 sitting). One
  extension service, `mkp-extension-adobe`, after the move:
  1. A build from `refs/heads/release/<line>` registers with the product its pipeline still sends
     and is filed under `mkp`; the response warns about the product.
  2. A `build → staging` candidate opens by itself with the steps **QA Review** and **Release
     Approval**; the promotion page says where its policy came from.
  3. The Extensions train's open release lists the service; the Core train's release does not, and
     shows no warning for it.
  4. **Mass approve** with that release chosen shows the candidate; approving it sends
     `promotion.approved` to `mkp-release`, and the marketplace SDP's subscription does not receive
     it (`GET /api/webhooks/matches`, then the delivery history).
  5. `staging → prod` waits for its work items, is approved, deploys; the release's checks complete.
  6. **Complete** records the version and the change set; the record survives reopening the page a
     day later with a hotfix of the line deployed.
- [ ] **Variants**, each differing from the scenario in one thing:
  1. *A core service* (`mkp-spotlight`): it is in the Core release, not in the Extensions one;
     approvals reach both deployers and each skips what it does not own, as before the move.
  2. *The service with a policy of its own* (`mkp-extension-operations`): its service-level rule
     wins over the group rule and the product's; the approvers are the ones it had before.
  3. *A support tool*: matched by group `support-tools` (or its pattern); excluded from the Core
     train; old address redirects.
  4. *A hotfix under a freeze:* a build from `refs/heads/hotfix/<ticket>` opens its staging candidate
     (Task 24) and shows on the same line's release. Freeze the Extensions train into production: the
     `staging → prod` candidate stays Pending with the banner naming the train while a core service's
     promotion goes through; lift, and it is re-evaluated.

## Open points

1. **Release-note channels for the extension services.** Release notes are per product and carry no
   service, so after the move a channel that listened to `mkp-extensions` either receives all of
   `mkp`'s notes or none. Release notes for a Release (stage 3) would give the Extensions train its
   own notes. Until then: accept whole-product notes, or leave those channels silent? Per channel, in
   the script's mapping file.
2. **The control scenario** is derived here; the specification's file does not contain one. The
   owner should confirm the scenario and the four variants before Task 18.
3. **Group or pattern at cutover.** Whether the service catalog publishes the groups before step 3
   decides `--match groups` or `--match patterns`. The namespace of the `support-tools` group is
   undecided on the catalog side and does not block the portal.
4. **The two infrastructure products** move to the catalog pseudo-product `platform` (D-9). The
   stage order names only extensions and support tools for step 3. Assumed here: they follow stage
   1's catalog take-over, not this script.
5. **Server-side flag for Releases themselves.** `/api/releases` has no flag check; the Promotions
   flag hides the pages only. This plan checks its own flag and leaves that as it is, because turning
   a check on would reject calls that pass today. Decide separately whether to add it with a warning
   period.
6. **Switching `Webhooks:RequireProductFilter` to `enforce`:** who decides, and when — after the
   list of subscriptions without a product filter is empty.
