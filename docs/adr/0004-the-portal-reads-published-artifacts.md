---
status: accepted (stored service list and local entries added by ADR-0005)
date: 2026-11-20
---

# The portal reads published artifacts; its copy is a derived cache

The portal is to show, per service, who owns it, where it runs, which resources it has and what it
costs. None of that originates in the portal. A service catalog repository publishes the desired state
(services, teams, products, shared resources), a scanner publishes the observed state, and a cost job
publishes the cost of each month attributed to services. Each publishes a JSON file as an immutable,
versioned artifact: today an OCI artifact in a container registry, tomorrow possibly a file in blob
storage or a release asset.

## Pull, not push

The portal **fetches** these artifacts on a schedule. The producers do not call the portal.

- The producers must not depend on the portal being up, reachable or on the right version. They
  publish to storage they already own; whether, when and how often anyone reads it is not their
  concern.
- The artifacts are snapshots of state, not events. Missing one push would leave the portal wrong
  until the next one; pulling the latest snapshot is always enough to be right again.
- Every snapshot is versioned and immutable, so "has it changed?" is a cheap question (a manifest
  digest, an `ETag`) and the portal downloads only what changed.

The existing ingest endpoints (deploy events, builds, Flux) stay push: those are events, and an
event that is not delivered is lost. This decision covers state only, and adds no ingest endpoint.

## The portal's copy is a derived cache

- The portal stores the raw file of every version it accepts, with where it came from, its digest or
  `ETag` and when it was fetched. Every query table is a projection of one stored file and can be
  dropped and rebuilt from it. Losing the portal's copy entirely loses nothing that the sources cannot
  give back.
- The portal never edits, corrects, merges or recomputes the data. Changes go through the producers
  (a pull request to the catalog, a fix to the cost job). Amounts are shown as published: no
  currency conversion, no re-splitting of shared cost, no adding direct and shared cost into one
  number without showing both.
- A failed or rejected fetch never removes data. The portal keeps the last good copy, records when it
  last refreshed and why the last attempt failed, and shows the age of what it displays. A file in a
  format version the portal does not support is rejected whole; it is never half-read.

## One abstraction for where a file comes from

The rest of the portal asks for "the current catalog", "the current status" or "the cost of a month"
and does not know where it came from. Behind that, an artifact source answers two questions, "has it
changed since version X?" and "give me the file", with one implementation per kind of place: `oci`
(registry, repository, tag, file name) and `https` (a URL). Feeds are listed in configuration, so
adding a feed, or moving one from the registry to blob storage, is a configuration change; a new kind
of place is one class.

## Consequences

- Outbound HTTP from the portal to configured hosts only: HTTPS, an allowlist of hosts (a host means
  port 443; another port is listed as `host:port`), no redirect off it, a size limit, a timeout, JSON
  only. The one exception is the registry's blob redirect: it carries no credentials, its content is
  checked against the digest in the manifest, and it may only lead to a public address — storage on
  a private address is reached by listing its host. Credentials come from the existing secret
  configuration, are sent only to the host they belong to, are redacted from logs and stored errors,
  and never reach the browser.
- The data is as fresh as the last successful fetch. Pages show its age; a feed that stops refreshing
  is visible, not silent.
- The portal joins the catalog to its own services by name: the catalog `id` is the service name the
  deploy events carry. The join is computed when read, not stored.
- Storage grows with retention, not with traffic: raw snapshots are compressed and pruned on a
  schedule (see `docs/plans/service-inventory.md`).
- The feature ships behind a flag, off by default, until its pages exist.
