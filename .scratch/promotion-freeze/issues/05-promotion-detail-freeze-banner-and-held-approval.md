# 05: Promotion detail: freeze banner and held approval

Status: ready-for-agent
Blocked by: 01

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
