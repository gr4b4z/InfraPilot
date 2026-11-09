"""InfraPortal wiring for product mpt while its services move from the marketplace SDP to mpt-release.

Plan: migrate-mpt-services.md. Dry run by default; --apply executes, --undo reverses. Prod
InfraPortal only (DEPLOYMENTS_URL / DEPLOYMENTS_API_KEY, admin role).

    python migrate-mpt-services.cutover.py              # dry run
    python migrate-mpt-services.cutover.py --apply
    python migrate-mpt-services.cutover.py --undo --apply

Wired once for the product, never per service. Every mpt approval then reaches both deployers, and
each skips the services it does not own:
- ADO subscription mpt-promotion-approved (unchanged) -> SDP, which skips marketplace sdp.exclude;
- "mpt-release deploy-build" and "GithubPromotion" (products += mpt) -> mpt-release, which skips
  its disabledComponents.
Moving a service is then a change in the two repositories only.

1. Policy mpt build -> dev: product-wide, auto-approved, auto-created from refs/heads/master,
   sourceRequiresDeploy false (build is a synthetic env), modelled on the mpt-extensions default.
   SDP never reads dev promotions; SDP's own dev deploy closes the candidate of a service it owns.
2. Subscription "mpt-release deploy-build": products += mpt.
3. Subscription "GithubPromotion": products += mpt.

Apply only once mpt-release's disabledComponents lists every service the SDP still deploys;
otherwise an approved build of a missing one lands in mpt-release and is deployed twice."""
import argparse, json, os, sys, urllib.error, urllib.request

PRODUCT = 'mpt'
SUBSCRIPTIONS = ['mpt-release deploy-build', 'GithubPromotion']
POLICY_FIELDS = ('escalationGroup', 'tracksWorkItems', 'requiredWorkItemRoles', 'requireAllWorkItemsApproved',
                 'requireAllWorkItemInstancesApproved', 'autoApproveOnAllWorkItemsApproved', 'autoApproveWhenNoWorkItems',
                 'sourceRequiresDeploy', 'deploysOnApproval', 'autoCreateFromBranches', 'approvedWebhookDelaySeconds')
MASTER = ['refs/heads/master']

parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
parser.add_argument('--undo', action='store_true', help='remove the wiring again')
parser.add_argument('--apply', action='store_true', help='execute; without it nothing is written')
args = parser.parse_args()

url = os.environ['DEPLOYMENTS_URL'].rstrip('/'); key = os.environ['DEPLOYMENTS_API_KEY']

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

pols = get('/api/promotions/admin/policies')['policies']
def find_policy(product, service, src, tgt):
    m = [p for p in pols if p['product'] == product and p['service'] == service and p['sourceEnv'] == src and p['targetEnv'] == tgt]
    return m[0] if m else None

subs = get('/api/webhooks')
subs = subs if isinstance(subs, list) else subs.get('subscriptions', subs)
def find_sub(name):
    m = [s for s in subs if s['name'] == name]
    if len(m) != 1: sys.exit(f'expected one subscription named {name!r}, found {len(m)}')
    return m[0]

ops = []
policy = find_policy(PRODUCT, None, 'build', 'dev')
if args.undo and policy:
    ops.append(('DELETE', f"/api/promotions/admin/policies/{policy['id']}", None, f'delete {PRODUCT} build->dev'))
elif not args.undo and not policy:
    template = find_policy('mpt-extensions', None, 'build', 'dev')
    if not template or template['steps']: sys.exit('mpt-extensions build -> dev default (auto-approved) not found')
    body = {k: template[k] for k in POLICY_FIELDS}
    body.update(product=PRODUCT, service=None, sourceEnv='build', targetEnv='dev', steps=[],
                tracksWorkItems=False, sourceRequiresDeploy=False, autoCreateFromBranches=MASTER)
    ops.append(('POST', '/api/promotions/admin/policies', body, f'{PRODUCT} build->dev (auto, refs/heads/master)'))

for name in SUBSCRIPTIONS:
    sub = find_sub(name); f = sub['filters']
    # An empty set means "any": a services filter would narrow every product, and dropping the last
    # product would open the subscription to all of them.
    if f['services']: sys.exit(f'{name!r} has a services filter {f["services"]}; this script expects none')
    products = [p for p in f['products'] if p != PRODUCT] if args.undo else sorted(set(f['products']) | {PRODUCT})
    if sorted(products) == sorted(f['products']): continue
    if not products: sys.exit(f'{name!r} would be left with no product filter, which means every product')
    ops.append(('PUT', f"/api/webhooks/{sub['id']}", {'filters': {'products': products, 'services': [], 'environments': f['environments']}},
                f"{name!r}: products {f['products']} -> {products}"))

if not ops:
    print('nothing to do')
for method, path, body, label in ops:
    if not args.apply:
        print('DRY', method, label); continue
    status, res = call(method, path, body)
    print(status, method, label, res.get('id') if isinstance(res, dict) else (res or '')[:200])
    if status >= 300: sys.exit('stopping: ' + label)
