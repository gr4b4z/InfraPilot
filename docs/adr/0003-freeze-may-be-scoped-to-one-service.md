---
status: accepted
date: 2026-10-22
---

# A freeze may be scoped to one service of the product

ADR-0002 keyed the Promotion Freeze by **(product, target environment)**: freezing a product
freezes every service it ships. In practice a freeze is often declared for one service — a hotfix
window on `checkout/checkout-api → production` while the product's other services keep promoting —
and freezing the whole product for it stops work that was never at risk. We therefore allow a
freeze to name an **optional service**: the key becomes **(product, service, target environment)**,
and a freeze without a service still covers the whole product exactly as before.

## Matching is a union, with no precedence

A Promotion Candidate `(product, service, targetEnv)` is held when **either** a whole-product freeze
`(product, ∅, targetEnv)` **or** a service freeze `(product, service, targetEnv)` is active. The two
shapes are independent rows that can coexist: freezing a service while the whole product is already
frozen is allowed (the service freeze simply outlives the product one if that is lifted first), and
lifting one never touches the other. There is no "more specific wins" rule and nothing to resolve —
a candidate is frozen or it is not. When both apply, the gate names the whole-product freeze, since
it is the one that would still hold the candidate after the service freeze is lifted.

Considered and rejected: **precedence** (a service freeze overriding or exempting a service from a
product freeze). An exemption shape would make "is this frozen?" depend on the order of two rows and
would let a service freeze *unfreeze* something, which is the opposite of what an operator reaching
for Freeze wants during a release window.

## The sentinel `""` instead of NULL

The whole-product shape stores the empty string in `Service`, not NULL. Postgres treats NULLs as
distinct in a unique index (two `(acme, NULL, prod)` rows would both be accepted), SQL Server treats
them as equal (one allowed); a filtered index or provider-specific DDL would be needed to get the
same behaviour from both. With `""` the one unique index `(Product, Service, TargetEnv)` behaves the
same everywhere, and the existing rows migrate with a column default. The API never exposes the
sentinel: responses, audit payloads and webhooks carry `service: null` for a whole-product freeze.

## Which scopes can be frozen

A service freeze needs an Approval Policy that covers it — a policy for that service, or a
product-level one (`Service = null`) that governs every service of the product — otherwise nothing
could ever be promoted on that edge and the freeze would hold nothing (same reasoning as ADR-0002).
It also needs the service to be **known**: named by at least one Promotion Candidate or deploy event
of the product. Without that check a typo would create a freeze that silently holds nothing while
the operator believes the service is frozen. Both failures are `422`.

## Consequences

- `promotion_freezes` gains `Service` (default `""`); the unique index is `(Product, Service, TargetEnv)`.
- Gate evaluation, auto-approve at creation, the approval progress panel, the bypass audit flag and
  the promotion detail all look up the freeze by `(product, service, targetEnv)` with the union rule.
- Lifting a service freeze re-evaluates only that service's Pending candidates; lifting a
  whole-product freeze re-evaluates the product's.
- `promotion.frozen` / `promotion.unfrozen` carry `service` (null for a whole product) and a service
  freeze also sets the webhook `service` filter, so subscriptions narrowed to other services do not
  receive it.
- The Freeze tab's form gets a service select defaulting to **All services**; cards, journal, banners
  and notices print `product/service → env` for a service freeze.
- Lifting is `DELETE /freezes/{product}/{targetEnv}` for the whole product and
  `DELETE /freezes/{product}/{targetEnv}/services/{service}` for one service; the product route never
  lifts a service freeze.
