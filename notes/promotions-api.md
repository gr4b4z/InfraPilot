# Promotions API

How versions move between environments under an approval gate. A **promotion candidate**
represents "service X version V should move from source env → target env," and carries the
authoritative set of changes (work items, PRs, commits) being promoted. Candidates are
**created by an external system** (typically the pipeline that computed the env-to-env diff) —
the platform does not auto-generate them from deploy events.

Lifecycle: `Pending → Approved → Deploying → Deployed`, with `Rejected` / `Superseded` as
terminal off-ramps. The design rationale lives in
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
  "sourceEnv":    "staging",           // required (recorded; not validated against a topology)
  "targetEnv":    "production",        // required
  "version":      "1.3.0",             // required
  "fromRevision": "a1b2c3d",           // optional — target env's current SHA (display/traceability)
  "toRevision":   "f9e8d7c",           // optional — SHA being promoted (display/traceability)
  "references": [                       // optional — the authoritative net change set
    { "type": "work-item",    "provider": "jira",   "key": "CHK-451",
      "title": "Add express checkout", "url": "https://jira/CHK-451" },
    { "type": "pull-request", "provider": "github", "key": "2087", "url": "https://github.com/o/r/pull/2087" },
    { "type": "repository",   "provider": "github", "revision": "f9e8d7c", "url": "https://github.com/o/r" }
  ],
  "participants": [                     // optional — promotion-level people (role/displayName/email)
    { "role": "release-manager", "displayName": "Dana Lee", "email": "dana@example.com" }
  ]
}
```

- A `reference` is `{ type, url?, provider?, key?, revision?, title?, participants? }`. Only
  `type == "work-item"` references feed the approval gate (they become the candidate's work
  items); `pull-request` / `repository` etc. are stored for display and traceability.
- Work-item references may carry their own `participants[]` (a ticket's QA, a PR's reviewer);
  these surface on the candidate.

**Responses**

| Status | Meaning | Body |
|---|---|---|
| `201 Created` | Candidate created (or an existing one for the same edge+version reused/updated) | `{ "id": "<guid>", "status": "Pending" \| "Approved" }` |
| `422 Unprocessable Entity` | **No promotion policy** exists for `(product, service, targetEnv)` — the product isn't enrolled for this edge. With no topology, this is the edge guard. | `{ "error": "..." }` |
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
- `GET /api/promotions/roles`, `GET /api/promotions/users/search?q=`,
  `GET /api/promotions/groups/search?q=` — directory-backed pickers (resolve against AD/Graph in
  MSAL mode; local users/static groups in dev).

---

## Approval policy (admin) — `/api/promotions/admin/policies`

A policy is keyed by `(product, service?, targetEnv)`. Resolution for a candidate: the
service-specific row wins, else the product-level row (`service: null`); **no row ⇒ the product
is not enrolled for that edge** (create returns `422`).

`GET /policies`, `GET /policies/{id}`, `POST /policies`, `PUT /policies/{id}`,
`DELETE /policies/{id}`.

**Upsert body** (`UpsertPolicyRequest`):

```jsonc
{
  "product":   "checkout",
  "service":   null,                    // null/"" ⇒ product-level default
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
  "gate": "PromotionOnly",              // "PromotionOnly" | "WorkItemsOnly" | "WorkItemsAndManual"
  "timeoutHours": 48,
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
- **Work-item gate**: when `requireAllWorkItemsApproved` (or a `WorkItems*` gate mode) is set and
  the candidate has work items, all must be approved; `autoApproveOnAllWorkItemsApproved` (or a
  `WorkItemsOnly` gate) promotes automatically once they are.
- An empty step tree (no requirements) ⇒ auto-approve (no human gate).
