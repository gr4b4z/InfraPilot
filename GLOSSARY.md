# InfraPilot

Infrastructure portal for self-service requests, approvals, deployments and promotions of
service versions between environments. This glossary covers the requests, promotions, services and
releases contexts.

## Language

### Requests

**Request Catalog**:
The list of things people can request from the platform team (a namespace, a repository, a role
assignment), each defined in YAML with its inputs, validations, approval rules and executor. A request
raised from it shows under **My Requests**. Shown at `/catalog`; the code keeps the older `Catalog` names.
_Avoid_: Service catalog (the former name), ticket catalog

### Promotions

**Environment**:
A named deployment target (e.g. development, staging, production) that services ship to.
Admin-curated; identified by a stable key.
_Avoid_: Stage, env tier

**Promotion Candidate**:
A proposal that a specific version of a service moves from a source Environment to a target
Environment, carrying the authoritative change set being promoted.
_Avoid_: Promotion request, release

**Approval Policy**:
The rules deciding who must approve Promotion Candidates on one source → target edge for a
product or service, including any work-item conditions.
_Avoid_: Approver group, gate config

**Approval Gate**:
The evaluated state of a Promotion Candidate against its Approval Policy. The candidate becomes
Approved only when the gate is satisfied.
_Avoid_: Check, threshold

**Auto-approve**:
An Approval Gate that is satisfied without any human decision, because the policy has no human
requirements, the candidate ships no work items, or every work item has been signed off.
_Avoid_: Skip approval, no gate

**Work Item Sign-off**:
A decision by an eligible approver on a single work item (ticket) included in a Promotion
Candidate's change set.
_Avoid_: Ticket approval

**Administrator Bypass**:
An audited, reason-required action by an administrator that moves a Pending Promotion Candidate
to Approved without satisfying its Approval Gate.
_Avoid_: Force approve, override

**Promotion Freeze**:
A switchable state on one product's promotions — optionally narrowed to one of its services — into
one target Environment, during which no Promotion Candidate it covers can become Approved. Approvals
and Work Item Sign-offs are still recorded; Auto-approve waits. Lifting the freeze re-evaluates every
Pending candidate it covered. ADR-0008 adds a third scope, the services of one Release Train; it is
not built yet.
_Avoid_: Environment freeze, lock, blackout, maintenance window, approval block

### Services and products

**Service**:
The smallest unit that is built and deployed on its own. Its name is its identity and is unique
across the whole system; the (product, service) pair is its address in URLs and the API, not its
identity. The portal's service list (part of the Service Inventory) holds every service the portal
knows.
_Avoid_: Application, component, microservice

**Product**:
A group of Services for the same audience. A Service has exactly one. The service catalog is the
source of a Service's Product (ADR-0005); until a product is switched over, the portal stores what
the pipeline sent. Shared platform services belong to the catalog's product `platform`.
_Avoid_: Project, application, domain, tenant

**Service Group**:
A named subdivision of a Product's Services declared in the service catalog (for example core,
extensions, support tools). A Service names at most one. Approval Policies and Release Trains will
be able to select services by it (ADR-0007).
_Avoid_: Domain, sub-product, tag, component group

### Releases

**Release**:
One run of shipping to production: it groups the promotions of its services, shows their progress on
a checklist and, once completed, records what shipped. Today a Release always ships a version line
(`major.minor`) for its products; ADR-0007 adds releases created from a Release Train, and ad hoc
releases of hand-picked services, for which alone the version line is optional (not built yet). It holds no promotion; only an
Approval Gate or a Promotion Freeze does.
_Avoid_: Release train (for one run), grouped promotion, deployment, rollout

**Release Train**:
Saved release settings — name, cadence, scope, environments, standing exclusions and checklist
steps — from which Releases are created. It always names its Products and its scope lies within
them. It selects services, never approvers. Decided in ADR-0007;
not built yet.
_Avoid_: Release (for the settings), release line, pipeline, schedule

### Service inventory

**Service Inventory**:
The portal's read-only copy of the services, teams, products and shared resources published by the
service catalog, with their observed state and attributed cost. Rebuilt at any time from the published
artifacts; never edited in the portal. A portal service belongs to the inventory entry whose `id` is its
service name. ADR-0005 adds a stored service list next to that copy (not built yet). What comes from
the catalog stays read-only there too. The list also holds the portal's own entries — services
labelled "not in catalog", and products and teams an admin added locally — which an admin can edit,
which are marked as the portal's own, and which are never part of the catalog copy.
_Avoid_: Service catalog (the request catalog's former name), service registry, CMDB

### Versions in an environment

**Declared version**:
The version a release repository says an environment should run for a Service. Published by the
service catalog in its status file for every component in the release repositories; the portal reads
it and never changes it. Decided in ADR-0009; not built yet.
_Avoid_: Desired version, target version, planned version

**Applied version**:
The version last installed successfully in an environment: the newest succeeded deploy event the
portal holds for that Service and Environment, in both deployment models. Not the newest event — a
failed or running deploy applied nothing. Decided in ADR-0009; not built yet.
_Avoid_: Current version, deployed version (for a failed deploy), installed version

**Observed version**:
The version of a Service's workloads actually running in a cluster, as the Cluster Agent last
reported it. Live state: it is replaced by every report and not kept as history. Decided in
ADR-0009; not built yet.
_Avoid_: Running version, actual version, live version

**Rollout pending**:
The Declared version differs from the Applied version: a change is declared and not installed yet.
The portal compares and records since when. Never called "pending", which means a Promotion
Candidate awaiting approval. Decided in ADR-0009; not built yet.
_Avoid_: Pending, out of date, behind

**Drift**:
The Applied version differs from the Observed version, outside a rollout: what runs is not what was
last installed. Workloads that are in the middle of a rollout are "rolling out", not Drift. Decided
in ADR-0009; not built yet.
_Avoid_: Mismatch, out of sync, deviation

**Cluster Agent**:
A small job in a cluster that lists the workloads of the service namespaces with their versions and
posts the whole list to the portal every minute. Read-only in the cluster; it reads no secrets and
deploys nothing. Decided in ADR-0009; not built yet.
_Avoid_: Scanner, collector, operator, deployer

## Relationships

- A **Promotion Freeze** applies to exactly one product — the whole product, or exactly one of its
  services — on exactly one **Environment**, in its role as promotion target. There is no
  whole-environment freeze.
- A **Promotion Freeze** holds the **Approval Gate** of every Pending **Promotion Candidate** it
  covers whose target is that **Environment**: a whole-product freeze covers every service, a service
  freeze only its own. A candidate is held when either kind applies; neither takes precedence over
  the other. It does not affect rollbacks or service requests.
- With ADR-0008 a **Promotion Freeze** may instead be scoped to one **Release Train**, which can span
  products. A product freeze still holds every **Service** of the product, whatever trains they are in.
  Not built yet; until then the two statements above are the whole rule.
- An **Administrator Bypass** ignores a **Promotion Freeze**.
- A **Promotion Candidate** already Approved before a **Promotion Freeze** begins is unaffected.
- A **Service** has exactly one **Product** and at most one **Service Group**; both come from the
  service catalog. A change of **Product** moves the **Service**'s data to the new product, and each
  deploy event keeps the product it had at the time (ADR-0006).
- A **Release** is created from a **Release Train** or ad hoc. Releases may overlap; a deploy belongs
  to a **Release** by its version line.
- The portal compares the **Declared**, **Applied** and **Observed version** of a **Service** in an
  **Environment**; nobody else does. **Rollout pending** and **Drift** are shown and hold nothing:
  no **Approval Gate**, **Promotion Freeze** or **Release** reads them (ADR-0009). A version that
  could not be read is unknown, never in sync.
- An **Approval Policy** is for one **Product** or selected **Services** of it. A **Release Train**
  and a **Release** never decide who approves a **Promotion Candidate**.
