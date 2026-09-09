# InfraPortal tutorial — presenter's guide

A walkthrough of InfraPortal on a local copy of the live data, built by
`scripts/tutorial/seed-tutorial.ps1`. This document lists the use cases worth showing, the
user processes behind them, and the order that tells a coherent story. The concrete ids, links
and curl commands for *this* build are in `.local/tutorial-cheatsheet.md` — the seed script
writes it every run, because the promotion ids change each time.

## Before you start

```powershell
.\scripts\tutorial\seed-tutorial.ps1 -Force   # ~5 minutes: fresh DB, snapshot replayed, storyline staged
.\scripts\start.ps1                           # web dev server (the API is already up after the seed)
.\scripts\tutorial\webhook-listener.ps1       # in a second, visible terminal
```

Open http://localhost:5173 in three browser profiles (or one normal + two private windows) so you
can switch roles without signing out:

| Role | Sign in as | What they are for |
|---|---|---|
| **Admin** — Anna Admin | `admin@localhost` / `admin123` | Release manager and platform owner: approves promotions and rollbacks, owns Settings, Webhooks, maintenance. |
| **QA** — Karol QA | `qa@localhost` / `qa123` | Signs off work items, raises issues, may raise a rollback. Has "My tasks" and the work-items queue. |
| **User** — Ula User | `user@localhost` / `user123` | Everyone else: reads deployments, artifacts, analytics; raises catalog requests. No queue, no approvals, no Settings. |

The pipelines' API key for the live commands is `tutorial-pipeline-key` (header `X-Api-Key`).

What the audience is looking at: a copy of the production InfraPortal as of the export — the
current version of every service in every environment for eight products, the last week of deploys,
the registered builds and every promotion that was open. Timestamps were slid so the newest deploy
is "30 minutes ago". On top of that, the seed staged ten scenes on the **mpt** product (the cheat
sheet names them A–J); everything below points at them.

The data contains real colleagues' names on commits and tickets. That is fine for an internal
session; say so, and do not record the screen for wider distribution.

---

## Part 1 — What is deployed where (any role, 10 min)

**Use case: "Which version of X is running in staging right now, and since when?"**

1. Sign in as **User**. Point out the role-aware landing: a plain user lands on **Deployments**;
   QA and Admin land on **My tasks**.
2. **Deployments** index: one card per product, environments ordered as the pipeline flows
   (dev → test → stable → staging → prod), freshness per environment. Products are ordered by
   latest activity.
3. Open **mpt**. The matrix: services × environments, each cell the current version. Point at:
   - a service whose staging and prod versions differ (that difference is what a promotion moves);
   - the **red cell** — scene F, the failed deploy the seed planted on a test/staging environment;
   - the environment colours, which come from Settings and are the same everywhere in the app.
4. Click the red cell → **deployment detail**: status, the pipeline run and its failure reason,
   the **captured Helm log** (expand it: the migration lock timeout), the change set (commit,
   work item TUT-101), participants, and the neighbouring deploys of the same service.
5. Click the service name → **service page**: where it runs, recent distinct versions and which
   environments each reached, its promotions. Then **History** for the flat per-deploy list.
6. Press `/` and type a service name — search is scoped to the page you are on. Press `:` to show
   the navigation palette; `?` for all shortcuts. Arrow keys move across the matrix.
7. **Hide products you don't care about**: the product control on the Deployments index is a
   per-user preference (the API applies it to every list), so a user sees only their products.

**Use case: "What did we build, and where did it land?"**

8. **Artifacts**: the build registry — every published build from any branch, newest first, with
   the environments each version reached. Filter by product/service/branch, or free-text.
   Explain the separation: a build is a fact about CI, a deployment is a fact about an environment;
   the registry joins them on `(product, service, version)`.

**Use case: "Is the team shipping, and is it hurting?"** (Analytics)

9. **Analytics** → mpt: deploy frequency per environment, failures and rollbacks, change-failure
   rate, promotion queue latency (p50/p75/p90 — never averages), work-item rollout matrix, lead
   time. Every panel shows its definition and its data coverage; say why (numbers without coverage
   are how dashboards lie).

---

## Part 2 — Moving a version to production (QA + Admin, 20 min)

This is the core process. The promotion is created by the release pipeline, not by hand; people
sign off, InfraPortal enforces the policy, and the pipeline deploys when the gate opens.

**The model, in one breath**: a *promotion candidate* is "service S, version V, from staging to
prod", carrying the net change set (work items, PRs, commits) between what prod runs and V. A
*policy* per edge says who must approve and whether every work item needs a QA sign-off first.
Lifecycle: Pending → Approved → Deploying → Deployed, with Rejected/Superseded as exits. A
succeeded deploy of V on the target closes the candidate whatever state it is in.

**Use case: QA signs off the tickets in a release** (scene A)

1. Sign in as **QA**. **My tasks**: everything waiting on Karol — promotions they can approve and
   work items assigned to them. The bell badge is the same number.
2. **Work items queue**: the tickets across all open promotions, with state (Pending / Approved /
   Issue / Blocked), assignee, and the missing-role flag for items the policy says need a `qa` but
   nobody is assigned. Filter to "assigned to me".
3. Open one of scene A's work items. The **work-item page**: the ticket's own title and
   description, the commits that carried it and the PRs they merged, people (author, reviewer,
   the assigned QA), the sign-off box, the comment thread. `o` opens the tracker link.
4. **Approve** it with a comment (keyboard `A`). Then open the **promotion** (scene A): the
   approval progress shows *n of m work items approved*; the human gate (release manager) is
   still open and QA can't satisfy it — the "Approve" button explains why.
5. Show scene C: the ticket with an **issue raised**. The gate is stalled but the promotion is
   still Pending — an issue is "something is wrong", reversible; a block is "not going out";
   rejecting is done to the whole promotion, never to one ticket. Read the thread.

**Use case: the release manager approves and the pipeline deploys** (scenes B and E)

6. Sign in as **Admin**. **My tasks**: scene B (all work items signed off, release approval
   pending) is at the top. Open it.
7. Read the page: the change set (link to the provider's compare view via from/to revisions),
   work items all green, participants, the policy snapshot ("Release approval — Release manager,
   1 of 1"), the notice **"Approving deploys this version to prod"** (it's the policy's
   `deploysOnApproval`; marketplace's edges say the opposite because their pipeline has its own
   gate after ours).
8. Approve with a comment. Status → **Approved**. The webhook listener terminal shows
   `promotion.approved` arrive. The system entry lands on the thread.
9. Scene E was approved before the session and is waiting for its deploy. In a terminal, run the
   **"Land the approved promotion"** curl from the cheat sheet — that is exactly what the
   pipeline posts when Helm finishes. Reload the promotion: **Deployed**, `deployedAt` set; the
   matrix cell for prod now shows the new version; the listener shows `deployment.created` and
   `promotion.deployed`.
10. **Bulk approve**: on the Promotions list, select several Pending candidates the admin may
    approve and approve them together; each still records its own approval and comment. (A prod
    candidate only becomes approvable once QA has signed off all of its work items, so sign a
    couple off as QA first if you want more than scene B to pick from.)
11. **Who approves what is per edge**: the `stable → staging` promotions are gated by the QA lead,
    not the release manager — as Admin, the Approve button on one of them says so.

**Use case: saying no** (scene D)

12. Open scene D: **Rejected**, with the reason on the thread. Rejection is terminal; the pipeline
    re-promotes from the next version, and the *newer* version on the same edge supersedes an
    older Pending one automatically (show a Superseded candidate if one exists).

**Use case: "who approved what for prod last week?"**

13. **Promotions → Audit**: every action, newest first, with tabs per category (approved, rejected,
    created, deployed, work-item, comment, people) and actor filter. Point out the approver names
    on gate-opening rows and that the rows come from the trail, not from current state.

**Use case: a promotion straight from a build** (from the Artifacts page)

14. As Admin, on **Artifacts** pick a recent mpt build and promote it to dev/test. This uses the
    `build → env` policies the seed created; the target list comes from those policies.

**Use case: the pipeline opens a promotion** (live, optional)

15. Run the "Register a build", "deploy it to dev" and "open a promotion" curls from the cheat
    sheet in sequence. Each shows up within seconds (SignalR pushes changes; no reload needed):
    the artifact in the registry, the deploy on the matrix, the candidate on the Promotions list.

---

## Part 3 — When it goes wrong: rollbacks (QA + Admin, 5 min)

**Use case: roll a service back to the previous version, with approval** (scene G)

1. As **QA**, **Rollbacks**: the open request the seed raised — one service in prod, from the
   current version back to the previous one, with the reason. Rollbacks are in-place within one
   environment; the target version must have run there before (the UI only offers those).
2. Show **Create**: two modes — *manual* (pick services and versions) and *align* (make prod look
   like staging, with an exclude list), both with a dry-run preview of what would move and what
   would be skipped and why.
3. As **Admin**, approve the request. Admins can also *override* a gate, but that is a distinct,
   reason-carrying action so the audit trail can tell a bypass from an approval.
4. **Settings → Rollbacks**: per product and environment, who may raise a rollback and who
   approves. Point out that "no policy" means not enrolled, which is not the same as "no gate".

---

## Part 4 — Telling people what shipped (Admin, 5 min)

**Use case: release notes for production**

1. **Release Notes** → mpt: scene H is a generated note for the last seven days to prod — one
   section per service with version transition, tickets, PRs, participants.
2. **New**: preview a window, edit the markdown, publish. The default window starts where the last
   note ended, so a pipeline calling `/generate` after each release gets exactly the delta.
3. **Settings → Release Notes Template**: Handlebars, resolved product/environment → product →
   global → built-in.

**Use case: notifications where the team lives**

4. **Webhooks**: scene I is a generic subscription filtered to mpt. Show the event list, the
   product/service/environment filters, the **Test** button (the listener prints it), and the
   **delivery history** with retry. Target types include Teams (Adaptive Card or HTML via Power
   Automate), Discord, GitHub `repository_dispatch` and Azure DevOps incoming hooks, each with a
   message template and live preview.

---

## Part 5 — Self-service requests (User + Admin, 5 min)

**Use case: a developer asks the platform team for something**

1. As **User**, **Service Catalog**: catalog items are YAML (repo, pipeline, namespace, DNS record,
   role assignment, free-form). Open **Create Namespace**: the form is generated from the YAML
   (inputs, validation, approval, executor).
2. **My Requests**: scene J is Ula's namespace request, awaiting approval; the older seeded
   requests show the other states (completed, failed with the executor's error, rejected with a
   reason, draft).
3. As **Admin**, **Approvals**: approve or request changes. Explain that executors (Azure DevOps,
   GitHub, Jira) are configured per installation and are stubbed here.
4. **Settings → Service Catalog**: enable/disable items, edit the YAML with validation, version
   history.

---

## Part 6 — Running the platform (Admin, 10 min)

Walk **Settings** left to right:

- **Environments** — the canonical list, order, colours, the production flag analytics keys off,
  and **aliases** (`production` → `prod`, `develop` → `dev`) so three pipelines naming one
  environment three ways stop producing three environments. **Merge** folds already-ingested
  history under the wrong name into the right one, with a preview of what moves and what can't.
- **Participant Roles** — the vocabulary (triggered-by, author, reviewer, qa, qa-owner, assignee,
  reporter). Only configured roles can be assigned by hand; ingest accepts anything and flags
  the unknown ones.
- **Activity Card Template** — what the activity cards print.
- **Feature Flags** — Promotions, Rollbacks, Release Notes, Analytics, Catalog, Approvals. Flip
  one off and the navigation loses the page.
- **Promotions** — the policies: per edge, steps → requirements → groups ∪ users with
  `minApprovers`; work-item gating flags; "deploys on approval"; auto-create from branches;
  approved-webhook delay. Explain evaluation: OR inside a requirement, AND across, one person
  counts once.
- **Rollbacks** — creators and approvers per product/environment.
- **Service Products** — override which product a service files under when a pipeline sends the
  wrong one, and remap the history already stored.
- **Maintenance** — duplicate deploy events and promotions, old pipeline logs, retired services
  (soft-delete a service and restore it), orphaned work items, reconcile completions, resend
  approved webhooks.
- **Audit** (via the API) — every action across modules, admin-only, with source IP.

Finally **Webhooks** and the API keys: keys are configured per installation, may be restricted to
products and to scopes (`build:register`, `promotion:create`), and are what every pipeline uses.

---

## Suggested 45-minute order

| Min | Part | Roles | Scenes |
|---|---|---|---|
| 0–10 | 1 Deployments, detail, artifacts, analytics | User | F |
| 10–30 | 2 Promotions end to end | QA → Admin → terminal | A, C, B, E, D, live curls |
| 30–35 | 3 Rollbacks | QA → Admin | G |
| 35–40 | 4 Release notes + webhooks | Admin | H, I |
| 40–43 | 5 Catalog request | User → Admin | J |
| 43–45 | 6 Settings tour | Admin | — |

## Resetting between rehearsals

`.\scripts\tutorial\seed-tutorial.ps1 -Force` rebuilds everything from the existing snapshot in a few
minutes. Add `-RefreshSnapshot` to pull the live instance again first (needs `DEPLOYMENTS_URL` and
`DEPLOYMENTS_API_KEY`). `-Products mpt,mpt-extensions` makes a smaller, faster build; `-HeroProduct`
moves the storyline; `-NoDateShift` keeps real timestamps.

## What is not in the local environment

- **Sign-in is local** (e-mail + password); production uses Entra ID and Graph group membership.
  Policies here name users by e-mail instead of groups.
- **The AI assistant** (⌘/Ctrl+K) needs an Azure OpenAI deployment configured; it is off.
- **Executors** for catalog requests (Azure DevOps, GitHub, Jira) are not configured, so a
  submitted request that needs one ends up Failed with a clear error rather than doing anything.
- **Work-item enrichment** from Jira/Azure DevOps is off; everything shown came with the events.
- Only the current version matrix, one week of history, open promotions and the newest 200 builds
  were copied. Deployed/rejected promotions from before the export are not there.
