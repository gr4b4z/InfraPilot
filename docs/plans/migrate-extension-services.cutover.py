"""InfraPortal half of moving the marketplace extension services to product mpt-extensions.

Plan: migrate-mpt-services.md ("Extension services"). Dry run by default; --apply executes, --undo
reverses. Prod InfraPortal only (DEPLOYMENTS_URL / DEPLOYMENTS_API_KEY, admin role).

    python migrate-extension-services.cutover.py              # dry run
    python migrate-extension-services.cutover.py --apply
    python migrate-extension-services.cutover.py --undo --apply
    python migrate-extension-services.cutover.py --dispatch-dev [--apply]   # after the mpt-release seed merged

For each service:
1. A catch-all service product override -> mpt-extensions. Anything still posted as mpt (SDP deploy
   events, builds from a pipeline that has not been updated, manifests seeded with spec.product mpt)
   is filed under mpt-extensions from then on.
2. A remap of its history (deploy events, builds, promotions with their sign-offs) into
   mpt-extensions, so promotion candidates find their source deploys under the same product.

The services' build pipelines pass product: mpt-extensions themselves, so the overrides can be
deleted once no manifest with spec.product mpt is left in an environment.

Run it in the cutover sitting, right before the marketplace and mpt-release PRs merge. From the
moment the override exists, new approvals for these services are mpt-extensions approvals: they go
to mpt-release only, not to the SDP. Pending candidates move with the remap and stay approvable.
An Approved or Deploying one would race the hand-over, so the script stops on it. The exception is an
Approved build -> dev: the SDP never acts on dev promotions, and mpt-release skipped it while the
service was disabled, so it is waiting rather than in flight.

--dispatch-dev deploys those waiting build -> dev candidates once the mpt-release seed is merged: one
Deploy Registered Build run (workflow_dispatch, gh) per candidate, with its digest-pinned manifest.
InfraPortal's bulk resend would also resend other mpt-extensions services' stale approvals.

--undo points each override back at mpt, remaps the history back and deletes the override."""
import argparse, json, os, subprocess, sys, urllib.error, urllib.parse, urllib.request

SERVICES = ['mpt-extensions', 'mpt-web-extensions', 'mpt-web-extensions-pages', 'mpt-extensions-ui-bootstrap',
            'swo-extension-nav', 'swo-extension-adobe', 'swo-extension-aws']
TARGET, SOURCE = 'mpt-extensions', 'mpt'
REASON = 'Migrated: deployed by mpt-release; the build pipeline passes product mpt-extensions (migrate-mpt-services.md)'
COUNTS = ('deployments', 'builds', 'buildConflicts', 'promotions', 'openPromotions', 'ticketApprovals', 'strandedTicketApprovals')

parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
parser.add_argument('--undo', action='store_true', help='move the services back to mpt and delete the overrides')
parser.add_argument('--apply', action='store_true', help='execute; without it nothing is written')
parser.add_argument('--dispatch-dev', action='store_true', help='deploy the approved build -> dev candidates through mpt-release')
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

def candidates(product, svc, status):
    # The service filter matches substrings: mpt-extensions also returns mpt-extensions-ui-bootstrap.
    q = urllib.parse.urlencode({'product': product, 'service': svc, 'status': status, 'limit': 50})
    return [c for c in get(f'/api/promotions?{q}').get('candidates') or [] if c['status'] == status and c['service'] == svc]

# An approval in flight would be dispatched by the product it was filed under before the switch and
# skipped by the deployer that owns the service after it. An approved build -> dev is not in flight:
# the SDP never acts on dev promotions, and mpt-release skipped it while the service was disabled.
busy, waiting = [], []
for svc in SERVICES:
    for product in (SOURCE, TARGET):
        for status in ('Approved', 'Deploying'):
            for c in candidates(product, svc, status):
                if status == 'Approved' and (c['sourceEnv'], c['targetEnv']) == ('build', 'dev'):
                    waiting.append((product, svc, c))
                else:
                    busy.append(f"{svc} {c['sourceEnv']}->{c['targetEnv']} {c['version']} ({product}, {status})")

if args.dispatch_dev:
    for product, svc, c in waiting:
        q = urllib.parse.urlencode({'product': product, 'service': svc, 'limit': 50})
        build = next((b for b in get(f'/api/builds?{q}').get('results') or [] if b['version'] == c['version']), None)
        if not build or not build.get('artifactRef') or not build.get('artifactDigest'):
            print(f"{svc} {c['version']}: no registered build with a manifest digest, skipped"); continue
        ref = build['artifactRef']
        if '@sha256:' not in ref:
            # Pin the tag to the registered digest, as Deploy Registered Build does for a candidate.
            repo = ref[:ref.rfind(':')] if ref.rfind(':') > ref.rfind('/') else ref
            ref = f"{repo}@{build['artifactDigest']}"
        cmd = ['gh', 'workflow', 'run', 'deploy-registered-build.yml', '--repo', 'softwareone-platform/mpt-release',
               '-f', 'targetEnvironment=dev', '-f', f'manifestRef={ref}', '-f', f'componentName={svc}', '-f', f"version={c['version']}"]
        if not args.apply:
            print('DRY', ' '.join(cmd)); continue
        print(subprocess.run(cmd).returncode, svc, c['version'])
    sys.exit(0)

if busy: sys.exit('in-flight promotions, let them finish first: ' + '; '.join(busy))
for product, svc, c in waiting:
    print(f"approved build -> dev {svc} {c['version']} ({product}): waiting; deploy it with --dispatch-dev after the hand-over")

overrides = {o['service'].lower(): o for o in get('/api/deployments/admin/product-overrides') if o['fromProduct'] is None}

for svc in SERVICES:
    row = overrides.get(svc)
    pending = [f"{c['sourceEnv']}->{c['targetEnv']} {c['version']}" for c in candidates(from_product, svc, 'Pending')]
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
