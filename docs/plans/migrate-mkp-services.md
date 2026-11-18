# Moving mkp services from the marketplace SDP to mkp-release

Services move one at a time and keep product `mkp`. The gap analysis and the list of what is ready
are in mkp-release `docs/marketplace-sdp-migration-gaps.md`. This page is the per-service checklist,
what the first migration (`mkp-spotlight`, 2026-10-22) taught, and the log.

Moving a service's source and build from Azure DevOps to GitHub is the next, separate step. It is
in [migrate-spotlight-to-github.md](migrate-spotlight-to-github.md), and the reusable procedure is
the runbook SS-CICD-RB-007 in the CTO & Platforms Confluence space.

## The rule

Exactly one deployer per service per environment, switched in one sitting. The August–September
pilot ran both, and reconcile reverted SDP's deploys dozens of times per service.

A migration is done when a **new version built from master** has gone the whole way through
mkp-release without anyone touching it. The cutover itself only proves the hand-over.

## InfraPortal: wired once, never per service

Every `mkp` approval goes to both deployers. Each one skips the services it does not own:

| Receiver | Subscription | Skips |
|---|---|---|
| SDP (ADO 5685/5686) | `mkp-promotion-approved`, unchanged | services in marketplace `modules.yaml` `sdp.exclude` |
| mkp-release (Deploy Registered Build, Promote Single Build Manifest) | `mkp-release deploy-build` and `GithubPromotion`, products include `mkp` | services in mkp-release `components.yaml` `disabledComponents` |

[`migrate-mkp-services.cutover.py`](migrate-mkp-services.cutover.py) made the three changes, and
they were applied on 2026-10-22:
- the policy `mkp` `build → dev` (product-wide, auto, `refs/heads/master`, `sourceRequiresDeploy:
  false`);
- `mkp` added to `mkp-release deploy-build`;
- `mkp` added to `GithubPromotion`.

It runs dry by default; `--undo --apply` reverses it.

Filters are include-only and AND-ed, so routing by service would need dedicated subscriptions and an
edit on every migration. Wiring by product keeps InfraPortal out of each migration.

**The cost** is no-op runs. Every `mkp` master build (about 13–18 a day) opens an auto-approved
`build → dev` candidate. Each one starts:
- a Deploy Registered Build run that skips the disabled component;
- 5685/5686 runs that end at "Nothing to release".

Each SDP `dev → test` and `staging → prod` approval starts a Promote Single Build Manifest run that
skips. A stage-to-stage approval also reaches Deploy Registered Build, which ignores it
(`sourceEnv` is not `build`).

## Per-service checklist

Set `SVC=<service>`. The commands need `DEPLOYMENTS_URL` and `DEPLOYMENTS_API_KEY`, `az` signed in,
and `helm`. Everything up to step 4 is read-only.

### 1. Is the service ready?

- **Mechanism and owners.** Check marketplace `modules.yaml`.
  - Is it a spec owner (`specifications`)? mkp-release `components.yaml` `openApi.specifications`
    already lists every one.
  - Is it a data-migration owner (`migrations.data`)? `migrations.data` in mkp-release must list it.
  - Check the blocked services in the gap doc (§3). `acx-auth`, `acx-extension-nav` and
    `acx-web-shell` have no build registration.
- **The version running in each environment.**
  ```bash
  for h in api.dev.example.com api.test.example.com api.staging.example.com api.platform.example.com; do echo -n "$h "; curl -s -H 'Accept: application/json' https://$h/public | jq -r --arg s $SVC '.components[$s]'; done
  ```
  The hosts are dev, test, staging and prod.
- **Each of those versions is a registered build with a manifest digest.**
  ```bash
  curl -s "$DEPLOYMENTS_URL/api/builds?product=mkp&service=$SVC" -H "X-Api-Key: $DEPLOYMENTS_API_KEY" | jq '.results[] | {id, version, branch, artifactRef}'
  ```
  The API returns at most 50 rows, newest first. Master versions are in dev and test; `release/*`
  versions are in staging and prod.
- **Chart `appVersion` equals the version.** If it doesn't, reconcile sees drift forever.
  ```bash
  helm show chart oci://acmetrixcr.azurecr.io/helm/$SVC --version <version> --username 00000000-0000-0000-0000-000000000000 --password "$(az acr login --name acmetrixcr --expose-token --output tsv --query accessToken)" | grep -E '^(version|appVersion):'
  ```
- **SDP's last deploy per environment**, for the seeded `deployment-status.yaml`:
  `GET /api/deployments/history/mkp/$SVC?environment=<env>&limit=1`.
- **No open promotions:** `GET /api/promotions?product=mkp&service=$SVC&status=Pending`, then the
  same for `Approved` and `Deploying`. An approval in flight during the switch is skipped by both
  sides.
- **Leftovers from the pilot.** If `<env>/$SVC/` already exists in mkp-release (`mkp-export`,
  `mkp-web-currency`, `mkp-web-publiccatalog`, `acx-web-notifications`), it holds an old version.
  The seed replaces it, and `Save-EnvironmentBuildManifest` archives the old one.

### 2. marketplace PRs (master and release/5)

- Add `$SVC` to `sdp.exclude` in `modules.yaml`.
- Comment out its `/master` and `/stable` triggers in `pipelines/marketplace-changelogs.yaml`, as
  for `mkp-spotlight`.
- Keep it in `components.services`. SDP's OpenAPI step bundles every service listed there at the
  version in `versions.json`, which mkp-release keeps current.
- Make the master commit first. It cherry-picks onto a branch from `release/5` without conflicts.
  Staging and prod SDP runs read release/5's `modules.yaml`.

### 3. mkp-release PR (seed and enable)

From an mkp-release checkout on a new branch, run:
```bash
pwsh pipelines/common/scripts/Import-SdpComponent.ps1 -ComponentName $SVC -DryRun
```
Then run it again without `-DryRun`. The script does everything below, after first running the
step 1 checks itself (it stops on any of them). It commits nothing, so review `git diff`, commit and
open the PR. By hand, it amounts to this.

For each environment, write `<env>/$SVC/`:
- **`build-metadata.yaml`**: the registered manifest, byte for byte. Pull it from the build's
  `artifactRef` (`oras pull`, or the ACR REST API with `az acr login --expose-token`) and check its
  sha256 against the digest. Land it with `Save-EnvironmentBuildManifest` (module
  `BuildManifestStaging`), as Deploy Registered Build does.
- **`instance.yaml`** from `New-ComponentInstanceFile`. The defaults are right for every
  marketplace service: `mp-platform` namespace, `helm/<component>` chart, product from the
  manifest's `spec.product`.
- **`deployment-status.yaml`** recording SDP's last deploy, in every environment that must not
  redeploy. Reconcile redeploys any component without one, even at the running version (`[RERUN]`).
  Leave it out of dev only: the takeover redeploy there is the first real test of the mkp-release
  path.

Then remove `$SVC` from `disabledComponents` in `components.yaml`.

`-RedeployEnvironments` picks the environments that get no `deployment-status.yaml` (default: dev).
An environment whose `versions.json` does not list the service is skipped.

### 4. Cut over

1. Merge the two marketplace PRs. SDP stops deploying the service.
2. Merge the mkp-release PR straight after. A push to `<env>/**` starts each environment's
   reconcile.
3. Within about 3 minutes:
   - Reconcile Dev has a successful `Deploy Helm ($SVC, <version>)` job;
   - test, staging and prod show `Deploy Helm` skipped;
   - InfraPortal has a succeeded `mkp` deploy event for dev;
   - `versions.json` is unchanged;
   - reconcile has committed dev's `deployment-status.yaml`.

### 5. Done: a new master build goes through

Merge something to the service's master. A version bump is enough. Spotlight's 6.0.31 took about
20 minutes from registration to test. In that time:
- `build → dev` is auto-approved, then Deploy Registered Build lands it and Reconcile Dev deploys
  it;
- `dev → test` is auto-approved, then Promote Single Build Manifest copies it and Reconcile Test
  deploys it;
- both candidates are `Deployed`, and the public `versions.json` of dev and test shows the new
  version;
- for a spec owner, `Publish OpenAPI specifications` runs its Azure login and publish steps (not
  just the "bundle changed?" check), and ADO 5321 is queued by the environment's identity.

The next `release/*` build then goes through `build → staging` and `staging → prod` with the usual
approvals.

## Findings from mkp-spotlight

| Finding | What to do |
|---|---|
| The gap doc said a no-diff landing deploys nothing. Reconcile still redeploys a component that has no `deployment-status.yaml`. | Seed one wherever no redeploy is wanted. |
| With product-wide wiring, `disabledComponents` is mkp-release's only guard. Six SDP services were missing from it. | Before each migration, check that every service SDP deploys is disabled. The check is `services ∪ web ∪ extensions − sdp.exclude` against the merged `disabledComponents`. |
| Promote Single Build Manifest failed, rather than skipped, a disabled component with no directory or a stale one. | Fixed in mkp-release #1653: it now checks disabled first. |
| A service's first build after the switch can fail for reasons that have nothing to do with the migration. Spotlight's first master build failed NuGet audit (NU1903, `System.Security.Cryptography.Pkcs` via `Microsoft.Data.SqlClient`) because versions float (`7.*`) and `TreatWarningsAsErrors` is on. | Before cutting over, run `dotnet restore` (or `dotnet list package --vulnerable --include-transitive`) on master **and** release/5. Fix by pinning the patched transitive version where it enters: mkp-spotlight !152695 / !152697 pinned Pkcs `10.*` and `Microsoft.OpenApi.Readers` `1.6.*`. Other services with SqlClient and WireMock.Net 1.7 will hit the same. |
| The reconcile OpenAPI publish and the API-model trigger had never run for real before spotlight. | Both worked first time (dev and test, 2026-10-22). Nothing more to prepare for later spec owners. |
| mkp-release squash-merges PRs and deletes the branch. | Start a follow-up from `origin/main`. Pushing to the merged branch recreates it with the pre-squash history. |
| `mkp-project scan` (the daily 09:00 run of 5399) still bumps excluded services in `manifest.json` and stamps the marketplace version in Jira Fixed In Build. | Harmless for deployment, so leave it. `sdp.exclude` cannot gate it: `acx-web-maintenence` is excluded from SDP but deploys through its own pipeline, which the scan tracks. |
| The 5685/5686 promotion branches used to take every approved `mkp` promotion, including excluded services. | marketplace !152683 drops `sdp.exclude` services, so an approval of only those ends at "Nothing to release". |

## Moving a service to `mkp-extensions` as well

The extension services also change product, like `mkp-extension-operations` and
`acx-extension-mscsp` before them. Changing product adds four things to the checklist:

- **Build pipeline.** The publish step names the product, so new builds register as
  `mkp-extensions` (and their manifests carry `spec.product: mkp-extensions`):
  - `aks-docker-templates/publish-steps.yaml` takes `product:`;
  - `frontend/client-portal/v2/publishbuild-job.yml` takes `product:`; pipelines that build with
    `v2/build.yml` need ops-build-templates "pass the InfraPortal product through build.yml"
    merged first.
  - Change master **and** release/5: release builds are what reach staging and prod.
- **InfraPortal override and remap**, with
  [`migrate-extension-services.cutover.py`](migrate-extension-services.cutover.py). A catch-all
  override files anything still posted as `mkp` under `mkp-extensions`: SDP deploy events, builds
  from a pipeline not yet changed, and reconcile's events for the seeded manifests, which still
  say `mkp`. The remap moves history, including pending candidates, so a promotion finds its
  source deploy under the same product. The override can be deleted once no environment runs a
  manifest with `spec.product: mkp`.
- **Approvals leave the SDP when the override is written.** `mkp-promotion-approved` is
  `mkp`-only, so from then on every approval of the service reaches mkp-release only. Write the
  override in the cutover sitting, never earlier.
- **Import with `-Product mkp-extensions`**, after the remap, since builds, deploy history and
  promotions are then all filed there. mkp-release `jira.fixedInBuild.products` includes
  `mkp-extensions`, so Fixed In Build keeps working.

Pending candidates no longer block a migration. `Import-SdpComponent -AllowPendingPromotions`
seeds each source environment at the version it promotes, so an approval after the hand-over
promotes through mkp-release. An Approved `build → dev` is carried over too: the SDP never acts on
dev promotions, and mkp-release skipped it while the service was disabled. Deploy it afterwards with
`migrate-extension-services.cutover.py --dispatch-dev`. Any other Approved, and any Deploying,
candidate still stops both the import and the cutover script.

For another service, pass `--services a,b`. `--dispatch-dev --version V` deploys a given registered
build rather than the newest approval. `--dispatch-dev` never sends a version older than, or equal
to, the one mkp-release `main` already declares for dev.

## Findings from the extension services

| Finding | What to do |
|---|---|
| The service pipeline PRs merged before the override. Their master builds registered under `mkp-extensions`, were auto-approved `build → dev`, and mkp-release skipped them while disabled. They stayed Approved, which both scripts treated as in flight. | Merge the service PRs last, as planned. If they go early anyway, the scripts carry those candidates over, and `--dispatch-dev` deploys the newest per service after the seed merges. |
| `GET /api/promotions?service=` matches substrings: `mkp-extensions` also returns `mkp-extensions-ui-bootstrap`. | Filter on the candidate's `service` field. Import-SdpComponent and the cutover script do. |
| InfraPortal's resend of approved webhooks is per product and target environment. For `mkp-extensions` it would also resend `mkp-extension-aws-management`'s stale approvals and move its environments back. | Dispatch Deploy Registered Build (`workflow_dispatch`, digest-pinned manifest) per candidate instead. |
| A same-version takeover deploy does not roll pods. Config from the Helm Secret only reaches a pod when its container next starts, and a pod already crash-looping fails Helm-Deploy's readiness check. | Before the cutover, look at restart counts (`kubectl get pods`). A takeover marked failed for this reason clears with the next real rollout. Reconcile will not retry it on its own: the status file exists and the version matches. |
| Reconcile Dev runs queue behind each other, and each redeploys from the commit that triggered it. With web deploys taking about 10 minutes, five dispatches took about 90 minutes to drain. | Expect it. Dispatch everything at once rather than waiting between services. |

## Helm readiness for GitHub (2026-11-18 audit)

A service built on GitHub deploys with the shared chart `ops-helm-charts/charts/acx-service` and a
`deploy/deployment-manifest.yaml` in its build metadata, as mkp-spotlight will
([migrate-spotlight-to-github.md](migrate-spotlight-to-github.md)). This audit checked every
mkp-release Helm component for what stops that. "own" means a per-service chart today.

| Component(s) | Chart | Blocker |
|---|---|---|
| nav-stats, csp-pricing-fix, interim-renewals, marketo-sync, nav-tools, platform-stats, weekly-digest, aws-management, platform-support | acx-service@2.2.0 | none |
| mkp-extensions, -producthub, -procurement, -nav-billing, acx-extension-mscsp | own | ADO repos; `apipool` |
| mkp-extension-adobe, -installation, -usage-reporting, -delivery | own | repo name ≠ component name |
| mkp-extension-scrm | own | manifest at the repo root; image component `api`; its build is disabled. With `main` in the `mkp` `build → dev` policy, re-enabling it auto-deploys to dev |
| acx-extension-adobe, acx-extension-aws | own + alias Service | needs extra objects |
| acx-extension-nav | own + 4 workers + cli chart | needs multiple workers |
| mkp-extension-operations | config-only chart + weekly schedule | redesign as a cronjob |
| ops-litellm | third-party chart | stays on its own chart |

**Blockers, by owner:**

| Owner | Blocker | Blocks |
|---|---|---|
| `.github` (reusable-ci) | Names the service, the release folder and the image paths after the GitHub repository, not `app.name` | adobe, installation, usage-reporting, delivery, acx-extension-adobe/aws: their repository and component names differ |
| `acx-service` | No extra objects or Service aliases | acx-extension-adobe/aws |
| | No multiple workers | acx-extension-nav |
| | No config-only mode | mkp-extension-operations |
| | `values.schema.json` forbids `nodeSelector` / `tolerations` on `api` (`additionalProperties: false`), so `apipool` placement is lost | Every service that wants `apipool`, mkp-spotlight first (its decision 6). The fix would be `acx-service` 2.4.0 with the keys |
| mkp-release | Hard-codes the `acx-service` version it seeds, 2.2.0. The chart is already at 2.3.0 | Every new `acx-service` seed |
| ops-infra-terraform | `stack` is gone since fe0796e (2026-11-17). Each new data file needs `acr_publish: true`, or the ACR push fails with AADSTS700213 | Every repository that publishes to ACR |

**Do not revoke app `38449d1e`.** The chart publish and mkp-release's `deploy-registered-build.yml`
still log in to ACR as `38449d1e`. ops-infra-terraform `data/registry.yaml` names a deleted workflow
as its only consumer, which is wrong. Revoke it only after both have moved to `a5a2409f`.

**Deprecation candidates found:**
- `.github`: the schema blocks `helm`, `deployment`, `role` and `central_release`;
- `.github`: the reusable workflows with no callers: reusable-service-build,
  reusable-service-publish, reusable-docker-build and reusable-dotnet-ci;
- `.github`: the `dispatch_release` input alias;
- `acx-service`: the `dbup` block (mkp-spotlight's cli image only feeds it; `migrations.schema` is
  the replacement);
- mkp-release: the dead `pipelines/deploy/helm/scripts/Prepare-Helm-Values.ps1` and
  `Convert-FlatToNested.ps1`.

## Not covered yet

- **Teams card.** The `deployment.created` Teams Logic App matches pipeline title `marketplace-sdp`,
  so deploys by mkp-release post no card (gap B8).
- **Teams release notes.** InfraPortal release notes for `mkp` have no Teams subscription. SDP's
  per-environment versions report covers only what SDP deploys (gap B6).
- **`deploysOnApproval`.** It is display-only and stays `false` on the shared `mkp` `staging → prod`
  policy.

## Log

**2026-10-22: mkp-spotlight migrated (dev and test proven; staging and prod wait for the next release build)**
- **Before:**
  - dev and test ran `6.0.29-g63db207e`; staging and prod ran `5.9.2-g5e489eb5`;
  - both builds were registered with digest-pinned manifests, and `appVersion` equalled the version;
  - no promotions were open.
- **11:02, cutover:**
  - merged marketplace !152683 (master) and !152684 (release/5), then mkp-release #1652;
  - Reconcile Dev redeployed 6.0.29 (the takeover, deploy event at 11:05:48); test, staging and
    prod deployed nothing.
- **11:19:** mkp-release #1653 merged. It disables the six missing SDP services
  (`mkp-web-servernotifications`, `acx-auth`, `acx-extension-nav`, `acx-web-errorpages`,
  `acx-web-oneheader-container`, `acx-web-shell`) and makes promotions of disabled components a
  skip. The InfraPortal wiring was then applied.
- **Spotlight's first master build after the switch** failed NuGet audit. mkp-spotlight !152695
  fixed master; !152697 is the same fix for release/5.
- **12:36–12:53, `6.0.31-gaa3bd9ea` from master:**
  - registered, then auto-promoted `build → dev`;
  - Deploy Registered Build and Reconcile Dev deployed it, with the first real OpenAPI publish and
    5321 queued at 12:46;
  - then `dev → test`, Promote Single Build Manifest and Reconcile Test;
  - both candidates are Deployed, and `versions.json` and the public OpenAPI bundle (504 paths, 5
    spotlight) are updated in dev and test.
- **What later services skip:** the product wiring, completing `disabledComponents` and the #1653
  fix were one-off. A later service needs only steps 1–5 above.

**2026-10-22: the extension services migrated (dev and test proven; staging and prod wait for QA)**

Seven services move together, and to `mkp-extensions`: `mkp-extensions`, `mkp-web-extensions`,
`mkp-web-extensions-pages`, `mkp-extensions-ui-bootstrap`, `acx-extension-nav`,
`acx-extension-adobe` and `acx-extension-aws`. All seven ran under `mkp` only, deployed by
`marketplace-sdp` / `marketplace-sdp-prod`.

| Service | dev = test | staging | prod | Seeded | Pending (carried over) |
|---|---|---|---|---|---|
| mkp-extensions | 6.0.74-gb9b38302 | 5.9.41-g1142ab0e | 5.9.40-gcfa2f739 | all | staging → prod 5.9.41 |
| mkp-web-extensions | 6.0.23-g1bc70123 | 5.9.4-gfe0d8441 | 5.9.1-gd6e8c9b4 | all | staging → prod 5.9.4 |
| mkp-web-extensions-pages | 6.0.13-gfe29a3a0 | 5.9.1-gae99e43a | 5.9.1-gae99e43a | all | — |
| mkp-extensions-ui-bootstrap | 6.0.3-gb934f24d | 5.9.1-g8828f18e | 5.9.1-g8828f18e | staging, prod | — |
| acx-extension-nav | 6.0.19-gdd23bebb | 5.9.1-g182c494d | 5.9.1-g182c494d | dev, test | build → staging 5.9.2 |
| acx-extension-adobe | 6.0.304-g4fa98b86 | 5.23.186-g06dd58b6 | 5.23.176-g65a0b289 | all | staging → prod 5.23.186 |
| acx-extension-aws | 6.0.293-g3937a24e | 5.9.34-gcf350709 | 5.9.28-g92a56b29 | all | staging → prod 5.9.34 |

- **Not seeded:**
  - ui-bootstrap dev/test: 6.0.3 predates its build registration. Its next master build lands
    there through `build → dev`.
  - nav staging/prod: 5.9.1 was never registered. The pending 5.9.2 `build → staging` lands staging.
- **Checks:**
  - Dry runs of `Import-SdpComponent` (`-Product mkp`, `-AllowPendingPromotions`) passed for all
    seven: digests, chart `appVersion`, and deploy events outside dev.
  - `dotnet restore` is clean on master and release/5 for `mkp-extensions` and `acx-extension-nav`.
  - `acx-extension-adobe` and `-aws` build from `main`. The `mkp-extensions` `build → dev` default
    auto-creates from `main` as well as `master`.
- **Planned cutover order (one sitting):**
  1. `migrate-extension-services.cutover.py --apply`: overrides and remap. Approvals stop reaching
     the SDP here.
  2. `Import-SdpComponent -Product mkp-extensions -AllowPendingPromotions`, per service, with the
     environments above; commit the seed to the mkp-release PR.
  3. Merge the marketplace PRs (master, then release/5), then the mkp-release PR.
  4. Merge the ops-build-templates PR, then the service PRs (`product: mkp-extensions`, master and
     release/5). Their master merges are the builds that prove step 5.
- **14:14–14:37, merged out of order.** The PRs went in before steps 1–2:
  - the service PRs between 14:14 and 14:17, except `mkp-web-extensions` release/5 (!152716,
    14:37);
  - ops-build-templates !152708 at 14:15;
  - marketplace !152709 and !152710 at 14:18.

  From 14:18 the SDP no longer deployed the seven, and mkp-release still had them disabled, so
  nothing deployed them until #1655 at 14:47. The new master builds already registered as
  `mkp-extensions` and were auto-approved `build → dev`. mkp-release skipped them, so they stayed
  Approved.
- **14:38, overrides and remap** (`cutover.py --apply`). All history moved, and nothing was left
  under `mkp`, including the five pending candidates. Both scripts first needed two fixes:
  - an exact-service filter, because the promotions API matches substrings;
  - Approved `build → dev` treated as waiting, not in flight (InfraPilot #400, mkp-release #1655).
- **14:47, mkp-release #1655 merged**: the seed from `Import-SdpComponent -Product mkp-extensions
  -AllowPendingPromotions`, with the environments in the table.
- **14:51–14:53, takeover Reconcile Dev.**
  - Five components redeployed their running version successfully. ui-bootstrap has no dev
    directory yet.
  - `mkp-extensions` was marked failed, for a reason that predates the cutover. Its worker had been
    crash-looping since about 13:05 UTC on the SDP-rendered env:
    `FormatException` on `Messaging:Orchestrator:ConnectionString`.
  - Helm rev 201 rendered valid Service Bus strings. A same-version upgrade does not roll the pod,
    so Helm-Deploy saw the old pod in back-off (25 restarts) and failed.
  - The container restarted at 14:55 with the new env and stayed Ready.
- **15:09, `cutover.py --dispatch-dev --apply`.** One Deploy Registered Build run each for
  `mkp-extensions` 6.0.75, `mkp-web-extensions` 6.0.24, `-pages` 6.0.14, ui-bootstrap 6.0.4 and
  `acx-extension-aws` 6.0.295. `acx-extension-adobe` was skipped:
  - its 6.0.308 was approved after #1655 and went through on its own;
  - the older 6.0.306 was still Approved, so `--dispatch-dev` was changed to send only the newest
    per service and to skip a version already in dev (InfraPilot #401).
- **By 16:45, six of seven proven** (nav followed on 2026-10-23, below). A new master build of each
  went build → dev → test through mkp-release, and both candidates are Deployed:

  | Service | dev = test |
  |---|---|
  | mkp-extensions | 6.0.75-g2117d032 (dev 15:33, test 15:35) |
  | mkp-web-extensions | 6.0.24-ga59e5942 |
  | mkp-web-extensions-pages | 6.0.14-g82a2e782 |
  | mkp-extensions-ui-bootstrap | 6.0.4-g77773857 (its first build mkp-release deployed) |
  | acx-extension-adobe | 6.0.308-g67a00692 |
  | acx-extension-aws | 6.0.295-gc779b996 |

  The 6.0.75 rollout cleared the failed takeover record. No Deploy Registered Build, Promote Single
  Build Manifest, or Reconcile Dev/Test run of the seven failed.
- **`acx-extension-nav`.** Its master build 6.0.20 failed in coverlet ("Unable to read beyond the
  end of the stream"; all 301 tests passed). After a rerun it registered on 2026-10-23 at 07:42, and
  Deploy Registered Build landed it in dev at 07:43. Reconcile Dev deployed it at 07:47, and
  Promote Single Build Manifest and Reconcile Test deployed it to test at 07:53. Both candidates
  are Deployed, so all seven are proven.
- **Still open:**
  - **Staging and prod** wait for QA: the carried-over `staging → prod` candidates (`mkp-extensions`
    5.9.41, `mkp-web-extensions` 5.9.4, adobe 5.23.186, aws 5.9.34) and the `build → staging`
    candidates from the release/5 merges.
  - **The overrides** stay until no environment runs a manifest with `spec.product: mkp`. dev and
    test are clear; staging and prod clear with their next release builds.

**2026-10-23: finops and platform-support refiled; mkp-web-spotlight handed over (dev takeover done; waits for a master build)**

Only `mkp-web-spotlight` was still on the SDP. The other two needed their product fixed, not a
hand-over. The cutover script gained `--services` and `--version` for them (InfraPilot #406).

- **`mkp-extension-finops`: refiled only.**
  - It has been off the SDP since marketplace !150776 (MKP-24995, 2026-09-08; the ticket says
    "prod migration was completed").
  - Its repository, `acmetrix/mkp-finops-extension`, is archived (last push
    2026-04-22). It never registered a build: its last builds, in April, predate build
    registration. mkp-release cannot seed it, and it cannot be built.
  - The SDP's last Helm releases still run (dev and test 6.0.1-g5051f315, staging and prod
    5.1.12-gd41f9157), and nothing deploys them.
  - 08:06: catch-all override and remap to `mkp-extensions`. All 18 history entries moved, and
    nothing is left under `mkp`.
- **`mkp-extension-platform-support`: refiled, dev repaired.**
  - It was mkp-release's already. It builds from `main` with the organisation's reusable workflow
    and registers as `mkp-extensions`. With no release branch, it has no staging or prod path.
  - Dev had gone backwards. On 08-27 17:51, the old `main → dev` track (removed by mkp-release
    #1636 on 10-14) copied an older manifest, 0.1.0-5.750ae1dd with `spec.product: mkp`, over
    dev's 1.0.0-2. Reconcile deployed it on 09-17. Test kept 1.0.0-2.304b5996.
  - 08:06: override and remap (520 entries moved).
  - 08:06, `cutover.py --services mkp-extension-platform-support --dispatch-dev --version
    1.0.0-2.304b5996 --apply`. Deploy Registered Build landed it at 08:07, and Reconcile Dev
    deployed it at 08:10. Dev and test now both run 1.0.0-2.
  - The stale `build → dev` 1.0.0-1 closed. The stale `dev → test` 1.0.0-0 is still Approved while
    test runs 1.0.0-2. Reject it in InfraPortal: a bulk resend for `mkp-extensions` would push it
    into test.
- **`mkp-web-spotlight`: handed over, staying under `mkp`** (no override, no pipeline change).
  - The import dry run passed with no open promotions:
    - dev and test v6.0.38-ga8946f1f;
    - staging and prod 5.9.2-g78834469.

    The `v` prefix appears in both `versions.json` and the build registry, so they match.
  - Merged in one sitting: marketplace !152737 (master) at 08:10:03, !152738 (release/5) at
    08:10:14, then mkp-release #1658 (the seed) at 08:10:28.
  - Reconcile at `d34d3a47`:
    - test, staging and prod skipped Deploy Web;
    - Reconcile Dev's takeover Deploy Web of v6.0.38 succeeded at 08:24:56, about 10 minutes, the
      usual for a web deploy;
    - InfraPortal has a succeeded `mkp` deploy event from mkp-release, and dev's
      `deployment-status.yaml` is persisted.
  - Done once a new master build goes build → dev → test through mkp-release. The last master
    build was on 09-29.
