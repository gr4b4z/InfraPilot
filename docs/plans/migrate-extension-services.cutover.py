"""InfraPortal half of moving the marketplace extension services to product mkp-extensions.

Plan: migrate-mkp-services.md ("Extension services"). Dry run by default; --apply executes, --undo
reverses. Prod InfraPortal only (DEPLOYMENTS_URL / DEPLOYMENTS_API_KEY, admin role).

    python migrate-extension-services.cutover.py              # dry run
    python migrate-extension-services.cutover.py --apply
    python migrate-extension-services.cutover.py --undo --apply

For each service:
1. A catch-all service product override -> mkp-extensions. Anything still posted as mkp (SDP deploy
   events, builds from a pipeline that has not been updated, manifests seeded with spec.product mkp)
   is filed under mkp-extensions from then on.
2. A remap of its history (deploy events, builds, promotions with their sign-offs) into
   mkp-extensions, so promotion candidates find their source deploys under the same product.

The services' build pipelines pass product: mkp-extensions themselves, so the overrides can be
deleted once no manifest with spec.product mkp is left in an environment.

Run it in the cutover sitting, right before the marketplace and mkp-release PRs merge. From the
moment the override exists, new approvals for these services are mkp-extensions approvals: they go
to mkp-release only, not to the SDP. Pending candidates move with the remap and stay approvable.
An Approved or Deploying one would race the hand-over, so the script stops on it.

--undo points each override back at mkp, remaps the history back and deletes the override."""
import argparse, json, os, sys, urllib.error, urllib.parse, urllib.request

SERVICES = ['mkp-extensions', 'mkp-web-extensions', 'mkp-web-extensions-pages', 'mkp-extensions-ui-bootstrap',
            'acx-extension-nav', 'acx-extension-adobe', 'acx-extension-aws']
TARGET, SOURCE = 'mkp-extensions', 'mkp'
REASON = 'Migrated: deployed by mkp-release; the build pipeline passes product mkp-extensions (migrate-mkp-services.md)'
COUNTS = ('deployments', 'builds', 'buildConflicts', 'promotions', 'openPromotions', 'ticketApprovals', 'strandedTicketApprovals')

parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
parser.add_argument('--undo', action='store_true', help='move the services back to mkp and delete the overrides')
parser.add_argument('--apply', action='store_true', help='execute; without it nothing is written')
args = parser.parse_args()

url = os.environ['DEPLOYMENTS_URL'].rstrip('/'); key = os.environ['DEPLOYMENTS_API_KEY']
to_product, from_product = (SOURCE, TARGET) if args.undo else (TARGET, SOURCE)

def call(method, path, body=None):
    req = urllib.request.Request(url + path, data=json.dumps(body).encode() if body is not None else None, method=method,
                                 headers={'X-Api-Key': key, 'Content-Type': 'application/json'})
    try:
        with urllib.request.urlopen(req) as r: raw = r.read(); return r.status, (json.loads(raw) if raw else None)
    except urllib.error.HTTPError as e: return e.code, e.read().decode()

def get(path):
    status, body = call('GET', path)
    if status != 200: sys.exit(f'GET {path} returned {status}: {body}')
    return body

def must(status, body, label):
    print(status, label)
    if status >= 300: sys.exit(f'stopping: {label}: {body}')
    return body

def counts(plan):
    return ', '.join(f'{k} {plan[k]}' for k in COUNTS if plan.get(k))

# An approval in flight would be dispatched by the product it was filed under before the switch and
# skipped by the deployer that owns the service after it.
busy = []
for svc in SERVICES:
    for status in ('Approved', 'Deploying'):
        q = urllib.parse.urlencode({'product': from_product, 'service': svc, 'status': status, 'limit': 50})
        busy += [f"{svc} {c['sourceEnv']}->{c['targetEnv']} {c['version']} ({status})"
                 for c in get(f'/api/promotions?{q}').get('candidates') or [] if c['status'] == status]
if busy: sys.exit('in-flight promotions, let them finish first: ' + '; '.join(busy))

overrides = {o['service'].lower(): o for o in get('/api/deployments/admin/product-overrides') if o['fromProduct'] is None}

for svc in SERVICES:
    row = overrides.get(svc)
    q =urllib.parse.urlencode({'product': from_product, 'service': svc, 'status': 'Pending', 'limit': 50})
    pending = [f"{c['sourceEnv']}->{c['targetEnv']} {c['version']}" for c in get(f'/api/promotions?{q}').get('candidates') or []]
    note = f" (pending, moves along: {'; '.join(pending)})" if pending else ''

    if args.undo and row is None:
        print(f'{svc}: no override, nothing to undo'); continue
    if not args.undo and row is not None and row['product'] == TARGET and not row['strandedEntities']:
        print(f'{svc}: already on {TARGET}'); continue
    if not args.apply:
        state = f"override {row['product']}, {row['strandedEntities']} stranded" if row else 'no override'
        print(f'DRY {svc}: {state} -> override {to_product} + remap{note}')
        continue

    row = must(*call('POST', '/api/deployments/admin/product-overrides',
                     {'service': svc, 'product': to_product, 'fromProduct': None, 'reason': None if args.undo else REASON}),
               f'{svc}: override -> {to_product}')
    preview = must(*call('GET', f"/api/deployments/admin/product-overrides/{row['id']}/remap"), f'{svc}: remap preview')
    print('   would move:', counts(preview) or 'nothing')
    applied = must(*call('POST', f"/api/deployments/admin/product-overrides/{row['id']}/remap"), f'{svc}: remap{note}')
    print('   moved:', counts(applied) or 'nothing')
    if args.undo:
        must(*call('DELETE', f"/api/deployments/admin/product-overrides/{row['id']}"), f'{svc}: override deleted')
