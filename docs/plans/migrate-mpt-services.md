# Moving mpt services from the marketplace SDP to mpt-release

Services move one at a time and keep product `mpt`. The gap analysis and the list of what is ready
are in mpt-release `docs/marketplace-sdp-migration-gaps.md`; this page is the per-service cutover and
its log. `mpt-spotlight` is the first.

## The rule

Exactly one deployer per service per environment, switched in one sitting. The August–September
pilot ran both, and reconcile reverted SDP's deploys dozens of times per service.

| Switch | SDP side (marketplace) | mpt-release side |
|---|---|---|
| Who deploys | `sdp.exclude` in `modules.yaml`, on master and release/5 | not in `components.yaml` `disabledComponents` |
| Desired state | — | `<env>/<service>/` seeded at the version running today |
| Approvals | the ADO subscription `mpt-promotion-approved` still gets them; `get-approved-promotions.ps1` / `get-approved-rollbacks.ps1` drop `sdp.exclude` services | `mpt/<service>` `build → dev` policy and the two `mpt-release … (mpt)` subscriptions ([`migrate-mpt-services.cutover.py`](migrate-mpt-services.cutover.py)) |

Why the InfraPortal side is shaped like this:

- **Filters are include-only and AND-ed.** `mpt-promotion-approved` cannot say "all mpt except X",
  so it stays as it is and marketplace filters. The mpt-extensions subscriptions cannot take one mpt
  service without a services filter that would cut off every extension, so the migrated services
  get two subscriptions of their own.
- **No `sourceEnv` filter.** The environment filter does the routing instead: `build → *` edges
  target dev and staging, so `deploy-build (mpt)` listens there; `dev → test` and `staging → prod`
  target test and prod, so `promote-single-build-manifest (mpt)` listens there. `mpt` has no
  `test → staging`; add staging to the second one if it ever gets one.
- **An empty services filter means every service.** The script never leaves one empty.
- **`build → dev` is per service.** A product-wide one would open dev candidates for every SDP
  service that nothing deploys. `build → staging` and `staging → prod` stay product-wide and shared
  with SDP services.
- **Direct dispatch, not the `GithubPromotion` relay.** Promote Single Build Manifest reads
  InfraPortal's envelope itself, and the relay function fails about 40% of its deliveries
  (537 of 1,292 on 2026-10-22).

## Seeding without a redeploy

Reconcile redeploys a component whose `deployment-status.yaml` is missing, even when the cluster
already runs the declared version (`[RERUN]` in `Get-DeploymentList.ps1`). A no-diff landing alone
is therefore not enough. Seed each environment with the registered build manifest of the version it
runs (pulled from ACR by the digest InfraPortal holds) and an `instance.yaml`. Add a
`deployment-status.yaml` recording SDP's last deploy wherever no redeploy is wanted.

For `mpt-spotlight`, dev is left without one on purpose: Reconcile Dev redeploys 6.0.29 once, which
is mpt-release's first Helm 4 takeover of the release, in the lowest environment and with no version
change. Test, staging and prod change nothing until their next promotion.

## Order

Do steps 2–4 in one sitting, with no spotlight promotion approved in between. Builds registered in
between get no `build → dev` candidate (the policy does not exist yet). Promote them afterwards with
`POST /api/promotions/from-build`, not with the bulk resend, which re-dispatches old promotions.

1. **Pre-flight.** No open promotions for the service. Its builds are registered with a manifest
   digest. Optional: a dry run of mpt-release **Publish OpenAPI Specifications** for dev. The
   reconcile publish path has never run past its "bundle changed?" check, and spotlight is the first
   spec owner mpt-release deploys.
2. **marketplace:** merge the master PR and the release/5 PR. SDP stops deploying the service.
3. **mpt-release:** merge the seeding PR.
4. **InfraPortal:** `python docs/plans/migrate-mpt-services.cutover.py --service <service> --apply`,
   with `MPT_RELEASE_DISPATCH_TOKEN` set the first time (it creates the two subscriptions).
5. **Verify.**
   - Reconcile Dev's deploy event arrives under product `mpt` with `run.workflowName` `Reconcile dev`.
   - `versions.json` is unchanged.
   - The next master build lands through `build → dev`, then `dev → test`.
   - The next release build lands through `build → staging`, and 5685 logs "Skipping … sdp.exclude".

**Undo:** add the service back to `disabledComponents`, run the script with `--undo --apply`, then
revert the marketplace PRs. SDP deploys it again on the next manifest bump.

## Not covered yet

- The `deployment.created` Teams Logic App matches pipeline title `marketplace-sdp`, so deploys by
  mpt-release post no card (gap B8).
- InfraPortal release notes for `mpt` have no Teams subscription; SDP's per-environment versions
  report covers only what SDP deploys (gap B6).
- `mpt-project scan` (daily 09:00 run of 5399) still bumps the service in `manifest.json` and stamps
  the marketplace version into Jira Fixed In Build, next to mpt-release's `<service>@<version>` label.
  It cannot use `sdp.exclude` for this: `swo-web-maintenence` is excluded from SDP but deploys through
  its own pipeline, which the scan tracks.
- `deploysOnApproval` is display-only. It stays `false` on the shared `mpt` `staging → prod` policy.

## Log

**2026-10-22 — mpt-spotlight prepared**
- Running versions: dev and test `6.0.29-g63db207e`, staging and prod `5.9.2-g5e489eb5`. Both builds
  are registered with digest-pinned manifests, and both charts have `appVersion` equal to the
  version. No open promotions.
- The marketplace PRs (master: `sdp.exclude`, triggers, promotion filtering; release/5: `sdp.exclude`,
  triggers), the mpt-release seeding and the cutover script were prepared. Nothing has been applied
  yet.
