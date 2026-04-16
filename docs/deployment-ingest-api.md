# Deployment Ingest API

Endpoint for CI/CD pipelines to report deployment events to InfraPilot.

```
POST /api/deployments/events
```

## Authentication

Every request must include an API key header:

```
X-Api-Key: <your-api-key>
```

Keys are provisioned by an admin and may be scoped to specific products. A scoped key can only ingest events for the products it was created for; attempts to post for other products return `403 Forbidden`.

Rate limiting is applied per key.

## Request body

```jsonc
{
  // ── Required ────────────────────────────────────────────────
  "product":     "ticketing-platform",   // Product name
  "service":     "order-api",            // Service / component name
  "environment": "production",           // Target environment
  "version":     "2.14.0-rc.3",         // Deployed version (semver, SHA, tag — any string)
  "source":      "github-actions",       // Origin system identifier
  "deployedAt":  "2026-04-16T10:30:00Z", // UTC timestamp of the deployment

  // ── Optional ────────────────────────────────────────────────
  "status":      "succeeded",            // Default: "succeeded"
  "isRollback":  false,                  // Default: false

  "references": [
    {
      "type":     "repository",          // Required
      "url":      "https://github.com/org/order-api",
      "provider": "github",
      "key":      "org/order-api",
      "revision": "a1b2c3d"
    }
  ],

  "participants": [
    {
      "role":        "PR Author",        // Required
      "displayName": "Jan Kowalski",
      "email":       "jan.kowalski@softwareone.com"
    }
  ],

  "metadata": {
    "buildNumber": "1234",
    "triggeredBy": "merge-to-main"
  }
}
```

## Field reference

### Top-level fields

| Field | Type | Required | Default | Description |
|-------|------|----------|---------|-------------|
| `product` | string | **yes** | — | Product name. Must be non-empty. |
| `service` | string | **yes** | — | Service or component within the product. |
| `environment` | string | **yes** | — | Target environment (e.g. `development`, `staging`, `production`). |
| `version` | string | **yes** | — | Version identifier. Any format is accepted (semver, git SHA, build tag). |
| `source` | string | **yes** | — | Identifies the CI/CD system sending the event. |
| `deployedAt` | DateTimeOffset | **yes** | — | UTC timestamp of the deployment. Must not be the zero value. |
| `status` | string | no | `"succeeded"` | One of: `succeeded`, `failed`, `in_progress`. Case-insensitive. |
| `isRollback` | boolean | no | `false` | Whether this deployment is a rollback to a previous version. |
| `references` | array | no | `[]` | Links to external resources (repos, pipelines, PRs, tickets). |
| `participants` | array | no | `[]` | People involved in the deployment. |
| `metadata` | object | no | `{}` | Free-form key-value pairs for custom data. |

### `status` values

| Value | Meaning |
|-------|---------|
| `succeeded` | Deployment completed successfully. Only this status creates promotion candidates and is eligible as a rollback target. |
| `failed` | Deployment failed. Recorded for tracking and dashboards. |
| `in_progress` | Deployment is still running. Can be updated later by sending a new event with the same version and a final status. |

### `references[]` object

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `type` | string | **yes** | Reference category. |
| `url` | string | no | Full URL to the external resource. |
| `provider` | string | no | Provider name (e.g. `github`, `azure-devops`, `gitlab`, `jira`). |
| `key` | string | no | Unique identifier in that system (commit SHA, PR number, ticket key). |
| `revision` | string | no | Git revision / commit SHA. Can be omitted if not available. |

Common `type` values:

| Type | Usage |
|------|-------|
| `repository` | Link to the source code repository. |
| `pipeline` | Link to the CI/CD build or workflow run. |
| `pull-request` | Link to the merged PR that triggered the deploy. |
| `work-item` | Link to a Jira ticket, Azure DevOps work item, etc. |

### `participants[]` object

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `role` | string | **yes** | Role in the deployment process. |
| `displayName` | string | no | Human-readable name. |
| `email` | string | no | Email address. Used for deployer identification in the promotions system. |

Common `role` values:

| Role | Usage |
|------|-------|
| `PR Author` | Person who authored the pull request. Used by the promotions system to identify the deployer (for exclude-deployer approval rules). |
| `PR Reviewer` | Person who reviewed/approved the PR. |
| `QA` | QA engineer who validated the change. |

## Response

### 201 Created

```json
{
  "id": "f47ac10b-58cc-4372-a567-0e02b2c3d479",
  "version": "2.14.0-rc.3",
  "previousVersion": "2.13.1"
}
```

`previousVersion` is the most recent prior version deployed to the same product/service/environment, or `null` for first-time deployments.

### 400 Bad Request

```json
{
  "errors": [
    "'product' is required",
    "'status' must be one of: succeeded, failed, in_progress"
  ]
}
```

### 403 Forbidden

Returned when the API key is scoped to specific products and the `product` in the payload is not in the allowed list.

## Examples

### Minimal payload

```json
{
  "product": "marketplace",
  "service": "search-api",
  "environment": "staging",
  "version": "1.0.42",
  "source": "github-actions",
  "deployedAt": "2026-04-16T14:00:00Z"
}
```

### Full payload (GitHub Actions)

```json
{
  "product": "ticketing-platform",
  "service": "order-api",
  "environment": "production",
  "version": "2.14.0",
  "source": "github-actions",
  "deployedAt": "2026-04-16T10:30:00Z",
  "status": "succeeded",
  "isRollback": false,
  "references": [
    {
      "type": "repository",
      "url": "https://github.com/SoftwareOne/order-api",
      "provider": "github",
      "key": "SoftwareOne/order-api",
      "revision": "a1b2c3d4e5f6"
    },
    {
      "type": "pipeline",
      "url": "https://github.com/SoftwareOne/order-api/actions/runs/87654",
      "provider": "github",
      "key": "87654"
    },
    {
      "type": "pull-request",
      "url": "https://github.com/SoftwareOne/order-api/pull/312",
      "provider": "github",
      "key": "312"
    },
    {
      "type": "work-item",
      "url": "https://softwareone.atlassian.net/browse/PLT-1234",
      "provider": "jira",
      "key": "PLT-1234"
    }
  ],
  "participants": [
    {
      "role": "PR Author",
      "displayName": "Jan Kowalski",
      "email": "jan.kowalski@softwareone.com"
    },
    {
      "role": "PR Reviewer",
      "displayName": "Anna Kowalska",
      "email": "anna.kowalska@softwareone.com"
    },
    {
      "role": "QA",
      "displayName": "Piotr Nowak",
      "email": "piotr.nowak@softwareone.com"
    }
  ],
  "metadata": {
    "buildNumber": "87654",
    "triggeredBy": "merge-to-main",
    "cluster": "prod-westeurope-01"
  }
}
```

### Full payload (Azure DevOps)

```json
{
  "product": "identity-platform",
  "service": "auth-service",
  "environment": "staging",
  "version": "3.1.0-beta.2",
  "source": "azure-devops",
  "deployedAt": "2026-04-16T09:15:00Z",
  "status": "succeeded",
  "references": [
    {
      "type": "repository",
      "url": "https://dev.azure.com/SoftwareOne/Identity/_git/auth-service",
      "provider": "azure-devops",
      "key": "auth-service",
      "revision": "f9e8d7c6b5a4"
    },
    {
      "type": "pipeline",
      "url": "https://dev.azure.com/SoftwareOne/Identity/_build/results?buildId=45678",
      "provider": "azure-devops",
      "key": "45678"
    },
    {
      "type": "pull-request",
      "url": "https://dev.azure.com/SoftwareOne/Identity/_git/auth-service/pullrequest/89",
      "provider": "azure-devops",
      "key": "89"
    }
  ],
  "participants": [
    {
      "role": "PR Author",
      "displayName": "Marta Wisniewska",
      "email": "marta.wisniewska@softwareone.com"
    },
    {
      "role": "PR Reviewer",
      "displayName": "Tomasz Wojcik",
      "email": "tomasz.wojcik@softwareone.com"
    }
  ]
}
```

### Rollback

```json
{
  "product": "marketplace",
  "service": "payment-gateway",
  "environment": "production",
  "version": "4.2.1",
  "source": "github-actions",
  "deployedAt": "2026-04-16T11:45:00Z",
  "status": "succeeded",
  "isRollback": true,
  "metadata": {
    "rollbackReason": "Elevated error rate after 4.3.0 deploy",
    "rollbackFrom": "4.3.0"
  }
}
```

## Promotion integration

When a `succeeded` deployment event is ingested, the system automatically checks for matching promotion topology edges. If the deployment's environment is a source in the promotion graph (e.g. `development` or `staging`), a **promotion candidate** is created for the next environment in the chain.

The `PR Author` participant is used as the deployer identity for the "exclude deployer" approval rule, which prevents the person who deployed from also approving the promotion to the next environment.

## cURL example

```bash
curl -X POST https://infrapilot.example.com/api/deployments/events \
  -H "Content-Type: application/json" \
  -H "X-Api-Key: your-api-key-here" \
  -d '{
    "product": "marketplace",
    "service": "search-api",
    "environment": "staging",
    "version": "1.0.42",
    "source": "github-actions",
    "deployedAt": "2026-04-16T14:00:00Z"
  }'
```
