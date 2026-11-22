# Navigation and My Tasks for approvers — implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A sidebar of 17 items in 5 groups instead of 20 in 8, ordered by what people come to do, and a **My Tasks** page that also serves people who approve promotions without being QA (E-19, E-20, D-21). Independent of stages 1–3; what those stages add to the sidebar and to Settings is listed under "Sidebar: today and target".

**Architecture:** Only the shell changes: `Sidebar.tsx`, `navTargets.ts`, the routes in `App.tsx`, two thin tab shells and the grouping inside `SettingsPage.tsx`. Merged pages keep their components and their URLs — a tab is a route, not a query parameter. One server addition tells the client whether the signed-in person is named on any promotion gate.

**Tech Stack:** React 19 + TypeScript (react-router), ASP.NET Core net10.0 Minimal API, xUnit.

**Spec:** the agreed specification of 2026-11-22, E-19 and E-20.

## Global Constraints

- Every URL that works today keeps working: unchanged, or by a redirect that carries the query string (as `/builds` → `/artifacts` does).
- No page changes who may open it, except **My Tasks**. Incidents stays admin-only; Webhooks and Settings stay admin-only.
- Work-item sign-off, assignment and the work-item queue stay with QA and admins (`WorkItemApprovalService`). Nothing in this plan touches them.
- Older "What's new" entries are not rewritten; their links (`/analytics/delivery`, `/inventory/cost`, `/incidents`, `/approvals`) must still open the right view.
- Task 0 comes first. The structure below is the proposal that is shown, not yet the one that is built.
- No commits on `main`, no push, no PR without the user's consent.

## Review Focus

1. A person who approves promotions and is not QA sees **My Tasks**, the bell and their pending promotions, and no work-item sections. A person who is neither sees no **My Tasks**. → Task 1 tests.
2. For QA and admins, **My Tasks** shows exactly what it shows today, with the same counts. → Task 1.
3. `/webhooks`, `/webhooks/{id}`, `/analytics/delivery`, `/inventory/coverage` and `/builds` each land on the right view with their query string. → Task 3.
4. The command palette offers every sidebar destination and nothing the sidebar hides. → Task 2.
5. Where the specification and the code disagree, the code was followed:
   - The specification asks what happens "when one of two merged features is off". In the code each pair shares one flag (`features.analytics`; `features.serviceInventory`), so the case cannot occur. The rule under "Merged pages" covers a later split.
   - Server-side nothing gates My Tasks: its data endpoints are open to every signed-in caller (`CanApprove`) and already answer per caller. The gate is client-only (`QaRoute`, and four copies of `isQA || isAdmin`).
   - The web app has no test runner; shell changes are verified by `npx tsc -b`, the build and the manual checklist in Task 6.

---

## Sidebar: today and target

Today (`components/shell/Sidebar.tsx`, routes in `App.tsx`):

| Group (gate) | Item | Route | Item gate |
|---|---|---|---|
| — (QA or admin) | My Tasks | `/my-tasks` | `QaRoute` |
| Catalog (`features.serviceCatalog`) | Request Catalog | `/catalog` | |
| | My Requests | `/requests` | |
| | Approvals | `/approvals` | `features.approvals` |
| Deployments | Deployments | `/deployments` | |
| | Migrations | `/migrations` | |
| | Analytics | `/analytics` | `features.analytics` |
| | Delivery metrics | `/analytics/delivery` | `features.analytics` |
| | Release Notes | `/release-notes` | `features.releaseNotes` |
| | Artifacts | `/artifacts` | |
| Service inventory (`features.serviceInventory`) | Service cost | `/inventory/cost` | |
| | Cost coverage | `/inventory/coverage` | |
| Releases (`features.promotions`) | Releases | `/releases` | |
| Promotions (`features.promotions`) | Promotions | `/promotions` | |
| | Work items queue | `/me/work-items` | |
| | Audit | `/promotions/audit` | admin (`AdminRoute`) |
| Rollbacks (`features.rollbacks`) | Rollbacks | `/rollbacks` | |
| System (admin) | Incidents | `/incidents` | `AdminRoute` |
| | Webhooks | `/webhooks` | `AdminRoute` |
| | Settings | `/settings` | `AdminRoute` |

Target:

| Group | Item | Route | Gate |
|---|---|---|---|
| — | My Tasks | `/my-tasks` | QA, admin or promotion approver |
| Delivery | Deployments | `/deployments` | |
| | Services (stage 1) | `/services` | `features.serviceList` |
| | Artifacts | `/artifacts` | |
| | Promotions (with the freeze page, stage 2) | `/promotions`, `/promotions/freeze` | `features.promotions` |
| | Work items queue | `/me/work-items` | `features.promotions` |
| | Releases (tabs **Releases**, **Trains**, stage 2) | `/releases` | `features.promotions`; the Trains tab also `features.releaseTrains` |
| | Rollbacks | `/rollbacks` | `features.rollbacks` |
| | Migrations | `/migrations` | |
| Insights | Analytics (tabs **Operations**, **Delivery metrics**) | `/analytics`, `/analytics/delivery` | `features.analytics` |
| | Release Notes | `/release-notes` | `features.releaseNotes` |
| | Incidents | `/incidents` | admin |
| | Service cost (tabs **Cost**, **Coverage**) | `/inventory/cost`, `/inventory/coverage` | `features.serviceInventory` |
| Requests (`features.serviceCatalog`) | Request Catalog | `/catalog` | |
| | My Requests | `/requests` | |
| | Request approvals | `/approvals` | `features.approvals` |
| Admin (admin) | Audit | `/promotions/audit` | `features.promotions` |
| | Settings (with **Webhooks**) | `/settings`, `/settings/webhooks` | |

A group with no visible item is not drawn (existing behaviour).

**What stages 1–3 add to this structure.** This plan delivers 17 items in 5 groups
(1 + 7 + 4 + 3 + 2), as in E-19. Stage 1 adds one item, the read-only **Services** page for
everyone (E-21, D-28), which makes 18 (1 + 8 + 4 + 3 + 2). Nothing else adds or removes an item:

- Stage 1 (`docs/plans/service-list-and-catalog-product.md`): Settings → Service Products becomes
  **Settings → Services** (`/settings/services`; `/settings/service-products` redirects) and a
  read-only **Settings → API Keys** (`/settings/api-keys`) is added. Both are Settings sections.
  The **Services** item (`/services`) is the same list without the admin actions; it is placed
  after **Deployments** because both answer "what runs where", one by product and one by service
  and team.
- Stage 2 (`docs/plans/release-trains.md`): a **Trains** tab on the Releases page, and the freeze
  page moves from `/settings/freeze` to `/promotions/freeze` so that release managers can open it
  (ADR-0008; everything under `/settings` is admin-only). Decided: it gets no sidebar item. It sits
  under **Promotions**, is opened by a **Freeze** button on the Promotions page and a **Manage
  freezes** link in the freeze banners, and Settings keeps a **Freeze** entry that links to it. Stage 2 opens that one page to release managers; this plan
  changes who may open My Tasks only.
- Stage 3 (`docs/plans/release-notes-for-a-release.md`): new filters and tables inside Analytics and
  Delivery metrics, which are the two tabs of the merged **Analytics** item here; no new item.

**URLs**

| Old URL | After |
|---|---|
| `/webhooks`, `/webhooks/:id` | redirect to `/settings/webhooks`, `/settings/webhooks/:id` (new) |
| `/analytics`, `/analytics/delivery`, `/inventory/cost`, `/inventory/coverage` | unchanged; each is a tab of its merged page |
| `/approvals`, `/approvals/:id`, `/incidents`, `/promotions/audit`, `/my-tasks` | unchanged; label or group only |
| `/builds`, `/settings/deployment-maintenance`, `/work-items/:key` | existing redirects, kept |
| `/settings/service-products`, `/settings/freeze` | not changed by this plan; stage 1 redirects the first to `/settings/services`, stage 2 the second to `/promotions/freeze` |

**Command palette** (`components/shell/navTargets.ts`): add **Artifacts** (`b`), **Service cost** (`o`, `features.serviceInventory`) and **Cost coverage** (`v`, same flag); rename **Approvals** → **Request approvals** and **Promotions audit** → **Audit**; point **Webhooks** (`h`) at `/settings/webhooks`; **My tasks** (`t`) follows the new rule instead of `qaOnly`; **Delivery metrics** (`m`) stays as a direct way to its tab.

## Merged pages

- **Tab state is the path.** `AnalyticsPage` and `DeliveryMetricsPage` both keep state in the query string under the same name with different meaning (`period` defaults to 14 on one and 30 on the other; `product` vs `products`), as do the two inventory pages (`month`). A `?tab=` parameter would mix them; a path per tab keeps every bookmark and every existing link valid with no redirect. Each merged page is a small shell (title, tab strip of `NavLink`s, `Outlet`), like `SettingsPage`; each tab keeps its own query string. The sidebar item is active for both paths, so `activeExcept: ['/analytics/delivery']` goes.
- **Flags.** Both tabs of a pair are behind one flag today. Rule for any later split: a tab whose flag is off is not drawn and its path redirects to the remaining tab; with one tab left the strip is hidden.
- **Deep links that must keep landing on the right tab** (no edit needed, verify): `/analytics/delivery` from `AnalyticsPage.tsx`, `IncidentsPage.tsx`, `DeploymentDetailPage.tsx`, `settings/AnalyticsSettings.tsx` and three "What's new" entries; `/analytics?product=` from `DeliveryMetricsPage.tsx`; `/inventory/coverage[?month=]` from `InventoryCostPage.tsx` and `ServiceInventorySection.tsx`; `/inventory/cost?product=|team=` from `ServiceInventorySection.tsx` and one "What's new" entry.
- **Links that change:** `navigate('/webhooks')` and the row link in `app/webhooks/WebhookDetailPage.tsx` and `WebhookListPage.tsx`; `PortalRoutes.Webhooks()` and the assistant's labels "Approvals" and "Webhooks" in `Agent/CatalogAgent.cs`.

## Settings

The 14 sections of `SettingsPage.tsx` plus Webhooks, under four headings. Routes do not change.

| Heading | Sections |
|---|---|
| Delivery | Environments, Promotions, Freeze, Releases, Rollbacks, Service Products, Release Notes Template, Delivery Metrics, Activity Card Template |
| Platform | Feature Flags, Webhooks, Maintenance |
| People | Participant Roles, Agent Tokens |
| Request catalog | Request Catalog |

The table lists today's sections. When the stage plans have merged: **Service Products** is
**Services** and **API Keys** is one more section, placed under a heading with the others in Task 0
(stage 1), and **Freeze** stays in the list as a link to `/promotions/freeze` (stage 2).

## My Tasks

**Today.** `app/me/MyTasksPage.tsx` is a read-only list fed by `stores/myTasksStore.ts`, which every signed-in session already polls:

| Section | Call | Server answer |
|---|---|---|
| Promotions awaiting your approval | `GET /api/promotions?status=Pending&view=summary`, kept where `canApprove` | For any caller; `canApprove` is computed per caller from the candidate's policy (`PromotionApprovalAuthorizer`). QA is not an approver by role. |
| Work items assigned to you | `GET /api/work-items/me/pending?assignee=<me>&roleRequirement=assigned` | Empty unless the caller is QA or admin (`WorkItemApprovalService`) |
| Work items with nobody assigned | `GET /api/work-items/me/pending?roleRequirement=missing` | Same |

Both API groups require only a signed-in caller. What hides the page from an approver is the client: `QaRoute` on `/my-tasks`, `qaOnly` on the sidebar group and the palette target, and `isQA || isAdmin` in `Topbar.tsx` (bell), `App.tsx` (`HomeRedirect`) and `CommandPalette.tsx`. The approver's count is already in the store; nothing shows it.

**Change.**

- Server: `GET /api/me/approver` → `{ isApprover }` — true when the caller is named, directly or through a group, on any requirement of any promotion policy (`PromotionApprovalAuthorizer.IsInPrincipalSetAsync` with `allowAdminShortcut: false`, the way `ReleaseService.IsOnGatesAsync` does it for release managers). Read once at start-up, next to `/api/me/emails`.
- Client: one hook `useCanSeeTasks()` = QA or admin or approver replaces the five checks; `QaRoute` becomes `TasksRoute`.
- For someone who is an approver only, the page shows the promotions section alone, and the store skips the two work-item calls.
- The landing page follows the same rule: an approver lands on **My Tasks**.

Stage 2 (`docs/plans/release-trains.md`, E-12) gives approvers a **Release** filter on Promotions,
inside Mass approve and on the Work items queue; it adds nothing to **My Tasks**, and its freeze
page asks its own endpoint (`GET /api/promotions/freezes/scopes`), not `GET /api/me/approver`.

**Must not change for QA and admins:** the three sections and their order, the counts on the bell and the sidebar, the environment filter, landing on **My Tasks**, and every QA/admin check in `WorkItemApprovalService` and `PromotionService`.

---

### Task 0: Show it to the people who use it (before any code)

Two or three people from each group: developers, approvers, QA, release managers, admins. Fifteen minutes each, with the current sidebar and the target side by side (a static picture is enough).

Script:
1. "Where would you go to …" — one task per group: see what runs in production (developer); approve what waits on you (approver); sign off a work item (QA); see how far the release is (release manager); add a webhook, record an incident (admin). Note first click and hesitation.
2. "What do you expect behind **Insights**? Behind **Request approvals**? Behind **Admin**?"
3. "Which item did you look for and not find?"
4. Approvers only: "Would you open **My Tasks** first, or **Promotions**? Should the portal open on it?"
5. Admins only: read the four Settings headings; "where is Webhooks? Freeze?"

The plan changes when: two or more people of one group miss the same first click; a group or item name is read differently from its content by two or more people overall; approvers prefer not to land on **My Tasks** (then they keep landing on Deployments). Record the answers and any change in this file before Task 1.

### Task 1: My Tasks for approvers (TDD)

**Files:** `src/Platform.Api/Features/Users/CurrentUserEndpoints.cs`; `src/Platform.Web/src/stores/authStore.ts`, `stores/myTasksStore.ts`, `components/auth/QaRoute.tsx` → `TasksRoute.tsx`, `components/shell/Sidebar.tsx`, `Topbar.tsx`, `CommandPalette.tsx`, `navTargets.ts`, `App.tsx`, `app/me/MyTasksPage.tsx`.

Tests (`tests/Platform.Integration.Tests`, new `CurrentUserApproverTests.cs`): named user on a requirement → true; member of a named group → true; QA without a gate → false; admin without a gate → false; no policies → false. `PromotionCanApproveTests` and `WorkItemQueueReadTests` unchanged and green.

### Task 2: Sidebar, palette, labels

**Files:** `Sidebar.tsx` (`navGroups`), `navTargets.ts`, `app/approvals/ApprovalsPage.tsx` and `ApprovalDetailPage.tsx` (title, "Back to request approvals"), `src/Platform.Api/Agent/CatalogAgent.cs` (labels).

Acceptance: 17 items in 5 groups for an admin with every flag on (18 once stage 1 adds **Services**); with all flags off, a plain user sees Deployments, Artifacts, Migrations.

### Task 3: Merged pages and Webhooks under Settings

**Files:** new `app/analytics/AnalyticsLayout.tsx`, `app/service-inventory/InventoryLayout.tsx`; `App.tsx` (nested routes, `/settings/webhooks[/:id]`, `LegacyWebhooksRedirect`); `app/webhooks/WebhookListPage.tsx`, `WebhookDetailPage.tsx`; `src/Platform.Api/Agent/PortalRoutes.cs`; `tests/Platform.Api.Tests/Agent/PortalRoutesTests.cs` (the Webhooks route).

Acceptance: review focus 3; page titles and the **?** help of each tab unchanged.

### Task 4: Settings headings

**Files:** `app/settings/SettingsPage.tsx` (`NAV` gains a heading per item and the Webhooks entry). Below `lg` the strip stays one scrolling row, headings as separators.

### Task 5: Docs, help, What's new

- `docs/src/content/siteContent.ts` (the delivery-metrics page names the two pages; "Approvals Queue"), `notes/release-notes.md` (it says "Admin → Release Notes Template"), `GLOSSARY.md` (**Request approvals** next to **My Requests**).
- Help: the **?** on a page (`components/guide/HelpButton.tsx`) is unaffected. The walkthroughs in `guides/*.yaml` point at `nav-…` anchors derived from routes; none of the routes they use changes. `GuideCorpusTests` must stay green.
- `src/Platform.Web/src/app/whats-new/entries/YYYY-MM-DD-new-menu.md` (the day the PR merges):

```markdown
---
title: A shorter menu, grouped by what you come to do
date: YYYY-MM-DD
audience: everyone
---
The menu now has five groups: **My Tasks**, **Delivery**, **Insights**, **Requests** and **Admin**.
**Delivery metrics** is a tab of **Analytics**, **Cost coverage** is a tab of **Service cost**, and
**Approvals** is now called **Request approvals**. Admins find **Webhooks** under **Settings**.
Your bookmarks still work.
```

- `YYYY-MM-DD-my-tasks-for-approvers.md`:

```markdown
---
title: My Tasks for everyone who approves promotions
date: YYYY-MM-DD
audience: approvers
link: /my-tasks
linkLabel: My Tasks
---
If you are named as an approver on a promotion, you now have **My Tasks** and the bell at the top:
they show the promotions waiting for your approval, and the portal opens on that page.
```

  (Drop the last clause if Task 0 keeps approvers landing on Deployments.)

### Task 6: Verify

`dotnet test` both projects; `npx tsc -b` and `npm run build` in `src/Platform.Web`. By hand, as plain user, approver, QA and admin: sidebar and palette agree; the URL table above; the deep links listed under "Merged pages"; `/whats-new` shows both entries and the older entries' links open the right tab.

## Open points

- Rollback approvals and request approvals are not on **My Tasks** today and are not added here.
- Whether release managers who are not approvers of anything should get **My Tasks** depends on Task 0.
- The Settings headings and the assignment of sections to them are a proposal until Task 0.
