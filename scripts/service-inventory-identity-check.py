#!/usr/bin/env python3
"""
Measure how the portal's services line up with the service inventory's catalog ids.

The portal names a service by (product, service) from deploy events; the catalog by one globally
unique `id`. This prints the numbers the join rule is chosen from. Read-only.

    DEPLOYMENTS_URL=... DEPLOYMENTS_API_KEY=... \
        python3 scripts/service-inventory-identity-check.py catalog.json [--details]

`catalog.json` is the published catalog file. The key is read from the environment and never printed.
Portal services are the (product, service) pairs in the current deployment state of any environment.
"""
import json
import os
import sys
import urllib.request
from collections import defaultdict


def portal_services(base: str, key: str) -> set[tuple[str, str]]:
    req = urllib.request.Request(f"{base.rstrip('/')}/api/deployments/state", headers={"X-Api-Key": key})
    with urllib.request.urlopen(req, timeout=60) as r:
        rows = json.load(r)
    return {(row["product"], row["service"]) for row in rows}


def main() -> int:
    args = [a for a in sys.argv[1:] if not a.startswith("--")]
    details = "--details" in sys.argv
    base, key = os.environ.get("DEPLOYMENTS_URL"), os.environ.get("DEPLOYMENTS_API_KEY")
    if not base or not key or not args:
        print(__doc__)
        return 2

    catalog = json.load(open(args[0]))
    catalog_ids = {s["id"]: s for s in catalog["services"]}
    pairs = portal_services(base, key)

    by_name = defaultdict(set)
    for product, service in pairs:
        by_name[service].add(product)
    by_lower = defaultdict(set)
    for name in by_name:
        by_lower[name.lower()].add(name)

    exact = {n for n in by_name if n in catalog_ids}
    folded = {n for n in by_name if n.lower() in catalog_ids}
    multi_product = {n: p for n, p in by_name.items() if len(p) > 1}
    case_variants = {k: v for k, v in by_lower.items() if len(v) > 1}
    product_differs = {
        n for n in folded
        if catalog_ids[n.lower()]["product"].lower() not in {p.lower() for p in by_name[n]}
    }
    unmatched_ids = sorted(set(catalog_ids) - {n.lower() for n in by_name})

    print(f"portal (product, service) pairs        {len(pairs)}")
    print(f"distinct portal service names          {len(by_name)}")
    print(f"names under more than one product      {len(multi_product)}")
    print(f"names differing only by case           {len(case_variants)}")
    print(f"catalog services                       {len(catalog_ids)}")
    print(f"portal names equal to a catalog id     {len(exact)} exact, {len(folded)} ignoring case")
    print(f"  of which under more than one product {len(folded & set(multi_product))}")
    print(f"  of which catalog product differs     {len(product_differs)}")
    print(f"catalog ids with no portal service     {len(unmatched_ids)}")
    if details:
        print("\nmulti-product names:", json.dumps({n: sorted(p) for n, p in sorted(multi_product.items())}, indent=1))
        print("matched:", sorted(folded))
        print("product differs:", {n: (catalog_ids[n.lower()]["product"], sorted(by_name[n])) for n in sorted(product_differs)})
        print("catalog ids without a portal service:", unmatched_ids)
    return 0


if __name__ == "__main__":
    sys.exit(main())
