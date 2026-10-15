# Plan: Retire the `main` / `stable` tracks and the "Staging Candidate" environment

**Status:** Draft for review. Nothing has been changed yet.
**Date:** 2026-10-14. Facts were read from live prod InfraPortal, mpt-release `origin/main`, marketplace `origin/master` + `origin/release/5`, and ADO definitions (around 11:00 UTC).
**Builds on:** [`feature-branch-builds.md`](feature-branch-builds.md), Phase E and §6. That plan dropped `dev → test` and `test → staging`; this plan keeps every non-dev/staging edge exactly as it is.

## Progress log

**2026-10-14 (Phases 0–1)**
- Backed up 17 promotion policies, the rollback policies and the webhooks (session scratchpad `backup/`).
- Tagged mpt-release `pre-stable-retirement` at `fde06d83`.
- Phase 1 policies applied in prod InfraPortal:
  - Dormant `build → staging` for the mpt-extensions default (`1bd11b27…`) and for `swo-extension-mscsp` (`b03b7d42…`). No auto-create yet.
  - Pilot: `mpt-extension-aws-management` `build → staging` with auto-create on `refs/heads/release/*` (`1e24c2ea…`).
  - Deleted the aws-management `build → stable` policy (`db24e2e7…`).
- Phase 0.2: the 14 stuck Approved `build → dev` promotions were not stale data. `PromotionVersionOrder` ignored the `-<run>.<sha>` build counter, so a newer build of the same release never superseded an older approved one. Fixed in this branch, with tests; run `reconcile-completions` once it is deployed.
  - 3 of the 14 (mpt-interim-renewals, mpt-platform-stats, mpt-swo-powerapps) are real dev deploy failures: missing App Configuration secrets, e.g. `secretValues.airtableApiKey`. They are not caused by the migration.
- Phase 0.3 + 0.4: [mpt-release PR #1633](https://github.com/softwareone-platform/mpt-release/pull/1633) (branch `claude/registered-build-onboarding`):
  - the onboarding (5691) trigger in Deploy Registered Build;
  - the dev/test/staging/prod guard in `Promote-RegisteredBuild.ps1`;
  - removal of `stable/mpt-extension-aws-management`.

**2026-10-14 (later)**
- #1633 merged.
- D4 resolved from deploy history (`local/versions.json` = marketplace, `/home/runner/work/mpt-release/...` = mpt-release):
  - `mpt-web-currency` belongs to marketplace. mpt-release's staging reconcile reverted marketplace's 5.9.1 back to 5.0.44 on 09-30. [#1634](https://github.com/softwareone-platform/mpt-release/pull/1634) disables it in mpt-release.
  - `mpt-extension-operations`: mpt-release owns dev (the only deployer since June). Staging stays with marketplace (Phase 5).
- Pilot moved to `swo-extension-mscsp`. [#1635](https://github.com/softwareone-platform/mpt-release/pull/1635):
  - the stable → staging walker now honours `disabledComponents`;
  - `stable/components.yaml` lists mscsp. That file is the switch for each component.
- Phase 2: [#1636](https://github.com/softwareone-platform/mpt-release/pull/1636) removes the `main/` → dev copy. Do not merge until the operations prerequisites below are done.
- Open InfraPortal steps (for the user; auto mode can't write them):
  - mpt/`mpt-extension-operations` `build → dev` policy;
  - a deploy-build webhook subscription for it (filters: products `[mpt]`, services `[mpt-extension-operations]`, environments `[dev]`; needs a GitHub token);
  - after #1635 merges: auto-create `refs/heads/release/*` on the mscsp `build → staging` policy (`b03b7d42…`).

**2026-10-14 (decision: all services end up in mpt-release)**
- Direction from the user: every service will eventually deploy via mpt-release; marketplace keeps only the ones not migrated yet. Phase 5 therefore means **migrating mpt services into mpt-release one at a time**, not teaching marketplace build → staging. Per service: marketplace `sdp.exclude`, an mpt-release directory, and the InfraPortal wiring.
- #1634 merged as an interim fix: marketplace owns `mpt-web-currency` until it migrates.
- `mpt-extension-operations` moved to product `mpt-extensions` (option B, like `mpt-extension-adobe`):
  - Override `14ed66ff…` now maps it to `mpt-extensions`.
  - History remapped: 18 deploy events, 2 builds, 6 promotions; nothing is left under `mpt`.
  - Its stuck dev → test 6.0.6 was promoted with Promote Single Build Manifest. Test now runs 6.0.6, and the promotion closed as Deployed.
  - The bulk "resend approved webhooks" was deliberately **not** used: it would also re-dispatch overtaken September promotions (aws-management `6.0.0-1`/`-2`…) and move test back to them.
- #1636 prerequisite is now just one operations master build landing in dev through Deploy Registered Build.
- Risk: release/5's `push-stable-release.ps1` (run by 5399) still posts `stable` markers for `mpt-extension-operations` (6 times since 09-10, version 5.9.0). Through the override they now land in `mpt-extensions`. A `stable → staging` 5.9.0 candidate would supersede the pending 5.9.3. Fix: release/5 copy honours modules.yaml / `sdp.exclude` like master (marketplace PR).

**2026-10-14 (afternoon)**
- **Phase 2 done:** #1636 merged at 14:05. Dev is reached only through `build → dev`.
- **mscsp pilot live:** #1635 merged at 13:45. The mscsp `build → staging` policy (`b03b7d42…`) auto-creates on `refs/heads/release/*` since about 14:20. No mscsp release build fell between the two.
  - Exit criterion: its next release build creates one `build → staging` promotion with work items, deploys through Deploy Registered Build, and closes.
- **Marketplace PRs** stop the `stable` feed for components marketplace never deploys:
  - [!152350](https://dev.azure.com/softwareone-pc/MPT/_git/marketplace/pullrequest/152350) (release/5, the live copy): master's library filter plus `sdp.exclude`, and `swo-extension-mscsp` moved into `sdp.exclude`;
  - [!152351](https://dev.azure.com/softwareone-pc/MPT/_git/marketplace/pullrequest/152351) (master): `sdp.exclude` subtraction.
  - Effect: the 11 library/template candidates, `mpt-extension-operations` and `swo-extension-mscsp` stop getting marketplace "Staging Candidate" markers.

**2026-10-14 (decision: no pilot, switch everything; no stable → staging left anywhere)**
- **Coverage:** every `mpt` and `mpt-extensions` service on the stable track registers its release builds, at the same version its stable marker shows. The exceptions are `swo-auth`, `swo-extension-nav` and `swo-web-shell`. The user accepted that those three get no staging promotion until they register.
- **InfraPortal switch:** [`retire-main-stable-tracks.cutover.py`](retire-main-stable-tracks.cutover.py). Dry-run by default, `--apply` to execute. Auto mode refused to run it, so the user runs it.
  1. Create `mpt` `build → staging`: a copy of mpt `stable → staging` (QA Review + Release Approval incl. the DevOps approver group), auto-create `refs/heads/release/*`, `sourceRequiresDeploy: false`.
  2. Turn on auto-create `refs/heads/release/*` for the mpt-extensions default `build → staging`; delete the now-identical aws-management copy.
  3. Delete `stable → staging` for the mpt-extensions default, `swo-extension-mscsp` and the mpt default. Pending stable candidates keep their snapshot and stay approvable.
  4. Webhook `mpt-release update-promotion-work-items`: products += `mpt`, so mpt build candidates get commits and work items. CommitDiffResolver handles ADO repos; the workflow's lock is per candidate, about 20 no-op runs a day.
  - Backup before the switch: scratchpad `backup/policies.prod.20261014T145651Z.json` and `webhooks.prod.20261014T145651Z.json`.
- **Producer cleanup PRs, to merge after the switch:**
  - [mpt-release #1639](https://github.com/softwareone-platform/mpt-release/pull/1639): removes Ingest Build Metadata; refresh skips stable.
  - [marketplace !152351](https://dev.azure.com/softwareone-pc/MPT/_git/marketplace/pullrequest/152351) (master, merge first): removes the stable-release steps from 5399 and `push-stable-release.ps1`.
  - [marketplace !152350](https://dev.azure.com/softwareone-pc/MPT/_git/marketplace/pullrequest/152350) (release/5): the same for release/5, plus mscsp into `sdp.exclude`.
- After all merges, nothing writes `stable` except imports into mpt-release `stable/` (5617, Phase 4).

**2026-10-14 15:10 — Phase 3 done (both products)**
- The user ran the cutover script at 15:08.
  - mpt and mpt-extensions `build → staging` auto-create on `refs/heads/release/*`.
  - No `stable → staging` policy is left for either product.
  - The work-item webhook includes `mpt`.
- Merged: mpt-release #1639, marketplace !152351 (15:09) and !152350.
- The 5399 preview on master compiles with no stable-reporting steps. No 5399 run has happened since the merge.
- Up to 15:10: no release build since the switch, and no new stable events or stable candidates.
- Next: Phase 4. Template step first. Stopping imports into `stable/` also protects the pending stable candidates: `stable/` keeps no archive, so an import that moves a component on would make its older approved candidate fail to promote.

**2026-10-14 — Phase 4 step 1 (template PRs)**
- Only two live copies of the 5617 trigger exist, found by searching every branch of `ops-build-templates-aks-releases`, `ops-build-templates` and `ops-mpt-infra`:
  - aks-releases `releases/v15` → [!152355](https://dev.azure.com/softwareone-pc/MPT/_git/ops-build-templates-aks-releases/pullrequest/152355);
  - ops-build-templates `feature/build_template` client-portal v2 → [!152356](https://dev.azure.com/softwareone-pc/MPT/_git/ops-build-templates/pullrequest/152356).
  - A third copy sits on someone's in-progress branch `fix/extract-openapi-spec-before-swagger-upload` and was left alone.
- "Upload build metadata to Storage" is kept; only its comment changed.
- Preview with the `templates` resource on the PR branch: mpt-billing (5352) and swo-platform (5294) compile, have no trigger, and keep registration and upload.
- Next: when both are merged and 5617 has had no new runs for 2–3 days, disable 5617 and 5657.

**2026-10-15 — re-queue on build → staging**
- InfraPilot #369 (the version-order fix) is merged.
- Created 14 `build → staging` promotions from registered builds with `POST /api/promotions/from-build`, all Pending with change sets attached:
  - the 13 that were pending as `stable → staging`;
  - `swo-web-oneheader-container` 5.9.3, replacing the `5.9.0` placeholder.
  - Build list: [retire-main-stable-tracks.pending-builds.md](retire-main-stable-tracks.pending-builds.md).
- The automatic route is firing: release builds created `build → staging` for `mpt-public-catalog` 5.9.8 and `swo-extension-adobe` 5.23.176 on their own.
- Next: reject the 25 `stable → staging` promotions with the comment "release/5 migration".
  - 23 can be rejected by the DevOps approver group or the named QA/Release approvers. They are done in the browser pane after the user signs in.
  - `mpt-extension-adobe` only by the named QA/Release approvers; `swo-extension-mscsp` only by the mscsp Guardians approver.

**2026-10-15 — stable queue cleared**
- Rejected 23 `stable → staging` promotions as an admin who is also in the DevOps approver group with the comment "release/5 migration", in that admin's own browser session. Verified via the API.
- Still pending, because only other people can reject them:
  - `mpt-extension-adobe` 5.1.211: the named QA/Release approvers;
  - `swo-extension-mscsp` 5.9.117: the mscsp Guardians approver.
  - Their `build → staging` replacements are already waiting.
- No other `stable → staging` promotion is pending in any product. Once those two are gone, `stable/` has no readers and Phase 4 step 3 (delete `main/` and `stable/`) can go.

**2026-10-15 — Phase 6 code**
- [gr4b4z/InfraPilot#371](https://github.com/gr4b4z/InfraPilot/pull/371) adds a "Retired" flag in Settings → Environments.
  - Current-state read paths drop retired environments: state matrix, product cards, search, service detail, analytics per environment, work-item "testable in", webhook suggestions.
  - Settings pickers skip them. History is kept.
  - 713 unit and 478 integration tests pass.
- After deploy: tick Retired on `stable`. The user does it, because the API key can't write `/api/settings`.

**2026-10-15 — Phase 6 done**
- #371 is merged and deployed, and `stable` is marked retired.
- Prod check:
  - no product card lists `stable`;
  - `/state?environment=stable` returns 0 rows;
  - service detail (e.g. swo-platform) shows dev/test/staging/prod only;
  - per-service history still returns the stable events.
- Template PRs !152355 and !152356 merged on 10-14 at 15:19. Since then 21 builds registered through the templates, and 5617 was queued 0 times; its last run was 14:31. The trigger is gone.
- Remaining stable → staging: `mpt-extension-adobe` 5.1.211 and `swo-extension-mscsp` 5.9.117, waiting on their own approvers.

**2026-10-15 — Phase 4 steps 2–3**
- ADO 5617 (`mpt-release.import-build`) and 5657 (`-dev`) set to `queueStatus: disabled` (revisions 5 and 3; reversible). Original definitions saved in the session scratchpad under `ado/`.
- The mpt-release cleanup branch `claude/remove-main-stable` is in progress.
  - It deletes `main/`, `stable/`, `pipelines/metadata-import/` and `metadata-import-from-api.yml`.
  - It removes the `main`/`stable` stages and `-EnsureSourceDeployEvent`, and updates the docs.

**2026-10-15 — Phase 4 cleanup PR**
- [mpt-release #1646](https://github.com/softwareone-platform/mpt-release/pull/1646):
  - deletes `main/`, `stable/`, metadata-import (ADO YAMLs, scripts, GHA workflow);
  - the map keeps dev → test, test → staging, staging → prod;
  - Promote-BuildMetadata loses its automatic mode and `main` default;
  - Push-InfraPortal-Promotion loses `-EnsureSourceDeployEvent`;
  - docs and diagrams updated; Pester 229/229.
- Merge after the adobe and mscsp `stable → staging` promotions are rejected. Otherwise approving one fails with "Unsupported stage 'stable'".

**2026-10-15 16:00 — migration complete**
- Last `stable → staging` promotions closed. In all three products: no open promotion touches `stable`, no policy references it, and no `stable` deploy event has been written since the 10-14 switch.
- mpt-release #1646 merged at 15:54. `origin/main` has no `main/` or `stable/`, and the promotion map is dev → test → staging → prod. PowerShell Tests and the docs publish passed on the merge.
- Open follow-ups (Phase 7 and outside the migration):
  1. Build templates: remove "Upload build metadata to Storage" and the `changelogs` blob container, once its other readers are inventoried (feature-branch-builds OQ4).
  2. Delete the disabled ADO definitions 5617 and 5657 after a release cycle.
  3. InfraPortal: delete the `mpt-release-promotion` generic webhook (no consumer) and check the `GithubPromotion` function's failure rate.
  4. InfraPilot knowledge docs (`knowledge/delivery-tracks.yaml`, `environments-and-tracks.yaml`, `promotion-policies.yaml`, `deploy-event-sources.yaml`) and `docs/plans/feature-branch-builds.md`: describe the build-registry-only flow.
  5. Release registration for `swo-auth`, `swo-extension-nav` and `swo-web-shell`. They have no way into staging until it exists.
  6. Migrate the remaining `mpt` services from marketplace into mpt-release, one at a time: `sdp.exclude` + an mpt-release directory + InfraPortal wiring. `mpt-web-currency` is the first obvious one.
  7. Unrelated, but seen repeatedly: `mpt-interim-renewals`, `mpt-platform-stats` and `mpt-swo-powerapps` fail in dev on missing App Config secrets (`airtableApiKey` / `airtableToken`).

## 1. Goal

| Edge | Today | After |
|---|---|---|
| → dev | `main/` copy (automatic, mpt-release) **and** `build → dev` (both run in parallel) | **`build → dev` only** |
| → Staging Candidate (`stable`) | `import-build` 5617 writes `stable/`; marketplace 5399 posts `stable` markers | **gone** |
| → staging | `stable → staging` (+ `test → staging` for mpt-support-tools) | **`build → staging`** (release/* builds), plus `test → staging` where it exists today (see D2) |
| dev → test, staging → prod | as configured | **unchanged** |

"Transparent" here means:
- developers notice nothing;
- QA sees the same approvers, steps and work items, only with a different source label;
- no step produces duplicate candidates, orange builds or a gap in deployments.

## 2. What the investigation found (the facts the steps rely on)

**Two deployment repos, two different situations**

- **mpt-release** (Track B/C; products `mpt-extensions`, `mpt-support-tools`, plus a few `mpt` stragglers) is already halfway there.
  - The `build → dev` path runs alongside the `main/` copy for every service. The registered-build dev commit lands about 9 s after the `main/` import.
  - `mpt-support-tools` is already in the target state: `build → dev`, `build → staging`, `dev → test`, `test → staging`, `staging → prod`.
- **marketplace** (Track A; product `mpt`) has **no** build-sourced path.
  - Dev **and test** deploy unconditionally in one run of 5462, which is triggered by every 5399 bump.
  - The `stable` markers and `stable → staging` candidates come from 5399's `push-stable-release.ps1` (changelogs.yaml 1098–1135).
  - Staging is deployed by 5685 from *approved staging promotions*. `get-approved-promotions.ps1` does **not** filter on sourceEnv, so an approved `build → staging` would already be picked up.

**`mpt-release.import-build` (ADO 5617)**

- It is queued by exactly one step: "Trigger pipeline … import-build" in `ops-build-templates-aks-releases` `aks-docker-templates/publish-steps.yaml@releases/v15` L557–600, with `continueOnError: true`.
- The same job runs "Register build in InfraPortal" first, and that step fails loudly.
- So all 26 components imported in the last 30 days (549 runs) **already register**.
- There is a stale copy of the trigger in `ops-build-templates@feature/build_template` `frontend/client-portal/v2/publishbuild-job.yml` L289–325. Nothing uses it.
- 5657 `import-build-dev` has had 0 runs.

**The `main/` copy is actively harmful**

- `ingest-build-metadata.yml` re-copies **every** `main/` component into dev on any push to `main/**` **or `stable/**`**.
- It has already downgraded dev twice: nav-billing 5.0.17→5.0.16 on 10-07, and nav-stats 0.1.0-55→0.1.0-47 on 09-10. This is a reason to do the dev cutover first.

**InfraPortal gaps**

- An environment cannot be retired. The `stable` column keeps rendering for as long as old events exist, because state is the newest event per environment with no time window.
- Removing `stable` from settings makes it worse: the column shows the raw key and sorts after prod.
- The only removal tool is an environment *merge*, which would rewrite stable history into another environment. That is wrong here.
- Webhook subscriptions filter on product, service and **targetEnv** only, never sourceEnv. Every consumer therefore has to tell `build` from other sources itself:
  - `deploy-registered-build.yml` does (L150–155);
  - marketplace `get-approved-promotions.ps1` does not.
- Deleting a policy leaves its Pending candidates Pending, with a frozen snapshot. Drain the queue first, delete the policy second.
- "Staging Candidate" is the `displayName` of the `stable` key in the `ui.app-settings` row. It is not in code, and the API key can't read `/api/settings`.

**Live queue that has to be drained**

- `stable → staging` Pending:
  - **mpt-extensions: 3**: nav-billing 5.0.13, adobe 5.1.207, mscsp 5.9.117.
  - **mpt: 24**. 11 of these are libraries or templates that can never deploy: `swo-lib-*` ×3, `mpt-library-framework`, `swo-design-system`, `swo-dynamic-translations`, `mpt-cli-adobe-plugin`, `swo-new-react-template`, `swo-web-libraries`, `swo-web-healthcheck`, `swo-web-maintenence`. They come from release/5's `push-stable-release.ps1`, which lacks master's modules.yaml filter.
- 14 `build → dev` candidates are stuck **Approved** and never closed (oldest 2026-08-27). Clear them first, so that a new stuck one is visible.

**Onboarding gap**

- First-time components get ADO 5691 (onboard-to-ops-platform-infra) only from the legacy import workflow.
- `deploy-registered-build.yml` only *warns* (L314–322). `mpt-interim-renewals`' first dev deploy (10-07) failed.

## 3. Readiness inventory — mpt-release `main/` and `stable/`

Key: ✓ = registered builds seen in the last 30 days, ✗ = none, — = not applicable.

| Component | Product | main/ | stable/ | reg master | reg release | Disposition |
|---|---|---|---|---|---|---|
| mpt-extension-adobe | ext | Y | Y | ✓ | ✓ | ready |
| mpt-extension-aws-management | ext | – | Y (via `build → stable`) | ✓ | ✓ | **pilot**: move to `build → staging` in Phase 1 |
| mpt-extension-delivery | ext | Y | – | ✓ | – | ready (has its own `build → dev`) |
| mpt-extension-installation | ext | Y | Y | ✓ | ✗ (last stable 09-07 unregistered) | **verify** the next release build registers (GH repo, ADO def 5653) |
| mpt-extension-nav-billing | ext | Y | Y | ✓ | ✓ | ready (pending candidate) |
| mpt-extension-operations | **mpt** (override) | Y | Y | ✓ | ✓ | **D4**: decide owner — mpt product has no `build → dev` |
| mpt-extension-platform-support | ext | Y | – | ✗ (last build 08-27) | – | verify on next build; product `build → dev` applies |
| mpt-extension-procurement / -scrm | ext | Y | – | ✓ | – | ready |
| mpt-extension-producthub / -usage-reporting | ext | Y | Y | ✓ | ✓ | ready |
| swo-extension-mscsp | ext | Y | Y | ✓ | ✓ | ready (service-specific approvers; pending candidate) |
| mpt-nav-stats | support-tools | Y | Y | ✓ (09-09) | ✓ (08-24) | ready (`build → staging` policy already exists) |
| mpt-web-currency | **mpt** | Y | Y | ✗ | ✓ | **D4**: also in the marketplace manifest — pick one owner |
| mpt-audit, mpt-export, mpt-web-publiccatalog, swo-web-notifications | mpt | Y (disabled) | – | — | — | delete; no effect |
| ops-dotnet-ci-sample | — | Y | – | — | — | delete; dev stays frozen at 08-04 |

`components.yaml` overlays in `main/` and `stable/` are empty, so nothing is lost. The only unique file is `stable/mpt-extension-operations/instance.yaml`; copy it if D4 keeps operations in mpt-release staging.

## 4. Target InfraPortal configuration

Prod InfraPortal only. The dev instance (infraportal-dev.s1.team) is out of scope.

| Product / service | Add | Remove | Keep |
|---|---|---|---|
| mpt-extensions (default) | `build → staging`, a copy of today's `stable → staging` steps (QA Review + Release Approval, tracksWorkItems) | `stable → staging` | `build → dev`, `dev → test`, `staging → prod` |
| mpt-extensions / swo-extension-mscsp | `build → staging`, a copy of its `stable → staging` (QA Review, single approver) | `stable → staging` | `dev → test`, `staging → prod` |
| mpt-extensions / mpt-extension-aws-management | — | `build → stable` | — (falls back to the product defaults) |
| mpt-extensions / mpt-extension-delivery | — | — | `build → dev` |
| mpt-support-tools | — | — | everything (already target) |
| mpt (default) | `build → staging`, a copy of `stable → staging` incl. the DevOps approver group | `stable → staging` | `dev → test`, `staging → prod` |
| mpt / mpt-extension-operations | `build → dev` (if D4 = mpt-release owns dev) | — | — |

**Rules for every new `build → staging` policy:**
- `sourceRequiresDeploy: false`. Otherwise every auto-create fails silently with a logged warning.
- Created with `autoCreateFromBranches: []` (dormant). `["refs/heads/release/*"]` is switched on only at that product's cutover.

**Webhooks**
- Keep `mpt-release deploy-build`. It already covers `build → staging` for both mpt-release products.
- If D4 keeps operations' dev in mpt-release, add a dedicated subscription: products `[mpt]`, services `[mpt-extension-operations]`, **environments `[dev]`**. Without the environment filter, an operations `build → staging` approval would also be deployed by mpt-release, on top of marketplace 5685.
- Leave `GithubPromotion` and `update-promotion-work-items` as they are.

## 5. Phased steps

Every phase can be shipped and reverted on its own. Each one flips a toggle first: a policy flag, `gh workflow disable`, or the ADO queue status. Code is deleted only after a quiet period.

### Phase 0 — Pre-flight (no behavior change)
1. Export all promotion policies and webhook subscriptions to JSON as the rollback baseline. Tag mpt-release `pre-stable-retirement`.
2. Triage the 14 stuck-Approved `build → dev` candidates: run `reconcile-completions`, then reject what's left with a comment.
3. Close the onboarding gap: port the 5691 trigger from `metadata-import-from-api.yml` L202–242 into `deploy-registered-build.yml`, replacing the warning at L314–322. *Must land before Phase 4 removes the last automatic onboarding.*
4. Make `Promote-RegisteredBuild.ps1` accept only `dev|test|staging|prod`. Today it accepts any env directory (L236–243), so a leftover `build → stable` could recreate `stable/`.
5. Resolve the readiness gaps in §3 (installation release registration, D4 owners). For Track A, finish release/5 registration coverage (see Phase 5 prerequisites).
6. Announce the plan to developers and to the QA/release approvers on today's `stable → staging` policies. Include the cutover dates and a screenshot of what a `build (release/5) → staging` candidate looks like.

### Phase 1 — Dormant target config + pilot (InfraPortal only)
1. Create the dormant `build → staging` policies from §4: mpt-extensions default and mscsp. Do **not** create mpt's yet; that is Phase 5.
2. **Pilot on mpt-extension-aws-management.** It already goes `build → stable → staging` with the same approvers.
   - Replace its `build → stable` policy with a *service-specific* `build → staging` policy: the same steps as the product `stable → staging`, `autoCreateFromBranches: ["refs/heads/release/*"]`. The product default stays dormant.
   - The next aws-management release build should then produce **one** `build → staging` candidate with work items. After approval, `deploy-build` → Deploy Registered Build commits `staging/…` → reconcile-staging → the deploy event closes it, and a `staging → prod` candidate appears as usual.
3. Exit: one pilot candidate fully closed, and `update-promotion-work-items` attached the change set. Rollback: restore `build → stable` from the JSON export.

### Phase 2 — Dev cutover in mpt-release (removes the downgrade bug)
1. PR to mpt-release: remove **Step 1** (`Promote-BuildMetadata.ps1 -SourceStage main`, `ingest-build-metadata.yml` L52–54).
   - Keep Step 2 (`stable → staging` candidates) for now.
   - 5617 keeps landing imports in `main/`. That is harmless and leaves a comparison record for the parallel check.
2. Watch one full working day.
   - Every `Import … to main` commit has a matching `deploy registered build … into dev` commit.
   - No `build → dev` candidate is Approved for more than about 15 min.
   - `deploy-build` webhook deliveries have no new failures (12 historical).
3. Rollback: revert the PR. This brings the downgrade risk back.

### Phase 3 — Staging cutover in mpt-release (`mpt-extensions`, `mpt-support-tools`)
1. Agree a date with QA, and ask them to clear the 3 pending mpt-extensions `stable → staging` candidates before it.
2. In one change window:
   - Remove the aws-management pilot copy.
   - Set `["refs/heads/release/*"]` on the mpt-extensions default and mscsp `build → staging` policies.
   - PR to mpt-release: disable or remove `ingest-build-metadata.yml` entirely (Step 2 included), and drop `stable` from the `refresh-promotions.yml` choices and the `Sync-InfraPortalPromotions.ps1` ValidateSet. Otherwise a manual refresh would re-post `stable` candidates.
3. Re-queue anything still pending.
   - Use `POST /api/promotions/from-build {buildId, targetEnv: staging}` with the matching release build. The versions are registered, but check each one.
   - Reject the old `stable` candidate with "moved to build → staging (<id>)".
   - Approvals must be redone. That is the only user-visible cost, so draining first matters.
   - Older open candidates are also superseded automatically by the next staging deploy.
4. Once no `stable → staging` candidate is open for these products, delete the mpt-extensions and mscsp `stable → staging` policies.
5. Rollback: re-enable the workflow and clear `autoCreateFromBranches`. The `stable → staging` policies are still in the JSON export.

### Phase 4 — Stop the import and delete `main/` + `stable/`
Order matters. Disabling 5617 first would turn every publish job across ~26 components orange: the trigger step fails and `continueOnError` makes the run partiallySucceeded.

1. Template PRs:
   - Remove the "Trigger pipeline … import-build" step from `publish-steps.yaml@releases/v15` L557–600.
   - Remove the stale copy in `ops-build-templates@feature/build_template` client-portal L289–325.
   - Keep "Upload build metadata to Storage" until the `changelogs` blob consumers are confirmed (feature-branch-builds OQ4).
2. Once no 5617 run has been queued for 2–3 days, **disable** 5617 and 5657 (queue status, reversible).
3. mpt-release cleanup PR (Pester must pass):
   - Delete `main/` and `stable/`, and their entries in `promotion-map.yaml`. `dev: test`, `test: staging` and `staging: prod` stay.
   - Delete `ingest-build-metadata.yml`, `metadata-import-from-api.yml`, `pipelines/metadata-import/` and `Publish-InfraPortal-PromotionCandidates.ps1`.
   - Remove `main`/`stable` from the `promote-single-build-manifest.yml` choices (L13–20, L132) and the `Promote-BuildMetadata.ps1` default `main` (L406).
   - Remove `Push-InfraPortal-Promotion.ps1`'s `-EnsureSourceDeployEvent` stable marker (L399–417, L472–483).
   - Fix the matching tests (`Promote-BuildMetadata.Tests.ps1`, `Push-InfraPortal-Promotion.Tests.ps1`).
   - Update the docs: README.md, pipelines/README.md, docs/release-pipelines.md, docs/commit-to-production.mmd (regenerate with `build-diagram.mjs`), docs/index.html, and the `dev|test|staging|prod/README.md` track diagrams.
4. Rollback: revert the PR (git keeps everything), re-enable 5617 and restore the template step.

### Phase 5 — marketplace (`mpt`): stop the Staging Candidate feed
Required to remove "Staging Candidate" altogether. It is independent of Phases 2–4 and can run in parallel once its prerequisites are met.

**Prerequisites**
1. **Registration:** every *deployable* mpt component registers its release/5 builds. Not yet registered, from the 30-day data:
   - `swo-auth` and `swo-extension-nav` (PRs merged 14 Oct, so expected on their next build);
   - `swo-web-shell` (bespoke, WI-5);
   - `mpt-web-spotlight` release builds;
   - `mpt-extension-finops`.
   Without a registered build, a component has **no way into staging** after the cutover.
2. **Work items:**
   - Today the mpt `stable → staging` candidates get commits and Jira keys from marketplace's `push-stable-release.ps1` → `enrich-versions-report.ps1`.
   - `build → staging` candidates for mpt are not enriched, because the `update-promotion-work-items` webhook covers only the mpt-release products.
   - Either extend that workflow to mpt's ADO-hosted repos, or add enrichment on the marketplace side. Pilot this, because the mpt `staging → prod` policy requires `qa-owner` work-item sign-off downstream.

**Steps** (paired master + release/5 PRs per the marketplace convention; 5399, 5685 and 5686 run master YAML)
1. Create the dormant mpt `build → staging` policy from §4.
2. Make `get-approved-promotions.ps1` filter on sourceEnv. During the transition accept `stable` and `build`; after the cutover accept only `build` (plus `test` if D2 ever applies to mpt).
3. Pilot one low-risk mpt web component end to end through 5685 → 5602 staging.
4. Cutover window:
   - Switch on `release/*` auto-create.
   - Remove or gate the two `push-stable-release.ps1` steps in `marketplace-changelogs.yaml` (L1098–1135, DEV + PROD instance).
   - Bulk-reject the 11 non-deployable `stable` candidates ("stable track retired; not a deployable component").
   - Re-queue the remaining real ones from build, as in Phase 3.
   - Delete the mpt `stable → staging` policy once it is drained.
5. **Unchanged:** 5462 dev+test, `dev → test` and `staging → prod`. 5399's master/release/5 manifest bumps keep running; they are the input to 5462 and the release branches.

### Phase 6 — Retire "Staging Candidate" in InfraPortal
**Preconditions**
- No `stable` events for 7 days on any product. Check with `GET /api/deployments/recent/{product}/stable?since=…`.
- No open candidates touching `stable`.
- No policy references `stable`.

**Steps**
1. **Small InfraPilot change:** a `retired` flag on environment settings (`EnvironmentConfigDto`).
   - Honored by the state matrix, product summaries, analytics env lists, work-item "testable in", and the environment and policy pickers.
   - History stays visible on service history and event detail.
   - This is the reversible alternative to purging or merging (D3).
2. Mark `stable` retired.
3. Update `knowledge/` (`environments-and-tracks`, `delivery-tracks`, `promotion-policies`, `deploy-event-sources`), `playbooks/promotion-stuck-pending.yaml`, the tutorial seed (`scripts/tutorial/seed-tutorial.ps1`) and the comment in `PromotionRoute.tsx:21`.

### Phase 7 — Cleanup after one release cycle with no fallback
- Delete ADO 5617 and 5657 definitions. Export their run history first if anyone wants it.
- Remove the blob upload and the `changelogs` container once OQ4 is closed.
- Verify that the generic `mpt-release-promotion` webhook (all products → ADO incoming webhook) has no consumer, then delete it. No YAML in mpt-release or marketplace references it.
- Fix the release/5 copy of `push-stable-release.ps1` or delete it with its callers.

## 6. Decisions needed

| # | Decision | Recommendation |
|---|---|---|
| D1 | Is marketplace's **dev** in scope? Making mpt dev promotion-driven needs a new consumer in marketplace, and 5462 deploys dev and test in one run, so test would need a new trigger to stay "as is". | **Defer.** Do only Phase 5 (stop the stable feed, `build → staging`). Handle mpt dev as its own project. |
| D2 | Keep `mpt-support-tools`' `test → staging`? It is a promotion into staging that is not created from an artifact. | **Keep.** Its master-only services have no other way into staging. |
| D3 | `stable` history: retire flag (hidden, kept), purge, or leave the column visible? | **Retire flag** (Phase 6.1). It is reversible and keeps the audit trail. |
| D4 | Owner of `mpt-extension-operations` and `mpt-web-currency`. Both are product `mpt` and sit in both the marketplace manifest and mpt-release. | Confirm which repo actually deploys each environment today, and drop them from the other. |
| D5 | Order of products | mpt-release products first (Phases 1–4, low risk, parallel run already proven), then mpt (Phase 5). |

## 7. Verification checks per phase (InfraPortal API)
- Stuck promotions: `GET /api/promotions?status=Approved`, filter `sourceEnv=build`. Nothing should be older than about 15 min.
- Dev coverage: every `/api/builds?product=<p>&branch=refs/heads/ma` build (substring match, so main and master) since the cutover has a dev deploy event for its version.
- Staging intake: every release/* build since the cutover has exactly one `build → staging` candidate, and none on `stable → staging`.
- Stable is quiet: `GET /api/deployments/recent/<p>/stable?since=<cutover>` returns nothing.
- Untouched edges: the counts of `dev → test` and `staging → prod` candidates created per day stay within their normal range.
