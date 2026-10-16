# MCP Server and Personal Access Tokens

InfraPortal serves a [Model Context Protocol](https://modelcontextprotocol.io) server so coding agents
(Claude Code, GitHub Copilot, any MCP client) can read deployment state, work with promotions and
diagnose problems through typed tools instead of hand-written REST calls.

```
POST {DEPLOYMENTS_URL}/api/mcp        (Streamable HTTP, stateless)
```

It lives inside the API (`src/Platform.Api/Mcp/`), not beside it: the tools call the same services
the REST endpoints do, so there is one copy of every rule, and they reach what REST does not expose —
the diagnosis playbooks and the knowledge base behind the in-app assistant. It is mounted under
`/api` because that is the prefix the production container's nginx forwards to the API.

## Authentication

Exactly what the `/api` groups accept (`CanApprove` policy): an `X-Api-Key` header — a configured key
or a personal access token — or a bearer token. Stateless mode means no session affinity across
replicas and every tool runs inside the HTTP request that called it, so `ICurrentUser` and the
claim checks see the caller. A per-caller limit of 300 requests/minute (`Mcp:RateLimit:PermitLimit`)
applies, separate from the ingestion limiter so an agent loop cannot starve a pipeline.

### Personal access tokens

Any signed-in user creates tokens for themselves on the **Agent access** page (`/me/agent-access`,
user menu), which also shows copy-paste setup for Claude Code, a project `.mcp.json` and VS Code.

| | |
|---|---|
| Format | `ipt_` + 43 url-safe characters; only its SHA-256 is stored, the secret is shown once |
| Header | `X-Api-Key` — same as configured keys, so every client set up for a key works unchanged |
| Identity | Its owner: `oid`, `name`, `preferred_username` claims, so comments, triggered-by and audit name the person |
| Access | `Standard` (default), `QA` or `Admin`, chosen at creation, never above the owner's own role (see below) |
| Roles | The owner's roles when it was created, **minus `InfraPortal.Admin`** unless it is an Admin token; a QA token adds `InfraPortal.QA` (none ⇒ `InfraPortal.User`) |
| Lifetime | 7, 30 or 90 days, always finite; revocable by its owner on the page and by an admin in Settings → Agent Tokens; `lastUsedAt` refreshed at most every 5 min |
| Owner check | Refused within 5 min once the directory reports the owner disabled or deleted (see below) |
| Limit | 10 active tokens per person |

#### Access levels

A token's access (`PersonalTokenAccess`, the `token_access` claim) decides whether it may make the
decisions a person makes: approving, rejecting, signing off. A token is how an agent acts for
someone, and an agent can be talked into a decision by text it reads, so the default makes none.
The owner opts in per token, and only up to their own role:

| Access | Who may create it | Roles on the token | Decisions it may make |
|---|---|---|---|
| `Standard` | anyone | owner's roles minus Admin | none |
| `QA` | `InfraPortal.QA` or `InfraPortal.Admin` | owner's roles minus Admin, plus `InfraPortal.QA` (an admin without QA gets it here) | work-item sign-off: approvals, issues, blocks |
| `Admin` | `InfraPortal.Admin` | owner's roles, Admin included | every one the owner can make: promotion approve / reject / cancel-approval / bulk approve, admin bypass / reject / orphan sweep, rollback approve / reject / override, request approvals, work-item sign-off |

`POST /api/me/tokens { name, expiresInDays, access? }` returns `403` for a level above the caller's
role, `400` for an unknown one; `GET /api/me/tokens` lists `allowedAccess` so the page offers only
those. Each decision route carries `.RequirePersonalTokenAccess(QA | Admin)`, which returns `403` to a
token below that level. A decision a token gets through still passes the route's own checks (approver
eligibility, the QA-or-Admin jurisdiction) as its owner, and adds a `personal_token.decision` audit
entry (`auth` module, entity = the token, metadata `{ method, path, status, access }`): the decision's
own record names the owner, and this entry is what says an agent made it.

Roles are a snapshot (see the owner check below): an Admin token stays Admin until it expires or is
revoked, even if the owner loses the role sooner. Revoke a person's tokens when their role changes.

What no token can do, and where that is enforced:

- **Pipeline-only endpoints** — deploy-event ingest, build registration, `POST /api/promotions`: the
  `DeploymentIngestion` policy requires `auth_method=api_key`, which only configured keys carry.
- **Decisions, below its access** — see the table above.
- **Admin groups, below Admin** — only an Admin token carries the Admin role.
- **Managing tokens** — `/api/me/tokens` takes a signed-in session only (`SignedInSession` policy),
  so a token cannot mint or revoke tokens, whatever its access.
- **Manual deploys, below Admin** — a token's identity type is `PersonalToken`, not `ApiKey`, so
  `POST /api/deployments/manual` treats it as a person, which must be admin.

Endpoints (signed-in session): `GET /api/me/tokens`, `POST /api/me/tokens { name, expiresInDays }`
→ `201` with `token` (the secret, once), `DELETE /api/me/tokens/{id}` (revoke). Creation and
revocation are audit-logged (`auth` module).

#### Owner check (disabled or deleted accounts)

A token carries a snapshot of its owner from creation time, so on its own it would outlive an
offboarding by up to 90 days. `PersonalTokenOwnerCheck` closes that: on each use it asks
`IIdentityService.GetAccountStatus` about the owner and caches the answer per owner for 5 minutes
(`CheckInterval`), so an agent's burst of calls costs one directory read per owner per window on
each replica.

- **Msal mode (Graph app configured):** `GET users/{oid}?$select=id,accountEnabled`, within the
  `User.Read.All` the Graph app already holds. `accountEnabled: false` → disabled, `404` → deleted.
- **Local mode:** the local user's `IsActive` flag, the same one that stops them signing in.
- **Fails closed only on a positive answer.** If the directory cannot answer (Graph throttled or
  down, an id the stub does not know), the token keeps working and the owner is looked up again
  after 1 minute (`RetryAfterUnknown`). A directory outage should not stop every agent at once.
- **Not re-checked: role assignments.** Reading a user's app-role assignments needs Graph's
  `Directory.Read.All`, which the Graph app does not have. Offboarding disables the account, which
  the check does catch. When someone loses a role but keeps their account, an admin revokes their
  tokens — QA and Admin ones first: those carry the role and make decisions with it until they
  expire. The admin list badges them.

A refusal is a plain `401`, logged once per owner per window as a warning (`Refusing the personal
access tokens of …`). Re-enabling the account restores the tokens within the same window; nothing
is revoked by the check itself.

#### Administration

Settings → **Agent Tokens** (`/settings/agent-tokens`, admin only) lists everyone's tokens grouped
by owner. Pasting a leaked token into its search finds it by prefix in the browser; the secret is
not sent to the server. Each token, or all of one person's at once, can be revoked there with an
optional reason. Each token shows its access level (a `QA access` / `Admin access` badge). Endpoints
(`CatalogAdmin` policy; of personal tokens only an Admin one reaches them, and still cannot mint):

| | |
|---|---|
| `GET /api/tokens/admin?includeInactive=` | Active tokens with `ownerEmail` / `ownerName`; with `true`, also those that ended within the last 90 days |
| `POST /api/tokens/admin/{id}/revoke { reason? }` | `204`, idempotent; `404` for an unknown id |
| `POST /api/tokens/admin/revoke-owner { email, reason? }` | Revokes every active token of that owner → `{ revoked }` |

Each revoked token gets its own `personal_token.revoked` audit entry, the same action the owner's
revoke writes, with the admin as actor, `owner` in the after-state, and `{ byAdmin: true, reason }`
as metadata.

## Tools

Reads are annotated `readOnlyHint`, so clients can auto-allow them. Results are compact JSON (nulls
dropped), carry absolute portal `url`s, and report any name correction in `nameResolution`. Unknown
or ambiguous names fail with the real names so the agent can retry; a non-GUID id says which tool
finds the right one.

| Tool | What it answers |
|---|---|
| `whoami` | The identity and what it may do (auth method, roles, product limits, scopes) |
| `list_products` | Products with per-environment deployed counts and last deploy |
| `find_services` | A service across products when the product is unknown |
| `get_deployment_state` | Version / status per service × environment right now (needs a filter) |
| `get_service` | One service: current per env, recent versions, recent promotions |
| `get_deployment_history` | A service's deploys, newest first |
| `list_recent_deployments` | A product's deploys since a time (default 24 h), with status filter |
| `get_deployment` | One deploy event: run, failure reason, change set, logs index, neighbours, promotions |
| `get_deployment_log` | A captured log block — tail, or lines matching text, numbered |
| `list_promotions` | Candidates with what each still waits on; filter by status, edge, reference |
| `get_promotion` | Full candidate: versions, approval progress and approvers, comments, change set, failed attempts |
| `get_work_item` | A ticket on one edge: required/missing roles, decisions, comments, commits, PRs |
| `list_builds` / `get_build` | Registry entries; `get_build` lists the targets `promote_build` can use |
| `diagnose_promotion` / `diagnose_deployment` | Playbook diagnosis: evidence, observations, ranked causes with confirm/fix |
| `search_knowledge` | Installation-specific pipeline knowledge, with as-of dates |
| `get_deployment_frequency` / `get_promotion_queue` / `get_lead_time` | Analytics, passed through with their definition and coverage blocks |
| `create_promotion` | Pipeline action — configured key with `promotion:create`, product scope applies |
| `promote_build` | Open a promotion from a registered build, as the caller |
| `add_promotion_comment` / `add_work_item_comment` | Comment as the caller |
| `set_promotion_participant` / `remove_promotion_participant` | Assign or clear a configured role |

No tool approves, rejects or signs off. With a standard token those stay human actions in the
portal; a QA or Admin token can make them over REST (see [Access levels](#access-levels)).

## Connecting

```bash
claude mcp add --transport http --scope user infraportal "$DEPLOYMENTS_URL/api/mcp" --header "X-Api-Key: $DEPLOYMENTS_API_KEY"
```

or a project `.mcp.json` (this repo has one) that reads both from the environment:

```json
{ "mcpServers": { "infraportal": { "type": "http", "url": "${DEPLOYMENTS_URL}/api/mcp", "headers": { "X-Api-Key": "${DEPLOYMENTS_API_KEY}" } } } }
```

Without an MCP client (scheduled agent runs, scripts, a session where the server is not added), the
`infrapilot-api` skill bundles `scripts/mcp_call.py`, which calls the same tools over plain HTTP from
the same two environment variables — one stateless `tools/call` request per call:

```bash
python -I -X utf8 .claude/skills/infrapilot-api/scripts/mcp_call.py list_promotions '{"status":"Approved"}'
```

Portal links use `Portal:BaseUrl` when set, else the request's host (https unless localhost) — right
for the single-container deployment, where portal and API share an origin.
