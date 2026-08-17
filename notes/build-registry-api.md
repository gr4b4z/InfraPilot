# Build Registry API

Endpoint for publish pipelines to register every published build — main, release and feature
branches alike — in InfraPilot's build registry (plan: `docs/plans/feature-branch-builds.md`,
Phase A). A registered build is a fact about CI: it says the artifacts exist and which branch
produced them. Deploying one is the promotion surface's job.

```
POST /api/builds
```

## Authentication

Same model as the deployment ingest API: an `X-Api-Key` header, per-key rate limiting, and
optional product scoping (a key limited to products can only register builds for them).

Additionally, keys may declare a `Scopes` list in configuration. A key **with** a `Scopes`
list must hold `build:register` to use this endpoint (and `promotion:create` to use
`POST /api/promotions`). A key **without** a `Scopes` list is unrestricted — every key
provisioned before scopes existed keeps working.

```jsonc
// appsettings — Deployments:ApiKeys entry for a build agent, least privilege:
{
  "Name": "build-agent",
  "KeyHash": "<sha256-hex>",
  "AllowedProducts": ["mpt"],
  "Scopes": ["build:register"]
}
```

## Request body

```jsonc
{
  // ── Required ────────────────────────────────────────────────
  "product": "mpt",                          // Product name (same normalization as deploy events)
  "service": "spotlight",                    // Service / component name
  "version": "5.0.347-g495d92f0",            // Build number as the pipeline stamped it
  "branch":  "refs/heads/feature/MPT-1234",  // Full git ref that produced the build

  // ── Optional provenance ─────────────────────────────────────
  "commitSha": "495d92f0aa11…",
  "buildId":   "812345",                     // CI run id (string)
  "buildUrl":  "https://dev.azure.com/…/_build/results?buildId=812345",

  // ── Optional manifest ───────────────────────────────────────
  "manifest": { /* the full BuildMetadata document, inline, verbatim */ },
  "artifactRef":    "acr.io/spotlight/build-metadata:5.0.347-g495d92f0", // OCI ref in ACR
  "artifactDigest": "sha256:0a1b2c…"         // immutable pointer deploy workflows pull by
}
```

The registry is a pure recipient: it never fetches from ACR or storage, so it needs no
credentials for either. Send the manifest inline.

## Idempotency

Registration is idempotent on `(product, service, version)`, backed by a unique index.
A re-POST of the same key **updates the row in place** and returns `200` with
`"replayed": true`; a first registration returns `201` with `"replayed": false`. Provenance
fields (branch, commit, build id/url, artifact ref/digest) are overwritten by the retry —
the retry is the fuller report. The stored manifest is only replaced when the re-POST
actually carries one.

This makes the fail-loud contract safe: the publish stage treats any non-2xx as a stage
failure and a re-run of the whole stage repeats the POST harmlessly.

## Responses

- `201 Created` — `{ "id", "version", "branch", "replayed": false }`
- `200 OK` — replay; same body with `"replayed": true` and the existing row's `id`
- `400 Bad Request` — `{ "errors": [ "'branch' is required", … ] }`
- `401 Unauthorized` — missing/invalid API key
- `403 Forbidden` — product outside the key's `AllowedProducts`, or the key declares
  `Scopes` without `build:register`
- `429 Too Many Requests` — per-key rate limit

## Read surface

```
GET /api/builds?product=&service=&branch=&limit=     — newest first; `branch` is a substring match
GET /api/builds/{id}                                  — one build, including the inline manifest
```

Read endpoints accept the same auth as the rest of the API (signed-in user or API key).
The web UI lists the registry under **Deployments → Builds**.
