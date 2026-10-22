# Moving mpt services from the marketplace SDP to mpt-release

Services move one at a time and keep product `mpt`. The gap analysis and the list of what is ready
are in mpt-release `docs/marketplace-sdp-migration-gaps.md`; this page covers InfraPortal and the
per-service cutover, and keeps the log. `mpt-spotlight` is the first.

## The rule

Exactly one deployer per service per environment, switched in one sitting. The August–September
pilot ran both, and reconcile reverted SDP's deploys dozens of times per service.

## InfraPortal: wired once, never per service

Every `mpt` approval goes to both deployers. Each one skips the services it does not own:

| Receiver | Subscription | Skips |
|---|---|---|
| SDP (ADO 5685/5686) | `mpt-promotion-approved`, unchanged | services in marketplace `modules.yaml` `sdp.exclude` |
| mpt-release (Deploy Registered Build, Promote Single Build Manifest) | `mpt-release deploy-build` and `GithubPromotion`, products += `mpt` | services in mpt-release `components.yaml` `disabledComponents` |

[`migrate-mpt-services.cutover.py`](migrate-mpt-services.cutover.py) makes the three changes. It
does a dry run by default; `--apply` executes and `--undo` reverses.

- **Policy `mpt` `build → dev`**: product-wide, auto-approved, auto-created from `refs/heads/master`,
  `sourceRequiresDeploy: false`. SDP never reads dev promotions. A candidate for a service SDP
  deploys is closed by SDP's own dev deploy of that version.
- **`mpt-release deploy-build`**: products += `mpt`.
- **`GithubPromotion`**: products += `mpt`.

Filters are include-only and AND-ed, so per-service routing would need dedicated subscriptions,
plus an edit on every migration. Wiring by product keeps InfraPortal out of each migration.

**What it costs.** Every `mpt` approval now starts runs on both sides, and most of them deploy
nothing. With about 13–18 `mpt` master builds a day (build registry, 2026-10-19 to 10-21):
- each build's auto-approved `build → dev` candidate starts a no-op Deploy Registered Build run in
  mpt-release, while spotlight is the only enabled service;
- it also starts the 5685/5686 promotion-branch runs, which end at "Nothing to release";
- SDP's `dev → test` and `staging → prod` approvals start Promote Single Build Manifest runs that
  skip the disabled component.

`GithubPromotion` is a relay function that failed 537 of its 1,292 deliveries (2026-10-22).
InfraPortal retries a failed delivery (30 s, 2 m, 10 m, 1 h, 4 h).

**Prerequisites before applying.**
- mpt-release `disabledComponents` lists every service SDP still deploys. A missing one would be
  landed and deployed by mpt-release too.
- Promote Single Build Manifest checks it before reading the source stage, so an SDP service's
  approval is a skip, not a failed run.

Both are in mpt-release #1652.

## Per service

1. **marketplace:** the service goes into `sdp.exclude` (master and release/5), and its changelog
   triggers are commented out.
2. **mpt-release:**
   - seed `<env>/<service>/` at the version running in each environment, with the registered build
     manifest pulled from ACR by its digest and an `instance.yaml`;
   - remove the service from `disabledComponents`.

   Reconcile redeploys a component that has no `deployment-status.yaml`, even at the running version
   (`[RERUN]`). Seed one, recording SDP's last deploy, wherever no redeploy is wanted.
3. **InfraPortal:** nothing.

Merge 1 and 2 together, marketplace first. A promotion approved in between is skipped by both sides
and has to be redone. Use `POST /api/promotions/from-build` for a build, not the bulk resend, which
re-dispatches old promotions.

**Undo:** add the service back to `disabledComponents`, then revert the marketplace change.

## Not covered yet

- **Teams card on deploy.** The `deployment.created` Teams Logic App matches pipeline title
  `marketplace-sdp`, so deploys by mpt-release post no card (gap B8).
- **Teams release notes.** InfraPortal release notes for `mpt` have no Teams subscription. SDP's
  per-environment versions report covers only what SDP deploys (gap B6).
- **Daily scan.** `mpt-project scan` (the daily 09:00 run of 5399) still bumps migrated services in
  `manifest.json`. It also stamps the marketplace version into Jira Fixed In Build, next to
  mpt-release's `<service>@<version>` label.
- **`deploysOnApproval`.** It is display-only and stays `false` on the shared `mpt` `staging → prod`
  policy.

## Log

**2026-10-22: mpt-spotlight prepared**
- Running versions:
  - dev and test: `6.0.29-g63db207e`;
  - staging and prod: `5.9.2-g5e489eb5`.
- Both builds are registered with digest-pinned manifests, both charts have `appVersion` equal to
  the version, and no promotions are open.
- Seeding: dev has no `deployment-status.yaml`, so Reconcile Dev redeploys 6.0.29 once as
  mpt-release's first deploy of spotlight. Test, staging and prod are seeded with one.
- PRs:
  - marketplace !152683 (master) and !152684 (release/5);
  - mpt-release #1652, which also disables the six SDP services that were missing:
    `mpt-web-servernotifications`, `swo-auth`, `swo-extension-nav`, `swo-web-errorpages`,
    `swo-web-oneheader-container`, `swo-web-shell`.
- Nothing has been applied in InfraPortal yet.
