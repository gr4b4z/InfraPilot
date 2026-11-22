#!/usr/bin/env python3
"""Generate demo service-inventory artifacts: catalog.json, status.json and cost files.

The portal never edits the service inventory; it reads the catalog, status and cost files that
the catalog tooling publishes (notes/service-inventory-api.md). These files are made-up data in
that published format, so a dev or demo instance can show the service section, **Service cost**
and **Cost coverage** pages without a real catalog. Service ids match services the dev instance
deploys, so the cost pages link to their deployment pages.

Deterministic: the same script gives the same files. Run it from anywhere:

    python3 samples/service-inventory/generate.py
"""
import hashlib
import json
import random
from pathlib import Path

OUT = Path(__file__).resolve().parent
CATALOG_VERSION = 12
CLOSED_MONTHS = ["2026-06", "2026-07", "2026-08", "2026-09", "2026-10"]
OPEN_MONTH = "2026-11"
OPEN_MONTH_FRACTION = 22 / 30            # the open month is "to date"
OPEN_MONTH_GENERATED = "2026-11-22T06:00:00Z"
STATUS_GENERATED = "2026-11-22T06:15:00Z"

SUB_MPT = "3f2a9c41-7d1e-4b6a-9c0e-5a1d2b3c4d01"
SUB_PRISM = "8b7c6d5e-4f3a-4e2b-8c1d-0a9b8c7d6e02"
SUB_DATA = "c1d2e3f4-a5b6-4c7d-8e9f-0a1b2c3d4e03"
AWS_ACCOUNT = "123456789012"

PRODUCTS = [
    ("mpt", "Marketplace Platform", "Marta Kowalczyk", "Piotr Zielinski"),
    ("mpt-extensions", "Marketplace Extensions", "Marta Kowalczyk", "Jonas Berg"),
    ("prism", "Prism", "Elena Varga", "Tomas Novak"),
    ("cloudiq", "CloudIQ", "Sofia Lindqvist", "Daniel Moreau"),
    ("pyracloud", "PyraCloud Analytics", "Anders Holm", "Katarzyna Wojcik"),
    ("nova", "Nova", "Elena Varga", "Lukas Meier"),
    ("cicd-infra", "CI/CD Infrastructure", "Ines Duarte", "Ines Duarte"),
    ("data-platform", "Data Platform", "Anders Holm", "TODO"),          # no services yet
]

TEAMS = [
    ("marketplace-core", "Piotr Zielinski", "Agnieszka Mazur", ["mpt"]),
    ("marketplace-web", "Julia Schmidt", "Michal Lewandowski", ["mpt"]),
    ("marketplace-extensions", "Jonas Berg", "Nina Petrova", ["mpt-extensions"]),
    ("prism-team", "Tomas Novak", "Eva Horvath", ["prism"]),
    ("cloudiq-team", "Daniel Moreau", "Clara Rossi", ["cloudiq"]),
    ("pyracloud-analytics", "Katarzyna Wojcik", "Henrik Dahl", ["pyracloud"]),
    ("platform-team", "Ines Duarte", "Lukas Meier", ["nova", "cicd-infra"]),
    ("sre-team", "Oskar Nilsson", "Ines Duarte", []),                    # owns shared resources only
]

# (id, product, team, type, tier, lifecycle, environments, monthly USD-equivalent at prod scale,
#  database bindings [(kind, database, shared server)], description)
MPT_ENVS = ["dev", "test", "staging", "prod"]
EXT_ENVS = ["dev", "test", "staging", "prod"]
PRISM_ENVS = ["development", "sandbox", "staging", "demo", "production"]
CIQ_ENVS = ["development", "sit", "uat", "staging", "prod"]
PYRA_ENVS = ["development", "test", "staging", "production"]
SERVICES = [
    ("mpt-billing", "mpt", "marketplace-core", "backend", 1, "production", MPT_ENVS, 4200,
     [("postgres", "billing", "shared-postgres-mpt")], "Invoices, charges and billing statements for marketplace orders."),
    ("mpt-procurement", "mpt", "marketplace-core", "backend", 1, "production", MPT_ENVS, 3100,
     [("postgres", "procurement", "shared-postgres-mpt")], "Orders, subscriptions and agreements across vendors."),
    ("mpt-notifications", "mpt", "marketplace-core", "backend", 2, "production", MPT_ENVS, 1250,
     [("postgres", "notifications", "shared-postgres-mpt")], "Email and in-app notifications with templates and preferences."),
    ("mpt-audit", "mpt", "marketplace-core", "backend", 2, "production", MPT_ENVS, 980,
     [("postgres", "audit", "shared-postgres-mpt")], "Append-only audit trail of every change in the marketplace."),
    ("mpt-currency", "mpt", "marketplace-core", "backend", 3, "production", MPT_ENVS, 310, [],
     "Exchange rates and currency conversion."),
    ("mpt-tasks", "mpt", "marketplace-core", "worker", 2, "production", MPT_ENVS, 760, [],
     "Background task runner for long-running marketplace jobs."),
    ("mpt-helpdesk", "mpt", "marketplace-core", "backend", 3, "production", MPT_ENVS, 540,
     [("postgres", "helpdesk", "shared-postgres-mpt")], "Support cases and chat between clients and operations."),
    ("mpt-public-catalog", "mpt", "marketplace-core", "backend", 2, "production", MPT_ENVS, 1480, [],
     "Public product catalog with search."),
    ("mpt-spotlight", "mpt", "marketplace-core", "backend", 3, "production", MPT_ENVS, 420, [],
     "Highlights things that need a user's attention."),
    ("mpt-scheduler", "mpt", "marketplace-core", "worker", 3, "production", MPT_ENVS, 190, [],
     "Cron-style scheduling for marketplace jobs."),
    ("mpt-export", "mpt", "marketplace-core", "worker", 3, "production", MPT_ENVS, 260, [],
     "CSV and Excel exports of marketplace data."),
    ("swo-auth", "mpt", "marketplace-core", "backend", 1, "production", MPT_ENVS, 1900,
     [("postgres", "auth", "shared-postgres-mpt")], "Sign-in, tokens and account membership."),
    ("swo-platform", "mpt", "marketplace-core", "backend", 1, "production", MPT_ENVS, 2650, [],
     "Platform API gateway in front of the marketplace services."),
    ("mpt-nav-stats", "mpt", "marketplace-core", "worker", 3, "deprecated", ["dev", "test", "staging", "prod"], 140, [],
     "Nightly statistics from the ERP. Being replaced by billing reports."),
    ("mpt-ai-assistant", "mpt", "marketplace-core", "backend", 2, "planned", ["dev"], 0, [],
     "Assistant that answers questions about orders and subscriptions."),
    ("swo-web-shell", "mpt", "marketplace-web", "frontend", 1, "production", MPT_ENVS, 380, [],
     "Micro-frontend shell that hosts every marketplace page."),
    ("swo-web-billing", "mpt", "marketplace-web", "frontend", 2, "production", MPT_ENVS, 150, [],
     "Billing pages."),
    ("swo-web-commerce", "mpt", "marketplace-web", "frontend", 2, "production", MPT_ENVS, 170, [],
     "Commerce pages: orders, subscriptions, agreements."),
    ("swo-web-dashboard", "mpt", "marketplace-web", "frontend", 3, "production", MPT_ENVS, 120, [],
     "Home dashboard."),
    ("mpt-web-procurement", "mpt", "marketplace-web", "frontend", 2, "production", MPT_ENVS, 140, [],
     "Procurement wizard pages."),
    ("mpt-extension-adobe", "mpt-extensions", "marketplace-extensions", "backend", 1, "production", EXT_ENVS, 890, [],
     "Fulfils Adobe VIP Marketplace orders."),
    ("mpt-extension-aws-management", "mpt-extensions", "marketplace-extensions", "backend", 2, "production", EXT_ENVS, 1320, [],
     "Creates and manages AWS accounts for marketplace clients."),
    ("mpt-extension-installation", "mpt-extensions", "marketplace-extensions", "backend", 2, "production", EXT_ENVS, 470, [],
     "Installs extensions into client accounts."),
    ("mpt-extension-operations", "mpt-extensions", "marketplace-extensions", "backend", 2, "production", EXT_ENVS, 610, [],
     "Operational tools for extension support."),
    ("mpt-extension-producthub", "mpt-extensions", "marketplace-extensions", "backend", 3, "production", EXT_ENVS, 330, [],
     "Product information sync from vendors."),
    ("mpt-extension-usage-reporting", "mpt-extensions", "marketplace-extensions", "worker", 2, "production", EXT_ENVS, 720, [],
     "Collects usage from vendors and reports it for billing."),
    ("swo-extension-mscsp", "mpt-extensions", "marketplace-extensions", "backend", 1, "production", EXT_ENVS, 1150, [],
     "Fulfils Microsoft CSP orders through Partner Center."),
    ("litellm", "mpt-extensions", "marketplace-extensions", "backend", 3, "experimental", ["dev"], 2350, [],
     "LLM gateway used by extension experiments."),
    ("prism-web-api", "prism", "prism-team", "backend", 1, "production", PRISM_ENVS, 2100,
     [("mssql", "prism-core", "shared-mssql-prism")], "Prism public API."),
    ("prism-web-portal", "prism", "prism-team", "frontend", 1, "production", PRISM_ENVS, 640, [],
     "Prism customer portal."),
    ("prism-minion-core", "prism", "prism-team", "worker", 1, "production", PRISM_ENVS, 1180,
     [("mssql", "prism-core", "shared-mssql-prism")], "Core background processing."),
    ("prism-minion-invoicing", "prism", "prism-team", "worker", 1, "production", PRISM_ENVS, 960,
     [("mssql", "prism-invoicing", "shared-mssql-prism")], "Invoice runs."),
    ("prism-minion-partnercenter", "prism", "prism-team", "worker", 2, "production", PRISM_ENVS, 870, [],
     "Partner Center synchronisation."),
    ("prism-minion-azure", "prism", "prism-team", "worker", 2, "production", PRISM_ENVS, 520, [],
     "Azure consumption import."),
    ("prism-minion-recon", "prism", "prism-team", "worker", 2, "production", PRISM_ENVS, 450,
     [("mssql", "prism-recon", "shared-mssql-prism")], "Reconciliation of vendor invoices."),
    ("prism-web-signalr", "prism", "prism-team", "backend", 3, "production", PRISM_ENVS, 210, [],
     "Real-time updates for the portal."),
    ("prism-identity-identityprovider", "prism", "prism-team", "backend", 1, "production", PRISM_ENVS, 380, [],
     "Identity provider for Prism users."),
    ("Insights.Application.Api", "cloudiq", "cloudiq-team", "backend", 1, "production", CIQ_ENVS, 1750, [],
     "CloudIQ insights API."),
    ("adobe-service", "cloudiq", "cloudiq-team", "backend", 2, "production", CIQ_ENVS, 820, [],
     "Adobe licence management."),
    ("adobe-service-graphql", "cloudiq", "cloudiq-team", "backend", 2, "production", CIQ_ENVS, 360, [],
     "GraphQL front for the Adobe service."),
    ("asset-api", "cloudiq", "cloudiq-team", "backend", 2, "production", ["development", "staging", "prod"], 590, [],
     "Software asset inventory."),
    ("agreement-api", "cloudiq", "cloudiq-team", "backend", 3, "production", ["development", "staging", "prod"], 280, [],
     "Customer agreements."),
    ("aws-billing", "cloudiq", "cloudiq-team", "worker", 2, "production", ["development", "staging", "prod"], 1040, [],
     "Imports AWS billing data."),
    ("aly-budgets-web-api", "pyracloud", "pyracloud-analytics", "backend", 2, "production", PYRA_ENVS, 690,
     [("mssql", "budgets", "shared-mssql-pyra")], "Budgets and alerts."),
    ("aly-cloud-utilization-web-api", "pyracloud", "pyracloud-analytics", "backend", 2, "production", PYRA_ENVS, 1420,
     [("mssql", "utilization", "shared-mssql-pyra")], "Cloud utilisation reports."),
    ("aly-consumption-ea-web-api", "pyracloud", "pyracloud-analytics", "backend", 1, "production", PYRA_ENVS, 2280,
     [("mssql", "consumption", "shared-mssql-pyra")], "Azure EA consumption."),
    ("aly-consumption-aws-web-api", "pyracloud", "pyracloud-analytics", "backend", 1, "production", PYRA_ENVS, 1960,
     [("mssql", "consumption", "shared-mssql-pyra")], "AWS consumption."),
    ("aly-consumption-consolidation-web-api", "pyracloud", "pyracloud-analytics", "worker", 2, "production", PYRA_ENVS, 3150,
     [("mssql", "consumption", "shared-mssql-pyra")], "Consolidates consumption across clouds."),
    ("aly-azure-consumption-csp-web-api", "pyracloud", "pyracloud-analytics", "backend", 2, "production", PYRA_ENVS, 1210,
     [("mssql", "consumption", "shared-mssql-pyra")], "Azure CSP consumption."),
    ("activation-manager-bo-web-api", "pyracloud", "pyracloud-analytics", "backend", 3, "production", PYRA_ENVS, 240, [],
     "Back-office activation manager."),
    ("nova-anvil", "nova", "platform-team", "backend", 3, "production", ["dev", "prod"], 330, [],
     "Nova build and template engine."),
    ("podinfo", "cicd-infra", "platform-team", "backend", 3, "production", ["staging"], 45, [],
     "Sample workload that checks the GitOps pipeline end to end."),
]

# Shared resources: (id, kind, owner, description, monthly USD-equivalent, services that share it)
SHARED = [
    ("shared-postgres-mpt", "postgres-server", "platform-team", "PostgreSQL flexible server for marketplace databases.", 2600, None),
    ("shared-mssql-prism", "mssql-server", "prism-team", "Azure SQL server for Prism databases.", 1850, None),
    ("shared-mssql-pyra", "mssql-server", "pyracloud-analytics", "Azure SQL elastic pool for PyraCloud.", 3400, None),
    ("aks-mpt-weu", "aks-cluster", "platform-team", "Shared AKS cluster that runs marketplace workloads.", 5200,
     lambda s: s[1] in ("mpt", "mpt-extensions") and s[5] != "planned"),
    ("servicebus-legacy", "servicebus-namespace", "sre-team", "Legacy Service Bus namespace; nothing is bound to it any more.", 410, "unsplit"),
]

PRISM_EUR = True                          # Prism is billed in EUR, to show more than one currency
EUR_RATE = 0.92
AWS_SERVICES = {"mpt-extension-aws-management": 0.55, "aly-consumption-aws-web-api": 0.4, "aws-billing": 0.6}


def r2(x):
    return round(x + 1e-9, 2)


def madd(a, b):
    out = dict(a)
    for k, v in b.items():
        out[k] = r2(out.get(k, 0) + v)
    return out


def money(currency, amount):
    return {currency: r2(amount)} if amount else {}


def currency_of(product):
    return "EUR" if PRISM_EUR and product == "prism" else "USD"


def sub_of(product):
    return SUB_PRISM if product == "prism" else SUB_DATA if product in ("pyracloud", "cloudiq") else SUB_MPT


def cluster_for(product, env):
    short = {"production": "prod", "development": "dev"}.get(env, env)
    if product in ("mpt", "mpt-extensions"):
        return "aks-mpt-weu" if short == "prod" else f"aks-mpt-{short}-weu"
    return f"aks-{product}-{short}"


def resource_ids(service):
    sid, product = service[0], service[1]
    slug = sid.lower().replace(".", "-")
    sub = sub_of(product)
    rg = f"/subscriptions/{sub}/resourceGroups/{product}-{slug}-prod-rg/providers"
    ids = [
        (f"{rg}/Microsoft.App/containerApps/{slug}-prod"[:200], "microsoft.app/containerapps", 0.55),
        (f"{rg}/Microsoft.Insights/components/{slug}-prod-ai", "microsoft.insights/components", 0.12),
        (f"{rg}/Microsoft.KeyVault/vaults/kv-{slug[:14]}-prod", "microsoft.keyvault/vaults", 0.03),
    ]
    if service[3] in ("backend", "worker"):
        ids.append((f"{rg}/Microsoft.Cache/Redis/{slug}-prod-redis", "microsoft.cache/redis", 0.18))
    ids.append((f"{rg}/Microsoft.Storage/storageAccounts/st{slug.replace('-', '')[:16]}prod", "microsoft.storage/storageaccounts", 0.12))
    total = sum(w for _, _, w in ids)
    return [(i, t, w / total) for i, t, w in ids]


def month_factor(rng, index, sid):
    # gentle growth month over month, with noise; a couple of services with a story
    f = (1.035 ** index) * rng.uniform(0.94, 1.06)
    if sid == "litellm":
        f = [0.15, 0.3, 0.55, 0.8, 1.0, 1.45][index]       # an LLM experiment that keeps growing
    if sid == "mpt-nav-stats":
        f = [1.0, 1.0, 0.9, 0.6, 0.35, 0.2][index]           # being retired
    if sid == "aly-consumption-consolidation-web-api" and index == 3:
        f *= 1.6                                              # a reprocessing spike in September
    return f


# ── catalog.json ────────────────────────────────────────────────────────────────

def build_catalog():
    services = []
    for s in SERVICES:
        sid, product, team, typ, tier, lifecycle, envs, _, dbs, desc = s
        entry = {
            "schemaVersion": 1,
            "id": sid,
            "description": desc,
            "product": product,
            "team": team,
            "lifecycle": lifecycle,
            "type": typ,
            "tier": tier,
            "repo": f"softwareone/{sid.lower().replace('.', '-')}",
            "exposure": "public" if typ == "frontend" or sid in ("swo-platform", "prism-web-api", "Insights.Application.Api") else "internal",
            "customer_facing": typ == "frontend" or tier == 1,
            "environments": {e: {"aks": {"cluster": cluster_for(product, e), "namespace": f"{product}-{e}"}} for e in envs},
            "workloads": {"api": {"kind": "web" if typ == "frontend" else "api"}} if typ != "worker" else {"worker": {"kind": "worker"}},
            "links": {},
            "consumers": {"apis": [], "events": []},
        }
        if typ == "backend" and tier == 1:
            entry["workloads"]["sync"] = {"kind": "worker"}
        if dbs:
            bindings = {}
            for kind, db, server in dbs:
                bindings.setdefault(kind, []).append({
                    "database": db, "server": server,
                    "owner": not (db == "consumption" and sid != "aly-consumption-consolidation-web-api") and not (db == "prism-core" and sid != "prism-web-api"),
                    "workloads": list(entry["workloads"].keys())[:1],
                })
            entry["bindings"] = bindings
        if lifecycle == "deprecated":
            entry["replaced_by"] = "mpt-billing"
        services.append(entry)

    doc = {
        "schemaVersion": 1,
        "catalog_version": CATALOG_VERSION,
        "content_hash": "",
        "products": [{"id": i, "name": n, "lifecycle": "active", "business_owner": b, "technical_owner": t} for i, n, b, t in PRODUCTS],
        "teams": [{"id": i, "lead": l, "deputy": d, "github_team": f"softwareone/{i}", "entra_group": f"sg-{i}",
                   "lifecycle": "active", "products": p} for i, l, d, p in TEAMS],
        "shared": [{"id": i, "kind": k, "owner": o, "description": d} for i, k, o, d, _, _ in SHARED],
        "services": services,
    }
    body = json.dumps({k: v for k, v in doc.items() if k != "content_hash"}, sort_keys=True).encode()
    doc["content_hash"] = "sha256:" + hashlib.sha256(body).hexdigest()
    return doc


# ── status.json ─────────────────────────────────────────────────────────────────

def build_status():
    rng = random.Random(7)
    services, tagged, bindings = [], [], []
    for s in SERVICES:
        sid, product, _, _, _, lifecycle, envs, _, dbs, _ = s
        planned = lifecycle == "planned"
        at = "2026-03-14T09:12:00Z" if not planned else "2026-11-18T10:05:00Z"

        def step(done, detail=None):
            if done is None:
                return {"status": "unknown", "detail": detail or "not observed yet"}
            return {"status": "done", "at": at} if done else {"status": "pending", "detail": detail}

        onboarding = {
            "registered_at": at,
            "repo": {"status": "done", "at": at, "checks": {"exists": True, "component_file": True, "codeowners": True,
                                                            "branch_protection": not planned and sid != "litellm"}},
            "component_file": step(True),
            "namespace": step(not planned, "namespace not created in dev yet"),
            "identity": step(not planned, "workload identity not federated yet"),
            "contract": step(None if sid in ("litellm", "podinfo") else not planned, "no API contract published"),
            "resources": step(not planned, "resources not provisioned"),
            "dev_deploy": step(not planned, "never deployed to dev"),
        }
        warnings = []
        if sid == "litellm":
            warnings.append({"code": "branch_protection", "message": "The default branch has no protection rule."})
        if sid == "mpt-nav-stats":
            warnings.append({"code": "deprecated", "message": "Deprecated: still deployed in 4 environments."})
        if sid == "aws-billing":
            warnings.append({"code": "untagged_resources", "message": "2 resources in its resource group have no service tag."})
        services.append({"id": sid, "stale": False, "unverified": planned, "warnings": warnings,
                         "onboarding": onboarding, "freshness": {"repo": STATUS_GENERATED, "azure": STATUS_GENERATED}})

        if not planned:
            tagged.append({"id": sid, "resources": [
                {"id": rid, "type": typ, "env": "prod"} for rid, typ, _ in resource_ids(s)]})
        for kind, db, server in dbs:
            for env in envs:
                state = "present"
                if sid == "mpt-helpdesk" and env == "staging":
                    state = "absent"
                if server == "shared-mssql-prism" and env == "demo":
                    state = "unresolved"
                bindings.append({"service": sid, "environment": env, "kind": kind, "database": db, "server": server,
                                 "state": state,
                                 "resource": None if state != "present" else shared_server_id(server, env)})

    shared = []
    for sh_id, kind, _, _, _, _ in SHARED:
        if kind not in ("postgres-server", "mssql-server"):
            shared.append({"id": sh_id, "state": "ok", "resources": [
                {"id": shared_server_id(sh_id, "prod"), "type": kind, "env": "prod", "databases": []}]})
            continue
        dbs = {}
        for s in SERVICES:
            for k, db, server in s[8]:
                if server == sh_id:
                    dbs.setdefault(db, []).append(s[0])
        databases = []
        for db, users in sorted(dbs.items()):
            owner = users[0] if len(users) == 1 else next(
                (u for u in users if u in ("aly-consumption-consolidation-web-api", "prism-web-api")), users[0])
            databases.append({"name": db, "state": "linked", "owner": owner, "used_by": users})
        if sh_id == "shared-postgres-mpt":
            databases.append({"name": "legacy_reports", "state": "unclaimed", "owner": None, "used_by": []})
        shared.append({"id": sh_id, "state": "ok", "resources": [
            {"id": shared_server_id(sh_id, "prod"), "type": kind, "env": "prod", "databases": databases}]})

    return {
        "schemaVersion": 1,
        "catalog_version": CATALOG_VERSION,
        "generated_at": STATUS_GENERATED,
        "services": services,
        "azure": {
            "observed_at": STATUS_GENERATED,
            "subscriptions": [SUB_MPT, SUB_PRISM, SUB_DATA],
            "services": tagged,
            "shared": shared,
            "bindings": bindings,
            "unknown_tags": [{"tag": "mpt-legacy-sync", "count": 3}],
            "untagged": [
                {"subscription": SUB_MPT, "resource_group": "networking-hub-rg", "count": 14},
                {"subscription": SUB_MPT, "resource_group": "MC_aks-mpt-weu_nodes", "count": 9},
                {"subscription": SUB_DATA, "resource_group": "legacy-vms-rg", "count": 6},
                {"subscription": SUB_PRISM, "resource_group": "backup-vault-rg", "count": 2},
            ],
        },
    }


def shared_server_id(server, env):
    sub = SUB_PRISM if "prism" in server else SUB_DATA if "pyra" in server else SUB_MPT
    provider = {
        "shared-postgres-mpt": "Microsoft.DBforPostgreSQL/flexibleServers",
        "shared-mssql-prism": "Microsoft.Sql/servers",
        "shared-mssql-pyra": "Microsoft.Sql/servers",
        "aks-mpt-weu": "Microsoft.ContainerService/managedClusters",
        "servicebus-legacy": "Microsoft.ServiceBus/namespaces",
    }[server]
    short = {"production": "prod", "development": "dev"}.get(env, env)
    return f"/subscriptions/{sub}/resourceGroups/shared-{short}-rg/providers/{provider}/{server}-{short}"


# ── cost files ──────────────────────────────────────────────────────────────────

UNALLOCATED = [  # (source, scope, group, monthly USD, unknown tag?)
    ("azure", SUB_MPT, "networking-hub-rg", 2850, False),
    ("azure", SUB_MPT, "MC_aks-mpt-weu_nodes", 1740, False),
    ("azure", SUB_DATA, "legacy-vms-rg", 1310, False),
    ("azure", SUB_PRISM, "backup-vault-rg", 380, False),
    ("azure", SUB_MPT, "mpt-legacy-sync-rg", 460, True),
    ("aws", AWS_ACCOUNT, "shared-networking", 690, False),
]


def build_cost(period, index, complete, generated_at):
    rng = random.Random(f"{period}-cost")
    scale = 1.0 if complete else OPEN_MONTH_FRACTION
    zero = {"total": {}, "direct": {}, "shared": {}, "shared_unsplit": {}, "unallocated": {}}
    src_totals = {"azure": json.loads(json.dumps(zero)), "aws": json.loads(json.dumps(zero))}
    src_rows = {"azure": 0, "aws": 0}

    def add(source, bucket, amount):
        src_totals[source][bucket] = madd(src_totals[source][bucket], amount)
        src_totals[source]["total"] = madd(src_totals[source]["total"], amount)

    services = []
    for s in SERVICES:
        sid, product, team, *_ = s
        base = s[7]
        cur = currency_of(product)
        rate = EUR_RATE if cur == "EUR" else 1.0
        amount = base * month_factor(rng, index, sid) * scale * rate
        resources = []
        if amount:
            aws_part = AWS_SERVICES.get(sid, 0)
            for rid, _, w in resource_ids(s):
                c = r2(amount * (1 - aws_part) * w)
                if c:
                    resources.append({"source": "azure", "id": rid, "cost": {cur: c},
                                      "via": "override" if "Insights/components" in rid and product == "cloudiq" else "tag"})
            if aws_part:
                c = r2(amount * aws_part)
                resources.append({"source": "aws",
                                  "id": f"arn:aws:organizations::{AWS_ACCOUNT}:account/{sid}", "cost": {"USD": c}, "via": "tag"})
        direct = {}
        for r in resources:
            direct = madd(direct, r["cost"])
            add(r["source"], "direct", r["cost"])
            src_rows[r["source"]] += rng.randint(20, 60)
        services.append({"id": sid, "product": product, "team": team, "direct": direct, "shared": {}, "total": dict(direct),
                         "resources": resources, "shared_from": []})

    by_id = {svc["id"]: svc for svc in services}
    shared_out = []
    for sh_id, kind, _, _, monthly, who in SHARED:
        sh_rng = random.Random(f"{period}-{sh_id}")
        cur = "EUR" if "prism" in sh_id else "USD"
        amount = r2(monthly * (1.02 ** index) * sh_rng.uniform(0.97, 1.03) * scale * (EUR_RATE if cur == "EUR" else 1))
        line = {"source": "azure", "id": shared_server_id(sh_id, "prod"), "cost": {cur: amount}, "via": "tag"}
        src_rows["azure"] += sh_rng.randint(10, 30)
        if who == "unsplit":
            add("azure", "shared_unsplit", {cur: amount})
            shared_out.append({"id": sh_id, "cost": {cur: amount}, "split": "equal", "services": [],
                               "unsplit": {cur: amount}, "note": "No service is bound to this resource.", "resources": [line]})
            continue
        if who is None:
            members = sorted({s[0] for s in SERVICES for k, db, server in s[8] if server == sh_id})
        else:
            members = [s[0] for s in SERVICES if who(s)]
        share = amount / len(members)
        given = 0.0
        for i, m in enumerate(members):
            part = r2(amount - given) if i == len(members) - 1 else r2(share)
            given = r2(given + part)
            svc = by_id[m]
            svc["shared"] = madd(svc["shared"], {cur: part})
            svc["total"] = madd(svc["total"], {cur: part})
            svc["shared_from"].append({"shared": sh_id, "cost": {cur: part}})
        add("azure", "shared", {cur: amount})
        shared_out.append({"id": sh_id, "cost": {cur: amount}, "split": "equal", "services": members, "resources": [line]})

    untagged, unknown, by_scope = {}, {}, []
    for source, scope, group, monthly, is_unknown in UNALLOCATED:
        u_rng = random.Random(f"{period}-{group}")
        c = {"USD": r2(monthly * (1.01 ** index) * u_rng.uniform(0.95, 1.05) * scale)}
        if is_unknown:
            unknown = madd(unknown, c)
        else:
            untagged = madd(untagged, c)
        add(source, "unallocated", c)
        src_rows[source] += u_rng.randint(30, 120)
        by_scope.append({"source": source, "scope": scope, "group": group, "cost": c})
    by_scope.sort(key=lambda x: -x["cost"]["USD"])

    totals = json.loads(json.dumps(zero))
    for src in src_totals.values():
        for k in totals:
            totals[k] = madd(totals[k], src[k])

    return {
        "schemaVersion": 2,
        "period": period,
        "complete": complete,
        "generated_at": generated_at,
        "catalog_version": CATALOG_VERSION,
        "sources": [
            {"id": "azure", "rows": src_rows["azure"], "scopes": 3, "totals": src_totals["azure"]},
            {"id": "aws", "rows": src_rows["aws"], "scopes": 1, "totals": src_totals["aws"]},
        ],
        "totals": totals,
        "services": services,
        "shared": shared_out,
        "unallocated": {"untagged": untagged, "unknown_tag": unknown, "by_scope": by_scope},
    }


def write(name, doc):
    (OUT / name).write_text(json.dumps(doc, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    print(f"wrote {name}")


def main():
    write("catalog.json", build_catalog())
    write("status.json", build_status())
    for i, period in enumerate(CLOSED_MONTHS):
        y, m = map(int, period.split("-"))
        nxt = f"{y + (m == 12):04d}-{m % 12 + 1:02d}-03T05:00:00Z"
        write(f"cost-{period}.json", build_cost(period, i, True, nxt))
    write("cost.json", build_cost(OPEN_MONTH, len(CLOSED_MONTHS), False, OPEN_MONTH_GENERATED))


if __name__ == "__main__":
    main()
