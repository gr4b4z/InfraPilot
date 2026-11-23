---
status: accepted
date: 2026-11-22
---

# The catalog is the source of a service's product, team and group; the portal keeps a stored copy and local entries

The portal has no list of services or products. `Product` and `Service` are free text that arrives
with every deploy event, build and promotion and is copied onto about sixteen tables; a product
"exists" when some row names it. The product pickers and "services of a product" lists read six
different sources, one of them a free-text field (Settings → Rollbacks), and the only correction is
an admin's `ServiceProductOverride` (`src/Platform.Api/Features/Deployments/Models/ServiceProductOverride.cs`).
Meanwhile the service catalog already states, per service, its product and team, and the portal
already reads it (`src/Platform.Api/Features/ServiceInventory`, ADR-0004), but only to display it
next to cost. The service inventory plan recorded the intent: "Deploys will be brought in line with
the catalog" (`docs/plans/service-inventory.md`, decision 5).

We decided that **the catalog is the source of a service's product, team and service group**, and
that the portal keeps a **stored service list** — every service it knows, with the product, group and
team the catalog gives it and the catalog version that came from — plus the list of products and
teams. Every picker and every "services of a product" list reads that one list. The plan is
`docs/plans/service-list-and-catalog-product.md` (stage 1).

## How this amends ADR-0004

What stays true:

- The portal pulls published artifacts; the producers do not call it. Raw snapshots are kept, every
  projection can be dropped and rebuilt, a failed fetch removes nothing.
- The portal never edits, corrects or merges catalog data. A wrong product, team or group is fixed by
  a pull request to the catalog, not in the portal.
- The outbound rules, and showing the age of what is displayed. A refresh can now also be asked for
  on demand; it goes through the same sources and rules.

What changes:

- ADR-0004 said the join between catalog and portal services "is computed when read, not stored".
  The service list **is** stored: one row per service, with the catalog's product, group and team and
  the catalog version they were read from. It is still a derived cache. Catalog-sourced fields are
  overwritten by the next refresh and are never editable; dropping the list and rebuilding it from the
  current snapshot and the portal's own records gives the same list.
- The catalog's answer now reaches delivery data. When the catalog names a product for a service, the
  portal resolves incoming events to it and moves the service's existing data (ADR-0006). ADR-0004
  kept inventory data and delivery data apart; from here on the product on a deploy event may come
  from the catalog.
- The list holds rows the catalog does not have: services labelled **not in catalog**, and **local**
  products and teams. They are the portal's own, are marked with their source, and are the only
  entries an admin can edit.

The stored list is **never a second owner**. This is the catalog's own rule for any copy of its data,
and it is why local and fallback entries stay inside the portal: a local product, a local team, the
label "not in catalog" and the product `default` are never written to resource tags and never appear
in the cost pipeline's input. The export of the service list (with products) is a *discovery source*
for the catalog — a list of what the catalog has yet to register — and the catalog decides what to do
with it.

## Identity and names

- A service's **identity is its name**, unique across the whole system. Production confirms it holds
  today: 361 services in 9 products, no name repeated across products. The **(product, service)** pair
  stays the address in URLs and API paths; it is an address, not the identity.
- Names of products and services follow the catalog id rule `^[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?$`.
  The portal **normalises on write**: lower-case, `.` and `_` become `-`. It does not reject the
  request; the response carries a warning. A write whose normalised name would collide with a
  *different* existing name is refused. Fourteen existing names that break the rule are fixed once.
  The inventory reader today only trims and lower-cases ids
  (`src/Platform.Api/Features/ServiceInventory/InventoryProjector.cs`); it is brought in line, so the
  portal, the catalog and the cost pipeline match tags by one rule.

## Three modes, switched per product

The catalog knows 6 products and 2 services today; the portal has 361 services. The catalog cannot
take over in one step, so each product is in one of three modes:

1. **Report** — nothing changes for senders. The portal records where the sent product differs from
   the catalog's and shows it on Settings → Services.
2. **Warn** — the same, and the API response to the sender says the product differs.
3. **Enforce** — the service is stored under the catalog's product whatever was sent. Only for a
   product that is fully imported into the catalog.

The `product` field in deploy, build and promotion requests **stays required** in every mode: the
governance rules treat it as the gate for registering a build, and the portal resolves the product
on its own side. A service the catalog does not know keeps the product the pipeline sent and gets
the label **not in catalog**, which exists only in the portal. A product named `default` is the last
resort, used only when no product is known from anywhere.

While a product moves through the modes, an API key or Flux client limited to certain products
(`AllowedProducts` in `src/Platform.Api/Infrastructure/Auth/ApiKeyAuthHandler.cs` and
`src/Platform.Api/Features/Integrations/Flux/FluxIntegrationOptions.cs`) is checked against the
product the write is **stored under**. In `report` and `warn` that is the product the caller sent,
as today, and a difference from the catalog is recorded, not refused. Under `enforce` the service
name decides where a write lands, so the key must cover that product; the sent product is enough
only when it equals the stored one or is a previous product of that service after an admin's move.
A product goes to `enforce` only once no recorded write still relies on the sent product alone, so
`warn` is the warning period. Otherwise a key limited to one product could write to another
product's service by naming it.

## Local products and teams

An admin may add a product or a team the catalog does not have yet, so that a new service is not
blocked on a catalog pull request. A local team id follows the catalog rule; a local product id is 2 to 8 lowercase letters and digits, starting with a letter, with no hyphen, because a product id is also the name prefix of its resources. When the catalog
publishes an entry with the same id, the catalog entry replaces the local one; nothing is merged.

## Considered options

- **Keep the join computed when read** (ADR-0004 unchanged). Rejected: the pickers, policies,
  freezes, webhooks and key scopes all need one answer to "which services does this product have",
  including for services the catalog does not know. Six read-time answers is the problem being fixed.
- **A portal-owned master table of services and products, edited in Settings.** Rejected: it makes
  the portal a second owner next to the catalog, and the two drift. Local entries are the narrow
  exception, and they give way to the catalog.
- **Make `product` optional in requests once the catalog knows the service.** Rejected: the field is
  the registration gate in governance, and services not in the catalog still need it.
- **Reject names that break the id rule.** Rejected: pipelines that pass today would start failing.
  Nothing that passes through the API today may start being rejected without a warning period.
- **Switch the whole installation to the catalog at once.** Rejected: with 2 of 361 services
  registered it would relabel almost everything as unknown. Per-product modes let a product switch
  when it is ready.

## Consequences

- New stored data: the service list and the product and team lists, each row with its source (catalog
  or local) and, for catalog rows, the catalog version. Settings → Service Products becomes
  **Settings → Services**; `ServiceProductOverride` and its endpoints stay as aliases.
- `ServiceProductOverrideService.ResolveProductAsync`
  (`src/Platform.Api/Features/Deployments/ServiceProductOverrideService.cs`), the one place every write
  path asks for a service's product, also consults the service list and the product's mode.
- A change of product in the catalog does not rewrite anything by itself: it marks the service "to be
  moved" and an admin executes the move (ADR-0006).
- The pre-production environment is `staging` in the portal, the release repositories and the
  catalog. *Corrected 2026-11-23:* this record first said the catalog calls it `stage` and that the
  portal adds an alias; the owner confirmed that `stage` in the catalog was an error and the catalog
  renames its key to `staging`, so no alias is added. Environment aliases stay the generic mechanism
  for any key that differs, with no special case.
- The portal shows catalog readiness (not in catalog, product differs) but **never gates a promotion
  on it** (ADR-0007).
- The service group read here is what approval policies for selected services and release-train
  scope build on in stage 2 (`docs/plans/release-trains.md`).
