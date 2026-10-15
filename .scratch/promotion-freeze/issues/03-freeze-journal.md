# 03: Freeze journal

Status: ready-for-agent
Blocked by: 02

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
