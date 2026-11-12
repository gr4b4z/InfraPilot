# Releases API

A **release** is a versioned release train, such as `5.10` or `6.0`, followed from planning and the
version cut to the production sign-off. The portal page is **Releases** (`/releases`). Each release
has a checklist that the release manager moves on by hand. The steps that the portal can measure also
carry a live **check**, worked out from records the portal already has: deploy events, promotions and
migration runs. The checks are computed on every read and never stored, so a backfilled deploy or a
late migration report shows up on the release without anyone touching it.

The release is gated with the Promotions feature flag, because its checks read promotions.

## Scope: which services are in a release

A service is in a release when, in one of the release's `products`, it ran or was promoted with a
version on the line in the release's staging or production environment. On the line means:

| Version | On `5.10`? |
|---|---|
| `5.10`, `5.10.0`, `5.10.27-g425981a8`, `v5.10.3`, `5.10-rc1` | yes |
| `5.100.1`, `15.10.1`, `5.9.31-gf167cabc`, `5.1.246-gd6a6cfef` | no |

Two lists adjust this:

- `includedServices`: services on their own version scheme that ship with the train anyway. For 5.10
  these were mpt-extension-adobe (`5.1.x`) and mpt-interim-renewals (`0.1.0-n`). Everything such a
  service deploys or promotes inside the window counts.
- `excludedServices`: services on the line that are not shipping, such as a 5.10 build still waiting
  on its work items.

Each entry is `service` or `product/service`. Services that an admin has retired are always left out.

## The evidence window

Records are read from `startedAt` (or creation, until a step starts) up to `completedAt` (or now). A
completed release goes on describing the day it shipped after the line's next hotfix.

There are two refinements:

- The lower bound applies only to included services. Nothing is on the line before the line exists.
- Promotions are read only up to the moment the **Approve the production promotions** step was marked
  done. A promotion raised after the approval round closed is that service's next change (a hotfix or
  a fresh build), not part of this release.

## Checks

| Check (`check`) | Done per service when | Needs attention when |
|---|---|---|
| `staging-builds` | A line build succeeded on staging, or is already in production | The newest staging deploy failed |
| `production-promotions` | Its promotion is Approved, Deploying or Deployed, or production already runs what staging has | A promotion was rejected |
| `production-deploys` | Production runs a succeeded line deploy, and no newer approved version is still waiting to deploy | The newest production deploy failed |
| `production-migrations` | Every migration run of the version production runs (the schema run of that deploy and the newest data run) finished | A migration failed |

Each check returns `state` (`waiting`, `in_progress`, `complete` or `attention`), `done`/`total`, a
one-line `summary`, `firstAt`/`lastAt` (the range of the underlying approvals, deploys or migrations),
`issues` (the services still open and why), and `runs` (the pipeline runs it read, see below).

A service's promotion is the newest one still in play: its newest non-superseded candidate, or the
newest candidate when all of them were superseded.

## Links

A release page links out in three ways:

| Where | Comes from | Examples |
|---|---|---|
| Release `links` | Typed in by the release manager | The Jira release version, the release plan, the retro, a post-mortem |
| Step `links` | Typed in on the step | The tag, the SDP or mpt-release run someone started by hand, a ticket for a manual step |
| Check `runs` and the service rows | **Read off the records**, nothing to type | Every SDP and mpt-release run behind the staging deploys, production deploys and migrations |

A link is `{ "label": "Jira release v5.10", "url": "https://…" }`. The label is optional: without
one, the page names the link from its address ("Azure DevOps run 8971132", "MPT-27923",
"Confluence page", "mpt-release #1668"). There are at most 20 links per release or step, each an
absolute http(s) address, and a repeated address is kept once.

The runs come from the `run` block of the deploy events and migration reports (see
`deployment-ingest-api.md`), so a producer that sends it gets linked with no extra work:

- On a service row, the staging deploy, the production deploy and each migration link the service's
  own job when the producer sent `jobUrl`, else the run.
- A check's `runs` groups those by whole run. A GitHub Actions job page counts as its run, and an
  Azure DevOps result page counts without its `view` and job parameters. Each run gives the number of
  the release's services it handled and its succeeded and failed attempts.
- A run is labelled the way people quote it. Azure DevOps uses the workflow and build id
  (`marketplace-sdp-prod 8971132`); its run number is a long build name. GitHub Actions uses the
  workflow and run number (`Reconcile Prod #75`).
- The promotions check lists the runs an approval dispatched, when the deploying pipeline reported one
  back on the promotion.

The page also links the portal's own views for the release: the pending promotions (where **Mass
approve** is), all promotions to production, the migrations, and each product's deployments and
release notes.

## Who may change a release

Anyone signed in can read releases. Only admins and the **release managers** can create a release,
edit it, move its steps, or mark it released, cancelled or reopened. QA has no extra rights here.

- **Release managers** are defined the same way as the promotion approval gates, because they are
  read from those gates. A product's release managers are the approvers (users and directory groups)
  of the **Release Manager** requirement on its promotion policy into the release's production
  environment. That is the product-wide policy, or the service-specific ones when the product has no
  product-wide policy there. The people who give a product's release approval are the people who run
  its releases, and both are configured in one place, **Settings → Promotions**.
  - The requirement name is a setting: **Settings → Releases**, platform setting
    `releases.managerRequirement`, default `Release Manager`.
  - A gate with nobody on it makes nobody a release manager.
  - Admins don't count as members of every group here; they have the rights anyway.
  - Signing another requirement of the same gate, such as QA Manager, gives no release rights.
- **Opening a release** of some products needs a release manager of at least one of them. Changing a
  release's products or production environment needs a release manager of the new ones.
- **The person named on a release** as its `releaseManager` (email or directory id) can run that
  release too, even without signing the gate. Only admins and release managers create releases, so
  only they choose who that is. The named manager can't move the release to other products or
  another production environment.
- **Deleting** a release is for admins only. Anyone else cancels it.

A personal access token acts as its owner, and an admin API key acts as an admin.

`GET /api/releases/managers?product=&environment=` is readable by anyone signed in, so the page can
say whom to ask. It returns:

- `requirement`: the requirement name.
- `gates`: per product and policy, the step, the requirement, and its `users` and `groups`. With no
  `environment` given, every production environment is included (marked so in
  **Settings → Environments**, or named like one).
- `knownRequirements`: every requirement name the gates use.
- `canEdit`: the caller is an admin.
- `isReleaseManager`: the caller is a release manager, for that product or for any product.

`PUT /api/releases/managers` with `{ "requirement": "Release Manager" }` changes the requirement
name. It is admin only and audited. `GET /api/releases` also returns `canCreate` for the caller.

## Endpoints

All of them are under `/api/releases`.

| Method and path | What it does |
|---|---|
| `GET /managers`, `PUT /managers` | Who the release managers are, read from the production promotion gates, and which gate requirement defines them (see above). PUT is admin only. |
| `GET /?status=&product=&limit=&offset=` | List. `status` is `active` (planned and in progress) or one release status. Releases in progress come first, then planned ones, then history, newest first. |
| `GET /{id}` | One release with its steps and `canManage` for the caller. |
| `GET /{id}/progress` | The live evidence: `services[]` (staging, promotion, production, failed attempts, migrations) and `checks[]`. |
| `POST /` | Create. The checklist comes from the default template (below). Returns `201`, or `409` with `existingId` when one of the products already has a release of that version. A cancelled release frees its version. |
| `PUT /{id}` | Replace the details (version, products, environments, dates, release manager, scope lists, notes). |
| `PATCH /{id}/steps/{stepId}` | Move or edit a step. See below. |
| `POST /{id}/steps` | Add a step: `{ title, description?, owner?, afterStepId? }`. Added steps are `custom`, and only they can be deleted. |
| `DELETE /{id}/steps/{stepId}` | Remove a custom step. A template step is skipped instead. |
| `POST /{id}/complete` | `{ completedAt? }`. Closes the release as released, open steps or not. |
| `POST /{id}/cancel` | `{ reason }`. Required. |
| `POST /{id}/reopen` | Clears completion or cancellation. |
| `DELETE /{id}` | Admin only. |

### Create

```jsonc
{
  "version": "5.11",                       // major.minor; a leading "v" is dropped
  "products": ["mpt", "mpt-extensions", "mpt-support-tools"],
  "title": "Release 5.11",                 // default "Release {version}"
  "stagingEnvironment": "staging",         // default "staging", aliases resolved
  "productionEnvironment": "prod",         // default "prod"
  "targetDate": "2027-01-14",              // the planned production date
  "releaseManager": "rm@example.com",      // default: the caller
  "releaseManagerName": "Release Manager",
  "includedServices": ["mpt-extensions/mpt-extension-adobe"],
  "excludedServices": [],
  "startedAt": null,                       // set it to record a release that is already under way
  "notes": "Scope, downtime, decisions",
  "links": [
    { "label": "Jira release v5.11", "url": "https://tracker.example.com/projects/MPT/versions/15000" },
    { "label": "Release plan", "url": "https://wiki.example.com/release-5.11" }
  ]
}
```

### Move a step

Every field is optional. `null` leaves a field as it is, and an empty string clears a text field.

```jsonc
{
  "status": "done",                        // pending | in_progress | done | skipped
  "startedAt": "2026-11-12T09:00:51Z",     // backdate; never in the future
  "completedAt": "2026-11-12T10:26:20Z",
  "note": "Mass approve at 09:01; swo-platform approved again at 10:26",
  "links": [                               // replaces the step's links; [] removes them
    { "label": "prod-branch run", "url": "https://dev.azure.com/.../_build/results?buildId=123" }
  ]
}
```

- `in_progress` stamps `startedAt` with now unless you pass one.
- `done` stamps `completedAt` with now unless you pass one. A step marked done in one go started when it
  finished. `skipped` never starts a step.
- `pending` clears the dates.
- The first step that starts sets the release's `startedAt`, at the time the step says it started.

The release `status` is derived on every write: `cancelled` or `completed` from those dates, otherwise
`in_progress` once it has started or any step is under way, otherwise `planned`.

## The default checklist

The checklist follows how the 5.10 release actually ran (November 2026). It was reconstructed from
InfraPortal, the marketplace SDP and mpt-release runs, Jira and the release plan.

| Step | Owner | Check | What happened for 5.10 (UTC) |
|---|---|---|---|
| Plan the scope and release date | Release manager | — | Scope meeting on 12 Oct. Jira fixVersion with release date 12 Nov. Plan: code freeze 2 Nov, go/no-go 6 Nov, release 12 Nov. |
| Start the release line | Release manager | — | 27 Oct, 12:21–12:47: `5.10.0` tags on `release/5` of every repo, cut from a workstation. One repo was tagged a day late. |
| Build and deploy to staging | Developers | `staging-builds` | First 5.10 component reached staging on 28 Oct 07:34. Fixes came as new `5.10.x` builds until release day. |
| Code freeze | Release manager | — | 2 Nov 08:35: staging branch cut. A failed install was fixed with a hand-edited manifest and a re-run. |
| Test on staging | QA | — | Regression and acceptance tests, then the QA green light. No system records the green light. |
| Go / no-go | Release manager | — | 6 Nov: no downtime, so no maintenance page; DevOps runs the deploy. |
| Approve the production promotions | Release manager | `production-promotions` | 12 Nov 09:00–09:01: Mass approve of QA Review and Release Approval. One service was approved again at 10:26 after a hotfix build. |
| Deploy to production | DevOps | `production-deploys` | 09:07–09:56 through SDP (after an approval on the Azure DevOps production environment) and mpt-release. One service failed twice on a schema-migration timeout, then shipped as a new build at 10:33–10:43. |
| Run schema and data migrations | DevOps | `production-migrations` | Schema migrations ran inside the deploys. Data migrations ran 10:43–11:29, part of them started by hand. |
| Sanity test in production | QA | — | QA sanity testing and sign-off. No system records it. |

Release-specific manual work, such as a data export before one service's deploy, goes in as a custom
step (5.10 had one, tracked in Jira). The template is copied when a release is created, so changing it
never rewrites a release that already happened.

### What no system records (yet)

Things nothing writes down today:

- The version-cut tags, apart from the tags themselves.
- The QA green light on staging and the production sanity sign-off.
- The go/no-go decision.
- The Azure DevOps production-environment approval.
- Why someone re-ran a job or hand-edited a manifest.

The release page is where the release manager writes these down, with a note and links on the step.
