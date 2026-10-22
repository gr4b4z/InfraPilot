"""InfraPortal wiring for an mpt service that moves from the marketplace SDP to mpt-release.

Plan: migrate-mpt-services.md. Dry run by default; --apply executes. Prod InfraPortal only
(DEPLOYMENTS_URL / DEPLOYMENTS_API_KEY, admin role).

    python migrate-mpt-services.cutover.py --service mpt-spotlight            # dry run
    python migrate-mpt-services.cutover.py --service mpt-spotlight --apply
    python migrate-mpt-services.cutover.py --service mpt-spotlight --undo --apply

Per service it makes sure of:
1. Policy mpt/<service> build -> dev: auto-approved, auto-created from refs/heads/master,
   sourceRequiresDeploy false (build is a synthetic env), modelled on the mpt-extensions default.
   The product has no build -> dev of its own, so SDP services get no dev candidates from it.
2. Subscription "mpt-release deploy-build (mpt)": github, event_type deploy-build, products [mpt],
   environments [dev, staging] (the targets of build -> * edges), services += <service>.
3. Subscription "mpt-release promote-single-build-manifest (mpt)": github, event_type
   promote-single-build-manifest, products [mpt], environments [test, prod] (dev -> test and
   staging -> prod; mpt has no test -> staging), services += <service>. Direct, not through the
   GithubPromotion relay function.

Filters are include-only and AND-ed across dimensions, so the mpt-extensions subscriptions cannot
take one mpt service; these two are dedicated to migrated mpt services. An empty services filter
means every service, so neither is ever left with one: --undo deactivates a subscription instead of
emptying it. The ADO subscription mpt-promotion-approved stays as it is: it cannot exclude a service,
and marketplace drops sdp.exclude services itself.

Creating a subscription needs a token that may dispatch to softwareone-platform/mpt-release, read
from MPT_RELEASE_DISPATCH_TOKEN. It is never printed."""
import argparse, json, os, sys, urllib.error, urllib.request

PRODUCT = 'mpt'
DISPATCH_URL = 'https://api.github.com/repos/softwareone-platform/mpt-release/dispatches'
SUBSCRIPTIONS = [
    {'name': 'mpt-release deploy-build (mpt)', 'githubEventType': 'deploy-build', 'environments': ['dev', 'staging']},
    {'name': 'mpt-release promote-single-build-manifest (mpt)', 'githubEventType': 'promote-single-build-manifest', 'environments': ['test', 'prod']},
]
POLICY_FIELDS = ('escalationGroup', 'tracksWorkItems', 'requiredWorkItemRoles', 'requireAllWorkItemsApproved',
                 'requireAllWorkItemInstancesApproved', 'autoApproveOnAllWorkItemsApproved', 'autoApproveWhenNoWorkItems',
                 'sourceRequiresDeploy', 'deploysOnApproval', 'autoCreateFromBranches', 'approvedWebhookDelaySeconds')

parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
parser.add_argument('--service', action='append', required=True, help='mpt service to wire (repeatable)')
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

template = find_policy('mpt-extensions', None, 'build', 'dev')
assert template and not template['steps'], 'mpt-extensions build -> dev default (auto-approved) not found'
assert find_policy(PRODUCT, None, 'build', 'dev') is None, \
    'mpt now has a product-wide build -> dev: every mpt master build would get a dev candidate; revisit this script'

subs = get('/api/webhooks')
subs = subs if isinstance(subs, list) else subs.get('subscriptions', subs)
def find_sub(name):
    m = [s for s in subs if s['name'] == name]
    assert len(m) <= 1, f'{len(m)} subscriptions named {name!r}'
    return m[0] if m else None

ops = []
for service in args.service:
    existing = find_policy(PRODUCT, service, 'build', 'dev')
    if args.undo and existing:
        ops.append(('DELETE', f"/api/promotions/admin/policies/{existing['id']}", None, f'delete {PRODUCT}/{service} build->dev'))
    elif not args.undo and not existing:
        body = {k: template[k] for k in POLICY_FIELDS}
        body.update(product=PRODUCT, service=service, sourceEnv='build', targetEnv='dev', steps=[],
                    tracksWorkItems=False, sourceRequiresDeploy=False, deploysOnApproval=True,
                    autoCreateFromBranches=['refs/heads/master'])
        ops.append(('POST', '/api/promotions/admin/policies', body, f'{PRODUCT}/{service} build->dev (auto, refs/heads/master)'))

for spec in SUBSCRIPTIONS:
    sub = find_sub(spec['name'])
    current = set(sub['filters']['services']) if sub else set()
    if sub and not current:
        sys.exit(f"{spec['name']!r} has no services filter, so it dispatches every {PRODUCT} service; fix it by hand first")
    wanted = (current - set(args.service)) if args.undo else (current | set(args.service))
    active = bool(sub and sub['active'])
    if sub and wanted == current and (args.undo or active):
        continue
    filters = {'products': [PRODUCT], 'services': sorted(wanted or current), 'environments': spec['environments']}
    if not sub:
        if args.undo: continue
        token = os.environ.get('MPT_RELEASE_DISPATCH_TOKEN', '')
        if args.apply and not token: sys.exit(f"MPT_RELEASE_DISPATCH_TOKEN is required to create {spec['name']!r}")
        body = {'name': spec['name'], 'url': DISPATCH_URL, 'events': ['promotion.approved'], 'filters': filters,
                'targetType': 'github', 'secret': token, 'githubEventType': spec['githubEventType']}
        ops.append(('POST', '/api/webhooks', body, f"create {spec['name']!r} services={filters['services']} environments={spec['environments']}"))
    elif not wanted:
        # Emptying the filter would mean every mpt service; stop dispatching instead.
        ops.append(('PUT', f"/api/webhooks/{sub['id']}", {'active': False}, f"deactivate {spec['name']!r} (no migrated service left)"))
    else:
        ops.append(('PUT', f"/api/webhooks/{sub['id']}", {'filters': filters, 'active': True},
                    f"{spec['name']!r}: services {sorted(current)} -> {filters['services']}"))

if not ops:
    print('nothing to do')
for method, path, body, label in ops:
    if not args.apply:
        print('DRY', method, label); continue
    status, res = call(method, path, body)
    print(status, method, label, res.get('id') if isinstance(res, dict) else (res or '')[:200])
    if status >= 300: sys.exit('stopping: ' + label)
