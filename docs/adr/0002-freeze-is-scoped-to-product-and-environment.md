---
status: accepted
date: 2026-10-15
---

# A freeze is scoped to one product on one environment, managed by administrators

ADR-0001 keyed the freeze by environment alone. In practice a release freeze is declared for a
product (e.g. checkout → production) while other products keep shipping to the same environment,
so we re-keyed it to **(product, target environment)** and renamed the concept to Promotion
Freeze. There is deliberately no whole-environment wildcard: freezing all of production is one
switch per product, which is acceptable at the current number of products and avoids precedence
rules between two record shapes. Freezes are managed in their own Settings tab, **Freeze**,
rather than as a block inside Settings → Promotions: a freeze is an operational switch flipped
during a release window, not policy configuration, and mixing it into the policies table hid it.
Only (product, target environment) pairs that have at least one approval policy can be frozen; a
pair without a policy cannot receive promotions, so freezing it would do nothing.

The tab's layout was chosen from three clickable prototypes (branch `prototype/promotion-freeze-ui`):
a register table of all pairs, a product × environment matrix with a detail panel, and an
operational board. The board won: **active freezes first** (one card per freeze with reason, who,
since when, held candidates and a two-click "Lift freeze"), then a one-line **freeze form**
(product → environment → required reason), then a **journal** of freeze/unfreeze events. It matches
the real usage pattern (freeze for a window, lift afterwards) and keeps what is frozen right now
impossible to miss; the price is that the full list of freezable pairs is only visible through the
form's selects.

On the **Promotions list** a frozen candidate is shown two ways, also chosen from prototypes on the
same branch: a page-level **banner** above the filters listing every active freeze that affects the
visible list (pair, reason, who, since when, how many promotions it holds, link to the Freeze tab),
and a separate **Frozen group** below the open pending candidates, in an info-tinted frame whose
header repeats the pairs and reasons once; the cards inside keep their normal layout plus a small
"Frozen" marker and stay actionable (approvals are still recorded). Rejected: an inline badge on
every frozen card (reason repeated per card, frozen cards interleaved with actionable ones) and a
per-card ribbon (tall cards, redundant with the group header). The detail page gets the same banner
above its approval card.

Setting and lifting a freeze is **administrator-only for now**. The earlier intent to let
approvers freeze their own product needs a "release manager" role that does not exist yet, and
Settings is already admin-gated; granting approvers API rights without a UI would be dead
weight. When the role exists, the freeze permission is the first thing it should carry.

## Consequences

- Gate evaluation checks the candidate's (product, target environment) against active freezes.
- Freeze/unfreeze webhooks carry both product and environment filters.
- The promotions pages show the freeze banner only for candidates of a frozen product + target.
