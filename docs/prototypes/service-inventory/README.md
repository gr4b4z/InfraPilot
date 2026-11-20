# Service inventory — view prototype (throwaway, do not merge)

Review-only prototype of the service inventory views, built inside the real portal on static
fixtures. No API, no database, no fetcher. It is not merged and gets no "What's new" entry.

## Run it

```bash
# API (any local setup that serves the portal; seed data is enough)
dotnet run --project src/Platform.Api/Platform.Api.csproj -- --ConnectionStrings:Platform "Host=localhost;Port=5432;Database=infrapilot;Username=postgres;Password=postgres"
# Web
npm run dev --prefix src/Platform.Web
```

Sign in as `admin@localhost` / `admin123` (development seed). The service section needs a portal
service named like a catalog id; post two deploy events locally, e.g. `storefront/order-router` and
`payments/ledger-api`, to the development API. Optional: add the environment aliases
`dev`, `stage`, `prod` to Development, Staging, Production in **Settings → Environments** so
catalog environment keys show as the portal's environments (`test` is left unmapped on purpose).

| View | Path |
|---|---|
| Service section | `/deployments/storefront/order-router`, `/deployments/payments/ledger-api`, any other service = not in the inventory |
| Cost of a product or team | `/inventory/cost`, `/inventory/cost?product=storefront`, `?team=storefront-team`, `&month=2026-10` |
| Coverage | `/inventory/coverage`, `?month=2026-10` |

The dashed **Prototype** strip on each view switches between states (also `?inv=failed`,
`?inv=unsupported`, `?inv=unresolved`): fresh data, refresh failed, unsupported schema, unresolved
values.

## Fixtures

`src/Platform.Web/src/app/service-inventory/fixtures/`: `catalog.json`, `status.json`,
`cost-2026-11.json` are **fictional** files with exactly the shape of the published artifacts (all
names, IDs and amounts are made up). `cost-samples.json` holds August–October 2026, **sample data** (each month satisfies `total = direct + shared + shared_unsplit + unallocated`;
October adds a second source billed in EUR to show that currencies stay apart). Sample months are
tagged "Sample" on screen.

## Screenshots

`screenshots/NN-view--{desktop|narrow}-{light|dark}.png`, at 2× scale.
