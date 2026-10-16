# InfraPilot

Infrastructure portal for self-service requests, approvals, deployments and promotions of
service versions between environments. This glossary covers the promotions context.

## Language

### Promotions

**Environment**:
A named deployment target (e.g. development, staging, production) that services ship to.
Admin-curated; identified by a stable key.
_Avoid_: Stage, env tier

**Promotion Candidate**:
A proposal that a specific version of a service moves from a source Environment to a target
Environment, carrying the authoritative change set being promoted.
_Avoid_: Promotion request, release

**Approval Policy**:
The rules deciding who must approve Promotion Candidates on one source → target edge for a
product or service, including any work-item conditions.
_Avoid_: Approver group, gate config

**Approval Gate**:
The evaluated state of a Promotion Candidate against its Approval Policy. The candidate becomes
Approved only when the gate is satisfied.
_Avoid_: Check, threshold

**Auto-approve**:
An Approval Gate that is satisfied without any human decision, because the policy has no human
requirements, the candidate ships no work items, or every work item has been signed off.
_Avoid_: Skip approval, no gate

**Work Item Sign-off**:
A decision by an eligible approver on a single work item (ticket) included in a Promotion
Candidate's change set.
_Avoid_: Ticket approval

**Administrator Bypass**:
An audited, reason-required action by an administrator that moves a Pending Promotion Candidate
to Approved without satisfying its Approval Gate.
_Avoid_: Force approve, override

**Promotion Freeze**:
A switchable state on one product's promotions into one target Environment, during which no
Promotion Candidate for that product and target can become Approved. Approvals and Work Item
Sign-offs are still recorded; Auto-approve waits. Lifting the freeze re-evaluates every Pending
candidate for that product and Environment.
_Avoid_: Environment freeze, lock, blackout, maintenance window, approval block

## Relationships

- A **Promotion Freeze** applies to exactly one product on exactly one **Environment**, in its role
  as promotion target. There is no whole-environment freeze.
- A **Promotion Freeze** holds the **Approval Gate** of every Pending **Promotion Candidate** for
  that product whose target is that **Environment**; it does not affect rollbacks or service requests.
- An **Administrator Bypass** ignores a **Promotion Freeze**.
- A **Promotion Candidate** already Approved before a **Promotion Freeze** begins is unaffected.
