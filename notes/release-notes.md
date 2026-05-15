# Release Notes

Aggregate `DeployEvents` into structured, human-readable release notes per `(product, environment, window)`. Notes can be previewed (raw or rendered), edited, persisted, and delivered via webhook to downstream consumers.

The feature is **off by default**. Enable it from Settings → Feature Flags by flipping `features.releaseNotes`.

---

## Endpoints

All endpoints sit under `/api/release-notes` and require the `CanApprove` policy (same baseline as the rest of the deployments surface).

| Method | Path | Purpose | Persists | Webhook |
|---|---|---|---|---|
| `GET`  | `/preview/raw` | Aggregate `DeployEvents` into the structured data shape | no | no |
| `GET`  | `/preview`     | Render the configured template against the raw aggregation | no | no |
| `GET`  | `/template`    | Read the saved template at a given scope | no | no |
| `PUT`  | `/template`    | Save a template at a given scope | no | no |
| `POST` | `/generate`    | Persist a release note + dispatch webhook | yes | yes |
| `GET`  | `/`            | List released notes (`product`, `environment`, `limit` filters) | no | no |
| `GET`  | `/{id}`        | Detail of a single released note | no | no |

### `GET /preview/raw`

Query params (all required): `product`, `environment`, `from`, `to` (ISO-8601).

Returns one `services[]` entry per service deployed in the window, picking the latest event per service. Each entry carries the version transition, the work items / pull requests / pipelines referenced by the event, and the participants (event-level + reference-level, deduped by `(role, email|displayName)`).

```jsonc
{
  "product": "identity-platform",
  "environment": "production",
  "from": "2026-05-06T21:12:17Z",
  "to":   "2026-05-07T14:00:00Z",
  "generatedAt": "2026-05-07T14:05:00Z",
  "services": [
    {
      "service": "auth-api",
      "previousVersion": "1.8.5",
      "currentVersion":  "1.10.0",
      "isRollback": false,
      "deployedAt": "2026-05-07T12:48:00Z",
      "workItems":    [{ "key": "IDP-2946", "title": "Fix timezone …", "type": "jira", "url": "https://acme.atlassian.net/browse/IDP-2946" }],
      "pullRequests": [{ "key": "888", "title": "Fix timezone …", "url": "https://dev.azure.com/.../pullrequest/888" }],
      "pipelines":    [{ "key": "build-79588", "title": null, "url": "https://dev.azure.com/.../buildId=79588" }],
      "participants": [
        { "role": "author",   "displayName": "Marta Wiśniewska", "email": "marta.wisniewska@acme.com" },
        { "role": "qa",       "displayName": "Piotr Nowak",      "email": "piotr.nowak@acme.com" }
      ]
    }
  ]
}
```

### `GET /preview`

Same query as `/preview/raw`. Returns `{ rendered, raw }` where `rendered` is the markdown produced by the resolved template. Useful for live previews in the UI without persisting anything.

### `POST /generate`

Body:

```jsonc
{
  "product": "identity-platform",
  "environment": "production",
  "from": "2026-05-06T21:12:17Z",   // optional; defaults to last note's generatedAt
  "to":   "2026-05-07T14:00:00Z",   // optional; defaults to UtcNow
  "renderedContent": "..."          // optional; bypass the template when present
}
```

Pipeline:

1. Aggregate `DeployEvents` for the window (same as `/preview/raw`).
2. If `renderedContent` is supplied, use it verbatim. Otherwise resolve the template (see [Templates](#templates)) and render it against the aggregation.
3. Insert a row into `release_notes` with the rendered output, the raw aggregation JSON, and a `published` status.
4. Dispatch the `release_note.generated` webhook with payload combining structured services and rendered markdown.
5. Return `201` + the new id.

The auto-window behaviour (`from = last published note for product+env`, `to = UtcNow`) makes the simplest pipeline call just `{ product, environment }` after each release.

`renderedContent` is the hook the UI uses to support the **draft → edit → publish** flow: a user previews via `/preview`, edits the markdown in a textarea, then publishes via `/generate` with the edited markdown attached.

---

## Templates

Release notes are rendered with [Handlebars.Net](https://github.com/Handlebars-Net/Handlebars.Net). Templates live in `platform_settings` and are resolved most-specific-first:

```
release-notes.template.{product}.{environment}   ← per-env override
release-notes.template.{product}                 ← product default
release-notes.template.default                   ← global default
(built-in default constant in code)              ← fallback when DB is empty
```

The Settings UI (Admin → Release Notes Template) lets you save / view at any of these three scopes. The editor loads the *exact* row for the selected scope so what you see is exactly what's persisted there; when nothing is saved at the scope yet it pre-fills with the inherited template so you can fork it.

### Context fields exposed to the template

Top-level:

| Field | Description |
|---|---|
| `product`        | Product name |
| `environment`    | Environment name |
| `date`           | Local-time date stamp for the rendering |
| `from`, `to`     | Window bounds (ISO-8601, `u` format) |
| `services`       | `{{#each services}}` block — one entry per service |

Inside `{{#each services}}`:

| Field | Description |
|---|---|
| `service`              | Service name |
| `previousVersion`      | Previous version string, or `—` when this is the service's first event |
| `currentVersion`       | Deployed version |
| `isRollback`           | Boolean — set when the deploy event was flagged a rollback |
| `deployedAt`           | Deploy timestamp (ISO-8601) |
| `workItems[]`          | Each: `{ key, title, type, url }` |
| `pullRequests[]`       | Each: `{ key, title, url }` |
| `pipelines[]`          | Each: `{ key, title, url }` |
| `participants[]`       | All participants (event + reference scope), deduped. Each: `{ role, displayName, email }` |
| `pullRequest`, `pipeline` | First entry of the corresponding array as a single object, or `null`. Lets templates avoid `{{#each}}` for the common single-PR case. |
| `author`, `qa`, `triggeredBy` | Single-best-match participant for each role, as `{ displayName, email }` or `null`. Lets templates write `[{{{author.displayName}}}](mailto:{{author.email}})` directly. |

### Built-in default template

```handlebars
# 🛠️ Release: {{product}} — {{environment}}

**Date:** {{date}} | **Window:** {{from}} → {{to}}

{{#each services}}
* **{{service}}** (`{{{previousVersion}}} → {{currentVersion}}`){{#if isRollback}} ⚠️ rollback{{/if}}
{{#each workItems}}
  * [{{key}}]({{url}}) — {{{title}}}{{#if ../pullRequest}} · PR [#{{../pullRequest.key}}]({{../pullRequest.url}}){{/if}}{{#if ../pipeline}} · Build [{{../pipeline.key}}]({{../pipeline.url}}){{/if}}{{#if ../author}} · author: [{{{../author.displayName}}}](mailto:{{../author.email}}){{/if}}{{#if ../qa}} · qa: [{{{../qa.displayName}}}](mailto:{{../qa.email}}){{/if}}
{{/each}}
{{#unless workItems}}
  * _no work items_{{#if pullRequest}} · PR [#{{pullRequest.key}}]({{pullRequest.url}}){{/if}}{{#if pipeline}} · Build [{{pipeline.key}}]({{pipeline.url}}){{/if}}{{#if author}} · author: [{{{author.displayName}}}](mailto:{{author.email}}){{/if}}{{#if qa}} · qa: [{{{qa.displayName}}}](mailto:{{qa.email}}){{/if}}
{{/unless}}
{{/each}}
```

### Escaping notes

Handlebars HTML-escapes `{{value}}` by default. Use `{{{value}}}` for content that's already safe (display names with diacritics, em-dashes, work-item titles). URLs inside markdown link syntax (`[{{title}}]({{url}})`) are not interpreted as HTML by the markdown renderer so the double-mustache is fine there.

---

## Webhook event

```
event: release_note.generated
filters: Product, Environment
```

Payload:

```jsonc
{
  "id": "ae1fa7ef-...",
  "product": "identity-platform",
  "environment": "production",
  "from": "2026-05-06T21:12:17Z",
  "to":   "2026-05-07T14:00:00Z",
  "generatedAt": "2026-05-07T14:05:00Z",
  "renderedContent": "# 🛠️ Release: identity-platform — production\n...",
  "services": [ /* same shape as GET /preview/raw → services[] */ ]
}
```

`renderedContent` carries the final markdown so downstream consumers (Azure Pipeline, Logic App, Teams channel webhook) can publish without a follow-up call back to InfraPilot. The structured `services` array is included alongside so a consumer that needs to render its own format (Confluence storage format, ServiceNow record, etc.) doesn't have to parse markdown.

---

## Persistence

```
release_notes (
  Id              uuid PK,
  Product         varchar(200),
  Environment     varchar(100),
  From            timestamptz,
  To              timestamptz,
  GeneratedAt     timestamptz,
  RenderedContent text,
  RawJson         jsonb,                   -- structured services snapshot
  Status          varchar(20)  default 'published',
  ServicesCount   int
)
```

Indexes: `(Product, Environment, GeneratedAt desc)` and `(GeneratedAt desc)`.

`RenderedContent` is the markdown as it was at publish-time; subsequent template edits do **not** retroactively re-render historical notes. Operators re-generate to produce a new row.

---

## UI

| Route | Purpose |
|---|---|
| `/release-notes`                           | Product picker (gated by `features.releaseNotes`) |
| `/release-notes/:product`                  | List + new-note form |
| `/release-notes/:product/new?env=…&from=…&to=…` | Draft / review screen: editable markdown left, live HTML preview right, `Publish` redirects to detail |
| `/release-notes/:product/:id`              | Detail — tabs: **Rendered** (HTML), **Services** (structured cards); **Copy markdown** button copies the source |
| `/settings/release-notes-template`         | Admin template editor with scope picker |

---

## Pipeline integration

The simplest call after a release:

```bash
curl -X POST "$PLATFORM_URL/api/release-notes/generate" \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -d '{ "product": "identity-platform", "environment": "production" }'
```

Server auto-derives `from` from the most recent published note for that `(product, environment)` and uses `UtcNow` for `to`. To post the rendered note straight to Teams without subscribing to the webhook, capture `renderedContent` from the response.
