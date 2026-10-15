"""Staging cutover: every mpt / mpt-extensions release build reaches staging through build -> staging.

Order matters only for the dry-run reader: build -> staging edges are switched on before the
stable -> staging defaults are deleted, so no release build falls between the two."""
import json, os, sys, glob, urllib.request, urllib.error
url = os.environ['DEPLOYMENTS_URL'].rstrip('/'); key = os.environ['DEPLOYMENTS_API_KEY']
APPLY = '--apply' in sys.argv
RELEASE = ['refs/heads/release/*']

def call(method, path, body=None):
    req = urllib.request.Request(url + path, data=json.dumps(body).encode() if body is not None else None, method=method,
                                 headers={'X-Api-Key': key, 'Content-Type': 'application/json'})
    try:
        with urllib.request.urlopen(req) as r: raw = r.read(); return r.status, (json.loads(raw) if raw else None)
    except urllib.error.HTTPError as e: return e.code, e.read().decode()

pols = call('GET', '/api/promotions/admin/policies')[1]['policies']
def find(product, service, src, tgt):
    m = [p for p in pols if p['product'] == product and p['service'] == service and p['sourceEnv'] == src and p['targetEnv'] == tgt]
    return m[0] if m else None
def body_from(p, **over):
    b = {k: p[k] for k in ('product','service','sourceEnv','targetEnv','escalationGroup','tracksWorkItems','requiredWorkItemRoles',
         'requireAllWorkItemsApproved','requireAllWorkItemInstancesApproved','autoApproveOnAllWorkItemsApproved',
         'autoApproveWhenNoWorkItems','sourceRequiresDeploy','deploysOnApproval','autoCreateFromBranches','approvedWebhookDelaySeconds')}
    b['steps'] = [{'name': s['name'], 'requirements': [{'name': r['name'], 'groups': r['groups'], 'users': r['users'], 'minApprovers': r['minApprovers']} for r in s['requirements']]} for s in p['steps']]
    b.update(over); return b

ops = []
mpt_stable = find('mpt', None, 'stable', 'staging')
ext_stable = find('mpt-extensions', None, 'stable', 'staging')
mscsp_stable = find('mpt-extensions', 'swo-extension-mscsp', 'stable', 'staging')
ext_build = find('mpt-extensions', None, 'build', 'staging')
aws_build = find('mpt-extensions', 'mpt-extension-aws-management', 'build', 'staging')
assert mpt_stable and ext_stable and mscsp_stable and ext_build and aws_build, 'unexpected policy set'
assert find('mpt', None, 'build', 'staging') is None, 'mpt build -> staging already exists'

# 1. mpt: build -> staging with the same approvers and work-item rules as stable -> staging.
ops.append(('POST', '/api/promotions/admin/policies', body_from(mpt_stable, sourceEnv='build', sourceRequiresDeploy=False, autoCreateFromBranches=RELEASE), 'mpt build->staging (release/*)'))
# 2. No stable -> staging anywhere: services that do not register release builds yet (swo-auth,
#    swo-extension-nav, swo-web-shell) get no staging promotion until they do.
# 3. mpt-extensions: the dormant default goes live; the aws-management copy is now identical to it.
ops.append(('PUT', f"/api/promotions/admin/policies/{ext_build['id']}", body_from(ext_build, autoCreateFromBranches=RELEASE), 'mpt-extensions build->staging auto-create release/*'))
ops.append(('DELETE', f"/api/promotions/admin/policies/{aws_build['id']}", None, 'delete aws-management build->staging copy (product default covers it)'))
# 4. No more stable -> staging promotions (pending ones keep their snapshot and stay approvable).
ops.append(('DELETE', f"/api/promotions/admin/policies/{ext_stable['id']}", None, 'delete mpt-extensions stable->staging'))
ops.append(('DELETE', f"/api/promotions/admin/policies/{mscsp_stable['id']}", None, 'delete swo-extension-mscsp stable->staging'))
ops.append(('DELETE', f"/api/promotions/admin/policies/{mpt_stable['id']}", None, 'delete mpt stable->staging (default)'))
# 5. Work items for mpt build candidates come from mpt-release's refresh workflow, like the extensions'.
subs = call('GET', '/api/webhooks')[1]
subs = subs if isinstance(subs, list) else subs.get('subscriptions', subs)
wi = [w for w in subs if w['name'].startswith('mpt-release update-promotion-work-items')]
assert len(wi) == 1, 'work-item subscription not found'
wi = wi[0]; f = wi['filters']
if 'mpt' not in f['products']:
    ops.append(('PUT', f"/api/webhooks/{wi['id']}", {'filters': {'products': f['products'] + ['mpt'], 'services': f['services'], 'environments': f['environments']}}, 'work-item refresh webhook: products += mpt'))

for method, path, body, label in ops:
    if not APPLY:
        print('DRY', method, label); continue
    st, res = call(method, path, body)
    print(st, method, label, (res.get('id') if isinstance(res, dict) else (res or ''))[:200] if not isinstance(res, dict) else res.get('id'))
    if st >= 300: sys.exit('stopping: ' + label)
