# 02: Settings → Freeze tab: active freezes and freeze form

Status: ready-for-agent
Blocked by: 01

**What to build:** A new **Freeze** tab in Settings (admin-only, like the rest of Settings) where an
administrator sees every active Promotion Freeze and can create or lift one without touching the
API. Layout per the winning prototype (variant C on branch `prototype/promotion-freeze-ui`,
Settings → Freeze): active freezes first, then a one-line form. The journal is a separate ticket.

**Active freezes** — one card per freeze: "{product} → {environment}", the reason as the main text,
"frozen by X · since <relative time>", a badge with the number of Pending candidates currently held,
and a "Lift freeze" button that needs a second click to confirm. Empty state: "No active freezes.
Promotions flow normally."

**Freeze a product** — product select (only products that have at least one approval policy),
environment select (only this product's target environments that are not already frozen), required
reason, "Freeze" disabled until all three are set. Hint under the form: approvals keep being
recorded while frozen; nothing is promoted until the freeze is lifted.

- [ ] "Freeze" appears in the Settings navigation and opens the tab; non-admins cannot reach it
- [ ] Each active freeze is shown with pair, reason, who, since when, and how many candidates it holds (the held count comes from the API)
- [ ] Lifting requires two clicks; the card disappears and held candidates are re-evaluated (ticket 01 behaviour)
- [ ] The product and environment selects only offer pairs that have an approval policy and are not already frozen
- [ ] Submitting the form creates the freeze and it appears in the active list; the reason is required
- [ ] Styling matches the existing Settings sections (border-first, 13px, CSS tokens)
