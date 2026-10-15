# 04: Promotions list: freeze banner and Frozen group

Status: ready-for-agent
Blocked by: 01

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
