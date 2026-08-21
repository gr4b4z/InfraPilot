# Promotions API

How versions move between environments under an approval gate. A **promotion candidate**
represents "service X version V should move from source env → target env," and carries the
authoritative set of changes (work items, PRs, commits) being promoted. Candidates are
**created by an external system** (typically the pipeline that computed the env-to-env diff) —
the platform does not auto-generate them from deploy events.

Lifecycle: `Pending → Approved → Deploying → Deployed`, with `Rejected` / `Superseded` as
terminal off-ramps. `Deployed` is the one state reality can force: when a succeeded deploy of the
candidate's exact version lands on its target environment, ingestion closes the candidate as
`Deployed` whatever state it was in — including `Pending` (nobody approved it; the version shipped
out-of-band) and `Rejected` (somebody said no and it shipped anyway). `Superseded` is excluded:
a newer candidate owns that edge and closes instead.

Every action on a promotion leaves a system entry on its comment thread — created, change set
refreshed, approved, rejected, bypassed, superseded, dispatched, deployed, participant assigned,
policy re-applied. System entries are immutable (nobody edits or deletes them, admin included), so
the thread is a reliable history of what happened to the promotion. The design rationale lives in
[`docs/plans/external-promotion-creation.md`](../docs/plans/external-promotion-creation.md).

## Auth model

| Route group | Auth | Used by |
|---|---|---|
| `POST /api/promotions` (create) | **API key** (`X-Api-Key`) + per-key rate limit + product scope | CI / external systems |
| Other `/api/promotions/*` (read, approve, reject, comments, participants) | **User** (`CanApprove` policy) | The web UI / approvers |
| `/api/promotions/admin/*` (policies) | **User** (`CatalogAdmin` policy) | Admins |

> Note: `POST /api/promotions` overrides the group's user-auth with API-key auth, mirroring
> `POST /api/deployments/events`. The product scope is enforced from the key's `allowed_product`
> claims (a key restricted to certain products gets `403` for others).

---

## Create a promotion — `POST /api/promotions`

The external system computes the **net change set** (the diff between the target env's current
SHA and the version being promoted) and posts it. The platform stores it verbatim and opens a
candidate; it does **not** recompute or infer the bundle.

**Request body** (`CreatePromotionDto`):

```jsonc
{
  "product":      "checkout",          // required
  "service":      "checkout-api",      // required
  "sourceEnv":    "staging",           // required; must match a succeeded deployment (else 422, see below)
  "targetEnv":    "production",        // required
  "version":      "1.3.0",             // required
  "fromRevision": "a1b2c3d",           // optional — target env's current SHA (display/traceability)
  "toRevision":   "f9e8d7c",           // optional — SHA being promoted (display/traceability)
  "references": [                       // optional — the authoritative net change set
    { "type": "work-item",    "provider": "jira",   "key": "CHK-451",
      "title": "Add express checkout",                           // the Jira ticket's own summary
      "url": "https://jira/CHK-451",
      "commits": ["f9e8d7c", "b41c0aa"],                         // the commit(s) that mentioned the ticket
      "content": "One-tap checkout for saved cards.\n\nOut of scope: guest checkout." },
    { "type": "commit",       "provider": "github", "key": "f9e8d7c", "revision": "f9e8d7c",
      "title": "Add one-tap express checkout for saved cards", "url": "https://github.com/o/r/commit/f9e8d7c" },
    { "type": "pull-request", "provider": "github", "key": "2087", "url": "https://github.com/o/r/pull/2087" },
    { "type": "repository",   "provider": "github", "revision": "f9e8d7c", "url": "https://github.com/o/r" }
  ],
  "participants": [                     // optional — promotion-level people (role/displayName/email)
    { "role": "release-manager", "displayName": "Dana Lee", "email": "dana@example.com" }
  ]
}
```

- A `reference` is `{ type, url?, provider?, key?, revision?, title?, subTitle?, content?,
  participants?, commits?, resolution?, occurredAt? }`. Only `type == "work-item"` references feed the approval gate (they
  become the candidate's work items); `pull-request` / `repository` etc. are stored for display
  and traceability. `occurredAt` has the same per-type meaning as on deploy ingest
  (`notes/deployment-ingest-api.md`) and is resolved into the work item's `CommittedAt` for
  lead-time analytics.
- Work-item references may carry their own `participants[]` (a ticket's QA, a PR's reviewer);
  these surface on the candidate.
- `content` is the reference's body copied from the source system (Jira description, PR
  description, commit message body) — `title` is the summary line, `content` is the prose under
  it. On a work item it becomes the **Content** section of the detail page, between People and
  Sign-off, and is omitted entirely when absent. Shown as plain text; markup is not interpreted.
- On a work-item reference, `title` should carry the **tracker's own summary** (the Jira ticket
  title). A ticket routinely rides several commits, so no single commit subject can name it —
  InfraPortal shows the messages of *all* the commits listed in `commits` as a second line under
  the title, resolved from the `commit` references in the same payload. Send those commit
  references (`type: "commit"`, `key` = hash, `title` = subject) or the second line has nothing to
  show. `subTitle` exists for producers that put a commit subject on `title` instead: whenever it
  is present it is read as the item's real name and displayed as the title. Never put commit
  messages in `subTitle`.
- A work-item reference may carry `resolution` — what its tracker says about the item:
  ```jsonc
  "resolution": {
    "resolved": true,                 // the tracker reports it finished
    "status":   "Done",               // the tracker's own word, display only
    "at":       "2026-08-14T09:12:00Z",
    "by":       { "displayName": "Farkas, Dariusz", "email": "dariusz.farkas@example.com" }
  }
  ```
  `resolved: true` makes InfraPortal note on the work item's thread that its tracker already
  considers it finished, naming the status, the date, and whoever performed the closing transition —
  so a reviewer can see where the ticket stands without opening Jira. It is **not** a sign-off: a
  closed ticket says the work is done, not that this release is fit to ship, which is the question
  the gate asks, so the work item stays pending and a human still signs it off. Only `true` is
  meaningful (`false` and an absent `resolution` are both "still open"), any decision already
  recorded on the item is left untouched, and notes are deduplicated by content so a producer
  re-posting the same change set does not repeat them.

  Report closed tickets rather than dropping them: a producer that omits them makes a release whose
  tickets are all finished look like it shipped nothing.
- Every work-item reference should declare `commits` — the hashes of the commits whose messages
  mentioned it — alongside a `commit` reference per hash: that is what lets the work-item detail
  page link the ticket to the actual change. `fromRevision`/`toRevision` plus a `repository`
  reference let the promotion page link to the provider's commit-diff view for the whole
  candidate ("what exactly is being promoted").

**Responses**

| Status | Meaning | Body |
|---|---|---|
| `201 Created` | Candidate created (or an existing one for the same edge+version reused/updated) | `{ "id": "<guid>", "status": "Pending" \| "Approved" }` |
| `422 Unprocessable Entity` | **No promotion policy** exists for the `(product, service, sourceEnv, targetEnv)` edge — the product isn't enrolled for this edge. | `{ "error": "..." }` |
| `422 Unprocessable Entity` | **Unknown source** — no *succeeded* deployment of `version` exists in `sourceEnv` for `product`/`service`. You can only promote something that actually shipped to the source env. | `{ "error": "..." }` |
| `422 Unprocessable Entity` | **Target already at version** — the target env's *current* version is already `version` (via a prior promotion, a rollback, or an out-of-band deploy). Nothing to promote. Compared against the target's latest succeeded deploy, so rollback-then-re-promote is still allowed. | `{ "error": "..." }` |
| `400 Bad Request` | Missing required fields | `{ "errors": [ ... ] }` |
| `403 Forbidden` | API key not scoped to `product` | — |

**Idempotency / supersede**: identity is the natural key `(product, service, sourceEnv,
targetEnv, version)`. Re-posting the same version updates the existing non-terminal candidate
(references/revisions) rather than duplicating. Posting a *newer* version on the same edge marks
the prior still-`Pending` candidate `Superseded` — a pure state flip; the new candidate is
self-contained (no inheritance).

**Completion**: a candidate auto-closes to `Deployed` when a `succeeded` deploy event lands its
version on the target environment (see `notes/deployment-ingest-api.md`).

---

## Read

### `GET /api/promotions` — list
Query params (all optional): `status`, `product`, `service`, `targetEnv`, `reference`.
Returns `{ "candidates": [ ... ] }`. Each candidate includes a **`canApprove`** boolean for the
current user (Pending + authorized for ≥1 open requirement + not already decided).

### `GET /api/promotions/{id}` — detail
Returns `{ candidate, approvals, sourceEvent, comments, eligibleRequirements, approvalProgress }`.

- **`eligibleRequirements`** — `[{ stepName, requirementName }]`: the open requirements the
  current user may approve (drives the "Approve as…" selector).
- **`approvalProgress`** — the live gate state, mirroring the evaluator:

```jsonc
{
  "requiresApproval": true,
  "allSatisfied": false,
  "totalRequired": 2,
  "totalApproved": 1,
  "steps": [
    { "name": "Release Approval", "satisfied": false,
      "requirements": [
        { "name": "Release managers", "required": 2, "approved": 1, "satisfied": false,
          "groups": [ { "id": "<group-id>", "name": "Release Managers" } ], "users": [] }
      ] }
  ],
  "workItems": {                 // null unless the policy gates on work items
    "required": true, "total": 3, "approved": 2, "satisfied": false,
    "autoApprove": false          // true ⇒ resolving all work items auto-approves the promotion
  }
}
```

### `GET /api/promotions/audit` — activity feed
Every recorded action on a promotion, newest first. This is what the **Promotions audit** page
(`/promotions/audit` in the web UI) is built on, and it answers the questions that arrive as
questions: what was approved today, what was created today, what went to prod last week and who
signed it off.

Auth is the group's `CanApprove` policy, not the admin-only `AuditViewer` that guards
`GET /api/audit` — every row is already visible one promotion at a time on the detail pages, and
"who approved this" is a question approvers ask about their own work. The row's `sourceIp` is
deliberately **not** returned, unlike on `/api/audit`.

Query params (all optional):

| Param | Meaning |
|---|---|
| `from`, `to` | Absolute instants bounding the window. A *calendar* day is the caller's, not the server's, so the UI resolves its own midnight and sends `from`. |
| `days` | Convenience for a URL written by hand: `?days=7`. Ignored when `from` is given. |
| `category` | Comma-separated kinds of action: `approved`, `approval-step`, `rejected`, `cancelled`, `created`, `updated`, `deployed`, `work-item`, `comment`, `people`, `other`. A category is just a named set of actions (`PromotionAuditCategories`); `other` resolves against the actions actually present, so it holds anything the map hasn't been taught. |
| `action` | Comma-separated raw action names, e.g. `promotion.bypassed`. Unioned with `category`. |
| `actor` | Exact actor id, or a case-insensitive fragment of an actor name. |
| `product`, `service`, `targetEnv` | Scope to a promotion. `service` is a substring match, as on the list. |
| `page`, `pageSize` | `pageSize` defaults to 50, capped at 200. |

```jsonc
{
  "entries": [
    { "id": "…", "timestamp": "2026-08-19T09:14:02Z", "correlationId": "…",
      "action": "promotion.approved", "category": "approved",
      "actorId": "…", "actorName": "System (gate satisfied)", "actorType": "system",
      "candidateId": "…", "product": "acme", "service": "api",
      "sourceEnv": "staging", "targetEnv": "prod", "version": "v1.4.0",
      "candidateStatus": "Deployed",        // status NOW, not at the time of the action
      "comment": null, "reason": null, "workItemKey": null,
      "role": null, "referenceKey": null, "trigger": "gate-evaluator",
      "approvedBy": [ { "id": "…", "name": "Maja Nowak" } ],
      "details": { "trigger": "gate-evaluator" } }
  ],
  "total": 137,                             // rows matching every filter, not this page
  "page": 1, "pageSize": 50,
  "range": { "from": "2026-08-12T09:00:00Z", "to": null },
  "actions": [ { "action": "promotion.approved", "category": "approved", "count": 12 } ],
  "actors":  [ { "id": "…", "name": "Maja Nowak", "type": "user", "count": 31 } ]
}
```

- **`approvedBy`** — on a gate-opening row, the people whose approvals opened it. The row itself is
  written by the evaluator with the system as its actor, so this is where "who approved it" lives.
  Read from the trail (the sibling `promotion.approval.recorded` rows sharing the correlation id),
  not from the candidate's current approval rows: cancelling an approval deletes those, and a
  historical line has to keep saying what happened. `null` for an auto-approval — nobody decided it.
- **`actions`** / **`actors`** are facet counts, computed under every filter **except** the one they
  feed. That is what lets the page's tab badges say what selecting them would show. `total` is
  derived from the same counts, so a badge and its own list can never disagree.
- The feed is **candidate-anchored**: rows are inner-joined to a promotion the caller may see, which
  is what applies hidden products and retired services here. Two consequences — the module's audit
  rows that hang off other entities (`work-item.approved`/`work-item.blocked`, which duplicate a
  sign-off already recorded against the candidate as `promotion.ticket.*`, and `work-item.comment.*`)
  are not in the feed, and neither is a sign-off recorded with no live candidate.

---

## Act

### `POST /api/promotions/{id}/approve`
Body: `{ "comment"?: string, "stepName"?: string, "requirementName"?: string }`.

- When the user is eligible for exactly **one** open requirement, the choice is auto-picked.
- When eligible for **more than one** and none is specified → `400` with the options:
  `{ "error": "...", "eligibleRequirements": [ { "stepName", "requirementName" } ] }`.
- `403` if not eligible for the named requirement; `409` if that requirement is already satisfied.
- On success returns the updated candidate.

The approval is recorded with its `(stepName, requirementName)` attribution and the gate
evaluator honors it (each approver counts toward at most one requirement — global
distinct-person rule).

### `POST /api/promotions/{id}/reject`
Body: `{ "comment"?: string }`. One rejection from an authorized approver terminates the candidate.

### `POST /api/promotions/bulk/approve`
Body: `{ "ids": ["<guid>", ...], "comment"?: string }`. Per-id outcome:
`{ "results": [ { "id", "ok": true, "status" } | { "id", "ok": false, "error" } ] }`.

### Other
- `GET /api/promotions/{id}/comments`, `POST /api/promotions/{id}/comments`,
  `PATCH /api/promotions/comments/{commentId}`, `DELETE /api/promotions/comments/{commentId}`.
- `POST /api/promotions/{id}/participants`, `DELETE /api/promotions/{id}/participants/{role}`.
- `PATCH /api/promotions/{id}/references/{referenceKey}/participants` — assign / reassign / clear a
  person on one work-item reference. Body `{ "role", "assignee": { "email", "displayName" } | null }`.
- `GET /api/promotions/roles`, `GET /api/promotions/users/search?q=`,
  `GET /api/promotions/groups/search?q=` — directory-backed pickers (resolve against AD/Graph in
  MSAL mode; local users/static groups in dev). Note `roles` here reports the roles **observed in
  the data**, which is not the same as the roles you may assign — see below.

### Work-item sign-off — `/api/work-items/{key}`

Three decisions, each its own POST with body `{ product, targetEnv, comment? }`:

| Route | Stored decision | Event |
|---|---|---|
| `POST /{key}/approvals` | `Approved` | `promotion.ticket.approved` |
| `POST /{key}/issues` | `Issue` | `promotion.ticket.issue-raised` |
| `POST /{key}/blocks` | `Blocked` | `promotion.ticket.blocked` |

Only an approval releases the gate. An issue ("something's wrong") and a block ("not going out") are
mechanically identical — both leave the item unresolved, which stalls the gate without terminating
the candidate, and both are reversible; a new version of the promotion clears them and asks again.
Vetoing is candidate-level (`POST /api/promotions/{id}/reject`), never something done to one ticket.

**Renamed from Block/Reject.** These decisions were once named on a shift of one: today's issue was
`Blocked` (`POST /blocks`, `promotion.ticket.blocked`) and today's block was `Rejected`
(`POST /rejections`, `promotion.ticket.rejected`). The `RenameWorkItemDecisions` migration rewrote
stored values so the database is consistent. Two things did not move: `POST /rejections` is gone
rather than aliased (an alias would keep `/blocks` working while silently changing which decision it
records), and audit rows written before the rename keep their original action names.

### Participant roles

Two different role sets, easily confused:

- **The configured vocabulary** — `ui.app-settings` → `roles` (Settings → Participant Roles). The
  roles the platform knows about: what the pickers and the work-item role filter list, and the only
  ones a person can be *manually* assigned to. Naming someone on any other role returns `400`
  (`POST /{id}/participants` and `PATCH …/references/{key}/participants`). Clearing a slot
  (`assignee: null`) is always allowed, whatever its role — an ingested payload can put someone on a
  role nobody configured, and that has to stay removable.
- **The assignee-role set** — `promotions.assignee_roles`, default `["qa","reviewer","assignee"]`.
  Only defines what counts as "assigned to somebody" when the work-items queue is filtered by person
  with no role picked. Narrowing it does not narrow what an operator can filter on.

**Ingest is exempt from both.** A producer's payload is a record of what happened, so any role is
accepted and stored as sent. Roles that aren't in the configured vocabulary are reported back as
`unknownRoles` on the work-items queue and flagged as unrecognised in the UI.

---

## Approval policy (admin) — `/api/promotions/admin/policies`

A policy is **edge-scoped**, keyed by `(product, service?, sourceEnv, targetEnv)`. Resolution for a
candidate: the service-specific row wins, else the product-level row (`service: null`); both must
match the exact `sourceEnv → targetEnv` edge. **No row ⇒ the product is not enrolled for that edge**
(create returns `422`). (Rollbacks, being in-place within one env, resolve a policy by target only.)

`GET /policies`, `GET /policies/{id}`, `POST /policies`, `PUT /policies/{id}`,
`DELETE /policies/{id}`.

**Upsert body** (`UpsertPolicyRequest`):

```jsonc
{
  "product":   "checkout",
  "service":   null,                    // null/"" ⇒ product-level default
  "sourceEnv": "staging",               // required — the edge's source env
  "targetEnv": "production",
  "steps": [                            // ordered for display; evaluated in parallel (all must pass)
    {
      "name": "Release Approval",
      "requirements": [
        {
          "name": "Release managers",
          "groups": [ { "id": "<ad-group-object-id>", "name": "Release Managers" } ],
          "users": [ "lead@example.com" ],   // a requirement is satisfiable by a group member OR a listed user
          "minApprovers": 2                  // distinct approvers needed for this requirement (≥ 1)
        }
      ]
    }
  ],
  "escalationGroup": "SRE-OnCall",      // optional
  "requireAllWorkItemsApproved": false,        // block manual approval until every work item is signed off
  "autoApproveOnAllWorkItemsApproved": false,  // auto-promote once all work items are signed off
  "autoApproveWhenNoWorkItems": false          // auto-approve at create time when the payload has no work items
}
```

`GET` responses return the same `steps[]` shape plus `id`, `createdAt`, `updatedAt`.

### Evaluation rules
- **Within a requirement** → OR: a group member *or* a listed user qualifies.
- **Within a step / across steps** → AND: every requirement (in every step) must be satisfied.
- **Distinct people** (global): one human satisfies at most one requirement across the whole
  policy. The matcher assigns most-constrained-requirement-first to avoid false "not satisfied".
- **Group membership is evaluated live** (token claims, then Microsoft Graph) at fetch/approval
  time — never snapshotted — so added/removed approvers take effect immediately. The *policy* is
  snapshotted onto the candidate at creation, but *who is in a group* is always current-state.
- **Gating is expressed by two orthogonal things**: the human approver tree (`steps[]` — an empty
  tree means no human gate) and the work-item flags below.
- **Work-item gate**: when `requireAllWorkItemsApproved` is set and the candidate has work items,
  all must be approved before the promotion can proceed; `autoApproveOnAllWorkItemsApproved`
  promotes automatically once every work item is approved, regardless of the human approver tree.
  A work item counts as resolved only with at least one `Approved` decision and no `Rejected` or
  `Blocked` one.
- **Work-item decisions** are `Approved`, `Rejected`, or `Blocked`, one row per approver per
  `(key, product, targetEnv)`. `Rejected` is a veto — it terminates the candidate. `Blocked` is a
  reversible hold: the gate stays unmet but the candidate stays Pending, and the same approver can
  switch to `Approved` later to release it. Re-deciding updates the approver's existing row
  (stamping `updatedAt`) instead of appending a second one; recording the *same* decision twice is
  a 400.
- An empty step tree (no requirements) ⇒ auto-approve (no human gate).
