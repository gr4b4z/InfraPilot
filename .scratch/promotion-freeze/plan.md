# Promotion Freeze — implementation plan

Generated from the tickets in `.scratch/promotion-freeze/issues/` (one task per ticket, same numbering).
Spec authority: `GLOSSARY.md` (Promotion Freeze), `docs/adr/0001-environment-freeze-holds-the-approval-gate.md`,
`docs/adr/0002-freeze-is-scoped-to-product-and-environment.md`. UI layouts: branch `prototype/promotion-freeze-ui`.

## Global Constraints

- Freeze key is the pair (product, target environment). No whole-environment wildcard. Only CatalogAdmin (role InfraPortal.Admin) may freeze/unfreeze; symmetric.
- A freeze holds only the transition of a Promotion Candidate to Approved. Approvals and Work Item Sign-offs are still recorded. All three auto-approve paths wait. A candidate created under auto-approve during a freeze is created Pending (no promotion.approved webhook).
- Lifting a freeze re-evaluates every Pending candidate of that pair (ReevaluateAsync) and only that pair.
- Administrator Bypass ignores the freeze; its audit entry and webhook payload carry frozen=true when the pair was frozen.
- Rollbacks, service-request approvals and candidate creation are out of scope (not blocked).
- A pair without any approval policy cannot be frozen (422).
- Reason is required on freeze (400 without), 409 on duplicate active freeze, 403 for non-admin.
- Audit actions: promotion.frozen / promotion.unfrozen. Webhook event types: promotion.frozen / promotion.unfrozen with WebhookEventFilters(Product, Environment).
- EF migrations must exist for BOTH providers: Migrations/Postgres and Migrations/SqlServer.
- UI style: border-first, 13px body, 11px uppercase labels, CSS tokens only (--info / --info-bg for freeze cues), same wrapper classes as existing Settings sections.
- Follow vertical-slice conventions of the repo: Features/<Slice>, Minimal API endpoints, tests in tests/Platform.Api.Tests/Features/Promotions.
- TDD: write the failing test first for backend behaviour.
- Do not merge or cherry-pick the branch prototype/promotion-freeze-ui; read it with `git show prototype/promotion-freeze-ui:<path>` and rewrite properly.
- Each task updates notes/promotions-api.md for its own scope.

## Task 1: Hold the Approval Gate for a frozen (product, environment) pair

**What to build:** An administrator can freeze one product's promotions into one target
Environment, with a reason, and lift the freeze later. While the Promotion Freeze is active, no
Promotion Candidate for that pair becomes Approved: human approvals and Work Item Sign-offs are still
recorded, Auto-approve waits, and a candidate created under an auto-approve policy is created as
Pending instead of Approved. Lifting the freeze re-evaluates every Pending candidate of that pair and
promotes whatever already satisfies its gate. The Administrator Bypass still works and is audited as
having happened under a freeze. Everything is exercisable through the API alone (no UI yet).

Decisions: GLOSSARY.md (Promotion Freeze), ADR-0001 (freeze holds the gate, not the approve action),
ADR-0002 (key is product + target environment, administrators only, no whole-environment wildcard).

- [ ] Admin can create a freeze for a (product, target environment) pair; the reason is required (400 without it)
- [ ] Creating a freeze for a pair that is already frozen returns 409; for a pair with no approval policy returns 422
- [ ] Admin can list active freezes and lift one; a non-admin gets 403 on all three operations
- [ ] A Pending candidate of a frozen pair stays Pending even when every approver requirement is satisfied, and the gate reports the freeze as the blocking reason
- [ ] Approvals and work-item sign-offs on a frozen candidate are still recorded (no error, no state change)
- [ ] An externally created candidate for a frozen pair under an auto-approve policy is created as Pending, and no `promotion.approved` webhook fires
- [ ] Lifting a freeze re-evaluates Pending candidates of that pair only: a fully approved one flips to Approved (webhook fires), a candidate of another product on the same environment is untouched
- [ ] Administrator Bypass of a frozen candidate succeeds; its audit entry and webhook payload carry `frozen: true`
- [ ] Freeze and unfreeze each write an audit entry with actor and reason
- [ ] Migrations exist for both database providers
- [ ] `notes/promotions-api.md` documents the admin freeze endpoints and the gate behaviour

## Task 2: Settings → Freeze tab: active freezes and freeze form

Blocked by: Task 1

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

## Task 3: Freeze journal

Blocked by: Task 2

**What to build:** The Freeze tab shows a **Journal** of freeze and unfreeze events under the form,
newest first, so an administrator can see who froze or lifted what and why. Layout per variant C of
the prototype (branch `prototype/promotion-freeze-ui`): a vertical timeline with a coloured dot per
event (one colour for frozen, another for lifted), text "{actor} froze|lifted {product} → {env}",
the reason in quotes when present, relative time right-aligned. Shows the latest 10 with a footer
"Showing latest 10" when there are more.

- [ ] The API exposes freeze/unfreeze history for administrators (may be derived from the audit log)
- [ ] The Journal lists events newest first with actor, action, pair, reason and relative time
- [ ] Creating or lifting a freeze in the tab adds a journal entry without a page reload
- [ ] At most 10 entries are shown, with the "Showing latest 10" footer when truncated

## Task 4: Promotions list: freeze banner and Frozen group

Blocked by: Task 1

**What to build:** On the Promotions page every signed-in user can see which pending promotions are
held by a Promotion Freeze and why. Layout per the winning prototype (variant D on branch
`prototype/promotion-freeze-ui`, Promotions page): a banner on top plus a separate Frozen group below.

**Banner** — above the filters, shown only when at least one active freeze matches the current
product and target-environment filters: "N active freeze(s) affecting this list", then one line per
freeze with the pair, the reason, "frozen by X <relative time>" and "N promotions held". Administrators
also get a "Manage freezes" link to the Freeze tab.

**Frozen group** — Pending candidates of a frozen pair are taken out of the main pending list (in both
"All pending" and "Awaiting my approval") and shown in their own group "Frozen (N)" below the open
ones, in an info-tinted frame whose header repeats each pair with its reason, who and since when, and
the hint that approvals are still recorded. Cards inside keep their normal layout and actions, with a
small "Frozen" marker in the meta row.

- [ ] The list API marks each candidate as frozen or not, and any signed-in user can read active freezes with their held counts
- [ ] The banner appears only when a freeze matches the current product/environment filters, and lists pair, reason, who, since, held count
- [ ] "Manage freezes" is visible to administrators only and opens the Freeze tab
- [ ] Frozen pending candidates appear in the Frozen group, not in the main list, in both list tabs; the group is absent when nothing is frozen
- [ ] The group header shows each affected pair with reason and who/since exactly once
- [ ] Frozen cards remain clickable and keep Review/View; approving from the detail still works (recorded, not promoted)

## Task 5: Promotion detail: freeze banner and held approval

Blocked by: Task 1

**What to build:** When an approver opens a Pending promotion whose pair is frozen, the detail page
says so before they act: a banner above the approval card (same visual language as the list banner)
with the pair, the reason, who froze it and since when, and a sentence that their approval will be
recorded but the promotion will not proceed until the freeze is lifted. The approval progress area
names the freeze as the reason the gate is not satisfied.

- [ ] The detail API returns the active freeze (reason, who, since) for a frozen candidate, and none otherwise
- [ ] A frozen Pending candidate shows the banner above the approval card; an unfrozen one does not
- [ ] The approval progress shows the freeze as the blocking reason even when all requirements are satisfied
- [ ] Approve and Reject remain available; approving records the decision and the candidate stays Pending with the banner still shown
- [ ] After the freeze is lifted (ticket 01), reloading the detail shows the candidate Approved if its gate was already satisfied

## Task 6: Webhooks for freeze and unfreeze

Blocked by: Task 1

**What to build:** External systems (Slack, Teams, CI) can react to a Promotion Freeze starting or
ending. Two new webhook event types, `promotion.frozen` and `promotion.unfrozen`, are dispatched when
an administrator creates or lifts a freeze, filtered by both product and environment like the other
promotion events, with a payload carrying the pair, the reason (on freeze), the actor and the time.

- [ ] Creating a freeze dispatches `promotion.frozen` to subscriptions matching the product and environment filters
- [ ] Lifting a freeze dispatches `promotion.unfrozen` the same way
- [ ] A subscription filtered to another product or environment does not receive the event
- [ ] Webhook dispatch failure does not fail the freeze/unfreeze request (same non-fatal behaviour as existing promotion webhooks)
- [ ] `notes/promotions-api.md` and the webhook documentation list the two new event types and their payloads
