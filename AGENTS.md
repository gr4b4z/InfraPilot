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

## What's new entries (the portal's changelog)

The portal has a **What's new** page (`/whats-new`) that tells the people who use it — developers,
approvers, QA and admins — what changed. A small blue "What's new" link shows up in the topbar for
anyone who hasn't seen the latest entries, and goes away once they open the page. (It has nothing to
do with the product **Release Notes** feature.)

**Any PR that changes what a user can see or do adds an entry in the same PR.** Don't leave it for a
follow-up.

### When to add one

- **Add one for:** a new page, button, filter, setting or option; a change in behaviour that people
  will notice (what holds a promotion, who can approve, what a page shows); an improvement people
  will notice in how a page works.
- **Skip it for:** bug fixes that restore intended behaviour, performance work, refactors, tests,
  docs, dependency bumps, CI/infra changes, and API or MCP changes nobody meets in the portal. Hidden
  features (the easter-egg themes) never get one. Most releases get no entry, and that is fine.
- A feature that only exists through the API counts only when people use it directly (for example
  the Agent access page); describe what they can do, not the endpoint.
- One PR with several separate user-visible changes can add several entries. Small related changes
  go into one entry.
- Don't rewrite older entries. Only fix them when they are wrong.

### Where and how

Each entry is its own file, `src/Platform.Web/src/app/whats-new/entries/YYYY-MM-DD-short-slug.md`
(lowercase kebab-case slug). Use separate files, not a shared list, so parallel PRs don't conflict.

```markdown
---
title: Roll back a failed deploy in one click
date: 2026-10-22
audience: everyone
link: /rollbacks
linkLabel: Rollbacks
---
When a deployment fails, its page now offers **Roll back to last known good version**. The portal
works out which version last deployed successfully and raises the rollback for you.
```

- `title` (required): a short, sentence-case line saying what people can now do, about 60
  characters at most.
- `date` (required): `YYYY-MM-DD`, the day the PR merges (use today's date). Entries are dated, not
  versioned: each merge to main gets the next patch version when it ships, so a PR can't know its
  version. Never guess one.
- `audience`: `everyone` (default), `approvers`, `qa` or `admins`, depending on who the change is
  for. `qa` and `admins` entries are hidden from other roles unless they choose to see them;
  `approvers` entries are shown to everyone, because anyone can be named on an approval gate.
- `link` / `linkLabel` (optional): the portal path where people can try it, with no host (`/promotions`,
  `/settings/freeze`), and its label (`Promotions`, `Settings → Freeze`). Put links here, not
  inline in the body.

### How to write it

- Write for the person using the portal: what they can do now, or what's different for them, and
  where to find it. Use the second person and plain words. Keep it to 1–3 short sentences, two short
  paragraphs at most.
- Name buttons, pages and settings exactly as the UI does, in bold: **Mass approve**,
  **Settings → Freeze**.
- Leave out technical detail: no endpoints, payload or field names, webhooks, database or migration
  details, class names, environment variables, ticket or PR numbers, or commit-speak ("refactor",
  "fix(...)").
- Say "the portal", not "InfraPortal" or "InfraPilot", because the name is configured per
  installation.
- Check it: run the web dev server and open `/whats-new`. The page reads the frontmatter at runtime.
  A malformed entry is left off the page, with a console warning that says why.
