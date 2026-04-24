# InfraPilot

InfraPilot is an open-source infrastructure portal for self-service requests, approvals, deployments, and operational workflows.

## Quick Start

If you just want to run the app locally, use Docker Compose.

### Prerequisites

- Docker
- Docker Compose

### Start The App

From the repository root:

```bash
docker compose up -d
```

Then open:

- frontend: `http://localhost:5173`
- API health endpoint: `http://localhost:5259/health`

### Stop The App

```bash
docker compose down
```

If you also want to remove the local Postgres volume:

```bash
docker compose down -v
```

## What Runs Locally

`docker compose` starts three services:

- `postgres`: PostgreSQL database on `localhost:5433`
- `api`: ASP.NET Core backend on `http://localhost:5259`
- `frontend`: React/Vite frontend on `http://localhost:5173`

The frontend is configured for local development through [src/Platform.Web/public/config.json](/Users/sylwestergrabowski/dev/infraPilot/src/Platform.Web/public/config.json:1), which points API calls to `http://localhost:5259`.

## Local Configuration

For the default local Docker setup, you do not need to provide extra environment variables.

The local stack already uses:

- Postgres database: `swo_platform`
- Postgres user: `postgres`
- Postgres password: `postgres`
- API connection string:
  `Host=postgres;Database=swo_platform;Username=postgres;Password=postgres`

If you want to change local settings, edit [docker-compose.yml](/Users/sylwestergrabowski/dev/infraPilot/docker-compose.yml:1).

## Production Deployment

Production uses a single container image:

- Nginx serves the frontend on port `8080`
- ASP.NET API runs inside the same container on `127.0.0.1:8081`
- Nginx proxies `/api`, `/agent`, and `/health` to the API

Build it from the repository root:

```bash
docker build -t infrapilot .
```

## Production Variables

These are the most important environment variables for a real deployment.

### Core Runtime Variables

| Variable | Required | Default | Why it is needed |
|---|---|---|---|
| `ConnectionStrings__Platform` | Yes | none | Tells the API how to connect to the database. The app cannot start correctly without a database connection. Format depends on `Database__Provider` (see below). |
| `Database__Provider` | No | `Postgres` | Selects the EF Core provider. Accepted values: `Postgres`, `SqlServer`. Must match the format of `ConnectionStrings__Platform`. |
| `ASPNETCORE_ENVIRONMENT` | Recommended | `Production` in the container image | Controls ASP.NET runtime behavior and environment-specific configuration. |
| `CatalogPath` | No | `/app/catalog` | Tells the API where to load catalog YAML definitions from. |

### Frontend Runtime And Branding Variables

These variables are used to generate `config.json` inside the container at startup.

| Variable | Required | Default | Why it is needed |
|---|---|---|---|
| `BACKEND_BASE_URL` | No | empty | Lets the frontend call a different public backend origin. Leave it empty for same-origin deployments. |
| `APP_NAME` | No | `InfraPilot` | Sets the main product name shown in the UI. |
| `APP_SUBTITLE` | No | `Infrastructure Portal` | Sets the smaller subtitle shown in the sidebar. |
| `ASSISTANT_NAME` | No | `InfraPilot Assistant` | Sets the label used in the assistant/chat area. |
| `PAGE_TITLE` | No | `InfraPilot \| Infrastructure Portal` | Sets the browser tab title. |
| `AZURE_CLIENT_ID` | No | empty | Entra app (client) ID for SPA sign-in. Empty disables MSAL and falls back to a local dev user — use that for local runs only. |
| `AZURE_TENANT_ID` | No | empty | Entra tenant ID that hosts the SPA app registration. Required when `AZURE_CLIENT_ID` is set. |

MSAL config is read from `/config.json` at page load, so the same container image can be pointed at a different tenant by changing env vars at deploy time (no rebuild).

### Example Production Configuration

```bash
ASPNETCORE_ENVIRONMENT=Production
ConnectionStrings__Platform="Host=my-postgres;Database=infrapilot;Username=postgres;Password=secret"
CatalogPath=/app/catalog

APP_NAME="Contoso Platform"
APP_SUBTITLE="Operations Portal"
ASSISTANT_NAME="Contoso Assistant"
PAGE_TITLE="Contoso Platform | Operations Portal"
BACKEND_BASE_URL=""
```

### Using Azure SQL instead of Postgres

InfraPilot supports Azure SQL Database as an alternative to PostgreSQL. Switch by setting `Database__Provider=SqlServer` and using a SQL Server-format connection string. Both providers share the same schema — migrations for each set live under `Migrations/Postgres` and `Migrations/SqlServer` and are applied automatically on startup in Development.

```bash
Database__Provider=SqlServer
ConnectionStrings__Platform="Server=tcp:<server>.database.windows.net,1433;Database=infrapilot;Authentication=Active Directory Default;Encrypt=True;TrustServerCertificate=False;"
```

Notes:
- Azure SQL requires `Encrypt=True`.
- Azure AD authentication (`Authentication=Active Directory Default`) is recommended over SQL auth when running on Container Apps with managed identity.
- On SQL Server, JSON payload columns are stored as `nvarchar(max)` (on Postgres they use `jsonb`). The application serialises JSON itself so there is no behavioural difference.

## Optional Integrations

You only need these if you want to enable the related features.

| Variable | Required | Why it is needed |
|---|---|---|
| `AzureAd__TenantId` | Only if using Entra ID auth | Identifies the Microsoft Entra tenant used for user authentication. |
| `AzureAd__ClientId` | Only if using Entra ID auth | Identifies the API application registration. |
| `AzureAd__Audience` | Only if using Entra ID auth | Defines the expected token audience for API auth validation. |
| `Graph__TenantId` | Only if using Microsoft Graph integration | Identifies the tenant for Graph client-credentials access. |
| `Graph__ClientId` | Only if using Microsoft Graph integration | Identifies the Graph app registration. |
| `Graph__ClientSecret` | Only if using Microsoft Graph integration | Secret used to authenticate to Microsoft Graph. |
| `AzureOpenAI__Endpoint` | Only if using AI features | Tells the app which Azure OpenAI resource to call. |
| `AzureOpenAI__ApiKey` | Only if using AI features | Authenticates requests to Azure OpenAI. |
| `AzureOpenAI__DeploymentName` | Only if using AI features | Selects the model deployment used by the app. |
| `AzureDevOps__Connections__default__OrganizationUrl` | Only if using Azure DevOps executors | Points to the Azure DevOps organization. |
| `AzureDevOps__Connections__default__Project` | Only if using Azure DevOps executors | Selects the Azure DevOps project used by the executor. |
| `AzureDevOps__Connections__default__Pat` | Only if using Azure DevOps executors | Personal access token used to call Azure DevOps APIs. |
| `Jira__Connections__default__BaseUrl` | Only if using Jira executors or lookups | Points to the Jira instance. |
| `Jira__Connections__default__Email` | Only if using Jira executors or lookups | Jira account email used for API authentication. |
| `Jira__Connections__default__ApiToken` | Only if using Jira executors or lookups | Jira API token used for authentication. |
| `AzureBlob__ConnectionString` | Only if using file attachments | Connects the app to Azure Blob Storage. |
| `AzureBlob__ContainerName` | Only if using file attachments | Selects the blob container for uploaded files. |
| `ServiceBus__ConnectionString` | Only if using Service Bus execution flow | Connects the app to Azure Service Bus. |
| `ServiceBus__ExecutionQueueName` | Only if using Service Bus execution flow | Selects the queue used for request execution. |
| `Notifications__PortalBaseUrl` | Recommended if using email/webhook notifications | Lets notifications link users back to the correct portal URL. |
| `Notifications__Channels__Email__Enabled` | Only if using email notifications | Enables or disables the email channel. |
| `Notifications__Channels__Email__SmtpHost` | Only if using email notifications | SMTP server hostname for sending emails. |
| `Notifications__Channels__Email__SmtpPort` | Only if using email notifications | SMTP server port. |
| `Notifications__Channels__Email__From` | Only if using email notifications | Sender address used in notification emails. |
| `Notifications__Channels__Email__UseSsl` | Only if using email notifications | Enables SSL/TLS for SMTP. |
| `Notifications__Channels__Webhook__Enabled` | Only if using outbound notification webhooks | Enables or disables the webhook channel. |
| `Notifications__Channels__Webhook__Url` | Only if using outbound notification webhooks | Destination URL for generic webhook notifications. |
| `APPLICATIONINSIGHTS_CONNECTION_STRING` | Only if using Application Insights | Enables Azure Monitor / Application Insights telemetry (distributed tracing, dependency tracking, live metrics). When empty or absent, OpenTelemetry is not registered and there is zero overhead. |
| `Deployments__Enrichment__Enabled` | Only if using deployment enrichment | Turns background deployment enrichment on or off. |
| `Deployments__Enrichment__IntervalSeconds` | Only if using deployment enrichment | Controls how often enrichment runs. |
| `Deployments__Enrichment__MaxEventsPerCycle` | Only if using deployment enrichment | Limits how many deployment events are processed per cycle. |
| `Deployments__Enrichment__LookbackHours` | Only if using deployment enrichment | Controls how far back the enrichment worker searches for events. |

## Entra ID App Registration

InfraPilot uses two Entra ID app registrations: one for user-facing authentication (SPA + API) and one for server-to-server Graph API calls. Without auth configured the app runs in open development mode — all endpoints are accessible and a stub dev user with admin rights is injected automatically.

### App 1 — InfraPilot (SPA + API)

This is the main app registration used by both the React frontend (MSAL) and the ASP.NET API (JWT validation).

#### Create the registration

1. Go to **Azure Portal > Microsoft Entra ID > App registrations > New registration**
2. Name: `InfraPilot` (or your preferred name)
3. Supported account types: **Single tenant**
4. Redirect URI: **Single-page application (SPA)** — `http://localhost:5173` for dev, add your production URL later

#### Expose an API scope

1. Go to **Expose an API**
2. Set the Application ID URI to `api://<client-id>` (the default)
3. Add a scope:
   - Scope name: `access_as_user`
   - Who can consent: **Admins and users**
   - Admin consent display name: `Access InfraPilot as user`
4. The frontend requests this scope when acquiring tokens:
   ```
   api://<client-id>/access_as_user
   ```

#### Define app roles

Go to **App roles > Create app role** and add:

| Display name | Value | Allowed member types | Description |
|---|---|---|---|
| `InfraPilot Admin` | `InfraPortal.Admin` | Users/Groups | Full admin access — catalog sync, audit log viewer |

The role value **must be exactly** `InfraPortal.Admin` (case-sensitive). This role controls:

| What it unlocks | Where it's enforced |
|---|---|
| Catalog sync trigger (`POST /api/catalog/sync`) | Backend policy `CatalogAdmin` |
| Audit log access (`GET /api/audit`) | Backend policy `AuditViewer` |
| "Admin" badge in sidebar | Frontend `user.isAdmin` check |

Users without this role can still browse the catalog, submit requests, approve requests, and view deployments.

#### Assign users to the role

1. Go to **Enterprise applications** > find `InfraPilot` > **Users and groups**
2. Click **Add user/group**
3. Select users or a security group, then select the `InfraPilot Admin` role
4. Click **Assign**

#### Token configuration (optional but recommended)

To include group memberships in tokens (used for approval routing):

1. Go to **Token configuration > Add groups claim**
2. Select **Security groups**
3. This populates the `groups` claim in the JWT, which the backend reads via `CurrentUser.Groups`

#### Backend environment variables

```bash
AzureAd__TenantId=<your-tenant-id>
AzureAd__ClientId=<client-id-from-above>
AzureAd__Audience=api://<client-id-from-above>
```

#### Frontend environment variables

Set these in `src/Platform.Web/.env` (or as build-time vars):

```bash
VITE_AZURE_CLIENT_ID=<client-id-from-above>
VITE_AZURE_TENANT_ID=<your-tenant-id>
```

If these are empty or start with `<`, MSAL is disabled and the app falls back to the stub dev user.

### App 2 — InfraPilot Graph (server-to-server)

A separate app registration used by the backend to call Microsoft Graph with client credentials. This is needed for resolving approval group members (e.g. looking up who belongs to `platform-infra-approvers`).

#### Create the registration

1. **App registrations > New registration**
2. Name: `InfraPilot Graph`
3. Supported account types: **Single tenant**
4. No redirect URI needed

#### Add API permissions

1. Go to **API permissions > Add a permission > Microsoft Graph > Application permissions**
2. Add:
   - `GroupMember.Read.All` — read group memberships
   - `User.Read.All` — read user profiles
3. Click **Grant admin consent**

#### Create a client secret

1. Go to **Certificates & secrets > New client secret**
2. Copy the secret value immediately (it won't be shown again)

#### Backend environment variables

```bash
Graph__TenantId=<your-tenant-id>
Graph__ClientId=<graph-app-client-id>
Graph__ClientSecret=<secret-from-above>
```

If these are empty or start with `<`, the backend falls back to `StubIdentityService` which returns an empty member list for all groups.

### How approvals use groups

Catalog YAML items reference Entra ID security groups by name in the `approver_group` field:

```yaml
approval:
  required: true
  strategy: any
  approver_group: "platform-infra-approvers"
```

When a request needs approval, the backend calls `IIdentityService.GetGroupMembers(groupId)` to resolve who can approve. In production this uses Graph API; in development it returns a stub list.

### Summary

| Registration | Used by | Purpose | Required for |
|---|---|---|---|
| InfraPilot (SPA + API) | Frontend + Backend | User login, JWT validation, role-based access | Auth in production |
| InfraPilot Graph | Backend only | Resolve group members via Graph API | Approval routing |

Without either registration configured, the app runs fully functional in open development mode.

## Deployment Ingestion API Keys

Pipelines post deployment events to `POST /api/deployments/events` with the header `X-Api-Key: <key>`. Keys are configured via `Deployments:ApiKeys`:

```bash
# Minimum — plaintext key (fine for dev, OK for prod if secrets are in a vault)
Deployments__ApiKeys__0__Name=azure-devops-pipeline
Deployments__ApiKeys__0__Key=dpk-a1b2c3d4e5f6g7h8i9j0-ado

# Production — hashed key; restricted to specific products; revocable
Deployments__ApiKeys__1__Name=github-actions-platform
Deployments__ApiKeys__1__KeyHash=9a86f1a7e8c6... # lowercase SHA-256 hex of the real key
Deployments__ApiKeys__1__AllowedProducts__0=platform
Deployments__ApiKeys__1__AllowedProducts__1=billing
Deployments__ApiKeys__1__Revoked=false
```

Hardening:

- **`KeyHash`** (preferred for prod) — store `sha256(key)` instead of the raw key. Generate with `printf '%s' 'dpk-...' | shasum -a 256`. If `KeyHash` is set, `Key` is ignored.
- **`AllowedProducts`** — restrict a key so it can only post events for specific products. Empty list = any product. Requests for other products get `403 Forbidden`.
- **`Revoked`** — set to `true` to instantly kill a key without removing the entry (keeps audit history aligned).
- **Rate limit** — each authenticated key is limited to 120 requests/minute (sliding window). Unauthenticated callers get a stricter shared 10/min bucket. Excess returns `429`.
- **Constant-time compare** — both plaintext and hash comparisons use `CryptographicOperations.FixedTimeEquals` to resist timing attacks.
- **HTTPS** — keys travel in a header, so always terminate TLS before the API. Never expose `/api/deployments/events` over plain HTTP.

### Event Payload Shape

A deployment event is a small JSON document. Only `product`, `service`, `environment`, `version`, `source`, and `deployedAt` are required; everything else is optional enrichment that the UI uses to render richer cards and links.

```jsonc
{
  "product": "platform",
  "service": "platform-api",
  "environment": "production",
  "version": "2.4.1",
  "source": "azure-devops",              // free-form tag (e.g. pipeline name)
  "deployedAt": "2026-04-15T09:12:00Z",
  "status": "succeeded",                  // "succeeded" | "failed" | "in_progress"
  "isRollback": false,                    // set true when this deploy reverted to a prior version
  "references": [
    { "type": "pull-request", "url": "https://github.com/acme/platform-api/pull/482", "provider": "github", "key": "482", "title": "Add idempotency key to checkout" },
    { "type": "work-item",    "url": "https://acme.atlassian.net/browse/PLAT-1234",   "provider": "jira",   "key": "PLAT-1234", "title": "Add idempotency key to checkout endpoint" },
    { "type": "repository",   "url": "https://github.com/acme/platform-api",           "provider": "github", "key": "acme/platform-api", "revision": "a1b2c3d4" },
    { "type": "pipeline",     "url": "https://dev.azure.com/acme/_build/results?buildId=98765", "provider": "azure-devops", "key": "98765" }
  ],
  "participants": [
    { "role": "PR Author",   "displayName": "Sylwester Grabowski", "email": "sg@acme.com" },
    { "role": "PR Reviewer", "displayName": "Alex Kim",             "email": "ak@acme.com" },
    { "role": "QA",          "displayName": "Jordan Lee",           "email": "jl@acme.com" }
  ],
  "metadata": { "runId": "20260415.1", "releaseNotes": "hotfix for auth cache" }
}
```

Reference types the UI recognises with a dedicated icon and label:

| `type` | Icon | Label preference |
|---|---|---|
| `work-item` | ticket | `key` (e.g. `PLAT-1234`) — shows the inbound `title` when supplied, otherwise the Jira title fetched server-side |
| `pull-request` | PR | `labels.prTitle` → `key` |
| `repository` | branch | `key` (e.g. `acme/platform-api`) → parsed from `url` → short `revision` |
| `pipeline` | workflow | `key` → `provider` |

Unknown types render with a generic external-link icon. Always include `url` when you have it — the UI turns the label into a link.

**Commit deep-linking.** A `repository` reference that includes both `url` and `revision` is rendered as a link directly to that commit, derived from the `provider`:

| Provider | Resolved URL |
|---|---|
| `github`, `azure-devops` | `{url}/commit/{revision}` |
| `gitlab` | `{url}/-/commit/{revision}` |
| `bitbucket` | `{url}/commits/{revision}` |
| _other / omitted_ | falls back to `url` |

So a payload like `{ "type": "repository", "provider": "github", "url": "https://github.com/acme/platform-api", "revision": "a1b2c3d4" }` deep-links to `https://github.com/acme/platform-api/commit/a1b2c3d4`. No org/repo names are hardcoded — the URL is derived purely from the inbound `url`.

**Minimal curl example:**

```bash
curl -X POST "$PLATFORM_URL/api/deployments/events" \
  -H "X-Api-Key: $DEPLOY_KEY" \
  -H "Content-Type: application/json" \
  -d '{
    "product": "platform",
    "service": "platform-api",
    "environment": "production",
    "version": "2.4.1",
    "source": "github-actions",
    "deployedAt": "2026-04-15T09:12:00Z",
    "references": [
      { "type": "repository", "url": "https://github.com/acme/platform-api", "provider": "github", "key": "acme/platform-api", "revision": "a1b2c3d4" }
    ]
  }'
```

`previousVersion` is computed automatically by the server from the last event for the same `(product, service, environment)` tuple — publishers never send it. Set `isRollback: true` when the new `version` is a re-deploy of a prior version (the UI then renders an `Undo2` icon next to the version with a `Rolled back from v{previousVersion}` tooltip).

## Secrets

Do not commit real secrets to the repository.

Keep these out of source control:

- `Graph__ClientSecret`
- `AzureOpenAI__ApiKey`
- `AzureDevOps__Connections__*__Pat`
- `Jira__Connections__*__ApiToken`
- `Deployments__ApiKeys__*__Key`

For real deployments:

- use environment variables or a secret store
- use Azure Container Apps secrets if deploying to Azure
- rotate any secrets that were previously committed or shared

## Azure Container Apps

The simplest recommended Azure setup is:

- one Azure Container App
- external ingress on port `8080`
- one custom domain
- managed PostgreSQL outside the container

This gives you:

- one deployment unit
- same-origin frontend and API
- no extra routing layer

## CI/CD

This repository uses two GitHub Actions workflows:

- [CI workflow](./.github/workflows/ci.yml)
  Validates that the single production image builds successfully on pull requests and pushes to `main` / `master`.
- [Release workflow](./.github/workflows/release-image.yml)
  Publishes the production image to `ghcr.io/<owner>/<repo>` when you push a Git tag like `v0.1.0`.

Example release flow:

```bash
git tag v0.1.0
git push origin v0.1.0
```

That publishes image tags such as:

- `ghcr.io/<owner>/<repo>:v0.1.0`
- `ghcr.io/<owner>/<repo>:latest`

## Repository Structure

```text
src/
  Platform.Api/
  Platform.Web/
catalog/
infra/
docs/
Dockerfile
docker-compose.yml
```

## Troubleshooting

### The frontend loads but API calls fail

Check that:

- the API container is running: `docker compose ps`
- the API health endpoint responds: `http://localhost:5259/health`
- `src/Platform.Web/public/config.json` still points to `http://localhost:5259`

### Postgres port conflict

This project maps Postgres to `localhost:5433` to avoid conflicts with a local `5432` instance.

### Start from a clean local database

```bash
docker compose down -v
docker compose up -d
```
