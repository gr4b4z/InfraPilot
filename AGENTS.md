# Agent Directives

## InfraPilot (InfraPortal) API access

This repository is InfraPilot, also branded **InfraPortal**. Any task that involves talking to
a running InfraPilot instance — reading deployment state or history, ingesting deploy events,
creating or querying promotions, registering builds, pulling analytics or release notes — must
use the **`infrapilot-api` skill** (`.claude/skills/infrapilot-api/SKILL.md`).

Connection is defined by two environment variables; use them, never a hardcoded host or key:

- `DEPLOYMENTS_URL` — base URL of the InfraPilot instance (endpoints under `{DEPLOYMENTS_URL}/api/...`)
- `DEPLOYMENTS_API_KEY` — sent as the `X-Api-Key` header on every request

If either variable is not set, ask the user for it instead of guessing. Never print the key. A user
without a key creates a personal access token for themselves on the portal's **Agent access** page
(`/me/agent-access`).

When the `infraportal` MCP server is connected (this repo's `.mcp.json` points it at
`${DEPLOYMENTS_URL}/api/mcp` with the same key), prefer its tools over curl — they include the
promotion/deployment diagnostics the REST API lacks. See `notes/mcp-server.md`.

Full API payload references live in `notes/` (`deployment-ingest-api.md`, `promotions-api.md`,
`build-registry-api.md`, `analytics-api.md`, `release-notes.md`, `mcp-server.md`).
