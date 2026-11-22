---
status: accepted
date: 2026-11-22
---

# The portal receives observed versions from a cluster agent and compares declared, applied and observed versions

The portal shows one version per service and environment: the newest deploy event it was sent
(`DeploymentService.LatestStateEvents` in `src/Platform.Api/Features/Deployments/DeploymentService.cs`).
It cannot tell a change that is declared in a release repository and not installed yet from one that
is installed, and it cannot tell what actually runs: a manual change in a cluster, a suspended
deployer and an install that failed half-way leave no trace. In the pull model it records only the
install outcomes Flux reports and drops everything else
(`src/Platform.Api/Features/Integrations/Flux/FluxEventMapper.cs`).

Three versions answer those questions — the **declared** version (what the release repository says an
environment should run), the **applied** version (what was last installed successfully) and the
**observed** version (what runs) — and two differences between them: **Rollout pending** (declared ≠
applied) and **Drift** (applied ≠ observed).

We decided:

1. **Observed versions are pushed to the portal by a small agent in each cluster.** First a job that
   runs every minute, lists the workloads of the service namespaces with their versions and posts
   the whole list; later, only if a minute proves too slow, a controller that reports changes as
   they happen. The portal's ingest contract is the same for both. On clusters with Flux the portal
   also starts accepting the notifications Flux already sends and the portal drops today.
2. **The portal is the one place that compares.** Applied is the portal's own deploy event, for both
   deployment models. Declared versions come from the service catalog's status artifact, which the
   portal already reads (ADR-0004); the catalog's reconciler publishes declared versions only,
   compares nothing and never reads a cluster. Equality is the portal's rule: surrounding whitespace
   removed and one leading `v` dropped, nothing else — no ordering, no release line.
3. **The comparison is on content, not only on the version string.** The main purpose of observing
   is to detect that someone replaced something by hand. The agent reports the image digest the
   pods actually run; the deploying pipeline reports the digest it deployed with the deploy event;
   the same version with another digest is Drift. Static front-end sites in blob storage, which have
   no workload, are covered the same way: the pipeline and the agent compute one content fingerprint
   over the container's file list and the portal compares the two. Where the applied digest is not
   known, versions alone are compared and the portal says so.
4. **The agent also reports the namespaces of its cluster, and the portal serves that list.** The
   service catalog's reconciler gets no cluster access at all; it takes its "namespace exists"
   observation from the portal.
5. **Nothing is gated on the result.** The three versions and the two markers are shown. No
   promotion, approval gate, freeze, release check or rollback reads them.

The plan is `docs/plans/declared-applied-observed-versions.md`.

## How this amends ADR-0004

What stays true:

- Catalog, status and cost are pulled as published artifacts; the producers do not call the portal;
  raw snapshots are kept; every projection of them can be dropped and rebuilt; a failed fetch removes
  nothing. Declared versions arrive this way and follow all of it.
- The portal reads no cluster and no release repository. It makes no new outbound call.

What changes:

- ADR-0004 said state is pulled and only events are pushed, and added no ingest endpoint. **Observed
  version state is pushed**: an agent posts it to a new ingest endpoint.
- ADR-0004 said the portal's copy can always be rebuilt from the published artifacts. **The observed
  state cannot**: no artifact holds it. The exception is acceptable because of what the data is:
  - it is live operational state, not a record. Its only source is the cluster, and the cluster
    sends all of it again within a minute. Losing the portal's copy loses at most that minute, and
    until the next report the portal shows "unknown", never "in sync";
  - every report is the complete list for its cluster, so one report repairs any missed or lost one
    — the property ADR-0004 wanted from snapshots, kept under push;
  - it is shown, never gating. A wrong or missing value can mislead a reader for a minute; it cannot
    hold or release a promotion.
- ADR-0004 said the portal never recomputes the data it reads. It still does not change or correct a
  declared version; it **compares** it with two values of its own. The two markers and how long a
  difference has lasted are the portal's own derived data, kept only for the current state.
- The Flux endpoint stays push, as ADR-0004 already said of events; it now also keeps the
  non-install notifications as signals beside a release, and still turns only install outcomes into
  deploy events.

## Considered options

- **The catalog's reconciler reads the clusters and publishes the comparison.** Rejected: its CI job
  would need network reach into every cluster and a credential for each, held in CI; and it runs
  every 30 minutes, so drift would be seen up to half an hour late and a normal rollout would look
  like drift for a run or two. With the agent in place the reconciler needs no cluster access for
  anything, namespaces included.
- **The reconciler reads the portal's deploy events and compares.** Rejected: there would be two
  comparers — the reconciler for what it can read, the portal for the rest (in the pull model the
  only applied record is the portal's deploy event) — with two equality rules and two answers to
  show side by side. It would also make the catalog's status depend on the portal answering, the
  dependency ADR-0004 avoids in the other direction.
- **The portal reads the clusters itself.** Rejected: the same reach problem from the other side,
  and credentials for every cluster stored in the portal, which today holds none.
- **An agent in each cluster pushes to the portal.** Chosen: outbound HTTPS from the cluster only,
  the path deploy events and Flux notifications already use; one narrow key per cluster; read-only
  rights inside the cluster and no access to secrets; a cadence of a minute.

## Consequences

- A new ingest endpoint with one contract and three kinds of item — workload, static site,
  namespace — and new stored data: the current observed items per cluster, and the current state of
  each difference with the time it began. Both hold the present only; no history is
  kept. Each schema change ships a Postgres and a SQL Server migration.
- **Who may post.** An API key carrying a new scope, limited to the clusters it names; one key per
  cluster. Such a key can report observed state for its cluster and do nothing else, and a key
  without the scope cannot post observed state — unlike the older scopes, this one is never implied
  by a key that lists none (`ApiKeyAuthHandler.HasScope`).
- A cluster that has not reported for a few minutes reads as **unknown** for every service in it. A
  service or environment no agent covers is **not compared**. Neither is ever shown as in sync.
- Which environment a cluster and namespace belong to is configured in the portal, not sent by the
  agent, so an agent cannot place its workloads in another environment. Workloads the portal cannot
  place are kept and listed, not dropped.
- The portal ships and versions the agent: a job with read-only access to workloads, pods (for the
  running image digest) and namespaces, and a blob data reader role on the storage accounts of the
  static sites; its manifest and its tests live in this repository. It is deployed to the clusters as a platform
  component: by Flux from the pull-model release repository where Flux runs, and on a cluster without
  Flux by that product's own deployment pipeline through the push-model release repository, or by
  hand.
- A rollback that Flux performs and reports as succeeded is recorded as a deploy event of the
  version rolled back to, marked as a rollback, so the applied version names what runs.
- No history of differences is kept. An audit entry per drift can be added later without changing
  the stored data.
- Deploy events gain an optional list of what was deployed by content: image digests and static-site
  fingerprints. The build registry's `artifactDigest` is the digest of the build manifest artifact,
  not of an image, so it cannot stand in for it.
- A read endpoint for the reported namespaces, per cluster with names, labels and the time of the
  report, readable with an ordinary API key. A stale or absent cluster must be read as unknown; the
  response says which clusters are stale. This makes the portal a source another system reads, for
  this one observation; the portal still gates nothing on it, and the catalog's status keeps being
  published when the portal does not answer (the observation is then unknown).
- Detection is the second line for static sites. Restricting write access to the pipeline's identity
  and turning blob versioning on are rollout steps outside the portal.
- Coverage no longer depends on the catalog for Drift: it works for every service an agent reports
  and the portal has a deploy event for. Rollout pending needs a declared version, which the catalog
  publishes for every component in the release repositories, in the catalog or not.
- The comparison judges time itself: workloads in the middle of a rollout are shown as **rolling
  out** for a grace period before they count as Drift.
- `GLOSSARY.md` gains **Declared version**, **Applied version**, **Observed version**, **Rollout
  pending**, **Drift** and **Cluster agent**.
