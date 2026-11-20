# Service inventory API

Read-only API over the service inventory: services, teams and products from the published catalog,
their observed state and their attributed cost. The portal fetches these artifacts and keeps a
derived copy (ADR-0004, `docs/plans/service-inventory.md`); nothing here edits them.

All endpoints are under `/api/service-inventory`, require a signed-in user or an API key, and answer
`404` with `"The service inventory is not enabled on this platform"` while the feature flag
`features.serviceInventory` is off.

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

## `GET /services/{serviceName}`

The catalog entry, its team and product, and whether the product the portal's deploy events report
differs from the catalog's.

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
  "portal": { "products": ["storefront"], "productDiffers": false },
  "catalogVersion": 5,
  "freshness": { … }
}
```

`404` when no catalog entry has that id. `workloads`, `type`, `bindings` and `databases` are empty
while the service's own file is missing or invalid.

## `GET /services/{serviceName}/observed`

The observed state from the status feed, as published. `unknown` and `unresolved` mean *not observed*,
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

The service's cost in one month (default: the month of the latest cost file). One entry per currency;
direct and shared cost are separate and currencies are never added together.

```jsonc
{
  "serviceId": "order-router", "period": "2026-10", "complete": true, "generatedAt": "…", "catalogVersion": 5,
  "amounts": [
    { "currency": "EUR", "direct": 205.00, "shared": 0, "total": 205.00 },
    { "currency": "USD", "direct": 113.40, "shared": 82.50, "total": 195.90 }
  ],
  "resources": [{ "source": "azure", "id": "/subscriptions/…", "cost": { "USD": 113.40 }, "via": "tag" }],
  "sharedFrom": [{ "shared": "shared-postgres", "cost": { "USD": 82.50 } }],
  "freshness": { … }
}
```

`complete: false` is a month to date, replaced by later files. `400` for a malformed `period`, `404`
when that month was never read or has no such service.

## Admin

- `GET /feeds` — the configured feeds (kind, location without credentials or query string, poll
  interval), configuration errors, and per feed and cost month: the stored copy, `lastSuccessAt`,
  `lastAttemptAt`, `lastChangedAt`, `lastError`, `consecutiveFailures`.
- `POST /rebuild` — re-projects the query tables from the stored raw copies.

## Configuration

See `docs/plans/service-inventory.md`, "Configuration". Feeds are listed under `ServiceInventory`
in configuration; credential values come from the secret configuration, for example
`ServiceInventory__Credentials__registry__Password`, and are never returned by this API.
