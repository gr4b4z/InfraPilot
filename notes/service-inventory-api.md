# Service inventory API

Read-only API over the service inventory: services, teams and products from the published catalog,
their observed state and their attributed cost. The portal fetches these artifacts and keeps a
derived copy (ADR-0004, `docs/plans/service-inventory.md`); nothing here edits them.

All endpoints are under `/api/service-inventory`, require a signed-in user or an API key, and answer
`404` with `"The service inventory is not enabled on this platform"` while the feature flag
`features.serviceInventory` is off. An admin turns it on under **Settings → Feature Flags** (**ServiceInventory**).

A service is addressed by its **portal service name**: the catalog `id` is the service name
(compared case-insensitively). Every response carries `freshness`, the age of the portal's copy:

```jsonc
"freshness": {
  "feed": "catalog",
  "period": null,                       // cost only
  "version": "catalog v5",              // "generated <time>" for status, "2026-11 to date" for cost
  "fetchedAt": "2026-11-20T15:37:00Z",  // when the copy shown was fetched
  "refreshedAt": "2026-11-20T16:37:00Z",// last successful check (unchanged counts)
  "lastAttemptAt": "2026-11-20T16:37:00Z",
  "lastError": null,                    // why the last attempt did not replace the copy
  "consecutiveFailures": 0
}
```

A failed refresh never removes data: the previous copy keeps being served, with `lastError` set.
Admins get the recorded reason, which names the source; everyone else gets
`"The last refresh failed. An administrator can see why."`.

## `GET /services/{serviceName}?product=`

The catalog entry, its team and product. Pass `product` (the portal product the service is deployed
under, as in `/deployments/{product}/{service}`) to learn whether it differs from the catalog's;
without it `portal` is `null`.

```jsonc
{
  "service": {
    "id": "order-router", "description": "…", "product": "storefront", "team": "storefront-team",
    "lifecycle": "planned", "type": "backend", "repo": "example-org/order-router", "exposure": "internal",
    "environments": [{ "environment": "dev", "cluster": "aks-shared-01", "namespace": "storefront-dev" }],
    "workloads": [{ "name": "router-api", "kind": "api" }],
    "databases": [{ "kind": "postgres", "database": "order-router", "server": "shared-postgres", "owner": true, "workloads": ["router-api"] }],
    "bindings": { /* as published, including other kinds and per-environment overrides */ }
  },
  "team": { "id": "storefront-team", "lead": "…", "deputy": "…", "githubTeam": "…", "entraGroup": "…" },
  "product": { "id": "storefront", "name": "Storefront", "businessOwner": "…", "technicalOwner": "…" },
  "portal": { "product": "storefront", "productDiffers": false },
  "catalogVersion": 5,
  "freshness": { … }
}
```

`404` when no catalog entry has that id. `workloads`, `type`, `bindings` and `databases` are empty
while the service's own file is missing or invalid.

## `GET /services/{serviceName}/observed`

The observed state from the status feed. The portal's own fields are camelCase; everything under
`onboarding`, `bindings`, `shared` and `taggedResources` is passed through with the field names the
status file uses. `unknown` and `unresolved` mean *not observed*,
never failed or absent.

```jsonc
{
  "serviceId": "order-router", "stale": false, "unverified": false, "warnings": [],
  "onboarding": { "registered_at": "…", "repo": { "status": "unknown", … }, … },
  "sourceFreshness": { },               // when each source was last read for this service
  "generatedAt": "…", "catalogVersion": 5,
  "azure": {                            // null when Azure was not scanned
    "observedAt": "…",                  // compare with generatedAt to show its age
    "bindings": [{ "service": "order-router", "environment": "prod", "kind": "postgres", "database": "order-router",
                   "server": "shared-postgres", "state": "present", "resource": "/subscriptions/…" }],
    "shared": [{ "id": "shared-postgres", "state": "ok", "resources": [ … ] }],
    "taggedResources": [ ]              // azure.services entries for this service, as published
  },
  "freshness": { … }
}
```

`404` when the status file has no entry for the service.

## `GET /services/{serviceName}/cost?period=YYYY-MM`

The service's cost in one month (default: the month of the latest cost file). Every amount is an
object per currency, the shape the published file uses (`{"USD": 1.5}`); a currency of the month the
service has no cost in is there as `0`. Direct and shared cost are separate and currencies are never
added together.

```jsonc
{
  "serviceId": "order-router", "period": "2026-10", "complete": true, "generatedAt": "…", "catalogVersion": 5,
  "direct": { "EUR": 205.00, "USD": 113.40 },
  "shared": { "EUR": 0, "USD": 82.50 },
  "total":  { "EUR": 205.00, "USD": 195.90 },
  "resources": [{ "source": "azure", "id": "/subscriptions/…", "cost": { "USD": 113.40 }, "via": "tag" }],
  "sharedFrom": [{ "shared": "shared-postgres", "cost": { "USD": 82.50 } }],
  "freshness": { … }
}
```

`complete: false` is a month to date, replaced by later files. `400` for a malformed `period`, `404`
when that month was never read or has no such service.

## `GET /cost?period=YYYY-MM&product=|team=`

What the **Service cost** page reads. Without `period`: the month of the latest cost file. Always:
`period`, `complete`, `catalogVersion`, `month` (the whole month's `total`, `direct`, `shared`,
`unallocated`), `months` (every stored month with `complete`), `options` (the catalog's products and
teams, for a picker), `scope` and `freshness`.

- Without `product` or `team`: `products` and `teams`, each row with `id`, `name`, `services` and
  `direct` / `shared` / `total`, sorted by the month's largest currency. Products and teams without
  services are listed too, with no amounts.
- With one of them: `scope` (`kind`, `id`, `name`, `known`: whether the catalog has it), `totals`,
  `services` (`id`, `product`, `team`, `direct`, `shared`, `total`, `sharedFrom`) sorted the same way,
  and `trend`: one row per stored month with `complete`, `direct` and `shared`.

## `GET /coverage?period=YYYY-MM`

What the **Cost coverage** page reads: `currencies` (one entry per currency with `total`, `direct`,
`shared`, `sharedUnsplit`, `untagged`, `unknownTag`, and the ten `largestGroups` and `largestScopes`
of unattributed cost), `sources` (per source and currency: `total`, `attributed`, `rows`, `scopes`),
`unsplitShared`, `trend` (per month and currency: `total`, `attributed`), `months` and `freshness`.

## Admin

- `GET /feeds` — the configured feeds (kind, location without credentials or query string, poll
  interval), configuration errors, and per feed and cost month: the stored copy, `lastSuccessAt`,
  `lastAttemptAt`, `lastChangedAt`, `lastError`, `consecutiveFailures`, and `configured` (false for
  rows a renamed or removed feed left behind).
- `POST /rebuild` — re-projects the query tables from the stored raw copies.

## Configuration

See `docs/plans/service-inventory.md`, "Configuration". Feeds are listed under `ServiceInventory`
in configuration; credential values come from the secret configuration, for example
`ServiceInventory__Credentials__registry__Password`, and are never returned by this API. A URL may
not carry a user name, password or query string: use a named credential. `AllowedHosts` entries are
hosts on port 443, or `host:port`; credentials are bound to that host and port. The registry's blob
redirect may leave the allowlist only for a public address: if the registry's storage is on a private
address (a private endpoint), add its host to `AllowedHosts`. A registry whose token
service is on another host needs that host as the source's `AuthHost`.
