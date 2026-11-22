---
status: accepted
date: 2026-11-22
---

# A release train is saved release settings; a Release groups promotions and gates nothing

The portal has Releases (`src/Platform.Api/Features/Releases`): one run for a version line
`major.minor`, a checklist the release manager drives over facts the portal already records. Its
scope is every service of its products present on staging or production, minus the services the
release manager left out (`Release.ExcludedServicesJson`, entries `product/service`). Its checks are
computed when read (`ReleaseProgressService.cs`), a deploy belongs to it when its version is on the
line (`ReleaseProgressService.OnLine`), and there is at most one non-cancelled release per version
line and product (`ReleaseService.EnsureUniqueAsync`). Every release is set up from scratch: the same
products, environments, exclusions and steps typed in again each cycle.

Two things were missing. Teams that ship on a cadence want the setup saved. And once extension
services move back into product `mkp` as a service group (stage 2), "the services of these products"
is no longer enough to say what ships together: the core services and the extensions are one product
but two cadences.

We decided to add the **Release Train**: saved release settings — name, cadence, scope, environments,
standing exclusions and checklist steps. A **Release** stays what it is, one run, and is the only one
of the two that has dates, progress and a record. The plan is `docs/plans/release-trains.md` (stage 2).

## Train and Release

- A train always names its products. Its **scope** is either *the services of the listed products
  minus standing exclusions*, or *services of those products selected by list, service group or name
  pattern with `*`*; standing exclusions take the same three forms. With the first form a new
  service of a product joins the next release without anyone adding it, as today.
- A release **created from a train copies its settings**. Editing the train afterwards changes the
  next release, not the open ones. The copy includes the scope: a release stores its own products and
  exclusions, or its own service selection (list, group or pattern), so it never has to go back to
  the train. For a product-scoped train that is today's shape — today's scope rule and today's three
  fields (version line, products, date) — and choosing a train fills them in.
- An **ad hoc release** has no train: hand-picked services and a time window, with the version line
  optional. It covers a one-off coordinated change that no cadence owns.
- **Releases may overlap**, and a service may be in several open releases. A deploy is attributed to
  a release by its version line, as today. A **hotfix** stays part of its release's line; there is no
  separate hotfix release type.
- When a service **outside** the release deploys a version on the release's line, the release page
  warns. Nothing is blocked and nothing is added to the scope silently.
- **Completing** a release records what shipped: the version of each service, the services that did
  not ship (partial completion is allowed), and the union of its promotions' change sets. The live
  computed state stays next to the record, so a late deploy report still shows up; the record is what
  release notes and analytics read (`docs/plans/release-notes-for-a-release.md`, stage 3). A release completed
  before the record existed gets one written on request, from the state at its completion.

## A Release gates nothing

A Release groups promotions, shows progress and records the outcome. It **never holds a promotion**:
not on a checklist step, not on another service of the release, not on the release's status. The
portal does not gate promotions on catalog readiness either (ADR-0005). What holds a promotion stays
exactly two things, the Approval Gate and a Promotion Freeze.

## The unit of approval is one service's promotion

One approves the promotion of **one version of one service**. An Approval Policy is set for a product
or for **selected services of a product**: an exact name, a service group, or a name pattern with
`*`. Resolution order is exact name, group, pattern, product; two rules of the same level that match
the same service are refused when saved, so a saved set of rules has no tie to break. The one tie
saving cannot see is a service registered later whose name two stored patterns match: the older
policy applies, the match is flagged as ambiguous until one rule is changed, and the promotion is
not rejected.

**A train never selects approvers.** A service's approvers are the same whichever release it ships
in, and whether it ships in one at all. Release managers keep being read from the production gate of
the release's products (`ReleaseService.IsReleaseManagerOfAsync`); they may manage trains.

Today a policy is looked up in `PromotionPolicyResolver` and separately in five other places
(`Features/Builds/BuildIngestHook.cs`, the build-targets endpoint, `Mcp/BuildTools.cs`, the
release-manager gates in `ReleaseService.cs`, `PromotionFreezeService.cs`); the plan found two more
readers of policy rows, the policy merge in `Features/Settings/EnvironmentMergeService.cs` and the
duplicate pre-check in `PromotionAdminEndpoints.cs`. Selected-service policies
come with **one shared resolution point** that all of them use.

## Considered options

- **Compose a release from an explicit include-list only.** Rejected as the only form: today a new
  service of a product appears in the release uninvited, and release managers rely on it to notice
  work they did not know about. With an include-list such a service would ship outside every release
  and nobody would see it. The list form exists, next to the product form.
- **A service may be in only one open release.** Rejected: planning the next line starts while the
  current one is still open, and the rule would block it. Attribution by version line already keeps
  two open releases of the same service apart.
- **Pin each service's version when the release is cut.** Rejected: builds keep arriving on the line
  until it ships — fixes found on staging, hotfixes after. A pinned version would be stale the same
  day. The versions are recorded once, at completion.
- **An approval policy on the train.** Rejected: it adds a second answer to "who approves this
  promotion", which would depend on which release the service happens to be in, and a service in two
  open releases would have two. Production also shows no need: core and extension services have the
  same approvers on all four edges, and one service has a policy of its own.
- **A separate hotfix release type.** Rejected: a hotfix is a later build on the same line, which the
  line rule already attributes correctly.
- **Let a release hold promotions until its steps are done.** Rejected: steps are progressed by a
  person and inform a decision; turning them into gates would create a second approval system beside
  the Approval Gate.

## Consequences

- New stored data: trains, and on a release its train and its record of what shipped. The releases
  list filters by train and product; promotions, Mass approve and the work-items queue filter by
  release. In Mass approve the release is chosen inside the dialog and never inherited from the page.
- Uniqueness of one release per (version line, product) no longer says enough once two trains share a
  product; the rule that replaces it is in the plan.
- The governance rules have no release-train concept, and their "grouped promotion" is something
  else. The portal does not use that term for a Release.
- `Release.cs` describes a Release as "a versioned release train". From here on **train** means the
  saved settings only; the comment and any UI text using it for one run are corrected with the code.
- Stage 2 ships behind its own feature flag, `features.releaseTrains`, checked on the server for
  everything it adds. Today the Promotions flag hides Releases in the UI only, and today's calls to
  `/api/releases` stay without a server-side check (an open point of the plan).
- A change of service group in the catalog can change a service's policy or train. It is executed by
  an admin after a preview and audited, like a product change (ADR-0006).
- A Promotion Freeze can be scoped to a train (ADR-0008).
- `GLOSSARY.md` gets **Release** and **Release Train** with this ADR, **Service**, **Product** and
  **Service Group** with ADR-0005, and the Promotion Freeze relationships are extended for ADR-0008.
