# Moving mpt services from the marketplace SDP to mpt-release

Services move one at a time and keep product `mpt`. The gap analysis and the list of what is ready
are in mpt-release `docs/marketplace-sdp-migration-gaps.md`. This page is the per-service checklist,
what the first migration (`mpt-spotlight`, 2026-10-22) taught, and the log.

## The rule

Exactly one deployer per service per environment, switched in one sitting. The August–September
pilot ran both, and reconcile reverted SDP's deploys dozens of times per service.

A migration is done when a **new version built from master** has gone the whole way through
mpt-release without anyone touching it. The cutover itself only proves the hand-over.

## InfraPortal: wired once, never per service

Every `mpt` approval goes to both deployers. Each one skips the services it does not own:

| Receiver | Subscription | Skips |
|---|---|---|
| SDP (ADO 5685/5686) | `mpt-promotion-approved`, unchanged | services in marketplace `modules.yaml` `sdp.exclude` |
| mpt-release (Deploy Registered Build, Promote Single Build Manifest) | `mpt-release deploy-build` and `GithubPromotion`, products include `mpt` | services in mpt-release `components.yaml` `disabledComponents` |

[`migrate-mpt-services.cutover.py`](migrate-mpt-services.cutover.py) made the three changes, and
they were applied on 2026-10-22:
- the policy `mpt` `build → dev` (product-wide, auto, `refs/heads/master`, `sourceRequiresDeploy:
  false`);
- `mpt` added to `mpt-release deploy-build`;
- `mpt` added to `GithubPromotion`.

It runs dry by default; `--undo --apply` reverses it.

Filters are include-only and AND-ed, so routing by service would need dedicated subscriptions and an
edit on every migration. Wiring by product keeps InfraPortal out of each migration.

**The cost** is no-op runs. Every `mpt` master build (about 13–18 a day) opens an auto-approved
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
  - Is it a spec owner (`specifications`)? mpt-release `components.yaml` `openApi.specifications`
    already lists every one.
  - Is it a data-migration owner (`migrations.data`)? `migrations.data` in mpt-release must list it.
  - Check the blocked services in the gap doc (§3). `swo-auth`, `swo-extension-nav` and
    `swo-web-shell` have no build registration.
- **The version running in each environment.**
  ```bash
  for h in api.s1.today api.s1.show api.s1.live api.platform.softwareone.com; do echo -n "$h "; curl -s -H 'Accept: application/json' https://$h/public | jq -r --arg s $SVC '.components[$s]'; done
  ```
  The hosts are dev, test, staging and prod.
- **Each of those versions is a registered build with a manifest digest.**
  ```bash
  curl -s "$DEPLOYMENTS_URL/api/builds?product=mpt&service=$SVC" -H "X-Api-Key: $DEPLOYMENTS_API_KEY" | jq '.results[] | {id, version, branch, artifactRef}'
  ```
  The API returns at most 50 rows, newest first. Master versions are in dev and test; `release/*`
  versions are in staging and prod.
- **Chart `appVersion` equals the version.** If it doesn't, reconcile sees drift forever.
  ```bash
  helm show chart oci://nlpapp0336cre.azurecr.io/helm/$SVC --version <version> --username 00000000-0000-0000-0000-000000000000 --password "$(az acr login --name nlpapp0336cre --expose-token --output tsv --query accessToken)" | grep -E '^(version|appVersion):'
  ```
- **SDP's last deploy per environment**, for the seeded `deployment-status.yaml`:
  `GET /api/deployments/history/mpt/$SVC?environment=<env>&limit=1`.
- **No open promotions:** `GET /api/promotions?product=mpt&service=$SVC&status=Pending`, then the
  same for `Approved` and `Deploying`. An approval in flight during the switch is skipped by both
  sides.
- **Leftovers from the pilot.** If `<env>/$SVC/` already exists in mpt-release (`mpt-export`,
  `mpt-web-currency`, `mpt-web-publiccatalog`, `swo-web-notifications`), it holds an old version.
  The seed replaces it, and `Save-EnvironmentBuildManifest` archives the old one.

### 2. marketplace PRs (master and release/5)

- Add `$SVC` to `sdp.exclude` in `modules.yaml`.
- Comment out its `/master` and `/stable` triggers in `pipelines/marketplace-changelogs.yaml`, as
  for `mpt-spotlight`.
- Keep it in `components.services`. SDP's OpenAPI step bundles every service listed there at the
  version in `versions.json`, which mpt-release keeps current.
- Make the master commit first. It cherry-picks onto a branch from `release/5` without conflicts.
  Staging and prod SDP runs read release/5's `modules.yaml`.

### 3. mpt-release PR (seed and enable)

From an mpt-release checkout on a new branch, run:
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
  Leave it out of dev only: the takeover redeploy there is the first real test of the mpt-release
  path.

Then remove `$SVC` from `disabledComponents` in `components.yaml`.

`-RedeployEnvironments` picks the environments that get no `deployment-status.yaml` (default: dev).
An environment whose `versions.json` does not list the service is skipped.

### 4. Cut over

1. Merge the two marketplace PRs. SDP stops deploying the service.
2. Merge the mpt-release PR straight after. A push to `<env>/**` starts each environment's
   reconcile.
3. Within about 3 minutes:
   - Reconcile Dev has a successful `Deploy Helm ($SVC, <version>)` job;
   - test, staging and prod show `Deploy Helm` skipped;
   - InfraPortal has a succeeded `mpt` deploy event for dev;
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

## Findings from mpt-spotlight

| Finding | What to do |
|---|---|
| The gap doc said a no-diff landing deploys nothing. Reconcile still redeploys a component that has no `deployment-status.yaml`. | Seed one wherever no redeploy is wanted. |
| With product-wide wiring, `disabledComponents` is mpt-release's only guard. Six SDP services were missing from it. | Before each migration, check that every service SDP deploys is disabled. The check is `services ∪ web ∪ extensions − sdp.exclude` against the merged `disabledComponents`. |
| Promote Single Build Manifest failed, rather than skipped, a disabled component with no directory or a stale one. | Fixed in mpt-release #1653: it now checks disabled first. |
| A service's first build after the switch can fail for reasons that have nothing to do with the migration. Spotlight's first master build failed NuGet audit (NU1903, `System.Security.Cryptography.Pkcs` via `Microsoft.Data.SqlClient`) because versions float (`7.*`) and `TreatWarningsAsErrors` is on. | Before cutting over, run `dotnet restore` (or `dotnet list package --vulnerable --include-transitive`) on master **and** release/5. Fix by pinning the patched transitive version where it enters: mpt-spotlight !152695 / !152697 pinned Pkcs `10.*` and `Microsoft.OpenApi.Readers` `1.6.*`. Other services with SqlClient and WireMock.Net 1.7 will hit the same. |
| The reconcile OpenAPI publish and the API-model trigger had never run for real before spotlight. | Both worked first time (dev and test, 2026-10-22). Nothing more to prepare for later spec owners. |
| mpt-release squash-merges PRs and deletes the branch. | Start a follow-up from `origin/main`. Pushing to the merged branch recreates it with the pre-squash history. |
| `mpt-project scan` (the daily 09:00 run of 5399) still bumps excluded services in `manifest.json` and stamps the marketplace version in Jira Fixed In Build. | Harmless for deployment, so leave it. `sdp.exclude` cannot gate it: `swo-web-maintenence` is excluded from SDP but deploys through its own pipeline, which the scan tracks. |
| The 5685/5686 promotion branches used to take every approved `mpt` promotion, including excluded services. | marketplace !152683 drops `sdp.exclude` services, so an approval of only those ends at "Nothing to release". |

## Not covered yet

- **Teams card.** The `deployment.created` Teams Logic App matches pipeline title `marketplace-sdp`,
  so deploys by mpt-release post no card (gap B8).
- **Teams release notes.** InfraPortal release notes for `mpt` have no Teams subscription. SDP's
  per-environment versions report covers only what SDP deploys (gap B6).
- **`deploysOnApproval`.** It is display-only and stays `false` on the shared `mpt` `staging → prod`
  policy.

## Log

**2026-10-22: mpt-spotlight migrated (dev and test proven; staging and prod wait for the next release build)**
- **Before:**
  - dev and test ran `6.0.29-g63db207e`; staging and prod ran `5.9.2-g5e489eb5`;
  - both builds were registered with digest-pinned manifests, and `appVersion` equalled the version;
  - no promotions were open.
- **11:02, cutover:**
  - merged marketplace !152683 (master) and !152684 (release/5), then mpt-release #1652;
  - Reconcile Dev redeployed 6.0.29 (the takeover, deploy event at 11:05:48); test, staging and
    prod deployed nothing.
- **11:19:** mpt-release #1653 merged. It disables the six missing SDP services
  (`mpt-web-servernotifications`, `swo-auth`, `swo-extension-nav`, `swo-web-errorpages`,
  `swo-web-oneheader-container`, `swo-web-shell`) and makes promotions of disabled components a
  skip. The InfraPortal wiring was then applied.
- **Spotlight's first master build after the switch** failed NuGet audit. mpt-spotlight !152695
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
