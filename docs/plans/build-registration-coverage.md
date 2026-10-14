# Migration Plan: Build Registration Coverage

**Status:** Draft / for review
**Snapshot:** 2026-10-14. Sources: prod InfraPortal (`/api/deployments/state`, `/api/builds`,
product overrides, promotion policies), marketplace `master` and `release/5` (`manifest.json`,
`modules.yaml`, `.gitmodules`), Azure DevOps `softwareone-pc/MPT` (254 build definitions, their
YAML on `master` and `release/5`, recent runs and timelines), GitHub `softwareone-platform`
workflows, `ops-build-templates-aks-releases`, `ops-build-templates` and `softwareone-platform/.github`.
**Relation to other plans:** this finishes Phase B of [feature-branch-builds.md](feature-branch-builds.md)
(exit criterion: "every main build appears in the registry with a valid digest") and extends it to
`release/*` builds and to components that do not ship a container.

---

## 1. Where we are

| Scope | Registered | Not registered |
|---|---|---|
| marketplace `master` `manifest.json` (current version of each component) | 38 / 57 | 19 |
| marketplace `release/5` `manifest.json` | 35 / 56 | 21 |
| Services tracked in InfraPortal under `mpt`, `mpt-extensions`, `mpt-support-tools`, `ops` | 58 / 80 have at least one build | 22 have none |

Share of the version currently running in each environment that has a registered build (same four
products): dev 80%, test 83%, stable 64%, staging 75%, prod 79%.

Products `cloudiq` (80 services), `prism` (33) and `pyracloud` (175) have **no** registered builds;
they are built outside the MPT templates (§3 category K).

Registration only makes a build *deployable* where a `build → *` promotion policy exists. Today
that is `mpt-extensions` (all → dev; `mpt-extension-aws-management` → stable) and
`mpt-support-tools` (all → dev, all → staging). For product `mpt`, registration is observability
only until its Phase E cutover.

## 2. What already registers

Six pipeline families call `POST /api/builds` today. Anything on one of them, building on the
right branches, is covered.

| Family | Where the step lives | Services |
|---|---|---|
| AKS backend, `publish-steps.yaml@releases/v15` | `MPT/ops-build-templates-aks-releases` | mpt-audit, mpt-billing, mpt-procurement, mpt-spotlight, mpt-tasks, swo-platform, swo-pyraproxy, swo-extension-mscsp, swo-extension-adobe (ADO 5488), swo-extension-aws (ADO 5489) |
| `component-pipeline.yaml@releases/v1` | `MPT/ops-mpt-infra` | mpt-notifications, mpt-currency, mpt-scheduler |
| `common-build/extends(-py).yml` | `MPT/ops-build-templates@refs/heads/feature/build_template`, wraps v15 publish-steps | mpt-export, mpt-extension-procurement, -producthub, -nav-billing, -scrm, -adobe, -installation, -usage-reporting, -delivery |
| Micro-frontend `frontend/client-portal/v2` | `MPT/ops-build-templates` master (PR 150228, 2 Sep) | all 11 mpt-web-*; swo-web-accounts, -audittrail, -billing, -catalog, -commerce, -dashboard, -invitation, -notifications, -program, -purchasewizard; swo-web-header and mpt-extensions-ui-bootstrap via `publishbuild-job.yml`. swo-web-errorpages and swo-web-oneheader-container register on master only (§3 C) |
| GitHub `reusable-ci.yml` | `softwareone-platform/.github` (needs `app.product` in `.github/ci-config.yaml`) | mpt-extension-aws-management, mpt-extension-platform-support, mpt-csp-pricing-fix, mpt-interim-renewals, mpt-marketo-sync, mpt-nav-stats, mpt-nav-tools, mpt-platform-stats, mpt-swo-powerapps, mpt-weekly-digest |

The Python extension repos on GitHub (`swo-adobe-vipm-extension`, `swo-aws-extension`,
`mpt-adobe-vipm-ef-extension`, ...) only tag releases in GitHub Actions. Their images are built,
and registered, by ADO pipelines in project MPT that point at the GitHub repo.

## 3. Catalog of gaps, grouped by how to fix them

Size: XS = config or a one-line change, S = a small PR per repo, M = template work, L = a new track.

### A. Publish stage missing; PRs already open (5 services) [S each]

| Service | ADO def | PR (→ master) | PR state |
|---|---|---|---|
| mpt-extensions | 5508 | 150593 | open since 2 Sep, no votes, 18 commits behind |
| mpt-helpdesk | 5535 | 150594 | Peter Warner and Supernova approved; Spartans pending; 28 behind |
| swo-auth | 5303 | 150595 | open, 2 behind |
| swo-extension-nav | 5296 | 150596 | open, 3 behind |
| swo-header | 5517 | 150597 | open, 4 behind. The stage is commented out on master with a `release/4` condition |

All five already use AKS `releases/v15` and include `acr-creds-group.yaml`, so the
`marketplace.deployment` group is already authorized. Product resolves to `mpt`, which matches
their deploy events. The PRs use the plan's condition
(`master || startsWith(release/) || Manual`).

Steps:
1. Merge master into each PR branch, get the team approvals, complete.
2. Open a cherry-pick PR to `release/5`. The release branch YAML has no publish stage either,
   which is why release/5, staging and prod show no builds for these services.
3. swo-header only: change ADO definition 5517's default branch from the deleted
   `feature/move-to-mpt` to `master`.
4. Verify that the first master run and the first release/5 run each show
   "Register build in InfraPortal" → HTTP 201.

**Status 2026-10-14:** all five master PRs were squash-merged at 08:34–08:37 UTC. Release/5
cherry-picks (branch `feat/register-builds-in-infraportal-r5`, each compiled against release/5 with
the ADO preview API): mpt-extensions 152299, mpt-helpdesk 152300, swo-auth 152301,
swo-extension-nav 152302, swo-header 152303. The first swo-auth master build (8895199) failed in
component tests, before publish. Definition 5517's default branch is still `feature/move-to-mpt`.

### B. Publish job condition skips release branches (2 broken, 3 latent) [XS each]

| Service | Condition on `master` and `release/5` | Effect |
|---|---|---|
| mpt-public-catalog (5359) | `eq(Build.SourceBranch, 'refs/heads/release/*')`. `eq` does not glob | Publish job is *skipped* on every release build (timeline of run 8893829). release/5 5.9.6, staging/prod 5.9.3 are not registered |
| mpt-extension-operations (5628) | `eq(..., 'refs/heads/release/4')` | release/5 build 5.9.1 (30 Sep) skipped publish. Its release/5 manifest is still `5.9.0` |
| mpt-procurement (5540), swo-extension-mscsp (5165), mpt-notifications (release/5 YAML) | `eq(..., 'refs/heads/release/5')` | Works today, breaks on `release/6` |

Fix: `startsWith(variables['Build.SourceBranch'], 'refs/heads/release/')` on both branches (the
mpt-billing/mpt-audit form), then queue a release/5 run of the two broken ones.

**Opened 2026-10-14:** mpt-public-catalog 152289 (master) / 152290 (release/5);
mpt-extension-operations 152291 / 152292. The three latent ones are not yet done.

### C. Micro-frontend v2 pipeline not triggered on `release/*` (2) [XS each]

| Service | Finding |
|---|---|
| swo-web-errorpages (5532) | `trigger:` is `master` only, on both branches. Release builds run through the **legacy** definition 5215 (v1 template, no registration; last run 30 Sep). release/5 = 5.9.0, prod = 5.0.8 |
| swo-web-oneheader-container (5519) | `trigger:` is `master` only, so release/5 has never built since the cut. release/5 = 5.9.0, prod = 5.0.13 |

Fix: add `release/*` to `trigger:` in the YAML on `release/5`, where ADO reads the CI trigger, and
on master for future branches. Queue a release/5 run. Retire 5215 once it is confirmed nothing
relies on its direct `frontend/client-portal/deploy.yml` deploy.

**Opened 2026-10-14:** swo-web-errorpages 152293 (master) / 152294 (release/5);
swo-web-oneheader-container 152295 / 152296.

### D. Pipeline registers, but has not built since registration went live (rebuild) [XS]

| Service | Last build on the default branch | Note |
|---|---|---|
| mpt-extension-operations (5628) | master 6.0.5, 16 Jun | do after B and E |
| mpt-extensions-ui-bootstrap (5560) | master 6.0.3, 3 Aug (its release/5 5.9.1 is registered) | |
| ops-dotnet-ci-sample (GitHub) | main, 5 Aug | sample repo; rebuild or exempt |

A rebuild of the same commit yields the same `git describe` version. Chart and image pushes are
skipped when present, and registration is idempotent. The import-build re-import produces no diff,
so nothing redeploys.

### E. Product mismatch (1 blocking, the rest hygiene) [XS, InfraPortal admin]

- **mpt-extension-operations**: `extensionServices` in publish-steps v15 files its builds under
  `mpt-extensions`, but its deploy events (marketplace SDP, release-track) are under `mpt`. No
  override exists, so a build-sourced promotion would fail `source_deploy_missing`. Add the
  override `mpt-extension-operations → mpt`, the same pattern as the existing
  mpt-web-currency, mpt-web-publiccatalog, swo-web-notifications and mpt-export "Wrong assignment"
  rows. Remap its stray `mpt-extensions` dev row.
  **Override added 2026-10-14** (id `14ed66ff-e894-4bab-a44b-acd784e94641`, catch-all → `mpt`).
  The remap has not been applied: its preview would move 78 deploy events and 1 open promotion from
  `mpt-extensions` into `mpt`.
- mpt-extension-platform-support: a stale `mpt` dev row (0.1.0-5) next to the real
  `mpt-extensions` one. Add an override → `mpt-extensions` and remap.
- Stray build rows filed under `mpt`. ADO rows from before product resolution landed (17–18 Aug):
  mpt-extension-nav-billing, -procurement, -delivery, probably one swo-extension-mscsp row.
  GitHub rows from before `app.product` was set: mpt-extension-aws-management (10 Sep),
  mpt-csp-pricing-fix ×5 (2–4 Sep; its override only catches `mpt-jenkins-tools`),
  mpt-marketo-sync ×2 (2 Oct). Remap with an override or delete.

### F. Bespoke or legacy frontend pipelines; migrate (3) [S–M]

| Service | Today | Recommended path |
|---|---|---|
| swo-web-shell (5511) | Bespoke `build-pipeline.yml`: a single `build:release`, zips `dist` + `api` (SWA), uploads to blob. Does not fit v2's TEST/RELEASE split | Extract the push+register steps of `v2/publishbuild-job.yml` into a step template and call it from 5511. Authorize `marketplace.deployment` and `shared.servicePrincipal-INFRASTRUCTURE` for 5511. Then retire 5114 (legacy, still runs PR builds) and 5195 (SWA, no runs) |
| swo-web-healthcheck (5104) | v1 `frontend/client-portal/build.yml` + `deploy.yml`, self-deploys to TEST only; in `sdp.exclude` | Standard MFE: move to `v2/validate.yml` + `v2/build.yml` |
| swo-web-maintenence (5223) | Static page pushed straight to a Static Web App (`AzureStaticWebApp@0`), no build step; in `sdp.exclude` | Add a minimal publish+register job (zip `src`) and keep the SWA deploy; or move to v2 |

### G. Package libraries: no template can register a package yet (7) [M template + S each]

| Ecosystem | Services | Pipeline today |
|---|---|---|
| NuGet | mpt-library-framework (5341); swo-lib-dotnet-appinsights (5119, last build Aug 2025), swo-lib-dotnet-servicebus (5097, Aug 2025) | 5341 extends `nuget-libraries-templates` (only on AKS `releases/v13`). The two swo-lib pipelines have their own "Deploy nuget package" stage on v13 helper templates |
| npm | swo-design-system (5102), swo-web-libraries (5174), swo-lib-playwright-e2e (5153, Dec 2025) | v1 frontend build + own publish; playwright is bespoke |
| PyPI | mpt-cli-adobe-plugin (5257) | bespoke `poetry publish` |

The registry already accepts a build without `artifactRef`/`artifactDigest`. GitHub `reusable-ci`
explicitly rejects `nuget`/`npm` components ("no build engine yet"), so the gap exists on both
CI systems.

Steps:
1. Decide the manifest shape for packages: `artifacts[].type` = `nuget-package` | `npm-package`
   | `pypi-package`, with feed, package id and version.
2. Add a "register package build" step template. It generates BuildMetadata and POSTs it; no ACR
   push is needed.
3. **Make it opt-in** (a `registerBuild` parameter, default `false`) in the NuGet template. The
   ten `*-nuget` client-package pipelines (mpt-billing-nuget, mpt-extensions-nuget, ...) use the
   same template under the service's repo name. Their version would collide with the service's
   own `(product, service, version)` row, and the upsert would replace that build's manifest.
4. v13's `acr-creds-group.yaml` lacks `marketplace.deployment`. Add the group to the package job
   and authorize it on each of the seven pipelines.
5. Wire the seven pipelines and queue the three dormant ones. Check that the registered version
   equals the manifest's `git describe` version (several manifests pin `5.9.0`).

### H. No build pipeline at all; create one or exempt (2) [decision]

- **swo-dynamic-translations**: an Azure Functions app (`host.json`). There is no build
  definition in ADO project MPT, and `modules.yaml` has build and deploy `null`. Frozen at 5.9.0 since 21 Apr. If it
  is deployed (manually?), give it a pipeline (zip → OCI → register). If not, drop it from
  `manifest.json` and `modules.yaml`.
- **swo-new-react-template**: scaffold repo, nothing deploys. Exempt it in the coverage check
  (§5) or drop it from the manifest.

### I. Third-party image (1) [S]

- **ops-litellm**: upstream `BerriAI/litellm` 1.94.0, onboarded by hand through an mpt-release
  manifest (pipeline ref `manual-onboarding`). The repo has no workflows. Cheapest fix: a one-line
  `Dockerfile` (`FROM` the pinned upstream digest), a `.github/ci-config.yaml` with
  `app.product: ops`, and the standard `reusable-ci` caller. It then builds, pushes and registers
  with no custom code. Separately, its test deploy fails on a missing `secrets.masterKey`.

### J. Retired or frozen; clean up instead of fixing (2) [XS]

- **swo-web-requests**: definition 5099 disabled, last build Apr 2026, not in
  `manifest.json`/`.gitmodules`, yet InfraPortal still shows it in prod/staging (5.0.7) and test
  (6.0.1).
- **mpt-extension-finops**: repo `mpt-finops-extension` archived, definition 5490's last build was
  Apr 2026, still shown in all five environments (prod 5.1.12).

Confirm decommissioning with the owners, then remove or hide their state rows so they stop
counting as gaps. In the same pass, disable superseded definitions: 5688/5689 mpt-nav-stats (moved
to GitHub; 5688's default branch `add-cron-cache-refresh` is malformed), 5316 mpt-interim-renewals
(moved to GitHub), 5414 mpt-extension-operations-release (v13, failing on release/5 on 10 Oct),
5216 swo-extension-adobe-legacy (v12), and later 5215, 5114 and 5195 (categories C and F).

### K. Other products, outside the MPT templates (L, separate track)

| Product | Services | Built in |
|---|---|---|
| cloudiq | 80 | `dev.azure.com/crayongroup/CustomerPlatform` (Crayon org) |
| prism | 33 | `dev.azure.com/rhipedevelopment/PRISM Platform` (classic releases) |
| pyracloud | 175 | `softwareone-pc/PyraCloud`; a one-off import from `versions.json`, nothing since 12 Jun |

These pipelines cannot consume the MPT templates. The plan: a portable "register build" task
(script + task group), one API key per product with only `build:register`, and a pilot service per
org. Confirm first that pyracloud is still live.

### Hardening: registers today, but fragile

1. Nine pipelines pin `ops-build-templates@refs/heads/feature/build_template` (13 commits ahead
   and 8 behind master). Merge it, cut a release ref, and repoint the consumers.
2. The `eq release/5` conditions from category B.
3. MFE pipelines reference `MPT/ops-build-templates` with no `ref:`, so every master change hits
   all ~25 MFEs at once. Pin them to `releases/v10`.
4. The `extensionServices` default list in publish-steps v15 has drifted: six of its 15 entries
   are redirected by overrides (mpt-export, mpt-web-currency, mpt-web-publiccatalog,
   swo-web-notifications → `mpt`; mpt-nav-stats → `mpt-support-tools`; ops-litellm → `ops`),
   and mpt-extension-operations is wrong (category E). Align the list so overrides stop being
   load-bearing.
5. mpt-extension-scrm builds in both GHA `reusable-ci` (`app.product: mpt`) and ADO 5687
   (common-build). Keep one, and fix the product in `ci-config.yaml`.
6. Both registering templates curl oras from GitHub releases with no retry, so a GitHub 504
   fails the publish and the build is never registered. That happened three times in the 30 days
   to 14 Oct; two of those runs were never retried. The metadata artifact step also fails every
   stage re-run with "artifact already exists". Fix PRs opened 2026-10-14:
   ops-build-templates-aks-releases 152317 (→ `releases/v15`) and ops-build-templates 152318
   (→ `master`).

## 4. Migration waves

Each wave is independently shippable. Service-repo YAML changes need a PR to `master` **and** a
cherry-pick to `release/5`: ADO reads triggers and conditions from the branch being built.

| Wave | Content | Who | master / release/5 coverage after |
|---|---|---|---|
| 0: data and config (day 1) | E overrides + remaps; J confirmations and state cleanup; 5517 default branch | InfraPortal admin | 38/57 · 35/56 (gaps become actionable) |
| 1: merge what exists (week 1) | A: 5 PRs + release/5 cherry-picks. B: 2 condition fixes. C: 2 trigger fixes. D: queue 2 rebuilds | Service teams (Spartans, Tajfun, Supernova, Unito and Vikings) | **45/57 · 44/56** |
| 2: frontends (weeks 2–3) | F: swo-web-shell, swo-web-healthcheck, swo-web-maintenence | MFE/template owners | 48/57 · 47/56 |
| 3: packages (weeks 2–4, parallel) | G: template step, then 7 pipelines, then rebuild the dormant ones | Template owners + library teams | 55/57 · 54/56 |
| 4: edge cases | H decision (pipeline or exemption), I ops-litellm, D ops-dotnet-ci-sample | Owners / ops | **57/57 · 56/56** (exemptions count as covered) |
| 5: hardening + guardrail | Hardening list 1–5; coverage check (§5); retire the superseded definitions | Platform | holds at 100% |
| Track K | cloudiq, prism, pyracloud onboarding | Product owners | separate |

staging and prod converge as release/5 builds get promoted. Versions built before 17 Aug (for
example prod mpt-extension-operations 5.0.21, swo-web-errorpages 5.0.8) stay unregistered until
replaced. Backfill is not recommended; nothing needs to deploy them from the registry.

After Wave 4, the next step is Phase E for product `mpt`: seed `build → dev` (and later edges)
policies, which no `mpt` service has yet.

## 5. Verification and guardrail

- Per service: `GET /api/builds?service=<svc>&version=<manifest version>` returns a row with
  `artifactDigest`, and the run log shows "Register build in InfraPortal" with HTTP 201/200.
- Guardrail (Wave 5): a daily check that loops over every component in `manifest.json` on
  `master` and `release/5`, minus an explicit exemption list (swo-new-react-template, plus
  anything decided in H), and alerts when `(service, version)` is missing from the registry. Run
  it from a marketplace pipeline or as an InfraPortal admin view, e.g. "deployed but never
  registered", built on the `deployments[]` join the builds API already returns. Without it,
  the next copied pipeline regresses silently.

## 6. Decisions needed

| # | Question | Recommendation |
|---|---|---|
| Q1 | Should libraries (NuGet/npm/PyPI) be registered? | Yes, as package builds without OCI refs (category G) |
| Q2 | swo-web-healthcheck / swo-web-maintenence: stay self-deploying, outside SDP? | Register either way; whether SDP deploys them is a separate call |
| Q3 | Is swo-dynamic-translations deployed anywhere? | Find the owner. Pipeline if yes, drop from the manifest if no |
| Q4 | Are mpt-extension-finops and swo-web-requests decommissioned? | Confirm, then clean up their state |
| Q5 | ops-litellm via a wrapper Dockerfile + reusable-ci? | Yes |
| Q6 | Backfill pre-registration versions? | No |
| Q7 | Track K ownership (cloudiq / prism / pyracloud) | Name an owner per product before any template work |
