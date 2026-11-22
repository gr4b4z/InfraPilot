---
status: accepted
date: 2026-11-22
---

# A freeze may be scoped to a release train

ADR-0002 keyed the Promotion Freeze by (product, target environment) and ADR-0003 added an optional
service: a freeze applies to exactly one product, the whole of it or one of its services
(`src/Platform.Api/Features/Promotions/Models/PromotionFreeze.cs`, `PromotionFreezeService.cs`). That
fitted while the things that ship together were a product. With release trains (ADR-0007) they are
not: a train may cover part of a product — the extensions group of `mkp` but not its core services —
or services of several products. Freezing "what ships with this train" today means one freeze per
product, which holds too much, or one per service, which is dozens of switches and misses the
service that joins tomorrow.

We therefore add a third scope. A Promotion Freeze is set on **a product, a release train or a
service**, always into one target Environment. The plan is `docs/plans/release-trains.md` (stage 2).

## How this amends ADR-0002 and ADR-0003

- "A freeze applies to exactly one product" no longer holds for the train scope: a train freeze
  **spans products** when the train does. The product and service scopes are unchanged, and there is
  still no whole-environment freeze.
- What a freeze does is unchanged (ADR-0001): it holds the Approval Gate of Pending candidates,
  approvals and sign-offs are still recorded, lifting re-evaluates what it covered, the Administrator
  Bypass ignores it, candidates already Approved are not recalled.
- A train freeze covers the services in the train's scope **at the moment the gate is evaluated**,
  not a list copied when the freeze was set, so a service that joins the train is held too.
- Matching stays the **union** of ADR-0003, now over three shapes: a candidate is held when a product
  freeze, a train freeze or a service freeze covers it. None overrides another, and lifting one never
  touches the others.

## A product freeze still holds everything

A product freeze holds **all** services of the product, whatever trains they are in. Considered and
rejected: an **implicit exception**, where a product freeze would leave out services that belong to
a train (on the reasoning that the train has its own freeze). A product freeze is the emergency
brake. The person pulling it must not have to know which trains exist to predict what stops, and a
service must not escape it by being added to a train. This is the same reasoning by which ADR-0003
rejected precedence between the product and service shapes: a freeze never unfreezes anything.

## Who may freeze

ADR-0002 made freezing administrator-only "for now" and named the missing piece: a release-manager
role, which "the freeze permission is the first thing it should carry". That role exists since
Releases (`ReleaseService.IsReleaseManagerOfAsync`: whoever can sign the release-manager requirement
of a product's production gate). **Release managers may set and lift freezes, as well as
administrators.** The governance rules assign the freeze decision to the product owner and the
production release manager, not to the platform administrators; with admin-only freezes the people
who decide had to ask someone else to flip the switch. Set and lift stay symmetric (ADR-0001).

## The banner says which freeze holds

With three scopes "Frozen" is not enough to act on: the person looking at a held promotion needs to
know whom to ask. The banner on the promotions list and the promotion page, and the gate's reason,
**name the freeze that holds the candidate** — product, train or service, with its reason, who set it
and since when. When several apply, all are listed, the widest first (product, then train, then
service), following ADR-0003: the widest is the one that would still hold the candidate after the
others are lifted.

## Considered options

- **Keep two scopes; freeze a train by freezing each of its services.** Rejected: many rows to set
  and lift together, and a service that joins the train during the window is not held.
- **Freeze a Release instead of a train.** Rejected: a Release gates nothing (ADR-0007), releases
  overlap, and a service in two open releases would be frozen or not depending on which one is asked.
- **A train freeze as an exception inside a product freeze.** Rejected above.
- **Keep freezing administrator-only.** Rejected: the role ADR-0002 waited for exists, and the
  governance rules name the release manager as the one who decides.

## Consequences

- The freeze record gains a train scope; the `""` sentinel rule of ADR-0003 applies to whatever
  column carries it. Each change ships a Postgres and a SQL Server migration.
- Gate evaluation, auto-approve at creation, the approval progress panel and the bypass audit flag
  look up the freeze by product, train membership and service.
- Lifting a train freeze re-evaluates the Pending candidates of the train's services into that
  environment; a candidate still covered by a product or service freeze stays held.
- `promotion.frozen` / `promotion.unfrozen` say which scope was frozen. Webhook subscriptions must
  keep a product filter under the governance rules; how a train freeze that spans products reaches
  them is in the plan.
- The freeze page gets the train in its scope select and moves from Settings → Freeze to
  `/promotions/freeze`, because everything under `/settings` is admin-only and release managers must
  reach it; `/settings/freeze` redirects and Settings keeps a **Freeze** entry that links there. A
  release manager may set or lift a product or service freeze for a product they are release manager
  of, and a train freeze only when they are release manager of **every** product the train covers.
  The rule that decides who runs a release (`ReleaseService.CanManageAsync`) is satisfied by any one
  of the release's products; that is enough for a Release, which holds nothing, and not enough for a
  freeze, which holds another product's promotions. The freeze check therefore asks per product.
  Admins may always, and set and lift stay symmetric.
- A service-level freeze follows its service when the service's product changes (ADR-0006).
- `GLOSSARY.md`: the Promotion Freeze entry and its relationships name the train scope.
