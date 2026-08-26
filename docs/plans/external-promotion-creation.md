# Implementation Plan: Externally-Created Promotions

**Status:** Draft / for review
**Author:** (planning session, 2026-06-26)

---

## 1. Goal & motivation

Today the tool **auto-generates** promotion candidates on deploy-event ingest and
**infers** "what ships" by unioning the commit/work-item deltas of every superseded
predecessor on the same edge (`SupersededSourceEventIds`). That inference assumes
*forward deploy ⇒ superset of older changes*, which is **wrong when a commit is
reverted in the source repo and shipped as a new forward version**: the reverted
work item stays in the bundle, gets gated for approval, and is later marked
"promoted" — false "what shipped" data.

**New model:** an external task (CI, which already has SCM access and computes
env-to-env diffs) computes the **authoritative net change set** and calls a
**create-promotion API**. The tool stops inferring and becomes a *ledger + gate*:
it records exactly what it's told.

### Consequences of the decision (confirmed with user)
1. **The tool no longer auto-generates candidates.** Candidate creation becomes
   external/push-only. This deletes the inference machinery rather than fixing it.
2. **Each candidate is self-contained** — it carries its own change set, so
   *supersede collapses to a pure state transition* (no copying / inheritance).
3. **`fromRevision` / `toRevision` are kept for display & traceability** (not used
   for gating).

---

## 2. Why this fixes the revert bug

- Old: `bundle = SourceDeployEventId ∪ SupersededSourceEventIds` → union of historical
  DEV deltas → reverted commit B survives because nothing cancels it.
- New: the external sends the **net diff** between the target env's current SHA and the
  promoted SHA. Git has already resolved the revert, so B simply isn't in the payload.
  Nothing in the tool reconstructs it, so it can't reappear.

---

## 3. Current-state references (where today's logic lives)

| Concern | Location |
|---|---|
| Auto-generation on ingest | `PromotionIngestHook.GenerateCandidatesAsync` → `src/Platform.Api/Features/Promotions/PromotionIngestHook.cs:105` |
| Candidate creation + supersede/inherit | `PromotionService.CreateCandidateAsync` → `src/Platform.Api/Features/Promotions/PromotionService.cs:86` |
| Supersede + inheritance accumulation | `PromotionService.SupersedeStalePendingAsync` → `:296` |
| Rollback bundle computation | `PromotionService.ComputeRollbackBundleAsync` → `:255` |
| Candidate model (bundle stored as event-id list) | `PromotionCandidate.SupersededSourceEventIds` → `src/Platform.Api/Features/Promotions/Models/PromotionCandidate.cs:49` |
| Gate: has-tickets / all-approved / evaluate | `PromotionService` `:822`, `:836`, `:868` |
| Auto-approve-when-no-tickets ticket probe | `PromotionService.cs:152` |
| Webhook payload bundle | `PromotionService.DispatchWebhookAsync` → `:1023` |
| Work-item relational index (build→ticket) | `DeployEventWorkItem` (keyed by `DeployEventId`) → `src/Platform.Api/Features/Deployments/Models/DeployEventWorkItem.cs` |
| Index population from deploy events | `WorkItemSyncService.SyncAsync` → `src/Platform.Api/Features/Deployments/WorkItemSyncService.cs` |
| **Approval queue / assignment / lookup** (all join via bundle event-ids) | `WorkItemApprovalService` `:~455`, `:~735`, `:~845` |
| Approval scoping (survives supersede) | `WorkItemApproval` keyed `(WorkItemKey, Product, TargetEnv)` |
| HTTP routes (no create endpoint today) | `PromotionEndpoints.cs` (all act on existing candidates) |
| Hook DI registration | `Program.cs:180` |

### ⚠️ The central refactor insight
The "bundle = `SourceDeployEventId` + `SupersededSourceEventIds`, then look up
`DeployEventWorkItems` by those IDs" pattern is **NOT confined to the gate**. It is
repeated across the whole approval surface:
- `PromotionService` gate (3 query sites + the auto-approve probe)
- `WorkItemApprovalService` pending queue (`~455`), assignment listing (`~735`),
  `FindPendingCandidateForTicketAsync` (`~845`)
- `DispatchWebhookAsync` (`~1030`)

All of these must move from **"work items belong to deploy events, reached via a
candidate's event bundle"** to **"work items belong to the candidate directly."**
This is the bulk of the work.

---

## 4. Target design

### 4.1 Data model changes

**`PromotionCandidate`** (`Models/PromotionCandidate.cs`) — add:
- `string ReferencesJson { get; set; } = "[]"` + a `List<ReferenceDto> References`
  computed property (same get/set JSON pattern already used for `Participants`).
  This is the self-contained net change set (work-item / pull-request / repository refs).
- `string? FromRevision { get; set; }` — display/traceability (target env current SHA).
- `string? ToRevision { get; set; }` — display/traceability (promoted SHA).
- **Remove** `SourceDeployEventId` **and** `SupersededSourceEventIds` (D14, D2):
  - `SupersededSourceEventIds` / `SupersededSourceEventIdsJson` → **remove** (no longer
    the source of truth for the bundle).
  - `SourceDeployEventId` → **remove entirely**. The candidate is self-contained; nothing
    in the gate or UI links back to an originating deploy event.
  - The separation-of-duties (`ExcludeRole`) feature — which was the only thing that needed
    the source event's participants — is **removed for now** (D17). No knock-on. See §8.3.
  - Keep `SupersededById` (still set when a newer candidate replaces this one).

**New candidate-scoped work-item index** — the linchpin that lets every call site
swap from event-id joins to candidate joins. Two options:

- **Option A (recommended): new table `PromotionWorkItem`**
  Columns: `Id`, `CandidateId`, `WorkItemKey`, `Product`, `TargetEnv`, `Provider?`,
  `Url?`, `Title?`, `Revision?`, `CreatedAt`. Populated at create time from the
  payload's `type == "work-item"` references. Leaves `DeployEventWorkItem` untouched
  for its deploy-event-history purpose (`DeployEventWorkItemBackfillService`,
  "which builds carry ticket X").
- **Option B: add nullable `CandidateId` to `DeployEventWorkItem`** and make
  `DeployEventId` nullable. Less new surface but overloads one table with two
  semantics (deploy-event history vs. candidate bundle) — rejected unless review
  prefers fewer tables.

> Note: the gate *could* read work-item keys straight from `candidate.References`
> JSON (one row, cheap). But the **approval queue/assignment UI** in
> `WorkItemApprovalService` needs to query *across candidates* by ticket key,
> product, env, and role — that needs a relational index. Hence Option A.

### 4.2 New API endpoint

`POST /api/promotions` — API-key auth + rate limiting, mirroring
`/api/deployments/events` (`DeploymentEndpoints.cs`). Product scope enforced from the
API-key claims, same as deploy ingest, **plus a distinct `promotion:create` scope/permission**
(D16) so a key may be granted deploy ingestion without the ability to open gated releases
(least privilege — the reporting agent and the promotion orchestrator are often different
systems). Reject with 403 when the key lacks the scope.

**Payload DTO** (reuses existing `ReferenceDto` / `ParticipantDto`):

```csharp
public record CreatePromotionDto(
    string Product,
    string Service,
    string SourceEnv,
    string TargetEnv,
    string Version,
    string? FromRevision,                 // display only
    string? ToRevision,                   // display only
    List<ReferenceDto>? References,        // authoritative net change set
    List<ParticipantDto>? Participants);   // promotion-level participants
```

**Example request:**

```jsonc
{
  "product":   "checkout",
  "service":   "checkout-api",
  "sourceEnv": "dev",
  "targetEnv": "test",
  "version":   "1.3",
  "fromRevision": "a1b2c3d",      // target env's current SHA  (display/trace)
  "toRevision":   "f9e8d7c",      // SHA being promoted        (display/trace)
  "references": [
    { "type": "work-item",    "provider": "jira",   "key": "CHK-451",
      "title": "Add express checkout", "url": "https://jira/CHK-451" },
    { "type": "pull-request", "provider": "github", "key": "2087",
      "title": "Express checkout", "url": "https://github.com/o/r/pull/2087" },
    { "type": "repository",   "provider": "github", "revision": "f9e8d7c",
      "url": "https://github.com/o/r" }
    // reverted commit B + CHK-450 are absent — that is the fix
  ],
  "participants": [
    { "role": "release-manager", "displayName": "Dana Lee", "email": "dana@x" }
  ]
}
```

### 4.3 New `PromotionService.CreateExternalCandidateAsync`

A new method (or a reworked `CreateCandidateAsync` taking an explicit bundle). Steps:
1. Validate edge fields; resolve policy via `PromotionPolicyResolver` for
   `(product, service, targetEnv)`. No policy → 422 (product not enrolled for this edge).
   With topology dropped (D19), this 422 **is** the edge guard — there is no separate
   source→target validation. `sourceEnv` is recorded for display but not validated; the
   external owns edge correctness.
2. **Idempotent reuse (D15):** identity is the natural key
   `(product, service, sourceEnv, targetEnv, version)` — if a non-terminal candidate exists
   for it, update its references/revisions and re-evaluate instead of duplicating. A repeat
   for the same version is a legitimate **update** (the external may have recomputed the net
   set after another revert), so update-in-place is the correct semantic — not dedup.
   - **Race safety:** add a DB unique constraint on the non-terminal natural key
     `(product, service, sourceEnv, targetEnv, version)` where status ∈ {Pending, Approved,
     Deploying}. A concurrent double-create loses the second insert cleanly; catch the
     conflict and treat as "reuse existing." No idempotency key needed.
   - Auto-approve webhooks should be consumer-idempotent anyway (a retry may re-fire one).
3. **Supersede = pure state flip:** mark any still-`Pending` candidate on the same
   `(product, service, sourceEnv, targetEnv)` as `Superseded`, set `SupersededById`.
   **No inheritance, no `inherited` set, no event-id copying.**
4. Snapshot policy (`ResolvedPolicyJson`); compute auto-approve
   (`IsAutoApprove` || (`AutoApproveWhenNoTickets` && payload has no `work-item` refs)).
   The no-tickets probe now reads the **payload references**, not `DeployEventWorkItems`.
5. Persist candidate (`References`, `FromRevision`, `ToRevision`, status).
6. Populate the candidate-scoped `PromotionWorkItem` index from `work-item` references.
7. Synthetic auto-approval row + `promotion.approved` webhook when born Approved
   (same as today).
8. Audit log `promotion.candidate.created` (source = "external").

### 4.4 Gate & approval read-path refactor

Swap every "bundle event-ids → `DeployEventWorkItems`" lookup for
"`candidateId` → `PromotionWorkItem`":
- `CandidateHasTicketsAsync` (`:822`)
- `AreAllTicketsApprovedAsync` (`:836`)
- `EvaluateTicketsGateAsync` (`:868`)
- auto-approve probe (`:152`) → reads payload refs at create time
- `WorkItemApprovalService` pending queue (`~455`), assignment listing (`~735`),
  `FindPendingCandidateForTicketAsync` (`~845`)
- `DispatchWebhookAsync` (`~1030`) → read `candidate.References` directly instead of
  re-aggregating from source deploy events; include `fromRevision`/`toRevision`.

Approval scoping `(WorkItemKey, Product, TargetEnv)` is **unchanged**, so an
already-approved ticket still counts after a supersede — no re-signoff. (Free win.)

### 4.5 Disable auto-generation

- `PromotionIngestHook.OnIngestedAsync`: **remove** the `GenerateCandidatesAsync` call
  (step 4) and the method, plus its `_topology.GetNextEnvironmentsAsync` use.
  - `MatchCompletionAsync` (mark candidate `Deployed` when the version lands on the
    target env) — **KEEP** (D18); matches by `(product, service, targetEnv, version)`, no
    bundle dependency. Promotions still auto-close on landing. Ingestion stops *creating*
    promotions but still *closes* them.
  - `WorkItemSyncService.SyncAsync` for deploy events — keep only if
    `DeployEventWorkItem` is still wanted as deploy-history (it has a backfill service
    and other readers). It no longer feeds the promotion gate.
  - Rollback completion matching (`RollbackService.MatchCompletionAsync`) — unaffected.
- `ComputeRollbackBundleAsync` and `SupersedeStalePendingAsync`'s inheritance logic —
  **delete** (rollback is now just "external sends a different net set").
- **Topology removal (D19):** delete `PromotionTopologyService`, the `GET/PUT /topology`
  admin endpoints (`PromotionAdminEndpoints`), the `PromotionTopology` model, topology seed
  data (`PromotionSeedData`), and the DI registration (`Program.cs`). The
  `promotions.topology` platform-setting row can be cleaned up in the migration. The ingest
  hook no longer depends on it (the only functional consumer was generation).

---

## 5. Migration & rollout

1. **Schema migration** (both providers — `Migrations/Postgres` + `Migrations/SqlServer`):
   add `PromotionCandidate.ReferencesJson`, `FromRevision`, `ToRevision`; add
   `PromotionWorkItem` table; (after backfill + soak) drop `SupersededSourceEventIdsJson`
   **and** `SourceDeployEventId` (D14).
2. **Backfill (REQUIRED if any in-flight candidates exist at cutover):** project each
   in-flight `Pending`/`Approved` candidate's current bundle
   (`SourceDeployEventId` ∪ `SupersededSourceEventIds` → references) into the new
   `References` / `PromotionWorkItem`. Without it, a live candidate's gate reads an empty
   `PromotionWorkItem` and **stalls** (no tickets found ⇒ wrong gate result). One-shot hosted
   service, mirroring `DeployEventWorkItemBackfillService`. Only skippable on a greenfield
   deploy with zero open candidates.
3. **Feature flag:** gate the new behavior behind `features.externalPromotions` (or
   reuse `features.promotions`). When on: ingest hook skips auto-generation; create
   endpoint is live. Allows staged rollout per environment.
4. **Decommission:** once all pipelines call the API, remove the inference code and
   (after a soak) the deprecated columns.

---

## 6. Decision log

### 6.1 Resolved
| # | Decision | Resolution | Where |
|---|---|---|---|
| D1 | Who creates promotion candidates | **External/push only** — tool no longer auto-generates | §1, §4.5 |
| D2 | Candidate bundle representation | **Self-contained** (candidate owns its references); supersede = pure state flip, no inheritance | §3, §4.1, §4.3 |
| D3 | `fromRevision` / `toRevision` | **Keep for display & traceability**, not used for gating | §4.1, §4.2 |
| D4 | Work-item index storage | **New `PromotionWorkItem` table** (candidate-scoped), leave `DeployEventWorkItem` for deploy history | §4.1 Option A |
| D5 | Work items in payload | **Inside `references`** as typed entries (one shape, consistent with deploy ingest) | §4.2 |
| D6 | Approval authority location | **Stays in the tool** (admin-managed, snapshotted); payload carries context only, never authority | §"trust boundary" / §8 |
| D7 | Approval rule shape | **Bounded step → requirement → (groups ∪ users) tree**, not a free-form rules engine | §8.1, §8.2 |
| D8 | Step evaluation | **Parallel** — all requirements across all steps must pass, any order | §8.1, §8.4 |
| D9 | Distinct-people scope | **Global** — one human satisfies at most one requirement across the whole policy | §8.1, §8.4 |
| D10 | Service vs product policy | **Full override** (no field-level merge) — current resolver behavior | §8.6 |
| D11 | QA blanket approve shortcut | **Removed** — QA is just a group on a requirement; admin bootstrap shortcut stays | §8.3 |
| D12 | Approver tree storage | **JSON column** (`ApprovalStepsJson`), snapshotted onto candidate | §8.2 |
| D13 | `repository` / PR refs in payload | **Optional / decorative** — accepted and stored for UI commit links + traceability; never required, don't reject on absence | §4.2 |
| D14 | `SourceDeployEventId` | **Dropped entirely** — candidate is self-contained; nothing links back to a deploy event (made clean by D17) | §4.1, §8.3 |
| D15 | Idempotency | **Natural key reuse-and-update** + a DB unique constraint on the non-terminal natural key for race safety. **No `IdempotencyKey`** — a repeat for the same version is a legitimate update, not a dedup | §4.3 |
| D16 | Create-endpoint auth | **API-key + distinct `promotion:create` scope** — separable from deploy-ingest permission (least privilege) | §4.2 |
| D17 | Separation of duties (`ExcludeRole`) | **Removed for now** — anyone authorized for a requirement may approve, incl. the deploy scheduler. Re-add later as payload-driven | §4.1, §8.2, §8.3 |
| D18 | Promotion completion | **Keep ingest-driven auto-close** — `MatchCompletionAsync` still marks a candidate `Deployed` when its version lands on the target env. Ingestion stops *creating* promotions but still *closes* them | §4.5 |
| D19 | Topology | **Dropped entirely** — remove `PromotionTopologyService`, `/topology` admin endpoints, `PromotionTopology` model, seed + DI. The external is the sole source of truth for edges; `sourceEnv` is recorded but not validated. The policy-resolution 422 (target-env scoped) is the de-facto edge guard | §4.3, §4.5 |

### 6.2 Open / deferred
1. **Sequential steps (deferred)** — the model already supports ordered stages; evaluation
   is parallel per D8. Revisit adding order-enforcement (gate/authorizer/UI) only when a
   real staged-gate need appears. No action now.
2. **Payload-driven separation of duties (deferred, D17)** — when "the person who scheduled
   the deploy can't approve its promotion" is needed, add a `scheduled-by` (or similar)
   participant to the create payload and a re-introduced exclusion check that reads
   `candidate.Participants` for that role. No deploy-event linkage required.

---

## 7. Work breakdown (suggested order)

This plan has **two workstreams** that both touch the `PromotionService` gate and the policy
snapshot. They are independent in motivation but overlap in code, so sequence matters:

- **Workstream A — externally-created promotions (§4–§5):** the revert fix + self-contained
  candidates + disabling auto-generation.
- **Workstream B — extended approval policy (§8):** multi-step / multi-requirement approvers.

**Recommended sequencing:** land **A first** (it's the load-bearing model change and the
gate read-path refactor), then **B** on top (it rewrites the *evaluation* logic of the same
gate methods A refactored). Doing B first would mean rewriting gate internals twice. If split
across people, freeze the gate-method signatures after A's step A4 so B can build against them.

### Workstream A
A1. Model + migration: `PromotionCandidate` new fields (`ReferencesJson`, `FromRevision`,
    `ToRevision`); `PromotionWorkItem` table; unique constraint on the non-terminal natural
    key `(product, service, sourceEnv, targetEnv, version)` for create-race safety.
A2. `CreatePromotionDto` + `POST /api/promotions` endpoint (validation, auth + `promotion:create`
    scope, rate limit).
A3. `CreateExternalCandidateAsync` (create + idempotent reuse + pure-flip supersede + index population).
A4. Refactor gate reads (`PromotionService`) to `PromotionWorkItem`/`candidate.References`.
A5. Refactor `WorkItemApprovalService` (queue/assignment/lookup) to candidate-scoped index.
A6. Refactor `DispatchWebhookAsync` to read `candidate.References` + emit revisions.
A7. Disable auto-generation in `PromotionIngestHook` (keep `MatchCompletionAsync`, D18);
    delete inheritance + rollback-bundle code; **remove topology** (service, `/topology`
    endpoints, model, seed, DI; clean up the `promotions.topology` setting) per D19.
A8. Backfill service for in-flight candidates; feature-flag wiring.
A9. Tests: create endpoint, idempotency, supersede-without-inherit, revert scenario
    (B reverted in 1.3 ⇒ B absent from gate), approval carry-over across supersede,
    completion matching still closes candidates.
A10. Docs / pipeline integration guide for the CI side that computes the net diff.

### Workstream B (after A)
B1. Model + migration: `ApproverRequirement` / `ApprovalStep`; `PromotionPolicy.ApprovalStepsJson`;
    `PromotionApproval.StepName` / `RequirementName`; retire `ApproverGroup`/`Strategy`/`MinApprovers`
    + `ExcludeRole`. Backfill existing policies into single-step/single-requirement.
B2. `ResolvedPolicySnapshot` carries `ApprovalSteps`; resolver projects it; `IsAutoApprove`
    redefined (no requirements ⇒ auto).
B3. `PromotionApprovalAuthorizer.IsAuthorizedForRequirementAsync`; remove `IsQA` blanket
    shortcut + `IsEmailExcludedByRoleAsync`.
B4. Gate evaluator: per-requirement satisfaction with constrained-first distinct-approver
    matching (§8.4).
B5. Admin DTOs + `PromotionAdminEndpoints` + admin UI for the step tree.
B6. Tests: multi-requirement gate (AND across requirements), distinct-person matching
    (Alice/Bob case), groups-OR-users, service-overrides-product, policy backfill.

---

## 8. Extended approval policy (multi-requirement approvers)

### 8.1 Motivation & decisions
The current policy expresses a **single** approver rule (`ApproverGroup` + `Strategy` +
`MinApprovers`). We need:
- Approvers defined per **product**, and per **product+service** when the service differs.
- A requirement satisfiable by **an explicit user list OR an AD group** (both, OR'd).
- **Multiple named requirements** per policy (e.g. Release Manager AD group **and** QA AD
  group), each independently required.
- The existing "all work items must be closed/approved" flag (`RequireAllTicketsApproved`)
  **persisted unchanged**.

**Decisions (confirmed):**
1. **Rule structure = ordered-by-name steps → requirements → (groups ∪ users).** Approvals
   are expressed as a list of **steps**, each holding one or more **requirements**. Add
   steps or add groups freely. Bounded structure — NOT a free-form rules/expression engine
   (rejected: hard to validate, snapshot, and audit for a security gate).
2. **Steps evaluated in parallel** — all steps/requirements are open for approval at once;
   the promotion passes when every requirement (across every step) is satisfied, in any
   order. No sequential ordering enforcement. The step layer is organizational/display
   (group requirements under a named stage in the UI).
3. **Distinct people across the whole promotion** — one human satisfies at most one
   requirement anywhere in the policy (global, not per-step). Strongest separation of duties.
4. **Service policy fully overrides product policy** — current resolver behavior kept
   (service row replaces product row; no field-level merge). Re-declare approvers on the
   service row when needed.
5. **Remove the blanket QA shortcut** in `PromotionApprovalAuthorizer` — QA becomes just a
   group on a requirement. Admin bootstrap shortcut stays.
6. **Store the step/requirement tree as a JSON column** on the policy (consistent with
   existing `…Json` columns and the snapshot-into-candidate model).

### 8.2 Model

```csharp
public record ApproverRequirement(
    string Name,                 // "Release Manager", "QA"
    List<string> Groups,         // AD group object-ids / names / role claims — member of ANY qualifies
    List<string> Users,          // explicit approver emails — being listed qualifies
    int MinApprovers = 1);       // distinct approvers needed for THIS requirement

// Note: the old PromotionStrategy enum (Any / NOfM) is NOT carried onto the requirement —
// MinApprovers subsumes it (Any ⇒ MinApprovers = 1; NOfM ⇒ MinApprovers = N). The enum is
// retired with the legacy single-group fields (B1).

public record ApprovalStep(
    string Name,                       // "QA Signoff", "Release Approval"
    List<ApproverRequirement> Requirements);  // step satisfied when ALL its requirements are
```

- **Within a requirement → OR:** `Users ∪ GroupMembers`.
- **Within a step → AND:** all its requirements must be satisfied.
- **Across steps → AND, parallel:** every requirement in every step must be satisfied; any
  order, all open at once (decision 2). Functionally a flat AND over all requirements —
  steps are a named grouping for structure/UI.
- **Distinct approvers globally:** one human counts for at most one requirement across the
  whole policy (decision 3).

`PromotionPolicy` changes:
- Add `string ApprovalStepsJson = "[]"` + computed `List<ApprovalStep> ApprovalSteps`.
- **Deprecate** `ApproverGroup`, `Strategy`, `MinApprovers` — migrate each existing policy
  into a single step `{ Name = "Approval", Requirements = [ { Name = "Approver",
  Groups = [ApproverGroup], Users = [], MinApprovers } ] }` (NOfM ⇒ MinApprovers = N, Any ⇒ 1).
  Keep columns through a deprecation window, then drop.
- Keep `RequireAllTicketsApproved`, `AutoApproveOnAllTicketsApproved`,
  `AutoApproveWhenNoTickets`, `EscalationGroup`, `TimeoutHours`, `Gate`.
- **Remove/retire `ExcludeRole`** (D17) — separation-of-duties dropped for now.

`ResolvedPolicySnapshot` changes:
- Carry `List<ApprovalStep> ApprovalSteps`.
- `IsAutoApprove => ApprovalSteps.All(s => s.Requirements.Count == 0)` (no requirements
  anywhere ⇒ no human gate).
- Ticket flags unchanged (still snapshotted).

`PromotionApproval` changes:
- Add optional `string? StepName` and `string? RequirementName` so an approval attributes to
  a specific step+requirement; the gate uses them for deterministic counting and the UI
  shows per-step / per-requirement progress ("QA Signoff ✓ / Release Approval ⏳").

### 8.3 Authorizer (`PromotionApprovalAuthorizer`)
- New `Task<bool> IsAuthorizedForRequirementAsync(ApproverRequirement req, string email, CancellationToken ct)`:
  `email ∈ req.Users` (case-insensitive) **OR** member of any `req.Groups` (reuse existing
  role-claim / group-claim / Graph-fallback logic in `IsInApproverGroupAsync`).
- Keep admin bootstrap shortcut. **Remove** the blanket `IsQA` shortcut (decision 5).
- **Separation-of-duties (`ExcludeRole`) is removed for now (D17).** Approval can be done by
  anyone authorized for a requirement (group member or listed user) — including whoever
  scheduled the deploy. Delete `IsEmailExcludedByRoleAsync` and the `ExcludeRole` field from
  policy + snapshot (or leave the column dormant/unread if cheaper).
  - **Future re-add (payload-driven):** when SoD is needed, the external passes the
    scheduling person as a participant (e.g. role `scheduled-by`) in the create payload, and
    a re-introduced exclusion check reads `candidate.Participants` for that role. No deploy-
    event linkage required. Tracked in §6.2.

### 8.4 Gate evaluator (`PromotionService`)
Replace the single-threshold promotion-side check with **per-requirement satisfaction over
the flattened requirement set** (all requirements across all steps; parallel):
1. Load the candidate's `PromotionApproval` rows (Approved, not Rejected).
2. Build the bipartite graph: each approver → the requirements they're authorized for
   (`IsAuthorizedForRequirementAsync`). Each requirement needs `MinApprovers` distinct
   approvers; each approver fills at most one requirement (global distinct-person, decision 3).
3. **Assignment order matters** — match the *most-constrained* requirement first (fewest
   eligible approvers) so a multi-group approver isn't greedily consumed by a requirement
   that had other candidates, producing a false "not satisfied". Worked example: reqs
   `RM{Alice,Bob}` and `QA{Bob}`; assigning RM→Bob first leaves QA empty (wrong); assigning
   the constrained QA→Bob first leaves RM→Alice (correct). For realistic N a greedy
   fewest-options-first pass suffices; a full bipartite-matching algorithm is overkill.
4. Manual gate passes when **every** requirement is satisfied.
5. Combine with the ticket gate per the existing `PromotionGate` mode + `RequireAllTicketsApproved`.

### 8.5 Admin surface
- Extend policy create/update DTOs + `PromotionAdminEndpoints` (`/policies`) to accept the
  step tree: `steps[] → { name, requirements[] → { name, groups[], users[], minApprovers } }`.
- Admin UI: add/remove steps; within each step add/remove requirements, each with a group
  picker + user-email list. Validate names are unique within their scope (steps unique in
  policy; requirements unique within a step) so approvals attribute deterministically.

### 8.6 Resolution
Unchanged: `PromotionPolicyResolver` service-row-wins → product-default. Full override
(decision 4) — no merge logic added.

### 8.7 Migration
- Schema migration (Postgres + SqlServer): add `ApprovalStepsJson`; backfill from existing
  `ApproverGroup`/`Strategy`/`MinApprovers` into a single step with a single requirement.
- After soak: drop deprecated columns + their snapshot fields.

---

## 9. Acceptance scenario (the bug, as a test)

- DEV deploys 1.2 (commits A, B). External creates promotion DEV→TEST for 1.2 with
  refs {A, B, CHK-450, CHK-451}. Candidate `C12` Pending.
- B is reverted in repo (revert R).
- DEV deploys 1.3. External computes net diff TEST→1.3 = {A, R, CHK-451} (B/CHK-450
  cancelled by the revert) and creates promotion DEV→TEST for 1.3.
- Expect: `C12` → `Superseded` (state flip only). `C13` gate lists **CHK-451 only**.
  CHK-450 is **not** gated and **not** reported as shipped. If CHK-451 was already
  approved on `C12`, it remains approved on `C13` (no re-signoff).
