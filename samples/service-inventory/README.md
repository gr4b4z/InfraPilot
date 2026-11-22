# Demo service inventory

Made-up `catalog.json`, `status.json` and cost files in the format the catalog tooling publishes
(`notes/service-inventory-api.md`). Point a dev or demo instance at them to show the service
section on deployment pages, **Service cost** (`/inventory/cost`) and **Cost coverage**
without a real catalog. Not for production: every owner, amount and resource id is invented.

What is in them:

- 8 products and 8 teams (one product and one team with no services, as the pages list those too),
  52 services. Service ids are services the dev instance deploys, so cost rows link to their
  deployment pages. One planned service (`mpt-ai-assistant`) and one deprecated (`mpt-nav-stats`).
- Cost for June to October 2026 (closed months) and November 2026 to date (`cost.json`): about
  70k USD and 9.6k EUR a month (Prism is billed in EUR), Azure and AWS sources, shared PostgreSQL,
  SQL and AKS costs split across services, one shared resource nobody is bound to, untagged and
  unknown-tag cost for the coverage page. A few stories in the trend: `litellm` keeps growing,
  `mpt-nav-stats` is winding down, `aly-consumption-consolidation-web-api` spiked in September.
- Status: onboarding steps, a few warnings, Azure bindings (one not found, some not observed), an
  unclaimed database on the shared PostgreSQL server.

Regenerate after editing `generate.py` (deterministic):

```bash
python3 samples/service-inventory/generate.py
```

## Pointing an instance at them

The repository is public, so the instance can read the files from GitHub with no credential. Set
these on the API (environment variables; `main` can be any branch), restart it, and turn on
**Settings → Feature Flags → ServiceInventory**:

```bash
ServiceInventory__AllowedHosts__0=raw.githubusercontent.com
ServiceInventory__Feeds__0__Name=catalog
ServiceInventory__Feeds__0__Type=catalog
ServiceInventory__Feeds__0__PollMinutes=60
ServiceInventory__Feeds__0__Source__Kind=https
ServiceInventory__Feeds__0__Source__Url=https://raw.githubusercontent.com/gr4b4z/InfraPilot/main/samples/service-inventory/catalog.json
ServiceInventory__Feeds__1__Name=status
ServiceInventory__Feeds__1__Type=status
ServiceInventory__Feeds__1__PollMinutes=60
ServiceInventory__Feeds__1__Source__Kind=https
ServiceInventory__Feeds__1__Source__Url=https://raw.githubusercontent.com/gr4b4z/InfraPilot/main/samples/service-inventory/status.json
ServiceInventory__Feeds__2__Name=cost
ServiceInventory__Feeds__2__Type=cost
ServiceInventory__Feeds__2__PollMinutes=60
ServiceInventory__Feeds__2__Source__Kind=https
ServiceInventory__Feeds__2__Source__Url=https://raw.githubusercontent.com/gr4b4z/InfraPilot/main/samples/service-inventory/cost.json
ServiceInventory__Feeds__2__Source__ClosedMonths__From=2026-06
ServiceInventory__Feeds__2__Source__ClosedMonths__UrlTemplate=https://raw.githubusercontent.com/gr4b4z/InfraPilot/main/samples/service-inventory/cost-{month}.json
```

`GET /api/service-inventory/feeds` (admin) shows whether each feed was read and, if not, why.
Closed months are fetched a few per cycle, so the trend fills in over the first minutes.
