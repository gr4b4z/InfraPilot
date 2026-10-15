# 01: Hold the Approval Gate for a frozen (product, environment) pair

Status: ready-for-agent

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
- [ ] Admin can list active freezes and lift one; a non-admin gets 403 on create and lift
- [ ] The active-freezes read includes, per freeze, the number of Pending candidates it currently holds
- [ ] Any signed-in user (CanApprove) can read active freezes with their held counts through a non-admin endpoint; create and lift stay admin-only
- [ ] A Pending candidate of a frozen pair stays Pending even when every approver requirement is satisfied, and the gate reports the freeze as the blocking reason
- [ ] Approvals and work-item sign-offs on a frozen candidate are still recorded (no error, no state change)
- [ ] An externally created candidate for a frozen pair under an auto-approve policy is created as Pending, and no `promotion.approved` webhook fires
- [ ] Lifting a freeze re-evaluates Pending candidates of that pair only: a fully approved one flips to Approved (webhook fires), a candidate of another product on the same environment is untouched
- [ ] Administrator Bypass of a frozen candidate succeeds; its audit entry and webhook payload carry `frozen: true`
- [ ] Freeze and unfreeze each write an audit entry with actor and reason
- [ ] Migrations exist for both database providers
- [ ] `notes/promotions-api.md` documents the admin freeze endpoints and the gate behaviour
