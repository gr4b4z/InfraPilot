# Moving mpt-spotlight and mpt-web-spotlight from Azure DevOps to GitHub

Both services already deploy through mpt-release (since 2026-10-22/23, see
[migrate-mpt-services.md](migrate-mpt-services.md)). What moves now is the **source and the build**:
the repositories go to `acmetrix` on GitHub, governed by `ops-infra-terraform`, and are
built by the platform CI (`acmetrix/.github` `reusable-ci.yml`). The deployer stays the
same. The build still registers in InfraPortal under product `mpt` with the same service names, so
promotions, history and Jira "Fixed In Build" carry on.

**Done** means: a new version of each service, built by GitHub Actions from the default branch, has
gone build → dev → test through mpt-release untouched; the public OpenAPI bundle still publishes; and
a GitHub-built `release/5` build has reached staging.

## Status (2026-11-18)

**Decided (2026-11-17):**
- `master` becomes `main` (decision 1).
- History moves with `gh ado2gh` (decision 2).
- Owner is Kumo, from commits and PR approvals (decision 3).
- The OpenAPI spec is read from ACR (decision 4).
- Sonar and Dependency-Track are dropped for now (decision 5).
- Web builds go through ACR only (see "Web artifacts: ACR only").

**Decided (2026-11-18):**
- The GitHub build only has to suit mpt-release. ADO web-build features that serve only the SDP, or
  nothing, are deprecated rather than ported (see "Deprecated with the move").
- The repositories carry the least build code possible: `.github/workflows/build.yml` is a verbatim
  copy of `acmetrix/.github` `workflow-templates/ci-standard.yml`, plus
  `.github/ci-config.yaml`. Everything reusable lives in `.github`.
- GitHub zip builds register the web artifact shape ADO web builds already use, with the digest
  recorded at build time. mpt-release accepts only that shape (gap 1).
- Toolchain versions move into `ci-config.yaml` under a top-level `runtimes:` block (`dotnet`,
  `node`), so callers pass no inputs.

**In review (2026-11-18), none merged:**
- [acmetrix/.github#111](https://github.com/acmetrix/.github/pull/111): the
  web contract, `runtimes:`, the verbatim template. CI green.
- [mpt-release#1676](https://github.com/acmetrix/mpt-release/pull/1676) (draft): one
  web contract in `Download-Build-Artifact.ps1`. **Merge after #111.**
- [ADO ops-build-templates PR 153829](https://dev.azure.com/acmetrix-pc/MPT/_git/ops-build-templates/pullrequest/153829):
  the ADO web template drops what nothing reads.

**Done:**
- mpt-release deploys web packages from ACR only:
  [mpt-release#1672](https://github.com/acmetrix/mpt-release/pull/1672), merged 12:14 UTC.
  - Proven in dev the same day: all four web components were force-redeployed and pulled by digest
    from `acmetrixcr`, with no storage read (Reconcile Dev 44081823510).
  - A new registered build, mpt-web-extensions `6.0.28-g4f9a9a58`, then deployed to dev the
    same way.
  - All four show `succeeded` in dev at their desired versions (mpt-web-spotlight after a fresh
    Reconcile Dev, 44084431202).
- [mpt-release#1673](https://github.com/acmetrix/mpt-release/pull/1673), merged 12:37 UTC:
  `Import-PowerShellYamlModule` retries the PSGallery install. A truncated download had failed
  spotlight's first attempt.
- The OpenAPI spec now comes from ACR:
  [mpt-release#1674](https://github.com/acmetrix/mpt-release/pull/1674), merged 13:07 UTC.
  - The `components.yaml` entry has `image: mpt-spotlight/web-api`, and the spec is copied out of
    `<ACR>/<image>:<deployed version>`.
  - Dry runs of Publish OpenAPI in all four environments read it from ACR, each with the same
    sha256 as the uploaded blob (6.0.33 `4655e9cc…` in dev and test, 5.10.0 `1ef8dbed…` in staging
    and prod), and passed the "keeps every API" check. Runs: dev 44088939434, test 44089588229,
    staging 44089595970, prod 44089602209.
- Lesson for the runbook: rerunning a failed reconcile job redeploys it, but the post-deploy job
  reuses attempt 1's deployment record, so the status stays `failed`. Use a fresh Force Component
  Deployment + Reconcile Dev instead.

**Documentation, following the [SS] Documentation Framework** (CP space, internal wiki):
- **The runbook:** `SS-CICD-RB-007 - MPT - Migrate a service repository from Azure DevOps to GitHub`,
  a draft created 2026-11-17 as page 3405643777 under [SS] CI/CD & Pipelines - Runbooks, owned by
  Mateusz Świetlicki.
  - It follows Preconditions → Steps 1–8 → Validation → Rollback → Troubleshooting, and Step 8 is
    the developer steps.
  - Publish it before announcing the freeze.
  - **v4, 2026-11-18 (status Active):** the verbatim caller and `runtimes:`, `acr_publish: true` in
    place of `stack`, the web contract (deployed from ACR by the recorded digest), the merge order
    #111 → #1676, the `instance.yaml` edits in the same window as the first build, a "What the
    GitHub build no longer produces" note in Step 8, five new troubleshooting rows, and links to
    the three PRs.
  - **v5, 2026-11-18 (status Active):** repository naming. The GitHub repository and its data file
    take the mpt-release component name (Steps 2 and 3, with `<ado-repo>` in the import and lock
    commands), a "Service name not allowed" troubleshooting row, and a link to .github#113.
  - **v6, 2026-11-18 (status Active):** corrects v5's example. acx-extension-adobe and
    acx-extension-aws are already on GitHub (`acx-adobe-vipm-extension`, `acx-aws-extension`) and
    are in the `.github` allow-list, so they keep their names. The rename-at-import rule applies to
    services still on Azure DevOps.
- **The repos:** they only link it, via a README note.
  - mpt-spotlight: ADO PR 153777. Merge it after the runbook is published.
  - mpt-web-spotlight: ADO PR 153782, committed with `--no-verify` at the user's OK. Its PR build
    will likely fail on the 12 outdated `@acx/*` packages, so it needs a separate bump or a policy
    override.
- **The announcement:** it goes to the migration chat, not a document.
- **Old draft:** an earlier announcement-style draft on acmetrix.atlassian.net (page 8187838466)
  is to be deleted by hand. The Atlassian connector has no delete operation.

**Still open:**
- The approval count. Tier `high` means 2; mpt-extension-dcrm runs Kumo at 1 under an exception.
- `apipool` placement for mpt-spotlight (decision 6).

The platform team must also run `ado2gh`, as org owner or with the migrator role.

**Next, in order:**
1. **InfraPortal (gap 8): done 2026-11-17 13:16 UTC.** Policy `14445233-d0ae-4daa-8a78-ada778b28b34`
   (`mpt`, `build → dev`) now auto-creates from `refs/heads/master` and `refs/heads/main`. Every
   other field is unchanged, and no pending candidate was re-applied. The rollback copy is
   `mpt-build-dev-policy.before.json`: PUT it back with only `refs/heads/master`.
2. **ops-infra-terraform PR (gap 7, data files, import blocks):** drafted as
   [ops-infra-terraform#161](https://github.com/acmetrix/ops-infra-terraform/pull/161)
   (2026-11-17).
   - The PR holds both data files, `stack: nodejs` in the schema, and import blocks.
   - **Needs a rebase (found 2026-11-18):** ops-infra-terraform replaced `stack` with
     `acr_publish` in fe0796e (2026-11-17), and the schema now rejects `stack`. Rebase #161, set
     `acr_publish: true` in both data files and drop the `nodejs` schema change (gap 7).
   - Settings: owner Kumo, contributor team MPT_Developers, 2 approvals, `ci / summary` required
     on `main` and `release/*`.
   - `validate-data`, `terraform-test` and `test-scripts` pass. `plan` fails only with "Cannot
     import non-existent remote object" for the two repositories, as expected until `ado2gh` has
     created them. Mark it ready and merge after that, and after the `master` → `main` rename.
   **Chart comparison, done 2026-11-17.** I rendered two charts with the same inputs: the deployed
   `oci://acmetrixcr.azurecr.io/helm/mpt-spotlight:6.0.33-ga15a3b5f`, and `acx-service` 2.2.0 with
   `deploy/deployment-manifest.yaml` as base values. The values were placeholders built from dev's App
   Configuration key names and `base64encode` tags (no values or Key Vault read) and
   `version=6.0.33-ga15a3b5f`.

   Both produce the same 8 resources with the same names: Secret, ConfigMap, Service,
   Deployment, VirtualService, the hook Secret and ConfigMap, and the DbUp Job
   `mpt-spotlight-migrations`. Env, secrets, selectors, ports, probes, routes and the Job image
   are identical. The differences:
   - **Label `app.acxcloud.com/repository` is dropped** on every resource. Cosmetic unless
     something selects on it.
   - **`checksum/config` and `checksum/secret` pod annotations are added.** Pods restart when
     config changes, an improvement.
   - **Replicas stay 1 in practice.** `acx-service` reads `api.replicaCount`, which is
     `shared/api.replicaCount` = 1 in dev and unset in test, staging and prod (chart default 1). No
     environment has `mpt-spotlight/apis.*` overrides.
   - **The Acx Key Vault certificate is dropped:** the env var
     `Acx_Acx__AzureAd__Authentication__CertificatePassword` and the volume
     `ops-cluster-teamsetup-acx-keyvault-certificate`, which the releases/v1 generator
     injects into every API. Neither the spotlight code nor any of its `Mpt.*`, `Acx.*` or
     `Acmetrix.*` packages references them (scanned the restored DLLs), so this is safe.
   - **`nodeSelector: agentpool: apipool` is dropped.** releases/v1 forces it, and `acx-service`
     2.2.0 has no nodeSelector option, so the pod can land on any schedulable pool, as for the
     other `acx-service` services. A values override can't bring it back either: the chart's
     `values.schema.json` forbids `nodeSelector` and `tolerations` on `api`
     (`additionalProperties: false`, found 2026-11-18). Open as decision 6.
   - **The cli image only feeds the DbUp hook,** through the manifest's `dbup` block, which
     `acx-service` marks deprecated. Optional follow-up: move it to `migrations.schema`.
   **mpt-spotlight branch prepared, 2026-11-17:** local commit `4ab4a8e` on `feature/move-to-github`
   (from ADO `master` a15a3b5, not pushed). **2026-11-18:** `9fac249` on top makes
   `.github/workflows/build.yml` the verbatim `ci-standard.yml` and declares `runtimes.dotnet:
   "10.0.x"` in `ci-config.yaml`. It needs .github#111 before it goes to GitHub.
   - Contents: caller + `ci-config.yaml`, CODEOWNERS/SECURITY/PR template, Dockerfiles on
     `mcr.microsoft.com/dotnet/sdk:10.0` with one restore through the `nuget_config` secret, the
     sql-cache DDL in `SqlFixture`, `deploy/deployment-manifest.yaml`, and `helm/` + `pipelines/`
     removed.
   - Local results: unit 122, component 24, architecture 112 (+1 skipped) and integration 20 pass in
     Release. Both images build with the feed config as the secret, and the image's `api.json` is
     byte-identical to the deployed 6.0.33 spec.
   - Still to do: the `release/5` cherry-pick at cutover.

   **mpt-web-spotlight branch prepared, 2026-11-17:** local commit `5e2dccf` on
   `feature/move-to-github` (from ADO `master` a8946f1f), not pushed. Committed with `--no-verify`, at
   the user's OK, because of the hook problem below. **2026-11-18:** `5d894e4` on top makes the
   caller the verbatim `ci-standard.yml` (the `node_version` input is gone) and declares
   `runtimes.node: "22.x"` once, for the bundle build and the node test suites. It needs
   .github#111 before it goes to GitHub.
   - Contents: caller + `ci-config.yaml` (zip `web`, lint and unit suites with
     `kind: unit`, since the schema requires `kind` and has no lint kind), CODEOWNERS/SECURITY/PR
     template, the AcxCloud feed moved to its GUID path in `.npmrc` and the 29 lockfile URLs (gap
     5), and `pipelines/` removed.
   - Feed check: `npm ci` with GUID-only auth installs 1518 packages. The old name-path lockfile
     with the same auth fails with E401, which proves gap 5.
   - Local results: `lint`, `test:coverage` (274 tests) and `build:ci` pass. Not tested with npm 10,
     which CI uses (npm 11 locally).
   - **Blocked commit:** the worktree's `core.hooksPath` points at husky's raw hook, which can't
     execute. Run the husky way, the hook's `mp-cli update-packages --mode check` fails because 12
     `@acx/*` packages on master are outdated (master's existing state, not this change).
3. **Migration branches, prepared locally:** these can't be pushed until the GitHub repositories
   exist.
   - Both repositories: `ci-config.yaml` + caller.
   - mpt-spotlight: Dockerfiles (gap 3), the sql-cache fixture (gap 4), `deploy/deployment-manifest.yaml`,
     and the old-vs-new chart render.
   - mpt-web-spotlight: `.npmrc` + lockfile URLs, with `npm ci` checked locally (gap 5).
4. **Active ADO PRs** (today !152904 and !152697 into release/5, plus the doc PRs 153777 and
   153782): they move with the repositories and continue on GitHub. After the migration commit is
   on their target branch, each needs a `ci / summary` run (update the branch) and fresh approvals.

## Where things are today (checked 2026-11-17)

| | mpt-spotlight | mpt-web-spotlight |
|---|---|---|
| Repository | ADO `MPT/mpt-spotlight`: `master`, `release/5`, stale `release/4` (last commit 2025-10-01), 7 feature/poc branches | ADO `MPT/mpt-web-spotlight`: `master`, `release/5` |
| Build | Definition 5383, `pipelines/build.yml` on `ops-build-templates-aks-releases@releases/v15` + `ops-mpt-infra@releases/v1` `helm-init` | Definition 5608, `ops-build-templates` `frontend/client-portal/v2` `validate.yml` + `build.yml` |
| Produces | images `mpt-spotlight/web-api`, `mpt-spotlight/cli` (DbUp); chart `helm/mpt-spotlight` generated from the root `deployment-manifest.yaml`; OpenAPI spec uploaded to blob `api-swagger/mpt-spotlight-api.<v>.json` and `mpt-spotlight.v6-latest.json` | zip in `nltapp0762sda/web/mpt-web-spotlight/v6/…zip`, OCI `web/mpt-web-spotlight:<v>`, plus a RELEASE copy (`opssaartifcats`, `:<v>-release`) that mpt-release does not use: it substitutes `process.env.target` itself at deploy time |
| Quality gates | unit, component, architecture, integration tests (Testcontainers MsSql + Azurite; integration needs the `dotnet-sql-cache` global tool, which the ADO template installs); SonarCloud; Dependency-Track SBOM | lint, tests, audit; Dependency-Track SBOM |
| Versions | master `6.0.33-ga15a3b5f` (tag `6.0.0`), release/5 `5.10.0` (tag `5.10.0`) | master `v6.0.38-ga8946f1f` (tag `v6.0.0`), release/5 `5.10.0` (tag `5.10.0`) |
| Dead ADO files | `release.yml`, `run-smoke-tests.yml`, `cron.yaml` (mpt-release deploys) | `release.yml` |
| Other ADO definitions | 5446, 5447 (load tests, `poc/load-tests`) | — |
| Open ADO PRs | !152904 (MPT-27246 framework 5.9.14 → release/5), !152697 (deps pin → release/5) | none |

History on both default branches is linear (squash merges), so first-parent and full commit counts
are equal; this matters for versioning below.

## Target state

- `acmetrix/mpt-spotlight` and `acmetrix/mpt-web-spotlight`, `internal`, full
  history and tags, described by `ops-infra-terraform/data/repositories/<name>.yaml`.
- Each repository owns two build files and nothing else: `.github/workflows/build.yml`, a verbatim
  copy of `.github` `workflow-templates/ci-standard.yml` (required check `ci / summary`), and
  `.github/ci-config.yaml` (what to build, and the toolchain versions under `runtimes:`).
- A push to the default branch or `release/*` publishes to `acmetrixcr` and registers the build in
  InfraPortal (`POST /api/builds`). The existing policies then open `build → dev` (auto) and
  `build → staging` (QA and release approval), and mpt-release deploys as it does today.
- mpt-spotlight deploys with the shared chart `ops-helm-charts/charts/acx-service` and a
  `deploy/deployment-manifest.yaml` bundled into the build's OCI metadata, like the other
  GitHub-built services. It no longer uses a per-service chart. The manifest contract is the same
  as the ops-mpt-infra generator's, so the App Configuration keys stay valid.

### `mpt-spotlight/.github/ci-config.yaml`

```yaml
schema_version: 1
app:
  name: mpt-spotlight
  product: mpt
runtimes:
  dotnet: "10.0.x"              # SDK for the test suites; the projects target net10.0
components:                     # image path = <registry>/<repo>/<name>, the same paths ADO pushes today
  - name: web-api
    artifact: docker
    dockerfile: src/Mpt.Spotlight.Presentation.WebApi/Dockerfile
  - name: cli                   # DbUp image, referenced by deploy/deployment-manifest.yaml
    artifact: docker
    dockerfile: src/Mpt.Spotlight.Cli/Dockerfile
tests:
  - { name: unit,         kind: unit,         coverage: true, project: tests/Mpt.Spotlight.Tests.Unit/Mpt.Spotlight.Tests.Unit.csproj }
  - { name: component,    kind: component,    coverage: true, project: tests/Mpt.Spotlight.Tests.Component/Mpt.Spotlight.Tests.Component.csproj }
  - { name: architecture, kind: architecture,                 project: tests/Mpt.Spotlight.Tests.Architecture/Mpt.Spotlight.Tests.Architecture.csproj }
  - { name: integration,  kind: integration,  coverage: true, project: tests/Mpt.Spotlight.Tests.Integration/Mpt.Spotlight.Tests.Integration.csproj }
versioning:
  strategy: commit-count        # <tag>.<first-parent commits since>-g<sha8>: the ADO version shape
```

`runtimes.dotnet` replaces the caller's `dotnet_version` input (the reusable default is 8.0.x), so
the caller passes nothing and stays a verbatim copy of the template.

### `mpt-web-spotlight/.github/ci-config.yaml`

```yaml
schema_version: 1
app:
  name: mpt-web-spotlight
  product: mpt
runtimes:
  node: "22.x"                  # web build and node tests; what the ADO v2 template builds with
components:
  - name: web
    artifact: zip
    type: nodejs
    build_command: build:ci     # cross-env target=ci webpack --mode=production
    output_dir: dist
tests:                          # `kind` is required by the schema; there is no lint kind
  - { name: lint, kind: unit, runtime: node, command: lint }
  - { name: unit, kind: unit, runtime: node, command: test:coverage }
versioning:
  strategy: commit-count
```

`runtimes.node` covers the bundle build and the node test suites, so the component no longer
repeats `node_version` and the caller passes nothing. The bundle is pushed as
`acmetrixcr.azurecr.io/mpt-web-spotlight/web:<v>`, and that is its only copy (see below). The
build registers it as a `type: web` artifact with the digest it pushed.

### Web artifacts: ACR only (decided 2026-11-17)

A web build is published to ACR and deployed from ACR, and from nowhere else. No storage-account
zip, no RELEASE copy, no pipeline artifact. The migration drops the outside-ACR copies from the
mpt-release deploy path instead of carrying them over:

- **mpt-release reads web packages only from ACR, for every web component it deploys**, not just
  the GitHub-built one. Today that is mpt-web-spotlight, mpt-web-extensions,
  mpt-web-extensions-pages and mpt-extensions-ui-bootstrap. The blob download goes, with no
  fallback. A build without an ACR reference fails the deploy, and nothing silently falls back to
  storage.
- **Nothing needs moving into ACR first.** Checked 2026-11-17: every current and archived
  `build-metadata.yaml` of those four (16 + 16 files, every environment) carries `ociRef` and
  `ociDigest`, and all 17 distinct digests exist in `acmetrixcr` under `web/<component>`. So
  rollbacks to ADO-built versions keep working from ACR.
- **mpt-release ignores the outside-ACR fields** that ADO metadata still carries: `uri`/`blobUri`
  on `*.blob.core.windows.net`, `pipelineArtifactName`, and the `web-release` artifact.
  GitHub-built metadata has none of them.
- **One web contract for both CI systems (decided 2026-11-18).** GitHub zip builds register the
  shape ADO web builds already use, and set `metadata.labels.type: web` when every component is a
  zip:
  ```yaml
    - type: web
      name: <component>
      repository: <service>/<component>
      registry: <registry>
      tag: <version>
      uri: oci://<registry>/<service>/<component>:<version>
      ociRef: <registry>/<service>/<component>:<version>
      ociDigest: sha256:<hex>
  ```
  The build reads the digest back from ACR after the push, and fails the registration if it
  can't. mpt-release accepts only this shape, reads `ociRef` + `ociDigest`, and pulls by that
  digest. The deploy-time lookup of a tag's digest goes. Done by .github#111 and
  mpt-release#1676, in that order.
- **Outside this migration:** the ADO frontend template (`ops-build-templates`
  `frontend/client-portal/v2/publishbuild-job.yml`) keeps uploading to `nltapp0762sda` for now.
  The marketplace SDP still deploys about 30 frontends from it (`pipelines/jobs/deploy-web-steps.yaml`).
  That upload goes when the last frontend leaves the SDP; ops-build-templates PR 153829 marks it
  `DEPRECATED (SDP-only)`. The template's RELEASE copy (`opssaartifcats`, `:<v>-release`) has no
  reader in marketplace, mpt-release or ops-build-templates, and PR 153829 drops it.

## Deprecated with the move (decided 2026-11-18)

**The principles:**
- **The GitHub build only has to suit mpt-release.** An ADO web-build feature that serves only the
  SDP, or nothing, is deprecated, not ported.
- **The repository carries the least build code possible:** `.github/workflows/build.yml`, a
  verbatim copy of `.github` `workflow-templates/ci-standard.yml`, and `.github/ci-config.yaml`.
  Everything reusable lives in `acmetrix/.github`.

**What goes, and why:**

| Item | Why it goes | Where it goes |
|---|---|---|
| The RELEASE copy of the web zip (`opssaartifcats`, `:<v>-release`, the `web-release` artifact entry) | Unused. Both deployers rewrite `process.env.target` to `"release"` themselves | Never ported; dropped from the ADO template by PR 153829 |
| The version the build writes into the zip's `manifest.json` | Unused. Nothing reads it: the shell passes it through, and deploys read `versions.json` | PR 153829 |
| The build-metadata pipeline artifact; `blobUri`, the https `uri` and `pipelineArtifactName` | Unused. mpt-release reads web packages only from ACR (#1672), by `ociRef` + `ociDigest` (#1676) | PR 153829 (the web `uri` becomes `oci://`); #1676 ignores the rest |
| `release.yml` / `release-to-env.yml` in the ADO web templates | Unused. No pipeline definition uses them, and callers are pinned to `releases/v10` | PR 153829 |
| The Kendo UI licence (`KENDO_UI_LICENSE`) | Unused. No users on ADO or GitHub | .github#111 (reusable-ci) and PR 153829 |
| `blockOutdatedPackages` | Already a no-op | PR 153829 declares it one |
| The deploy-time tag → digest lookup | The build now records the digest | mpt-release#1676 |
| The zip upload to `nltapp0762sda` | SDP-only: read only by marketplace `pipelines/jobs/deploy-web-steps.yaml` | Marked `DEPRECATED (SDP-only)` by PR 153829; removed when the last frontend leaves the SDP. The account itself stays: it also holds the OpenAPI specs and bundle metrics |

mpt-spotlight's ADO `release.yml`, `cron.yaml` and `run-smoke-tests.yml` never ran (mpt-release
deploys), so there is nothing to port from them either.

**Not ported, still on ADO, and outside the rule.** These are neither unused nor SDP-only, so the
rule doesn't settle them. Decide each when a service that uses it migrates:
- npm audit;
- the `mp-cli` package check;
- bundle-size: only acx-web-billing uses it;
- Playwright `test:integration`: 4 SDP repositories use it on master, and reusable-ci has no
  support for it yet.

**Which PR does what (none merged):**

| PR | Does |
|---|---|
| [acmetrix/.github#111](https://github.com/acmetrix/.github/pull/111) | The web contract in reusable-ci (`type: web` with `ociRef` + `ociDigest`, `labels.type: web`); `runtimes:` in the schema and reusable-ci; `ci-standard.yml` usable verbatim; drops the Kendo secret; new `tests/build-metadata-test.sh`. CI green |
| [mpt-release#1676](https://github.com/acmetrix/mpt-release/pull/1676) (draft) | One web contract in `Download-Build-Artifact.ps1`: `ociRef` + `ociDigest` required, pulled by digest; the tag-resolve path removed. **Merge after #111**: until then a GitHub zip build registers no `web` artifact, and #1676 rejects it |
| [ADO ops-build-templates PR 153829](https://dev.azure.com/acmetrix-pc/MPT/_git/ops-build-templates/pullrequest/153829) | Removes the RELEASE variant, the `manifest.json` version, the metadata pipeline artifact, `blobUri` and `pipelineArtifactName`, the Kendo licence and `release.yml` / `release-to-env.yml`; the web `uri` becomes `oci://`; marks the `nltapp0762sda` upload `DEPRECATED (SDP-only)`; `blockOutdatedPackages` becomes a declared no-op |

## Gaps found, and the fix for each

These are things the platform CI does differently from the ADO templates. Each one breaks the
migration if nobody handles it.

1. **mpt-release deploys web packages from blob storage. Blocking for mpt-web-spotlight.**
   `pipelines/deploy/web/scripts/Download-Build-Artifact.ps1` (its only caller is the web job in
   `.github/workflows/reconcile-shared.yml`) downloads
   `web/<component>/<major>/<component>-<version>.zip` from `WEB_ARTIFACT_STORAGE_ACCOUNT`
   (`nltapp0762sda`, every environment). The reusable CI pushes the zip only to ACR. Fix in
   mpt-release, following the ACR-only rule above: replace the blob download with an `oras pull`
   for every web component.
   - **ADO builds:** pull `web/<component>@<ociDigest>` from the `web` artifact.
   - **GitHub builds (changed 2026-11-18):** #1672 resolved their `oci://…/mpt-web-spotlight/web:<v>`
     `uri` to a digest at deploy time, because the metadata carried none. Now the metadata pins it:
     .github#111 registers the same `type: web` artifact as ADO, with `ociRef` and `ociDigest`
     recorded at build time, and mpt-release#1676 accepts only that shape and drops the
     tag lookup. One contract for both CI systems. Merge #111 first.
   - **Both artifact shapes:** ADO pushes `application/vnd.acmetrix.web-package.v1+zip` with
     `<component>-<version>.zip`, GitHub pushes `application/vnd.acmetrix.webapp.zip` with
     `<name>.zip`. Take the single `application/zip` layer, whatever the file is called.
     `artifactComponent` in `instance.yaml` still selects the repository.
   - **Login:** reuse the existing ACR login pattern (`az acr login --expose-token`, data-plane
     roles), which reconcile's Helm jobs already use with the same identities.
   - **Cleanup:** remove `WEB_ARTIFACT_STORAGE_ACCOUNT` from the web job; the OpenAPI steps still
     use it for ADO-built specs.

2. **No OpenAPI spec upload. Blocking for the whole public bundle.** `Build-OpenApiBundle.ps1`
   throws when a deployed component's `api-swagger/mpt-spotlight-api.{version}.json` is missing, and
   then "the published bundle was left unchanged" for every service. The reusable CI has no such
   step. A job of the repository's own can't authenticate to Azure either, because the org's OIDC
   subject template only matches jobs that call `reusable-ci.yml`. The spec is already in the image
   (`/app/docs/api.json`, built by the Dockerfile's publish stage). Fix in mpt-release, in the same
   spirit as the ACR-only rule: for a GitHub-built component, `Build-OpenApiBundle.ps1` reads the
   spec straight from `mpt-spotlight/web-api:<version>` in ACR. Nothing new is written to the
   storage account. ADO-built services keep their blob specs until they migrate. Before dropping
   `mpt-spotlight.v6-latest.json`, find who reads it (nothing in mpt-release does).

3. **The Dockerfiles can't build on GitHub.** They start `FROM acmetrixcr.azurecr.io/dotnet/sdk:10.0`,
   but the reusable CI only logs in to ACR when it publishes, so PR builds can't pull it. That image
   also supplied the private NuGet feed config, and the projects need `Mpt.Framework.*`,
   `Mpt.Essentials.*`, `Acx.*`, `Acmetrix.AppInsights.Kubernetes` and the `Mpt.OpenApiCli`
   tool from it. Fix in the service: `FROM mcr.microsoft.com/dotnet/sdk:10.0`, and
   `RUN --mount=type=secret,id=nuget_config dotnet restore … --configfile /run/secrets/nuget_config`
   (the same for `dotnet tool install`). This only works on GitHub, so it lands with the migration
   PR, not before.

4. **The integration tests need `dotnet sql-cache`.** `SqlFixture` runs `dotnet sql-cache create …`,
   and the ADO template installed the global tool (`installSqlCacheTool: True`). The reusable test
   lane only runs `dotnet test`. Fix in the service: create the cache table with its DDL in the
   fixture (no tool), or add `.config/dotnet-tools.json` and run the tool through
   `dotnet tool restore`/`dotnet tool run`.

5. **The `npm ci` auth is likely to fail for `@acx`. To verify on the first PR build.** The repository's
   `.npmrc` and 29 lockfile `resolved` URLs use `acmetrix-pc.pkgs.visualstudio.com/_packaging/AcxCloud/…`
   (the feed **name**). `setup-package-feeds` writes the token for `…/_packaging/18b4703c-…/` (the
   feed **GUID**), in both host forms, and npm matches auth by URL path. Fix in the service: switch
   `.npmrc` and the lockfile URLs to the GUID form `feeds.yaml` uses, and check `npm ci` locally.
   The platform alternative is to emit the name form too. Every marketplace frontend will hit this.

6. **Versioning needs new tags.** `commit-count` turns tag `<prefix>X.Y` into `X.Y.<count>-g<sha8>`. The
   existing `6.0.0` / `5.10.0` tags would give `6.0.0.33-g…`. Add annotated **`v6.0`** (on the
   `6.0.0`/`v6.0.0` commit of master) and **`v5.10`** (on the `5.10.0` commit of release/5) in both
   repositories. The first GitHub builds then continue the ADO numbers: mpt-spotlight's first
   master build reads `6.0.35-g…` or later (checked 2026-11-18), and release/5 `5.10.1-g…` or later.
   Without the tag, `commit-count` falls back to `0.0.N`, so push the tags before the first build.
   - mpt-web-spotlight already has `v6.0.0` on that commit. `git describe` takes the newest
     annotated tag among several on one commit, so `v6.0` wins. Simulate it locally before pushing.
   - mpt-web-spotlight versions lose their `v` (`v6.0.38` → `6.0.39`). Only cosmetic after gap 1,
     except that the Jira "Fixed In Build" label of major `v6` won't be replaced once.
   - Release managers: the next stable minor on these two needs a `v5.11` tag (the ADO repos use
     `5.11.0`).

7. **Both data files need `acr_publish: true` (rewritten 2026-11-18).** The old gap, "add
   `nodejs` to the `stack` enum", is obsolete: ops-infra-terraform replaced `stack` with an
   explicit `acr_publish` flag in fe0796e (2026-11-17), and the schema now rejects `stack`.
   `acr_publish` is what creates the ACR OIDC subject template (`live/github/oidc.tf`). Without it
   the ACR push, image or zip, fails with AADSTS700213. #161 still says `stack: dotnet` /
   `stack: nodejs`, so rebase it and set `acr_publish: true` for both repositories.

8. **The InfraPortal branch filter.** `mpt` `build → dev` auto-creates only from `refs/heads/master`
   (`build → staging`: `refs/heads/release/*`). Both repositories move to `main` (decision 1), so
   add `refs/heads/main` to that one product-wide policy and keep `refs/heads/master` for the
   services still in ADO. Never add a per-service policy. Without this, the first GitHub build
   registers but no `build → dev` candidate opens. Applying it needs an Admin token
   (`PUT /api/promotions/admin/policies/{id}`).

9. **Toolchain versions lived in the caller (added 2026-11-18).** The reusable defaults are .NET
   `8.0.x` and Node `20.x`, while the ADO builds use .NET 10 and Node 22, so the first branches
   passed `dotnet_version` / `node_version` inputs and the caller stopped being a template copy.
   Fix in .github (#111): a top-level `runtimes:` block in `ci-config.yaml` (`dotnet`, `node`)
   that wins over the inputs. The caller then passes nothing. The inputs stay as fallbacks for
   callers that predate `runtimes:`.

**Accepted losses (decision 5):** SonarCloud and Dependency-Track aren't in the reusable CI. The
schema's `quality.sonar` and `security.sbom` exist but nothing implements them. GitHub Advanced
Security (code scanning, secret scanning, Dependabot) is switched on by Terraform instead. The
ADO-only OpenAPI lint step goes too.

## Decisions

1. **Default branch: decided 2026-11-17, `master` becomes `main` on import.** It's the org standard
   on all 34 Terraform-managed repositories, and the policy pattern from gap 8, added once, serves
   every later `mpt` service. The reusable CI treats `main` as implicitly release-bearing, so
   neither `ci-config.yaml` needs a `publish` block. Developers rename local clones
   (`git branch -m master main`, then set the new remote).
2. **How the history moves: decided 2026-11-17, GitHub Enterprise Importer (`gh ado2gh`).**
   - **What it moves:** git source with history, pull requests with their history, work-item
     links and attachments, and branch policies. User-scoped and cross-repo policies don't move,
     and neither do Git LFS objects (these repositories have none).
   - **Who runs it:** an org owner or a holder of the migrator role (`grant-migrator-role`). In
     practice that's the platform team.
   - **The command:**
     `gh ado2gh migrate-repo --ado-org acmetrix-pc --ado-team-project MPT --ado-repo <repo>
     --github-org acmetrix --github-repo <repo> --target-repo-visibility internal`.
   - **Repository name (decided 2026-11-18):** `--github-repo` is the mpt-release component name
     ([migrate-mpt-services.md](migrate-mpt-services.md), "Repository naming"). Spotlight already
     complies: `mpt-spotlight` and `mpt-web-spotlight` are both component names.
   - **It creates the repository itself**, and it does nothing if the name already exists. So
     Terraform must not create the repositories. The data-file PR adopts them afterwards with
     `import {}` blocks in `live/github/imports.tf` (the `.github` pattern).
   - **Default branch:** the importer keeps `master`, so rename it to `main` on GitHub
     (`POST /repos/{owner}/{repo}/branches/master/rename`) before the Terraform apply.
   - **Branches:** it migrates every branch, including the stale feature/poc branches and
     `release/4`. Delete them on GitHub afterwards if they aren't wanted.
   - **Branch policies** come across as GitHub branch protection. Remove those once Terraform's
     rulesets are attached, as with mpt-extension-dcrm's old ruleset.
   - **Afterwards:** read the "Migration Log" issue, reclaim mannequins (org owner only), and
     confirm that no org ruleset blocks the import (none is defined in Terraform).
   - **ADO side:** `gh ado2gh lock-ado-repo` makes each ADO repository read-only, which keeps the
     ADO PR links in commit messages and InfraPortal working. Use it rather than
     `disable-ado-repo`, which makes the repository unreadable.
3. **Owner: decided 2026-11-17, `Kumo`, from who maintains the code** (ADO history since
   2025-11-17, master + release/5; GitHub identity by SSO email).
   - **mpt-spotlight:** commits by Jerzy Los 28, Krzysztof Jakielaszek 12, Maciej Biernat 8. Of
     52 completed PRs, the ADO group `[MPT]\Kumo` approved 48. The top individual approvers were
     Biernat 22, Jakielaszek 20, Michal Walczak 16 and Los 13.
   - **mpt-web-spotlight:** commits by Deen Kishore 24, Los 21, Guilherme Meireles 7 and
     Biernat 8. `[MPT]\Kumo` approved 37 of 55 PRs. The top approvers were Biernat 27,
     Walczak 21, Meireles 13 and Tomasz Kondraciuk 10.
   - **On GitHub,** Los (`jml-pl`), Jakielaszek (`DrJakiellSWO`), Biernat (`mbeernut`) and
     Walczak (`GitchalWoo`) are all in the `kumo` team, and so is `dk1990`, probably Kishore (no
     SSO identity to confirm it). No other delivery team holds any of them.
   - **Write-access gap:** Meireles (`gbmeireles`) and Kondraciuk (`TomaszKondraciuk`) review
     the frontend often, but they're in no team that grants write (only `cto-transition-temp`).
     They need adding to `kumo` or `mpt_developers` to keep reviewing.
   - **Approval count:** not decided yet. Tier `high` means 2; mpt-extension-dcrm runs Kumo at 1
     under exception `PENDING-DCRM-001`.
4. **Where gap 2 is fixed: decided 2026-11-17, in mpt-release, from the image in ACR.** A
   platform CI feature that publishes the spec next to the build metadata may still be worth it
   once more .NET services migrate. Gap 1 is settled the same way.
5. **Sonar / Dependency-Track: decided 2026-11-17, dropped for now.** GitHub Advanced Security
   covers part of it, and either can come back when the platform CI implements `quality.sonar` /
   `security.sbom`.
6. **`apipool` placement for mpt-spotlight: open (2026-11-18).** releases/v1 pins the API pod to
   `agentpool: apipool`. `acx-service` 2.2.0 can't: its `values.schema.json` forbids `nodeSelector`
   and `tolerations` on `api` (`additionalProperties: false`), so the pod can land on any
   schedulable pool. The choice:
   - **Accept it,** as for the other `acx-service` services. Nothing else changes.
   - **Ship `acx-service` 2.4.0 with the keys** (`api.nodeSelector`, `api.tolerations`) and pin the
     spotlight `instance.yaml` to it. The same applies to every own-chart service that uses
     `apipool` (see [migrate-mpt-services.md](migrate-mpt-services.md), "Helm readiness for
     GitHub").

   Decide before the `instance.yaml` edits (Work by repository, mpt-release (c)), since they name
   the chart version.

## Work by repository

| Repository | Change | When |
|---|---|---|
| **mpt-release** | (a) web packages from ACR only, for all four web components (gap 1): **done**, #1672 + #1673, proven in dev. (b) OpenAPI spec read from the image in ACR (gap 2): **done**, #1674, proven by dry runs in all four environments | Done |
| | (c) `dev/` + `test/` `mpt-spotlight/instance.yaml`: `chartPath: charts/acx-service`, `chartVersion: 2.2.0` (or 2.4.0, decision 6). Without it the chart resolves to `helm/mpt-spotlight@<version>`, which doesn't exist for a GitHub build, so the deploy fails safely | In the same window as the first GitHub master build |
| | (d) `staging/` + `prod/` the same | In the same window as the first GitHub release/5 build, before its `build → staging` is approved |
| | (e) [#1676](https://github.com/acmetrix/mpt-release/pull/1676): one web contract, pull by the recorded digest | After .github#111, before the first GitHub web build |
| **acmetrix/.github** | [#111](https://github.com/acmetrix/.github/pull/111): the web contract, `runtimes:`, `ci-standard.yml` usable verbatim, Kendo secret dropped (gaps 1 and 9) | Before mpt-release (e) and before the migration PRs |
| **ops-build-templates** (ADO) | PR 153829: the ADO web template drops what nothing reads (see "Deprecated with the move") | Independent of the cutover |
| **ops-infra-terraform** | `data/repositories/mpt-spotlight.yaml` and `mpt-web-spotlight.yaml`, both with `acr_publish: true` (gap 7; #161 still has `stack` and needs a rebase), hand-authored like `mpt-extension-dcrm.yaml`: no `seed`/`template`, `product: mpt`, `tier: high`, `visibility: internal`, `owning_team: Kumo`, `contributor_teams: [MPT_Developers]` (the schema's product-devs rule, as in mpt-extension-platform-support), `default_branch: main`, ruleset with `required_status_checks: ["ci / summary"]`, `branch_rulesets` for `release/*`, security features; `import {}` blocks | Reviewed in prep; merged after the repositories exist |
| **mpt-spotlight** (GitHub PR to default branch, then a cherry-pick PR to release/5) | verbatim caller + `ci-config.yaml` with `runtimes.dotnet`; baseline files the health report checks (CODEOWNERS, SECURITY.md, PR template); Dockerfiles (gap 3); sql-cache (gap 4); `deployment-manifest.yaml` → `deploy/`, remove `helm/` and `pipelines/`; CLAUDE.md/README CI section; `dotnet restore` NuGet-audit check on both branches first (the 2026-10-22 lesson) | After import |
| **mpt-web-spotlight** (same two PRs) | verbatim caller + `ci-config.yaml` with `runtimes.node`; baseline files; `.npmrc` + lockfile URLs (gap 5); remove `pipelines/` | After import |
| **InfraPortal** | Add `refs/heads/main` to `mpt` `build → dev`, keeping `refs/heads/master` (gap 8): **done 2026-11-17**. Confirm the org-level `DEPLOYMENTS_URL` / `DEPLOYMENTS_API_KEY` are visible to the new repositories and the key allows product `mpt` with `build:register` (the GitHub builds registered so far are under `mpt-extensions` and `mpt-support-tools`) | Prep |
| **marketplace** (ADO, master + release/5) | `.gitmodules` for both submodules → `git@github.com:acmetrix/<repo>` (the existing GitHub-submodule convention), pointers to the GitHub commits. `sdp.exclude` and the commented changelog triggers stay | After cutover |
| **Azure DevOps** | Disable definitions 5383 and 5608; `gh ado2gh lock-ado-repo` for both repositories (read-only, still readable); decide on load-test definitions 5446/5447 | At cutover |

## Cutover sequence

The deployer doesn't change, so there's no double-deploy risk. The risk is a window with no build
at all, so keep the freeze short.

**Prep (no impact):**
1. Do a trial import into `<repo>-trial` for each repository, then delete it. Check that
   `master`, `release/5`, the tags and every active ADO PR arrived (`az repos pr list --status
   active` against `gh pr list --state open`). Active PRs are not closed before the move
   (decided 2026-11-17): the importer moves them with the branches. The docs don't say whether
   they arrive open, so the trial is what confirms it.
2. Merge mpt-release (a) and (b): both done on 2026-11-17. Then add `refs/heads/main` to the `mpt`
   `build → dev` policy. It's
   harmless before cutover, because no `mpt` build comes from `main` yet. Get the Terraform PR
   approved.
   **Prerequisites added 2026-11-18** (from the Helm audit):
   - **Merge order:** .github#111, then mpt-release#1676. #1676 rejects a GitHub zip build
     without the `type: web` artifact, which only #111 registers. Both before the migration PRs.
   - **Terraform:** rebase ops-infra-terraform#161 and set `acr_publish: true` for both
     repositories (gap 7). Without it the first publish fails with AADSTS700213.
   - **Decide `apipool` placement** (decision 6), since it fixes the chart version in the
     `instance.yaml` edits.
   - **The `instance.yaml` edits land in the same window as the first GitHub build** (steps 9
     and 11), never apart from it. Without them the chart resolves to a nonexistent
     `helm/mpt-spotlight@<version>` and the deploy fails safely.
   - **Tags:** the first GitHub build is `6.0.35-g…` or later once `v6.0` exists, and `0.0.N`
     without it (gap 6).
3. Prepare both migration branches locally. For mpt-spotlight, render the old chart
   (ops-mpt-infra `releases/v1` generator) against the new one (`acx-service` 2.2.0 with
   `deploy/deployment-manifest.yaml` and the same Prepare-Helm-Values inputs) for dev and test, and
   compare names, env, secrets, selectors, routes and the DbUp job image. This is the method from
   the common-Helm-template migration.

**Cutover (one sitting):**
4. Freeze merges in ADO. The last ADO master builds must be green and registered.
5. Platform team, per repository:
   - `gh ado2gh migrate-repo … --target-repo-visibility internal` (decision 2).
   - Rename `master` → `main`. This retargets the open PRs that targeted `master`.
   - Push the new `v6.0` / `v5.10` tags (gap 6).
   - Check the Migration Log issue, and that `main`, `release/5`, the tags and every active PR
     arrived. Reclaim mannequins.
   - Delete the stale feature/poc branches and `release/4` if they aren't wanted, but never the
     source branch of an open PR.

   Tell the team the new remote and the branch rename.
6. Merge the Terraform PR (with its `import {}` blocks). Apply rulesets, teams, the OIDC subject
   template and security features, then remove the branch protection the importer created.
7. Disable ADO definitions 5383/5608, and run `gh ado2gh lock-ado-repo` for both ADO repositories.
8. Open the migration PRs on GitHub. The PR build (`ci / summary`) proves tests, Docker and
   `npm ci` without publishing.
9. Check that .github#111 and mpt-release#1676 are merged, in that order. Merge mpt-release (c),
   then the two migration PRs, in the same window. Expect: publish, registration, `build → dev`
   auto-approved, Deploy Registered Build, Reconcile Dev, then `dev → test`.

**Stable track:**
10. Cherry-pick the migration commits onto `release/5` (GitHub PRs, one per repository). The merge
    registers `5.10.N` and opens `build → staging`.
11. Merge mpt-release (d) just before that promotion is approved. Staging → prod then follows the
    usual QA flow.

**Afterwards:**
12. Marketplace `.gitmodules` PRs (master, release/5). Log the outcome in this file and in
    mpt-release `docs/marketplace-sdp-migration-gaps.md`.

## Verification

- `ci / summary` green on PR and push. The push run's summary shows
  `mpt-spotlight/web-api`, `mpt-spotlight/cli` and `mpt-web-spotlight/web` pushed at the expected
  versions, and `register_build` logs HTTP 2xx.
- InfraPortal `list_builds` for both services shows a `github` build on the default branch, and a
  `build → dev` candidate appears and closes as Deployed.
- dev: helm release `mpt-spotlight` on `acx-service`, the DbUp job ran with
  `mpt-spotlight/cli:<new version>`, and `/acx-spotlight-api/` routes. The web app serves the new
  version (`versions.json`), with configuration substituted from App Configuration. The web job
  pulled it from `acmetrixcr.azurecr.io/mpt-web-spotlight/web` by digest, and nothing was read
  from a storage account. `dev/mpt-web-spotlight/build-metadata.yaml` has `labels.type: web` and a
  `type: web` artifact whose `ociDigest` is the digest the build pushed, and the deploy pulled
  exactly that one.
- Publish OpenAPI run succeeds with the spotlight spec taken from the `web-api` image.
- test reached by `dev → test` without manual steps. Later, the release/5 build reaches staging.

## Rollback

- **Before step 7:** re-enable the ADO definitions and keep building there. Remove the GitHub
  repositories from Terraform and delete them. `ado2gh` is not a delta migration, so a retry
  migrates again from scratch.
- **After it:** stay on GitHub and fix forward. To roll mpt-spotlight back to an ADO-built version
  in an environment, revert that environment's `instance.yaml` first. ADO builds use the old
  per-service chart, and their metadata has no `deployment-manifest.yaml`. Web rollbacks to ADO
  builds keep working from their ACR copies (`ociRef` + `ociDigest`, all present on 2026-11-17).
- **mpt-release (a) itself:** revert #1672 to go back to blob downloads for the four web
  components; their ADO builds still have the blob copies. Once a GitHub-built web version is
  deployed, that is no longer possible for it: it exists only in ACR.
