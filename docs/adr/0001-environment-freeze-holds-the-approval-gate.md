---
status: accepted (scope amended by ADR-0002)
date: 2026-10-15
---

# Environment Freeze holds the approval gate, not the approve action

We need a way to temporarily stop promotions into a given environment (e.g. a release freeze on
production) that covers both human approvals and the three auto-approve paths. We decided that an
Environment Freeze is a per-environment on/off switch under which **no Promotion Candidate
targeting that environment can become Approved**: the gate evaluator treats a frozen target as
"not satisfied". Approvals and work-item sign-offs are still recorded while frozen, auto-approve
candidates are created as Pending instead of Approved, and lifting the freeze re-evaluates every
Pending candidate on that environment so whatever already has a full set of approvals flips to
Approved at that moment. The Administrator Bypass deliberately ignores the freeze.

## Considered options

- **Reject the approve call while frozen** (409, button disabled, nothing recorded). Rejected:
  it has to be enforced at every entry point that can lead to Approved (approve, bulk approve,
  work-item sign-off, external create with auto-approve) and still needs a separate
  re-evaluation for auto-approve policies when the freeze ends. Approvers also lose the ability to
  pre-approve during the window.
- **Block only auto-approve**. Rejected: contradicts the requirement that manual approvals are
  frozen too.
- **Block candidate creation as well**. Rejected: CI would have to handle a new error and retry,
  and candidates would not queue up for the end of the window.
- **Bypass also frozen**. Rejected: a hotfix during a freeze would need unfreeze → bypass →
  refreeze, leaving the environment open to everyone in between. Bypass already requires a reason
  and is audited; the audit entry additionally records that the environment was frozen.

## Consequences

- A single enforcement point (gate evaluation) plus one re-evaluation sweep on unfreeze.
- Candidates already Approved before the freeze are **not** held: the `promotion.approved`
  webhook has already fired and deployment is executed by external CI, so the platform cannot
  recall it.
- Unfreezing can release several candidates at once, so downstream systems may receive a burst
  of `promotion.approved` webhooks.
- The freeze applies to the environment in its role as **promotion target** only. Rollbacks and
  service-request approvals are out of scope by decision, not oversight.
- Freeze and unfreeze are symmetric: whoever may set one may lift one, regardless of who set it.
  Who that is, and the freeze key, are decided in ADR-0002.
