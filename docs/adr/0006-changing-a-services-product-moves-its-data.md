---
status: accepted
date: 2026-11-22
---

# Changing a service's product moves its data; every event remembers the product at the time

With ADR-0005 a service's product comes from the catalog, so a product can change without anyone
touching the portal: a service is transferred, or a product is folded into another as a service
group. The portal has no service table to update. The product is a string copied onto about sixteen
tables, and everything that reads them filters by it. We had to decide what happens to the rows
already stored under the old product.

We decided that **a product change moves the service's data to the new product**, as today's override
and remap already do (`src/Platform.Api/Features/Deployments/ServiceProductOverrideService.cs`,
`ServiceProductRemapService.cs`), and that **every event additionally stores the product at the time
of the event** in a column a move never rewrites. The plan is
`docs/plans/service-list-and-catalog-product.md` (stage 1).

## The move

- A product change in the catalog only marks the service **to be moved**. An admin **executes** the
  move on Settings → Services after a preview of what it touches: row counts, open promotions,
  approval policies, webhook subscriptions and API keys that name the old product. The admin does not
  approve the catalog's decision — the catalog already made it — only chooses the moment.
- The move is **held while a promotion of that service is Approved or Deploying**. The
  `promotion.approved` webhook has already gone out and an external deployer is acting on it; the
  deploy event that completes the candidate must find it where it was approved. Pending candidates
  move, and their policy snapshots (`src/Platform.Api/Features/Promotions/Models/ResolvedPolicySnapshot.cs`)
  are refreshed from the new product's policies afterwards. Today's remap has no such hold; it only
  counts open promotions in the preview.
- The move is also **held while a rollback request that names the service is open**: Pending,
  Approved or Rolling back, with the service's own item not finished
  (`src/Platform.Api/Features/Rollbacks/Models/RollbackRequest.cs`, `RollbackItem.cs`). A rollback
  request stays under its product (below), and the deploy event that completes its item must find
  it there. The move waits until the rollback finishes or is rejected or cancelled.
- Today's remap moves deploy events and their work items, builds, promotion candidates and their
  work items, work-item sign-offs and comments, and deleted-service tombstones. The move must
  **additionally cover five kinds of service data it skips today**: retired versions, unfinished
  migration runs (pending, running or failed), incidents, service-level Promotion Freezes and release exclusions. A test fails when
  a new table carries a product and a service and the move does not know it.
- **Product-level configuration stays**: approval policies, rollback policies, product freezes. They
  belong to the product, and the new product's are the ones that apply from now on. Rollback requests
  stay too, as today, because one request spans several services.
- A read with the old product in the path **redirects** to the new one when the (product, service)
  pair is unambiguous. Links in tickets and chat keep working.

## The product at the time

Each deploy event keeps the product it was stored under when it happened, in its own column
(`src/Platform.Api/Features/Deployments/Models/DeployEvent.cs`; the plan gives promotion candidates
and incidents the same column, `ProductAtEvent`, and leaves builds without it). The move rewrites `Product` and never this column. Nothing in the delivery flow reads
it; it exists for reporting.

This is how the decision satisfies the rule the catalog and the cost pipeline share, **a transfer
does not rewrite past periods**. The live address of a service moves so that the service keeps
working as one service; what a past month looked like stays answerable, because analytics can group
by the product at the time (`docs/plans/release-notes-for-a-release.md`, stage 3).

## Considered options

- **History stays under the old product; reads go by service name.** The clean model on paper: the
  service is the identity (ADR-0005), so nothing needs to move. Rejected, because the code has no
  place to implement it:
  - Filters on product equality sit in about 178 places across about 30 files, with no shared read
    path. Each would have to learn "this product, or wherever the service used to be". The single
    write choke point, `ServiceProductOverrideService.ResolveProductAsync` (nine call sites in seven files), is what
    makes the move cheap and this alternative expensive.
  - Leaving rows behind breaks the delivery flow, not only reports. A Pending or Approved candidate
    under the old product never completes, because its deploy event arrives under the new one.
    Work-item sign-offs are keyed by (work item, product, service, target environment) and stop
    counting towards the gate. Rollbacks see no earlier version under the new product and offer
    **Remove** after a failed first deploy.
- **Move automatically when the catalog changes.** Rejected: a move changes which approval policy,
  webhook subscriptions and key scopes apply to the service. Someone has to see the preview, and a
  move in the middle of a deployment is exactly the case the hold exists for.
- **Let the admin accept or reject the product change.** Rejected: that makes the portal a second
  owner of the product (ADR-0005). A wrong product is fixed in the catalog.
- **Move everything, including policies.** Rejected, as in today's remap: policies are configuration
  an admin set for a product, and copying them would silently grant the old product's approvers
  rights in the new one.

## Consequences

- One move code path for an admin's override and a catalog change; the existing override and remap
  endpoints stay as aliases.
- After a move the service is governed by the new product's approval policies. The promotion page
  says where a candidate's policy came from and whether it changed after a move.
- Webhook subscriptions and API keys filtered to the old product stop matching the service. The
  preview lists them; the portal does not rewrite them.
- A service with a promotion stuck in Approved or Deploying, or with an open rollback request, cannot
  be moved until that promotion completes or is cancelled, or the rollback finishes or is rejected.
  The preview says which promotion or rollback request holds the move.
- One more column on deploy events, filled on every write. How rows stored before the column existed
  are filled is in the plan.
