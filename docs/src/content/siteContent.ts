export type NavItem = {
  label: string;
  href: string;
};

export type FeatureCard = {
  title: string;
  body: string;
  points: string[];
};

export type DocsSection = {
  slug: string;
  group: string;
  title: string;
  summary: string;
  paragraphs: string[];
  bullets?: string[];
  code?: string;
  note?: string;
};

export const docsGroupOrder = [
  'Overview',
  'Getting Started',
  'Configuration',
  'Integrations',
  'Catalog',
  'Use Cases',
  'API',
  'Operations',
] as const;

export type TableColumn = {
  key: string;
  label: string;
};

export type TableRow = Record<string, string>;

export type ApiDocBlock = {
  title: string;
  description?: string;
  columns?: TableColumn[];
  rows?: TableRow[];
  code?: string;
};

export type ExampleCard = {
  title: string;
  category: string;
  description: string;
  snippet: string;
};

export type UseCaseCard = {
  title: string;
  audience: string;
  summary: string;
  href: string;
};

export type ProofPanel = {
  title: string;
  label: string;
  eyebrow: string;
  lines: string[];
};

export type ChecklistStep = {
  title: string;
  action: string;
  expected: string;
};

export type WorkflowStep = {
  title: string;
  body: string;
};

export type Metric = {
  value: string;
  label: string;
  detail: string;
};

export type Integration = {
  name: string;
  description: string;
};

export const navItems: NavItem[] = [
  { label: 'Overview', href: '#product' },
  { label: 'Use Cases', href: '#use-cases' },
  { label: 'Examples', href: '#examples' },
  { label: 'Integrations', href: '#integrations' },
  { label: 'Docs', href: '#/docs/introduction' },
];

export const heroHighlights = [
  'YAML-defined catalog requests with guided forms and validations',
  'Approvals, audit, deployment history, and promotions in one portal',
  'Static documentation hub for fast evaluation and easy onboarding',
];

export const repositoryUrl = 'https://github.com/gr4b4z/InfraPilot';
export const imageUrl = 'https://github.com/gr4b4z/InfraPilot/pkgs/container/infrapilot';

export const proofMetrics: Metric[] = [
  {
    value: '7+',
    label: 'Catalog examples',
    detail: 'Infrastructure, access, CI/CD, rollback, data, and general request patterns.',
  },
  {
    value: '6',
    label: 'Operational surfaces',
    detail: 'Catalog, approvals, deployments, promotions, webhooks, and admin controls.',
  },
  {
    value: 'Tested',
    label: 'Lifecycle behavior',
    detail: 'Promotion flow, request transitions, duplicate cleanup, and rollback-aware deployment history.',
  },
];

export const featureCards: FeatureCard[] = [
  {
    title: 'Service catalog for real platform work',
    body: 'Publish reusable workflows as YAML so teams request infrastructure through guided forms instead of ad hoc tickets.',
    points: [
      'Dynamic inputs, validations, approval policies, and executor mappings',
      'Examples for namespaces, DNS, repositories, pipelines, role assignments, and rollback',
      'Versionable platform workflows that stay close to the codebase',
    ],
  },
  {
    title: 'Requests, approvals, and audit in one path',
    body: 'InfraPilot tracks each request from creation to approval, execution, retry, and cancellation with an operational paper trail.',
    points: [
      'Approval routing for groups and escalation windows',
      'Request lifecycle and state enforcement',
      'Audit-friendly history for operators and stakeholders',
    ],
  },
  {
    title: 'Deployment visibility that feeds promotions',
    body: 'Ingest deployment events from CI/CD, keep history by product and environment, and use that signal to drive promotion decisions.',
    points: [
      'API-key protected deployment ingest endpoint',
      'Rollback-aware version history and previous version lookup',
      'Promotion candidates created from successful deployments',
    ],
  },
  {
    title: 'Release notes that write themselves',
    body: 'Turn the stream of deployment events into structured release notes per (product, environment, window). Preview, edit, publish, and broadcast.',
    points: [
      'Handlebars templates with per-product and per-environment overrides',
      'Draft → edit markdown → publish flow with a permanent URL',
      'release_note.generated webhook (markdown) + release_note.generated.html (with server-rendered HTML for Confluence / email)',
    ],
  },
  {
    title: 'Governance without replacing your toolchain',
    body: 'InfraPilot works with the delivery and identity systems teams already use instead of forcing a new all-in-one stack.',
    points: [
      'Azure DevOps, GitHub Actions, Jira, Entra ID, Microsoft Graph',
      'Webhook notifications, blob-backed attachments, and optional Service Bus flow',
      'Single-container deployment model for same-origin browser traffic',
    ],
  },
];

export const workflowSteps: WorkflowStep[] = [
  {
    title: '1. Publish a request pattern',
    body: 'Platform teams define request inputs, validation rules, approval needs, and executor mappings in catalog YAML.',
  },
  {
    title: '2. Teams submit through the portal',
    body: 'Developers choose a catalog item, fill in guided inputs, and submit a request with less back-and-forth.',
  },
  {
    title: '3. Approvers review with context',
    body: 'Approvers act on requests and promotions with clear status, comments, escalation rules, and history.',
  },
  {
    title: '4. Delivery systems report back',
    body: 'Deployments are ingested from CI/CD, surfaced in the portal, and used to drive rollback and promotion visibility.',
  },
];

export const proofPanels: ProofPanel[] = [
  {
    title: 'Service Catalog',
    label: 'Catalog UI',
    eyebrow: 'Requestable workflows',
    lines: [
      'Create Namespace',
      'Request DNS Record',
      'Create Repository',
      'Request Role Assignment',
    ],
  },
  {
    title: 'Approvals Queue',
    label: 'Approval UI',
    eyebrow: 'Review in context',
    lines: [
      'prod rollback · Pending',
      'role assignment · Needs security review',
      'namespace request · Approved',
      'repo bootstrap · Changes requested',
    ],
  },
  {
    title: 'Deployments and Promotions',
    label: 'Operations UI',
    eyebrow: 'Operational visibility',
    lines: [
      'order-api · production · 2.14.0',
      'auth-service · staging · 3.1.0-beta.2',
      'promotion candidate · staging → prod',
      'rollback target · previousVersion available',
    ],
  },
  {
    title: 'Release Notes',
    label: 'Release UI',
    eyebrow: 'Auto-generated, editable, broadcast',
    lines: [
      'identity-platform · production · 10 services',
      '[IDP-2946] Fix timezone handling — PR #888 · Build #79588',
      'draft → edit markdown → publish',
      'webhook: release_note.generated',
    ],
  },
];

export const evaluatorChecklist: ChecklistStep[] = [
  {
    title: 'Start the local stack',
    action: 'Run `docker compose up -d` from the repository root and open the frontend on `http://localhost:5173`.',
    expected: 'You should see the InfraPilot portal with catalog, requests, approvals, deployments, and promotions sections available.',
  },
  {
    title: 'Browse the service catalog',
    action: 'Open the catalog and inspect request patterns like namespace creation, role assignment, or repository bootstrap.',
    expected: 'You should see structured inputs, approval metadata, and executor-backed request definitions rather than generic forms.',
  },
  {
    title: 'Submit one request',
    action: 'Create a draft request from a catalog item, then submit it for processing.',
    expected: 'The request should appear in the request lifecycle with status progression and approval visibility.',
  },
  {
    title: 'Post a sample deployment event',
    action: 'Use the deployment ingest API example from the docs to send one deployment payload.',
    expected: 'A new deployment entry should appear in the deployments view and become available for rollback/promotion-related flows.',
  },
  {
    title: 'Review approval and promotion behavior',
    action: 'Open approvals and promotions after the sample request and deployment activity.',
    expected: 'You should be able to see how InfraPilot connects request review, deployment history, and promotion candidate handling into one workflow.',
  },
];

export const docsSections: DocsSection[] = [
  {
    slug: 'introduction',
    group: 'Overview',
    title: 'Introduction',
    summary: 'InfraPilot is an open-source self-service infrastructure portal for requests, approvals, deployments, promotions, and operational workflows.',
    paragraphs: [
      'It gives platform teams a governed front door for common operational work. Instead of managing requests through ad hoc tickets, chat, and scattered approval paths, teams can expose standard workflows as guided forms.',
      'The product is built for platform teams first, while still helping app developers, approvers, and release stakeholders work through the same portal. It sits in front of the systems teams already run rather than replacing them.',
    ],
    bullets: [
      'Catalog-driven self-service workflows',
      'Request and approval lifecycle tracking',
      'Deployment ingest and promotion visibility',
      'Admin controls for webhooks, feature flags, and audit',
    ],
    note: 'The docs hub is static and GitHub Pages-friendly, but the content is sourced from the real repository behavior and contracts.',
  },
  {
    slug: 'quick-start',
    group: 'Getting Started',
    title: 'Quick start',
    summary: 'Run InfraPilot locally with Docker Compose to evaluate the full portal quickly.',
    paragraphs: [
      'The repository already supports a three-service local stack: frontend, API, and Postgres. That makes the quickest evaluation path very short.',
      'Once the stack is running, you can browse the catalog, inspect example request definitions, and begin testing request and deployment flows without extra environment wiring.',
    ],
    bullets: [
      'Run from the repository root with Docker and Docker Compose installed.',
      'Frontend is served on http://localhost:5173.',
      'The API health endpoint is available on http://localhost:5259/health.',
    ],
    code: `docker compose up -d

# open the portal
http://localhost:5173

# API health
http://localhost:5259/health`,
  },
  {
    slug: 'evaluation-guide',
    group: 'Getting Started',
    title: 'Evaluation guide',
    summary: 'Use this guided checklist to evaluate InfraPilot end to end in a single local session.',
    paragraphs: [
      'If you are opening InfraPilot for the first time, start here before reading the deeper architecture or API pages. This is the fastest path to understanding how the product behaves in practice.',
      'The goal of this guide is to show the full loop in a realistic order: open the portal, inspect the catalog, submit one request, send one deployment event, and then verify how approvals, deployments, and promotions connect together.',
    ],
    bullets: [
      '1. Start the stack with `docker compose up -d` and open `http://localhost:5173`.',
      '2. Browse the catalog and inspect request patterns like namespace creation or role assignment.',
      '3. Create and submit one request so it appears in the request lifecycle.',
      '4. Send one deployment event using the deployment ingest example from the API docs.',
      '5. Open deployments, approvals, and promotions to verify how the workflow connects.',
    ],
    code: `# 1. Start InfraPilot locally
docker compose up -d

# 2. Open the portal
http://localhost:5173

# 3. Post a sample deployment event later in the flow
POST /api/deployments/events
X-Api-Key: <your-api-key>

{
  "product": "ticketing-platform",
  "service": "order-api",
  "environment": "production",
  "version": "2.14.0",
  "source": "github-actions",
  "deployedAt": "2026-04-16T10:30:00Z"
}`,
    note: 'Expected outcome: after one request submission and one deployment ingest, you should be able to see InfraPilot working across catalog, request lifecycle, deployment history, and promotion-related views.',
  },
  {
    slug: 'architecture',
    group: 'Getting Started',
    title: 'Architecture',
    summary: 'InfraPilot ships as a same-origin frontend and API pair, with a simple production container model.',
    paragraphs: [
      'In production, Nginx serves the frontend and proxies API traffic to the ASP.NET backend running in the same container. This keeps browser traffic same-origin and reduces routing and CORS complexity.',
      'The application supports PostgreSQL by default and Azure SQL as an alternative, with matching migration sets for both providers.',
    ],
    bullets: [
      'Nginx serves the frontend on port 8080.',
      'ASP.NET API runs on 127.0.0.1:8081 behind Nginx.',
      'Production can be deployed as a single Azure Container App.',
    ],
    code: `docker build -t infrapilot .

ASPNETCORE_ENVIRONMENT=Production
ConnectionStrings__Platform="Host=my-postgres;Database=infrapilot;Username=postgres;Password=secret"
CatalogPath=/app/catalog`,
  },
  {
    slug: 'configuration',
    group: 'Configuration',
    title: 'Core configuration',
    summary: 'Configure runtime, branding, and storage with environment variables rather than custom code changes.',
    paragraphs: [
      'Core runtime settings cover the selected database provider, the main connection string, ASP.NET environment, and catalog path.',
      'The frontend also supports install-specific naming through runtime variables such as app name, subtitle, assistant name, and page title.',
    ],
    bullets: [
      'Core runtime: ConnectionStrings__Platform, Database__Provider, ASPNETCORE_ENVIRONMENT, CatalogPath',
      'Branding: APP_NAME, APP_SUBTITLE, ASSISTANT_NAME, PAGE_TITLE, BACKEND_BASE_URL',
      'Optional integrations: Azure Blob, Service Bus, notifications, Application Insights, deployment enrichment',
    ],
  },
  {
    slug: 'authentication',
    group: 'Configuration',
    title: 'Authentication and identity',
    summary: 'InfraPilot supports local development auth and Entra-backed production auth paths.',
    paragraphs: [
      'For local evaluation, InfraPilot can run in an open development mode with a local JWT-backed flow and seeded local users. That lowers the cost of getting started.',
      'For production, it supports a SPA + API Entra app registration and a separate Graph app registration for server-to-server group lookup and approval resolution.',
    ],
    bullets: [
      'Local mode for low-friction evaluation',
      'MSAL and Entra ID for real user authentication',
      'Graph integration for approval group membership lookups',
      'InfraPortal.Admin app role for admin-only capabilities',
    ],
  },
  {
    slug: 'catalog-authoring',
    group: 'Catalog',
    title: 'Catalog authoring',
    summary: 'Catalog YAML defines the user experience, validation rules, approval path, and executor handoff.',
    paragraphs: [
      'Catalog items are the core of InfraPilot. They let platform teams publish self-service workflows without hardcoding a custom form for each request type.',
      'Each item can define inputs, conditional visibility, validations, approval requirements, escalation settings, and the executor mapping that hands work to downstream systems.',
    ],
    bullets: [
      'Inputs for text, select, user pickers, key-value lists, toggles, and resource pickers',
      'Approval strategies, reviewer groups, and escalation windows',
      'Executor mappings for Azure DevOps, GitHub, Jira, and webhooks',
    ],
    code: `id: create-namespace
name: Create Namespace
category: infrastructure

approval:
  required: true
  strategy: any
  approver_group: "platform-infra-approvers"

executor:
  type: azure-devops-pipeline`,
  },
  {
    slug: 'integration-setup',
    group: 'Integrations',
    title: 'Integration setup overview',
    summary: 'InfraPilot fits in front of existing delivery systems, so successful adoption depends on configuring the right executors and services.',
    paragraphs: [
      'InfraPilot does not replace your CI/CD, ticketing, or identity systems. It standardizes the request and governance layer while continuing to call those systems for fulfillment and operational signal.',
      'A practical rollout usually starts by choosing one or two executor-backed workflows, wiring the downstream platform credentials, and then enabling the matching catalog items.',
    ],
    bullets: [
      'Choose one fulfillment path first: Azure DevOps, GitHub, Jira, or webhooks',
      'Configure the matching credentials and connection settings in the application runtime',
      'Align catalog definitions with the downstream executor contract',
      'Add deployment ingest and notifications once the basic request flow is working',
    ],
  },
  {
    slug: 'executor-setup',
    group: 'Integrations',
    title: 'Executor setup',
    summary: 'Executors turn approved request input into action in Azure DevOps, GitHub, Jira, or webhook-driven systems.',
    paragraphs: [
      'InfraPilot catalog items declare an executor type and then map validated user input into the downstream request shape. That means your executor setup needs two things: application credentials and a stable contract between the catalog definition and the target system.',
      'The current examples focus heavily on Azure DevOps pipelines and repository operations, plus Jira and generic webhook handoff. Those are the best-supported starting points for an initial rollout.',
    ],
    bullets: [
      'Azure DevOps: configure organization URL, project, and PAT',
      'Jira: configure base URL, email, and API token',
      'Webhook executors: provide stable downstream endpoints and request contracts',
      'Keep catalog `parameters_map` aligned with the target pipeline or API input names',
    ],
  },
  {
    slug: 'deployment-and-promotions-setup',
    group: 'Integrations',
    title: 'Deployment and promotions setup',
    summary: 'Deployment ingest and promotions are most useful when configured as one operational path instead of separate features.',
    paragraphs: [
      'The deployment ingest API should be wired into the CI/CD systems that already know when deployments happen. Once those events are arriving, InfraPilot can build current state, rollback target history, and promotion candidates from them.',
      'Promotion setup then adds the environment topology and policy layer on top of that event stream. This is where teams define target environments, approval strategy, minimum approvers, whether the pipeline triggerer is excluded from approving their own promotion, and escalation behavior.',
    ],
    bullets: [
      'Send deployment events from CI/CD to `/api/deployments/events`',
      'Configure promotion topology to describe environment flow',
      'Create promotion policies per product or service and target environment',
      'Use successful deployments as the source of truth for promotions and rollback targets',
    ],
  },
  {
    slug: 'release-notes-setup',
    group: 'Integrations',
    title: 'Release notes setup',
    summary: 'Aggregate deploy events into structured, templated release notes per (product, environment, window) and broadcast them via webhook.',
    paragraphs: [
      'Release Notes turn the stream of ingested deploy events into human-readable summaries — one note per (product, environment, window). Each note is rendered through a Handlebars template, persisted, and dispatched via the `release_note.generated` webhook so downstream consumers (Teams, Confluence, an email blast) can publish without a second call back to InfraPilot.',
      'Templates are stored at three scopes and resolve most-specific-first: per (product, environment), per product, and a global default. Operators can edit a template at any scope from Settings — Release Notes Template. The UI exposes a draft / review workflow: pick window + environment, preview the rendered markdown, edit it inline, then publish to a permanent URL.',
      'The feature is gated by the `features.releaseNotes` flag and is off by default — enable it per environment from the Feature Flags screen.',
    ],
    bullets: [
      'GET `/api/release-notes/preview` for read-only rendered preview, no persistence or webhook',
      'POST `/api/release-notes/generate` to persist + fire `release_note.generated`',
      'Auto-derived window: `from = generatedAt of last published note`, `to = now`',
      'Per (product, environment) template overrides via `release-notes.template.{product}.{environment}` in `platform_settings`',
      'Webhook payload includes both rendered markdown and structured services array',
    ],
  },
  {
    slug: 'notifications-and-webhooks-setup',
    group: 'Integrations',
    title: 'Notifications and webhooks setup',
    summary: 'Webhook subscriptions and notifications extend InfraPilot beyond the portal so downstream systems and teams can react to events.',
    paragraphs: [
      'Webhook subscriptions are useful when another system needs to observe deployment, approval, or operational events. InfraPilot signs outbound webhook deliveries and tracks their retry history.',
      'Notifications are useful when humans need links back into the portal. Email and generic webhook notification channels can be enabled separately from executor webhooks.',
    ],
    bullets: [
      'Create outbound webhook subscriptions for system-to-system delivery',
      'Store the generated webhook secret at creation time and verify signed payloads',
      'Configure portal base URL and SMTP settings for email notification flows',
      'Use delivery history and retry endpoints to debug outbound failures',
    ],
  },
  {
    slug: 'infrastructure-self-service',
    group: 'Use Cases',
    title: 'Infrastructure self-service',
    summary: 'Use InfraPilot as the front door for common platform provisioning requests such as namespaces and DNS records.',
    paragraphs: [
      'A common first adoption path is replacing repetitive infrastructure tickets with guided request forms. Instead of asking teams to open generic issues and wait for clarification, platform teams can expose precise workflows for the infrastructure actions they support most often.',
      'The repository already includes examples for Kubernetes namespace creation and DNS record requests. Those examples show how teams can capture parameters, enforce validation, route approvals, and execute changes through an existing delivery pipeline.',
      'This use case works best when the platform team already knows the standard set of safe, repeatable requests they want to turn into self-service operations.',
    ],
    bullets: [
      'Good fit for namespaces, DNS, quotas, environment setup, and common cluster-level changes',
      'Lets platform teams standardize inputs and reduce back-and-forth clarification',
      'Adds approval and escalation paths for changes that should stay governed',
      'Works well with Azure DevOps pipeline executors already present in the examples',
    ],
    note: 'Relevant catalog examples: `create-namespace.yaml` and `request-dns-record.yaml`.',
  },
  {
    slug: 'access-governance',
    group: 'Use Cases',
    title: 'Access governance',
    summary: 'Use InfraPilot to make access requests structured, reviewable, and auditable instead of relying on email or chat approvals.',
    paragraphs: [
      'Access workflows are a strong fit because they are frequent, policy-sensitive, and often require the same information every time. The request-role-assignment example shows how to collect target principal, role, scope, duration, and business justification in a single guided form.',
      'InfraPilot then routes the request through approval groups and escalation windows before handing execution to downstream automation. That means the platform team can keep access processes consistent without manually checking that every request includes the right details.',
      'This use case is especially valuable when security or platform teams want stronger evidence and traceability for who approved what and why.',
    ],
    bullets: [
      'Structured access requests with justification and duration',
      'Approval groups and escalation for security-sensitive actions',
      'Audit trail across request creation, review, and execution',
      'Good fit for RBAC, cloud role assignment, and other entitlement workflows',
    ],
    note: 'Relevant catalog example: `request-role-assignment.yaml`.',
  },
  {
    slug: 'repository-and-pipeline-bootstrap',
    group: 'Use Cases',
    title: 'Repository and pipeline bootstrap',
    summary: 'Use InfraPilot to standardize how teams create repositories and trigger common CI/CD workflows.',
    paragraphs: [
      'Platform teams often want every new repository or pipeline run to follow a consistent pattern. InfraPilot can expose that as a guided workflow rather than asking engineers to remember naming rules, templates, and pipeline parameters.',
      'The repository includes examples for repository creation and pipeline execution. Those definitions show how to branch by platform, collect optional variables, validate names, and then hand work to Azure DevOps or GitHub-oriented automation.',
      'This use case helps teams move faster while still preserving naming standards, templates, and the right platform defaults.',
    ],
    bullets: [
      'Create repositories from approved templates',
      'Trigger pipelines or workflows with guided parameter input',
      'Validate naming rules before execution',
      'Reduce one-off bootstrap work for the platform team',
    ],
    note: 'Relevant catalog examples: `create-repo.yaml` and `run-pipeline.yaml`.',
  },
  {
    slug: 'deployment-visibility-and-rollbacks',
    group: 'Use Cases',
    title: 'Deployment visibility and rollbacks',
    summary: 'Use InfraPilot as the shared operational layer for deployment history, rollback target selection, and promotion awareness.',
    paragraphs: [
      'InfraPilot is not only a request portal. It also becomes an operational timeline once CI/CD systems start sending deployment events through the deployment ingest API.',
      'That unlocks a practical rollback use case: operators can select from versions that actually shipped to a target environment, request a rollback with a reason, and send that instruction to downstream automation through a webhook or executor.',
      'When paired with promotions, the deployment signal becomes more than a dashboard. It becomes the source of truth for which versions are eligible to move forward and which versions are valid rollback targets.',
    ],
    bullets: [
      'CI/CD pipelines report deploys through the ingest API',
      'Rollback targets come from successful deployed versions, not guesswork',
      'Production rollbacks can still require approval',
      'Deployment history feeds promotion candidates and operational review',
    ],
    note: 'Relevant assets: the deployment ingest API and `rollback-deployment.yaml`.',
  },
  {
    slug: 'general-platform-intake',
    group: 'Use Cases',
    title: 'General platform intake and triage',
    summary: 'Use InfraPilot as a structured intake layer even when the work still lands in Jira or another downstream queue.',
    paragraphs: [
      'Not every request is ready for full automation on day one. InfraPilot still provides value as a structured intake front door for platform teams that want cleaner requests before they reach Jira or an internal backlog.',
      'The general-request example shows a lightweight pattern: collect a clear description and priority, then create a downstream ticket. This is useful when the team wants consistency and visibility now, while deciding later which workflows deserve deeper automation.',
      'This use case works especially well as an early adoption strategy because it lets teams standardize intake without needing to automate every executor immediately.',
    ],
    bullets: [
      'Good first step before deeper self-service automation',
      'Makes requests more consistent and easier to triage',
      'Lets teams keep Jira or another ticket system as the execution system of record',
      'Creates a single front door even when backend fulfillment varies',
    ],
    note: 'Relevant catalog example: `general-request.yaml`.',
  },
  {
    slug: 'deployment-ingest-api',
    group: 'API',
    title: 'Deployment ingest API',
    summary: 'The deployment ingest endpoint is the main CI/CD integration surface for recording deploy events. The event payload also drives promotion approval rules — references and participants nested in the payload feed the gate evaluator directly.',
    paragraphs: [
      'Pipelines send deployment events to InfraPilot using an API key. Those events become the source of truth for deployment history, rollback target selection, promotion candidate creation, and the participants that promotion approval rules check (e.g. "the person who triggered this deploy cannot approve their own promotion").',
      'The request contract supports product, service, environment, version, source, timestamps, status, rollback flags, references, participants, and metadata. Each `references[]` entry can carry its own nested `participants[]` array — that is the more specific signal for "who QA-ed this ticket" or "who reviewed this PR" and wins over the event-level layer for the same role.',
      'Beyond ingest, two operator-facing surfaces sit on the same event: a PATCH endpoint for reference participant overrides (reassign / tombstone a slot without re-running the pipeline), and an enrichment layer populated server-side from Jira / Azure DevOps that is surfaced on read APIs.',
    ],
    bullets: [
      'Endpoint: POST /api/deployments/events',
      'Auth: X-Api-Key header',
      'Status values: succeeded, failed, in_progress',
      'Successful deploys are eligible for promotions and rollback history',
      'References can be repository, pipeline, pull-request, or work-item — work-item references drive the Tickets queue',
      'Participants carry on two levels (event-level + reference-level) with operator overrides and enrichment as additional layers',
    ],
    code: `POST /api/deployments/events
X-Api-Key: <your-api-key>

{
  "product": "ticketing-platform",
  "service": "order-api",
  "environment": "production",
  "version": "2.14.0",
  "source": "github-actions",
  "deployedAt": "2026-04-16T10:30:00Z",
  "status": "succeeded",
  "isRollback": false
}`,
    note: 'A 201 response returns the created event id, the current version, and the previous version for the same product, service, and environment when one exists.',
  },
  {
    slug: 'catalog-api',
    group: 'API',
    title: 'Catalog API',
    summary: 'Catalog endpoints expose active self-service items publicly and provide admin endpoints for catalog management.',
    paragraphs: [
      'The public catalog endpoints are the discovery layer for requestable services. They return active items and detailed input definitions for a single item.',
      'Admin endpoints support catalog CRUD, YAML validation, active-state toggling, and fetching stored YAML for editing.',
    ],
  },
  {
    slug: 'requests-api',
    group: 'API',
    title: 'Requests API',
    summary: 'Requests endpoints create, list, submit, retry, and cancel catalog-backed requests.',
    paragraphs: [
      'These endpoints sit at the center of the self-service workflow. They let the frontend or other clients create requests from catalog items and move them through the lifecycle.',
      'The create endpoint accepts either a catalog item GUID or a slug, which makes integration simpler for human-authored clients.',
    ],
  },
  {
    slug: 'approvals-api',
    group: 'API',
    title: 'Approvals API',
    summary: 'Approvals endpoints expose the review queue and accept approval decisions, rejections, and change requests.',
    paragraphs: [
      'Approvals are modeled as a separate workflow surface so approvers can act on pending work without needing to recreate the request context themselves.',
      'Rejections and change requests require comments, which is enforced directly at the endpoint layer.',
    ],
  },
  {
    slug: 'promotions-api',
    group: 'API',
    title: 'Promotions API',
    summary: 'Promotions endpoints expose promotion candidates, detail views, decision actions, free-form participants, comments, directory search, and admin configuration for promotion policies and topology.',
    paragraphs: [
      'Promotion candidates are created from deployment ingest and then processed through approval and deployment flows. The non-admin endpoints focus on queue operations and review actions.',
      'Each candidate carries deploy-event references (pull requests, work items, commits) and participants (author, reviewer, `triggered-by`) pulled from its source deploy event, plus promotion-level participants added in the portal (QA, release manager, or any custom role) and a free-text comment thread. Role strings are canonicalised on write; display names are controlled by an admin-managed role dictionary in Settings — the same pattern used for environment display names.',
      'Listing supports filtering by status, product, target environment, substring service search, and reference key. When no status filter is supplied the list returns all Pending candidates plus the most-recent resolved tail, so actionable work is never clipped.',
      'A directory-search endpoint proxies Entra ID (via Microsoft Graph) when configured and falls back to local users otherwise, so the portal can resolve real people when assigning participants.',
      'Two invariants keep candidates honest against rollbacks. Source-drift: a candidate can only be approved while its source environment still runs the candidate version — if the source is rolled back off that version the gate blocks it as stale. Idempotent reactivation: redeploying that exact version to the source reactivates the original candidate (clearing the drift block) instead of creating a duplicate. See "Promotion and rollback logic" for the full model.',
      'Admin endpoints configure the policy and topology model that drives the promotion machinery.',
    ],
  },
  {
    slug: 'rollbacks-api',
    group: 'API',
    title: 'Rollbacks API',
    summary: 'Create, preview, approve, reject, and cancel rollback requests that revert one or more services in an environment to an earlier, previously-deployed version.',
    paragraphs: [
      'A rollback is the inverse of a promotion: the environment stays fixed and the version moves backward. One request groups N items so the same model covers a single malfunctioning service (one item) and aligning a whole environment to a reference (many items). Approval, gate, and tracking reuse the promotion policy for that (product, target env), so rollbacks follow promotion rules.',
      'There are two selection modes. `manual` takes an explicit `items` list of `{ service, toVersion }`. `align` derives the items from the diff between the target environment and a `referenceEnv`, optionally minus an `exclude` list ("roll everything back to match prod, except these services"). `POST /api/rollbacks/preview` returns the resolved items with an `eligible` flag and a `skipReason` for each, so the UI can show "will roll back N, skip M" before submitting.',
      'The safety rule: a target version must have previously run successfully in that environment (verified against deploy history) and must differ from what is currently running. Ineligible items are dropped; a request with zero eligible items is rejected.',
      'If the resolved promotion policy has an approver group the request is created Pending and needs approval; with no approver group it auto-approves. On approval the `rollback.approved` webhook fires and your executor performs the deploy. Completion is detected from the resulting deploy event (matched on product/service/env/version after approval) — there is no trusted callback, and the `IsRollback` flag is corroboration, not a requirement. A request only allows Cancel while Pending; once Approved it has been dispatched.',
      'Endpoints: `GET /api/rollbacks` (queue + per-request `canApprove`), `GET /api/rollbacks/{id}` (detail + approvals), `POST /api/rollbacks/preview`, `POST /api/rollbacks`, `POST /api/rollbacks/{id}/approve|reject|cancel`. Per-product enrollment is managed at `PUT /api/rollbacks/admin/enabled-products`. Everything is gated by the global `features.rollbacks` flag plus per-product enrollment, on top of promotion enrollment.',
    ],
    code: `// Single service (manual): roll "api" in staging back to 2.0
POST /api/rollbacks
{
  "product": "acme",
  "targetEnv": "staging",
  "mode": "manual",
  "reason": "2.1 broke checkout",
  "items": [ { "service": "api", "toVersion": "2.0" } ]
}

// Multiple services (manual): explicit per-service targets
POST /api/rollbacks
{
  "product": "acme",
  "targetEnv": "staging",
  "mode": "manual",
  "items": [
    { "service": "api", "toVersion": "2.0" },
    { "service": "web", "toVersion": "5.4" }
  ]
}

// Multiple services (align): match staging to production, except payments-api
POST /api/rollbacks
{
  "product": "acme",
  "targetEnv": "staging",
  "mode": "align",
  "referenceEnv": "production",
  "exclude": [ "payments-api" ],
  "reason": "resync staging to the prod baseline"
}

// rollback.approved webhook payload (what your executor consumes)
{
  "rollbackId": "0f1e...",
  "product": "acme",
  "targetEnv": "staging",
  "mode": "Align",
  "referenceEnv": "production",
  "status": "Approved",
  "reason": "resync staging to the prod baseline",
  "approvedAt": "2026-06-19T10:12:00Z",
  "items": [
    { "service": "api", "fromVersion": "2.1", "toVersion": "2.0", "status": "Pending" },
    { "service": "web", "fromVersion": "5.6", "toVersion": "5.4", "status": "Pending" }
  ]
}`,
    note: 'The executor deploys the toVersion of each item; emitting the resulting deploy event (ideally with isRollback=true) is what flips that item to RolledBack, and the request to RolledBack once all items land.',
  },
  {
    slug: 'webhooks-api',
    group: 'API',
    title: 'Webhooks API',
    summary: 'Webhook endpoints manage outbound subscriptions, delivery history, retries, and test deliveries.',
    paragraphs: [
      'Webhooks are an operational integration surface for emitting deployment and promotion events to downstream systems — useful for pushing Jira/ServiceNow updates, Slack notifications, or triggering Logic Apps.',
      'Promotion-related events include `promotion.approved`, `promotion.rejected`, `promotion.deployed`, and `promotion.updated`. The `promotion.updated` event fires for editorial changes (participant added/removed, comment added/edited/deleted) and carries a `change.changeType` discriminator plus the full current candidate state, so subscribers can act without re-fetching.',
      'Rollback events are `rollback.approved`, `rollback.rejected`, `rollback.deployed`, and `rollback.cancelled`. `rollback.approved` is the one your executor (e.g. a Logic App) should act on: it carries the `rollbackId`, `product`, `targetEnv`, `mode`, and an `items` array of `{ service, fromVersion, toVersion }` to deploy. `rollback.deployed` fires once every item has been confirmed back via deploy events. See the Rollbacks API page for full payload shapes.',
      'The create endpoint generates a secret once and returns it only in the creation response, which is important to capture at subscription creation time.',
    ],
  },
  {
    slug: 'auth-api',
    group: 'API',
    title: 'Auth API',
    summary: 'Local auth endpoints support development login and current-user inspection when InfraPilot runs in local auth mode.',
    paragraphs: [
      'These endpoints are relevant primarily for local development and evaluation flows when MSAL is not configured.',
      'The login endpoint returns a self-issued JWT plus a normalized user object with roles and admin status.',
    ],
  },
  {
    slug: 'feature-flags-api',
    group: 'API',
    title: 'Feature flags API',
    summary: 'Feature flag endpoints expose current flag state and allow admin-only mutation of flag values.',
    paragraphs: [
      'Feature flags let operators gate features and UI surfaces without code redeploys.',
      'Listing and reading flags are authenticated operations, while mutation is restricted to the catalog admin authorization policy.',
    ],
  },
  {
    slug: 'audit-api',
    group: 'API',
    title: 'Audit API',
    summary: 'Audit endpoints expose filterable audit log access for admin and compliance use cases.',
    paragraphs: [
      'The audit API is designed for querying event history by entity, actor, module, action, and time range.',
      'Responses are paginated using page and pageSize query parameters.',
    ],
  },
  {
    slug: 'release-notes-api',
    group: 'API',
    title: 'Release Notes API',
    summary: 'Aggregate deploy events into structured, templated release notes; preview, persist, and dispatch them via webhook.',
    paragraphs: [
      'Release Notes turn deploy events into human-readable summaries per (product, environment, window). Preview endpoints are read-only; the `generate` endpoint persists a row in `release_notes` and dispatches the `release_note.generated` webhook.',
      'Templates resolve most-specific-first: per (product, environment), per product, then a global default. Templates render with Handlebars.Net against the aggregated services. The `generate` endpoint accepts an optional `renderedContent` override so an "edit before publish" UI can persist user-tweaked markdown verbatim.',
      'The feature is gated by the `features.releaseNotes` flag and is off by default. All endpoints require the `CanApprove` authorisation policy.',
    ],
  },
  {
    slug: 'promotion-and-rollback-logic',
    group: 'Operations',
    title: 'Promotion and rollback logic',
    summary: 'How InfraPilot turns deploy events into promotion candidates, gates them, detects drift, and runs rollbacks as the inverse — including what happens to candidates and stories across a rollback.',
    paragraphs: [
      'Both promotions and rollbacks are driven by one input: the stream of deploy events ingested from CI/CD. A promotion answers "this version reached environment A — should it move forward to B?" A rollback answers "environment A is on the wrong version — put it back to a known-good one it ran before." Rollback reuses the promotion policy/approval machinery; the difference is that the environment stays fixed and the version moves backward.',
      'Promotion candidates. When a successful, non-rollback deploy lands in a source environment and a promotion policy exists for the (product, target env) edge, a candidate is created. Its lifecycle is Pending → Approved → Deploying → Deployed, with Superseded and Rejected as terminal off-ramps. A newer version on the same edge supersedes any still-Pending candidate and inherits its work items / PRs / participants, so the latest candidate always carries the full set of forward changes. Completion is matched from the deploy event of the target environment itself (same product/service/env/version), which flips the candidate to Deployed.',
      'The gate. Approval is evaluated against the policy snapshot taken at creation time: an approver group with an Any or N-of-M strategy, optional role exclusion (e.g. the pipeline triggerer cannot approve their own promotion), and optional ticket gates (every work item approved, or auto-approve when there are no tickets). With no approver group the candidate auto-approves.',
      'Source-drift invariant. A candidate is only promotable while its source environment still runs the candidate version. If the source is rolled back (or otherwise moves off that version), the gate blocks the candidate as stale rather than letting it promote a version no live environment runs. Drift is judged on positive evidence — a differing source deploy exists — so an absence of history never blocks. Redeploying that exact version to the source reactivates the original candidate (clearing the block) instead of minting a duplicate.',
      'Rollbacks. A rollback request reverts one or more services in one environment to an earlier version that previously ran there. It is approved through the same policy/gate as a promotion (auto-approve when there is no approver group), then your executor performs the deploy on the rollback.approved webhook. Completion is inferred from the resulting deploy event; cancel is only allowed while Pending, because once approved the work has already been dispatched.',
      'What a rollback does to promotions. When a rollback lands, InfraPilot decides whether the rolled-back version is still worth promoting: if it differs from the current version of the target environment it (re)creates a promotion candidate for it; if it already matches the target, forward promotion is suppressed (there is nothing to ship). This is why rolling staging back to 2.0 while prod runs 1.5 brings the 2.0 promotion back, but rolling staging down to match prod does not.',
      'What a rollback does to stories. A rollback-created candidate does not inherit the stories of the reverted (newer) candidate — those changes were undone. Instead it bundles the true diff against the target: every work item from source-env deploys for versions introduced after the current target version, up to and including the rolled-back-to version. Ordering uses the first-seen deploy time of each version in the source environment, so rolling back to 1.3 after 1.4 shipped carries only the 1.3 stories, while a later roll-forward to 1.4 carries 1.3 + 1.4.',
    ],
    bullets: [
      'Promotion candidate lifecycle: Pending → Approved → Deploying → Deployed; Superseded / Rejected are terminal',
      'Newer version supersedes a pending candidate and inherits its work items, PRs, and participants',
      'Gate = approver group + Any/N-of-M strategy + optional role exclusion and ticket gates; no group ⇒ auto-approve',
      'Source-drift: a candidate is blocked while its source env no longer runs the version; a redeploy reactivates it idempotently',
      'Rollback safety rule: target version must have previously run in that environment and differ from what is running now',
      'Rollback recreates a promotion only when the rolled-back version differs from the target env (otherwise suppressed)',
      'Rollback story bundle = the version diff vs the target (first-seen ordering), never the stories of the reverted version',
      'Cancel a rollback only while Pending; once Approved the rollback.approved webhook has dispatched it',
    ],
    note: 'Gated by `features.rollbacks` plus per-product enrollment, on top of promotion enrollment. Manage enrollment in Settings — Rollbacks.',
  },
  {
    slug: 'operations',
    group: 'Operations',
    title: 'Operations and admin',
    summary: 'InfraPilot includes the controls platform teams need after the first request goes live.',
    paragraphs: [
      'The product includes webhooks, feature flags, duplicate deployment cleanup, audit access, and real-time event streaming. Those features matter once teams move from experimentation to production operations.',
      'This is one of the reasons InfraPilot is more than a request form generator. It is built to support the operational loop around those requests.',
    ],
    bullets: [
      'Feature flags and catalog administration',
      'Webhook subscription management and delivery retries',
      'Audit log access and deployment duplicate cleanup',
      'Server-sent events for live updates',
    ],
  },
];

export const exampleCards: ExampleCard[] = [
  {
    title: 'Create Namespace',
    category: 'Infrastructure',
    description: 'Provision Kubernetes namespaces with quotas and network policy through an approval-aware form.',
    snippet: `id: create-namespace
name: Create Namespace
category: infrastructure

approval:
  required: true
  strategy: any
  approver_group: "platform-infra-approvers"

executor:
  type: azure-devops-pipeline`,
  },
  {
    title: 'Request Role Assignment',
    category: 'Access',
    description: 'Collect user, scope, role, and business justification before sending access changes into the delivery system.',
    snippet: `id: request-role-assignment
name: Request Role Assignment

inputs:
  - id: target_user
    component: UserPicker
  - id: role
    component: Select

approval:
  required: true`,
  },
  {
    title: 'Create Repository',
    category: 'CI/CD',
    description: 'Offer reusable repo creation with optional pipeline setup for either Azure DevOps or GitHub.',
    snippet: `id: create-repo
name: Create Repository

inputs:
  - id: platform
    component: Select
  - id: template
    component: Select

executor:
  type: azure-devops-repo`,
  },
  {
    title: 'Roll Back a Deployment',
    category: 'Deployments',
    description: 'Expose rollback as a governed workflow using deployment history to populate valid target versions.',
    snippet: `id: rollback-deployment
name: Roll back a deployment

inputs:
  - id: target_version
    component: ResourcePicker
    source: deployments/versions

approval:
  required: true`,
  },
];

export const useCaseCards: UseCaseCard[] = [
  {
    title: 'Infrastructure self-service',
    audience: 'Platform engineering',
    summary: 'Turn recurring infrastructure tickets like namespaces and DNS records into governed self-service forms.',
    href: '#/docs/infrastructure-self-service',
  },
  {
    title: 'Access governance',
    audience: 'Security and platform teams',
    summary: 'Capture access context, approvals, and audit history in one request flow.',
    href: '#/docs/access-governance',
  },
  {
    title: 'Repository and pipeline bootstrap',
    audience: 'Developer platform teams',
    summary: 'Standardize repo creation and CI/CD triggers with guided workflows and validation.',
    href: '#/docs/repository-and-pipeline-bootstrap',
  },
  {
    title: 'Deployment visibility and rollbacks',
    audience: 'Release and operations teams',
    summary: 'Use deployment ingest, rollback target history, and promotion awareness as one operational layer.',
    href: '#/docs/deployment-visibility-and-rollbacks',
  },
  {
    title: 'General platform intake',
    audience: 'Teams starting adoption',
    summary: 'Use InfraPilot as a structured intake portal even before every request is fully automated.',
    href: '#/docs/general-platform-intake',
  },
];

export const deploymentApiRequest = `POST /api/deployments/events
X-Api-Key: <your-api-key>

{
  "product": "ticketing-platform",
  "service": "order-api",
  "environment": "production",
  "version": "2.14.0",
  "source": "github-actions",
  "deployedAt": "2026-04-16T10:30:00Z",
  "status": "succeeded",
  "isRollback": false
}`;

export const deploymentApiFullPayload = `{
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
      "url": "https://github.com/Acmetrix/order-api",
      "provider": "github",
      "key": "Acmetrix/order-api",
      "revision": "a1b2c3d4e5f6"
    },
    {
      "type": "pipeline",
      "url": "https://github.com/Acmetrix/order-api/actions/runs/87654",
      "provider": "github",
      "key": "87654",
      "title": "Release 2.14.0"
    },
    {
      "type": "pull-request",
      "url": "https://github.com/Acmetrix/order-api/pull/312",
      "provider": "github",
      "key": "312",
      "title": "Add idempotency key to checkout",
      "participants": [
        {
          "role": "author",
          "displayName": "Jan Kowalski",
          "email": "jan.kowalski@acmetrix.com"
        },
        {
          "role": "reviewer",
          "displayName": "Anna Kowalska",
          "email": "anna.kowalska@acmetrix.com"
        }
      ]
    },
    {
      "type": "work-item",
      "url": "https://acmetrix.atlassian.net/browse/PLT-1234",
      "provider": "jira",
      "key": "PLT-1234",
      "title": "Add idempotency key to checkout endpoint",
      "participants": [
        {
          "role": "qa",
          "displayName": "Piotr Nowak",
          "email": "piotr.nowak@acmetrix.com"
        },
        {
          "role": "assignee",
          "displayName": "Maria Wiśniewska",
          "email": "maria.wisniewska@acmetrix.com"
        }
      ]
    }
  ],
  "participants": [
    {
      "role": "triggered-by",
      "displayName": "Jan Kowalski",
      "email": "jan.kowalski@acmetrix.com"
    }
  ],
  "metadata": {
    "buildNumber": "87654",
    "triggeredBy": "merge-to-main",
    "cluster": "prod-westeurope-01"
  }
}`;

export const deploymentApiResponse = `{
  "id": "f47ac10b-58cc-4372-a567-0e02b2c3d479",
  "version": "2.14.0",
  "previousVersion": "2.13.1"
}`;

export const deploymentApiErrors = `{
  "errors": [
    "'product' is required",
    "'status' must be one of: succeeded, failed, in_progress"
  ]
}`;

export const deploymentApiFieldColumns: TableColumn[] = [
  { key: 'field', label: 'Field' },
  { key: 'type', label: 'Type' },
  { key: 'required', label: 'Required' },
  { key: 'default', label: 'Default' },
  { key: 'description', label: 'Description' },
];

export const deploymentApiFieldRows: TableRow[] = [
  {
    field: '`product`',
    type: 'string',
    required: 'Yes',
    default: '—',
    description: 'Product name. Must be non-empty.',
  },
  {
    field: '`service`',
    type: 'string',
    required: 'Yes',
    default: '—',
    description: 'Service or component name within the product.',
  },
  {
    field: '`environment`',
    type: 'string',
    required: 'Yes',
    default: '—',
    description: 'Target environment such as development, staging, or production.',
  },
  {
    field: '`version`',
    type: 'string',
    required: 'Yes',
    default: '—',
    description: 'Deployed version identifier. Can be a semantic version, SHA, tag, or build label.',
  },
  {
    field: '`source`',
    type: 'string',
    required: 'Yes',
    default: '—',
    description: 'Origin system identifier, for example github-actions or azure-devops.',
  },
  {
    field: '`deployedAt`',
    type: 'DateTimeOffset',
    required: 'Yes',
    default: '—',
    description: 'Deployment timestamp. The API rejects the default zero value.',
  },
  {
    field: '`references`',
    type: 'array',
    required: 'No',
    default: '`[]`',
    description: 'Links to external resources such as repositories, pipelines, PRs, and work items.',
  },
  {
    field: '`participants`',
    type: 'array',
    required: 'No',
    default: '`[]`',
    description: 'People involved in the deployment process.',
  },
  {
    field: '`metadata`',
    type: 'object',
    required: 'No',
    default: '`{}`',
    description: 'Free-form key-value payload for custom deployment metadata.',
  },
  {
    field: '`status`',
    type: 'string',
    required: 'No',
    default: '`"succeeded"`',
    description: 'One of succeeded, failed, or in_progress. Validation is case-insensitive.',
  },
  {
    field: '`isRollback`',
    type: 'boolean',
    required: 'No',
    default: '`false`',
    description: 'Marks the deployment as a rollback to a previously shipped version.',
  },
  {
    field: '`previousVersion`',
    type: 'string',
    required: 'No',
    default: '_server-derived_',
    description: "The predecessor version the caller observed. When omitted the server derives it from the most recent event for the same product/service/environment. Supplying this lets integrators assert the predecessor they saw and detect drift vs. the server's history.",
  },
];

export const deploymentApiStatusColumns: TableColumn[] = [
  { key: 'value', label: 'Value' },
  { key: 'meaning', label: 'Meaning' },
];

export const deploymentApiStatusRows: TableRow[] = [
  {
    value: '`succeeded`',
    meaning: 'Deployment completed successfully. Only this status creates promotion candidates and counts as a rollback target.',
  },
  {
    value: '`failed`',
    meaning: 'Deployment failed. The event is still recorded for history and dashboards.',
  },
  {
    value: '`in_progress`',
    meaning: 'Deployment is still running. A later event for the same version can report the final result.',
  },
];

export const deploymentApiReferenceRows: TableRow[] = [
  {
    field: '`type`',
    type: 'string',
    required: 'Yes',
    default: '—',
    description: 'Reference category.',
  },
  {
    field: '`url`',
    type: 'string',
    required: 'No',
    default: 'null',
    description: 'Full URL to the external resource.',
  },
  {
    field: '`provider`',
    type: 'string',
    required: 'No',
    default: 'null',
    description: 'Provider name such as github, azure-devops, gitlab, or jira.',
  },
  {
    field: '`key`',
    type: 'string',
    required: 'No',
    default: 'null',
    description: 'Provider-specific identifier such as repo name, run id, PR number, or ticket key.',
  },
  {
    field: '`revision`',
    type: 'string',
    required: 'No',
    default: 'null',
    description: 'Git revision or commit SHA when available.',
  },
  {
    field: '`title`',
    type: 'string',
    required: 'No',
    default: 'null',
    description: 'Human-readable title (work-item summary, PR title). When supplied for a work-item reference, the server uses it directly and skips the Jira lookup.',
  },
  {
    field: '`participants`',
    type: 'array',
    required: 'No',
    default: '`[]`',
    description: 'People scoped to this specific reference (e.g. author / reviewer on a PR; qa / assignee on a work item). Same shape as the top-level `participants[]`. Reference-level entries are the more specific signal — they win over the event-level layer for the same role on promotion approval rules.',
  },
];

export const deploymentApiReferenceTypeColumns: TableColumn[] = [
  { key: 'type', label: 'Type' },
  { key: 'usage', label: 'Usage' },
];

export const deploymentApiReferenceTypeRows: TableRow[] = [
  { type: '`repository`', usage: 'Link to the source code repository.' },
  { type: '`pipeline`', usage: 'Link to the CI/CD build or workflow run.' },
  { type: '`pull-request`', usage: 'Link to the merged pull request that triggered the deploy.' },
  { type: '`work-item`', usage: 'Link to a Jira issue, Azure DevOps work item, or similar tracking record.' },
];

export const deploymentApiCommitLinkColumns: TableColumn[] = [
  { key: 'provider', label: 'Provider' },
  { key: 'url', label: 'Resolved URL' },
];

export const deploymentApiCommitLinkRows: TableRow[] = [
  { provider: '`github`, `azure-devops`', url: '`{url}/commit/{revision}`' },
  { provider: '`gitlab`', url: '`{url}/-/commit/{revision}`' },
  { provider: '`bitbucket`', url: '`{url}/commits/{revision}`' },
  { provider: '_other / omitted_', url: 'falls back to `url`' },
];

export const deploymentApiParticipantRows: TableRow[] = [
  {
    field: '`role`',
    type: 'string',
    required: 'Yes',
    default: '—',
    description: 'Role in the deployment process. Canonicalised on write to lower-kebab-case (so `TriggeredBy`, `triggered_by`, and `Triggered By` all collapse to `triggered-by`). Display names are controlled by an admin-managed dictionary in Settings.',
  },
  {
    field: '`displayName`',
    type: 'string',
    required: 'No',
    default: 'null',
    description: 'Human-readable name of the participant.',
  },
  {
    field: '`email`',
    type: 'string',
    required: 'No',
    default: 'null',
    description: 'Email address. Used by the promotions system to populate the pipeline trigger identity (canonical role `triggered-by`).',
  },
];

export const deploymentApiParticipantTypeRows: TableRow[] = [
  {
    type: '`triggered-by`',
    usage: 'Canonical. Person or service principal that initiated the pipeline run. Used by the promotions system for the "exclude deployer" approval rule (same person cannot approve their own promotion).',
  },
  {
    type: '`author`',
    usage: 'Git commit author on the deployed revision.',
  },
  {
    type: '`reviewer`',
    usage: 'Person who reviewed or approved the pull request.',
  },
  {
    type: '`qa`',
    usage: 'QA engineer or tester who validated the change.',
  },
];

export const deploymentApiAuthColumns: TableColumn[] = [
  { key: 'area', label: 'Area' },
  { key: 'detail', label: 'Detail' },
];

export const deploymentApiAuthRows: TableRow[] = [
  {
    area: 'Header',
    detail: '`X-Api-Key: <your-api-key>` is required on every request.',
  },
  {
    area: 'Scope',
    detail: 'Keys may be limited to specific products. If the payload product is outside that scope, the API returns 403 Forbidden.',
  },
  {
    area: 'Rate limit',
    detail: 'Requests are rate limited per API key.',
  },
];

// ── Participant model: event-level vs. reference-level vs. overrides vs. enrichment ──────

export const deploymentApiParticipantLayerColumns: TableColumn[] = [
  { key: 'layer', label: 'Layer' },
  { key: 'source', label: 'Source' },
  { key: 'precedence', label: 'Precedence' },
  { key: 'usage', label: 'Usage' },
];

export const deploymentApiParticipantLayerRows: TableRow[] = [
  {
    layer: 'Override',
    source: 'Operator action via PATCH on a reference',
    precedence: 'Highest — including tombstones that suppress lower layers',
    usage: 'Reassign or clear a slot when the upstream payload is wrong (Jira out of date, missing reviewer, etc.). Persists across re-ingest because it never mutates the source JSON.',
  },
  {
    layer: 'Reference-level',
    source: '`references[].participants` on the ingest payload',
    precedence: 'Wins over event-level for the same role on the same reference',
    usage: 'Author / reviewer on a PR. QA / assignee on a work-item ticket.',
  },
  {
    layer: 'Event-level',
    source: '`participants[]` on the ingest payload',
    precedence: 'Fallback when no reference-level entry covers the role',
    usage: 'Pipeline triggerer (`triggered-by`), deployer, or any role that applies to the whole deploy rather than a single reference.',
  },
  {
    layer: 'Enrichment',
    source: 'Server-derived from `EnrichmentJson`',
    precedence: 'Lowest — augments the event-level layer',
    usage: 'Auto-populated by the enricher (Jira / Azure DevOps lookups). Surfaced as `enrichment.participants` on read APIs and merged into the event-level fallback by promotion authorisation.',
  },
];

// ── Reference participant overrides (PATCH endpoint) ──────────────────────────────────────

export const deploymentApiOverrideEndpointRows: TableRow[] = [
  {
    method: '`PATCH`',
    path: '`/api/deployments/{eventId}/references/{key}/participants`',
    auth: 'Authenticated user',
    description: 'Upsert (or tombstone) the participant for a `(referenceKey, role)` slot on an existing deploy event.',
  },
];

export const deploymentApiOverrideRequest = `PATCH /api/deployments/f47ac10b-.../references/PLT-1234/participants
Content-Type: application/json

{
  "role": "qa",
  "assignee": {
    "email": "ola.kowalska@acmetrix.com",
    "displayName": "Ola Kowalska"
  }
}`;

export const deploymentApiOverrideTombstone = `PATCH /api/deployments/f47ac10b-.../references/PLT-1234/participants
Content-Type: application/json

{
  "role": "qa",
  "assignee": null
}`;

export const deploymentApiOverrideResponse = `{
  "tombstone": false,
  "override": {
    "role": "qa",
    "displayName": "Ola Kowalska",
    "email": "ola.kowalska@acmetrix.com",
    "isOverride": true,
    "assignedBy": "Sylwester Grabowski"
  },
  "participants": [
    {
      "role": "qa",
      "displayName": "Ola Kowalska",
      "email": "ola.kowalska@acmetrix.com",
      "isOverride": true,
      "assignedBy": "Sylwester Grabowski"
    },
    {
      "role": "assignee",
      "displayName": "Maria Wiśniewska",
      "email": "maria.wisniewska@acmetrix.com"
    }
  ]
}`;

// ── Enrichment shape on read APIs ─────────────────────────────────────────────────────────

export const deploymentEnrichmentExample = `{
  "labels": {
    "service-tier": "tier-1",
    "owning-team": "checkout"
  },
  "participants": [
    {
      "role": "qa",
      "displayName": "Piotr Nowak",
      "email": "piotr.nowak@acmetrix.com"
    }
  ],
  "enrichedAt": "2026-04-16T10:31:42Z"
}`;

export const deploymentApiResponseColumns: TableColumn[] = [
  { key: 'status', label: 'Status' },
  { key: 'body', label: 'Body' },
  { key: 'notes', label: 'Notes' },
];

export const deploymentApiResponseRows: TableRow[] = [
  {
    status: '`201 Created`',
    body: '`{ id, version, previousVersion }`',
    notes: '`previousVersion` is the most recent prior version for the same product, service, and environment, or null for first-time deployment.',
  },
  {
    status: '`400 Bad Request`',
    body: '`{ errors: string[] }`',
    notes: 'Returned when required fields are missing or `status` is not one of the supported values.',
  },
  {
    status: '`403 Forbidden`',
    body: 'Empty or framework default forbid response',
    notes: 'Returned when the API key is scoped to products that do not include the payload product.',
  },
];

export const defaultEndpointColumns: TableColumn[] = [
  { key: 'method', label: 'Method' },
  { key: 'path', label: 'Path' },
  { key: 'auth', label: 'Auth' },
  { key: 'description', label: 'Description' },
];

export const bodyFieldColumns: TableColumn[] = [
  { key: 'field', label: 'Field' },
  { key: 'type', label: 'Type' },
  { key: 'required', label: 'Required' },
  { key: 'description', label: 'Description' },
];

export const queryFieldColumns: TableColumn[] = [
  { key: 'name', label: 'Query Param' },
  { key: 'type', label: 'Type' },
  { key: 'description', label: 'Description' },
];

export const apiDocs: Record<string, ApiDocBlock[]> = {
  'deployment-ingest-api': [
    {
      title: 'Authentication',
      columns: deploymentApiAuthColumns,
      rows: deploymentApiAuthRows,
    },
    {
      title: 'Full payload',
      code: deploymentApiFullPayload,
    },
    {
      title: 'Top-level fields',
      columns: deploymentApiFieldColumns,
      rows: deploymentApiFieldRows,
    },
    {
      title: 'Status values',
      columns: deploymentApiStatusColumns,
      rows: deploymentApiStatusRows,
    },
    {
      title: '`references[]` fields',
      columns: deploymentApiFieldColumns,
      rows: deploymentApiReferenceRows,
    },
    {
      title: 'Common reference types',
      columns: deploymentApiReferenceTypeColumns,
      rows: deploymentApiReferenceTypeRows,
    },
    {
      title: 'Commit deep-linking',
      description: 'A `repository` reference that includes both `url` and `revision` is rendered as a link directly to that commit, derived from the `provider`. The URL is built purely from the inbound `url` (with any trailing `.git` or `/` stripped) — no org/repo names are hardcoded.',
      columns: deploymentApiCommitLinkColumns,
      rows: deploymentApiCommitLinkRows,
    },
    {
      title: '`participants[]` fields',
      description: '`participants[]` is the event-level layer — people scoped to the whole deploy (e.g. the pipeline triggerer). Reference-level participants nested under `references[].participants` cover people scoped to one PR or one ticket; both layers are honoured when promotion approval rules look up roles.',
      columns: deploymentApiFieldColumns,
      rows: deploymentApiParticipantRows,
    },
    {
      title: 'Common participant roles',
      columns: deploymentApiReferenceTypeColumns,
      rows: deploymentApiParticipantTypeRows,
    },
    {
      title: 'Participant model — four layers, in descending precedence',
      description: 'Reads of a deploy event resolve participants through a four-layer model. Promotion authorisation, the Tickets queue, and the detail view all use the same precedence so a person who appears at the right layer for a given (reference, role) is consistently surfaced.',
      columns: deploymentApiParticipantLayerColumns,
      rows: deploymentApiParticipantLayerRows,
    },
    {
      title: 'Reference participant overrides — endpoint',
      description: 'Operators can reassign or clear a participant on a specific reference of an already-ingested deploy event without re-running the pipeline. Overrides live in their own table keyed by `(eventId, referenceKey, role)`, so re-ingesting the same event preserves the override (the source `references[]` JSON is never mutated).',
      columns: defaultEndpointColumns,
      rows: deploymentApiOverrideEndpointRows,
    },
    {
      title: 'Reassign — request',
      description: 'Pass `assignee.email` + `assignee.displayName` to upsert a real person into the `(referenceKey, role)` slot. The merged participant list for the reference is returned so the UI can re-render without a follow-up GET.',
      code: deploymentApiOverrideRequest,
    },
    {
      title: 'Tombstone — request',
      description: 'Pass `assignee: null` to suppress the slot — useful when an upstream Jira/PR participant is wrong and there is no replacement. The tombstone hides the lower layers (reference-level, event-level, enrichment) for that `(referenceKey, role)`.',
      code: deploymentApiOverrideTombstone,
    },
    {
      title: 'Response — merged participant list',
      description: 'The response carries the effective participant list for that reference after overrides are applied. `isOverride: true` flags entries that came from an override (rather than the upstream payload); `assignedBy` records the operator who wrote it.',
      code: deploymentApiOverrideResponse,
    },
    {
      title: 'Enrichment (read-side)',
      description: 'When the enricher service is wired in, it populates `EnrichmentJson` after ingest. Read APIs surface the enriched data on the `enrichment` field of the deploy event response. Promotion authorisation treats enrichment participants as part of the event-level fallback layer (so a Jira-supplied QA shows up alongside any explicitly-supplied participant).',
      code: deploymentEnrichmentExample,
    },
    {
      title: 'Responses',
      columns: deploymentApiResponseColumns,
      rows: deploymentApiResponseRows,
    },
    {
      title: '201 Created example',
      code: deploymentApiResponse,
    },
    {
      title: '400 Bad Request example',
      code: deploymentApiErrors,
    },
  ],
  'catalog-api': [
    {
      title: 'Endpoints',
      columns: defaultEndpointColumns,
      rows: [
        { method: '`GET`', path: '`/api/catalog`', auth: 'Authenticated user', description: 'List active catalog items with optional category and search filtering.' },
        { method: '`GET`', path: '`/api/catalog/{slug}`', auth: 'Authenticated user', description: 'Return one catalog item with full input, validation, approval, and executor metadata.' },
        { method: '`GET`', path: '`/api/catalog/admin`', auth: 'Catalog admin', description: 'List all catalog items including inactive entries.' },
        { method: '`POST`', path: '`/api/catalog/admin`', auth: 'Catalog admin', description: 'Create a catalog item from YAML content.' },
        { method: '`PUT`', path: '`/api/catalog/admin/{slug}`', auth: 'Catalog admin', description: 'Replace a catalog item using YAML content.' },
        { method: '`DELETE`', path: '`/api/catalog/admin/{slug}`', auth: 'Catalog admin', description: 'Delete a catalog item.' },
        { method: '`PATCH`', path: '`/api/catalog/admin/{slug}/active`', auth: 'Catalog admin', description: 'Toggle the active state of a catalog item.' },
        { method: '`POST`', path: '`/api/catalog/admin/validate`', auth: 'Catalog admin', description: 'Validate YAML without saving it.' },
        { method: '`GET`', path: '`/api/catalog/admin/{slug}/yaml`', auth: 'Catalog admin', description: 'Fetch the stored YAML content for editing.' },
      ],
    },
    {
      title: 'List query parameters',
      columns: queryFieldColumns,
      rows: [
        { name: '`category`', type: 'string', description: 'Optional category filter for catalog list results.' },
        { name: '`search`', type: 'string', description: 'Optional free-text search filter for catalog list results.' },
      ],
    },
    {
      title: 'Admin request bodies',
      columns: bodyFieldColumns,
      rows: [
        { field: '`yamlContent`', type: 'string', required: 'Yes', description: 'Used by create, update, and validate endpoints. Contains the full catalog item definition in YAML.' },
        { field: '`isActive`', type: 'boolean', required: 'Yes', description: 'Used by the active-state toggle endpoint.' },
      ],
    },
    {
      title: 'Create catalog item example',
      code: `POST /api/catalog/admin

{
  "yamlContent": "id: create-namespace\\nname: Create Namespace\\ncategory: infrastructure\\n..."
}`,
    },
  ],
  'requests-api': [
    {
      title: 'Endpoints',
      columns: defaultEndpointColumns,
      rows: [
        { method: '`GET`', path: '`/api/requests`', auth: 'CanApprove policy', description: 'List requests. By default returns requests for the current user unless scope=all is used.' },
        { method: '`GET`', path: '`/api/requests/{id}`', auth: 'CanApprove policy', description: 'Fetch one request by id.' },
        { method: '`POST`', path: '`/api/requests`', auth: 'CanApprove policy', description: 'Create a draft request from a catalog item id or slug.' },
        { method: '`POST`', path: '`/api/requests/{id}/submit`', auth: 'CanApprove policy', description: 'Submit a request for validation and next-step processing.' },
        { method: '`POST`', path: '`/api/requests/{id}/retry`', auth: 'CanApprove policy', description: 'Retry request execution through the retry handler.' },
        { method: '`POST`', path: '`/api/requests/{id}/cancel`', auth: 'CanApprove policy', description: 'Cancel a request.' },
      ],
    },
    {
      title: 'List query parameters',
      columns: queryFieldColumns,
      rows: [
        { name: '`status`', type: 'string', description: 'Optional request status filter.' },
        { name: '`requesterId`', type: 'string', description: 'Optional requester id. Defaults to the current user unless scope=all is used.' },
        { name: '`scope`', type: 'string', description: 'When set to `all`, returns all requests rather than only the current user view.' },
      ],
    },
    {
      title: 'Create request body',
      columns: bodyFieldColumns,
      rows: [
        { field: '`catalogItemId`', type: 'string', required: 'Yes', description: 'Catalog item GUID or slug.' },
        { field: '`inputs`', type: 'object', required: 'Yes', description: 'Key-value payload matching the selected catalog item inputs.' },
      ],
    },
    {
      title: 'Create request example',
      code: `POST /api/requests

{
  "catalogItemId": "create-namespace",
  "inputs": {
    "namespace_name": "team-payments-api",
    "cluster": "k8s-prod-weu",
    "cpu_limit": 2,
    "memory_limit": 4,
    "enable_network_policy": true
  }
}`,
    },
  ],
  'approvals-api': [
    {
      title: 'Endpoints',
      columns: defaultEndpointColumns,
      rows: [
        { method: '`GET`', path: '`/api/approvals`', auth: 'CanApprove policy', description: 'List approval items and total count.' },
        { method: '`GET`', path: '`/api/approvals/{id}`', auth: 'CanApprove policy', description: 'Return one approval detail object.' },
        { method: '`POST`', path: '`/api/approvals/{id}/approve`', auth: 'CanApprove policy', description: 'Approve an item. Comment is optional.' },
        { method: '`POST`', path: '`/api/approvals/{id}/reject`', auth: 'CanApprove policy', description: 'Reject an item. Comment is required.' },
        { method: '`POST`', path: '`/api/approvals/{id}/request-changes`', auth: 'CanApprove policy', description: 'Request changes. Comment is required.' },
      ],
    },
    {
      title: 'Decision body',
      columns: bodyFieldColumns,
      rows: [
        { field: '`comment`', type: 'string', required: 'Conditional', description: 'Optional for approve, required for reject and request-changes.' },
      ],
    },
    {
      title: 'Reject example',
      code: `POST /api/approvals/8f0a6d85-efab-4bf2-a6e5-c2c4d59ec2df/reject

{
  "comment": "Missing rollout evidence for production."
}`,
    },
  ],
  'promotions-api': [
    {
      title: 'Queue and decision endpoints',
      columns: defaultEndpointColumns,
      rows: [
        { method: '`GET`', path: '`/api/promotions`', auth: 'CanApprove policy', description: 'List promotion candidates with optional status, product, service, targetEnv, and limit filters.' },
        { method: '`GET`', path: '`/api/promotions/{id}`', auth: 'CanApprove policy', description: 'Return one promotion candidate plus approval trail.' },
        { method: '`POST`', path: '`/api/promotions/{id}/approve`', auth: 'CanApprove policy', description: 'Approve one promotion candidate.' },
        { method: '`POST`', path: '`/api/promotions/{id}/reject`', auth: 'CanApprove policy', description: 'Reject one promotion candidate.' },
        { method: '`POST`', path: '`/api/promotions/bulk/approve`', auth: 'CanApprove policy', description: 'Bulk-approve multiple candidates and return per-id outcomes.' },
      ],
    },
    {
      title: 'Queue query parameters',
      columns: queryFieldColumns,
      rows: [
        { name: '`status`', type: 'string', description: 'Optional promotion status filter. Invalid values return 400.' },
        { name: '`product`', type: 'string', description: 'Optional product filter.' },
        { name: '`service`', type: 'string', description: 'Optional service filter.' },
        { name: '`targetEnv`', type: 'string', description: 'Optional target environment filter.' },
        { name: '`limit`', type: 'integer', description: 'Optional result cap. Defaults to 200 when omitted or invalid.' },
      ],
    },
    {
      title: 'Decision request bodies',
      columns: bodyFieldColumns,
      rows: [
        { field: '`comment`', type: 'string', required: 'No', description: 'Optional comment for single approve or reject requests.' },
        { field: '`ids`', type: 'Guid[]', required: 'Yes', description: 'Required for bulk approve; list of candidate ids to process.' },
      ],
    },
    {
      title: 'Admin endpoints',
      columns: defaultEndpointColumns,
      rows: [
        { method: '`GET`', path: '`/api/promotions/admin/policies`', auth: 'Catalog admin', description: 'List promotion policies.' },
        { method: '`GET`', path: '`/api/promotions/admin/policies/{id}`', auth: 'Catalog admin', description: 'Get one promotion policy.' },
        { method: '`POST`', path: '`/api/promotions/admin/policies`', auth: 'Catalog admin', description: 'Create a promotion policy.' },
        { method: '`PUT`', path: '`/api/promotions/admin/policies/{id}`', auth: 'Catalog admin', description: 'Update a promotion policy.' },
        { method: '`DELETE`', path: '`/api/promotions/admin/policies/{id}`', auth: 'Catalog admin', description: 'Delete a promotion policy.' },
        { method: '`GET`', path: '`/api/promotions/admin/topology`', auth: 'Catalog admin', description: 'Fetch the promotion topology.' },
        { method: '`PUT`', path: '`/api/promotions/admin/topology`', auth: 'Catalog admin', description: 'Replace the promotion topology definition.' },
      ],
    },
    {
      title: 'Policy create/update body',
      columns: bodyFieldColumns,
      rows: [
        { field: '`product`', type: 'string', required: 'Yes', description: 'Product identifier for the policy.' },
        { field: '`service`', type: 'string', required: 'No', description: 'Optional service identifier. Null or empty means product default.' },
        { field: '`targetEnv`', type: 'string', required: 'Yes', description: 'Target environment for the promotion policy.' },
        { field: '`approverGroup`', type: 'string', required: 'No', description: 'Optional approver group name.' },
        { field: '`strategy`', type: 'enum', required: 'Yes', description: 'Promotion strategy enum. NOfM requires minApprovers >= 1.' },
        { field: '`minApprovers`', type: 'integer', required: 'Yes', description: 'Minimum approver count. Clamped to at least 1.' },
        { field: '`excludeRole`', type: 'string \\| null', required: 'No', description: 'When set, anyone tagged with this role on the source deploy event cannot approve. Typically `triggered-by`. Null disables the rule.' },
        { field: '`timeoutHours`', type: 'integer', required: 'Yes', description: 'Timeout in hours. Clamped to 0 or higher.' },
        { field: '`escalationGroup`', type: 'string', required: 'No', description: 'Optional escalation group.' },
      ],
    },
  ],
  'webhooks-api': [
    {
      title: 'Endpoints',
      columns: defaultEndpointColumns,
      rows: [
        { method: '`POST`', path: '`/api/webhooks`', auth: 'Catalog admin', description: 'Create a webhook subscription and return the secret once.' },
        { method: '`GET`', path: '`/api/webhooks`', auth: 'Catalog admin', description: 'List subscriptions with delivery statistics.' },
        { method: '`GET`', path: '`/api/webhooks/{id}`', auth: 'Catalog admin', description: 'Fetch one subscription plus recent deliveries.' },
        { method: '`PUT`', path: '`/api/webhooks/{id}`', auth: 'Catalog admin', description: 'Update subscription fields.' },
        { method: '`DELETE`', path: '`/api/webhooks/{id}`', auth: 'Catalog admin', description: 'Delete a subscription and its deliveries.' },
        { method: '`GET`', path: '`/api/webhooks/{id}/deliveries`', auth: 'Catalog admin', description: 'List deliveries with pagination.' },
        { method: '`POST`', path: '`/api/webhooks/deliveries/{id}/retry`', auth: 'Catalog admin', description: 'Retry one failed delivery.' },
        { method: '`POST`', path: '`/api/webhooks/{id}/test`', auth: 'Catalog admin', description: 'Queue a ping delivery for testing.' },
      ],
    },
    {
      title: 'Delivery headers and verification',
      columns: [
        { key: 'header', label: 'Header' },
        { key: 'description', label: 'Description' },
      ],
      rows: [
        {
          header: '`X-Hub-Signature-256`',
          description: 'HMAC-SHA256 signature of the raw request body, formatted as `sha256=<hex digest>`.',
        },
        {
          header: '`X-Webhook-Event`',
          description: 'Event type being delivered, for example `deployment.created` or `ping`.',
        },
        {
          header: '`X-Webhook-Delivery`',
          description: 'Unique delivery id for tracing, replay handling, and log correlation.',
        },
      ],
    },
    {
      title: 'How verification works',
      description:
        'InfraPilot generates a secret when the subscription is created and returns it only once in the create response. For each delivery it signs the exact raw JSON payload with HMAC-SHA256 using that secret, then sends the result in `X-Hub-Signature-256`. Receivers should compute the HMAC over the raw body bytes and compare it to the header value using a constant-time comparison.',
    },
    {
      title: 'Create request body',
      columns: bodyFieldColumns,
      rows: [
        { field: '`name`', type: 'string', required: 'Yes', description: 'Subscription display name.' },
        { field: '`url`', type: 'string', required: 'Yes', description: 'Destination URL.' },
        { field: '`events`', type: 'string[]', required: 'Yes', description: 'At least one event type is required.' },
        { field: '`filters.product`', type: 'string', required: 'No', description: 'Optional product filter.' },
        { field: '`filters.environment`', type: 'string', required: 'No', description: 'Optional environment filter.' },
      ],
    },
    {
      title: 'Update request body',
      columns: bodyFieldColumns,
      rows: [
        { field: '`name`', type: 'string', required: 'No', description: 'Optional replacement name.' },
        { field: '`url`', type: 'string', required: 'No', description: 'Optional replacement URL.' },
        { field: '`events`', type: 'string[]', required: 'No', description: 'Optional replacement event list.' },
        { field: '`filters`', type: 'object', required: 'No', description: 'Optional replacement filters object.' },
        { field: '`active`', type: 'boolean', required: 'No', description: 'Optional active-state change.' },
      ],
    },
    {
      title: 'Delivery query parameters',
      columns: queryFieldColumns,
      rows: [
        { name: '`limit`', type: 'integer', description: 'Optional page size. Defaults to 50.' },
        { name: '`offset`', type: 'integer', description: 'Optional pagination offset. Defaults to 0.' },
      ],
    },
    {
      title: 'Create subscription example',
      code: `POST /api/webhooks

{
  "name": "Production deployment hook",
  "url": "https://example.com/infrapilot/webhook",
  "events": ["deployment.created"],
  "filters": {
    "product": "ticketing-platform",
    "environment": "production"
  }
}`,
    },
    {
      title: 'Create response and secret capture',
      code: `201 Created
{
  "id": "2f16df8e-6289-43d5-8f45-c4422db7f0ce",
  "name": "Production deployment hook",
  "url": "https://example.com/infrapilot/webhook",
  "secret": "whsec_base64value",
  "events": ["deployment.created"],
  "filters": {
    "product": "ticketing-platform",
    "environment": "production"
  },
  "active": true,
  "createdAt": "2026-04-16T12:15:00Z"
}`,
    },
    {
      title: 'Node.js verification example',
      code: `import crypto from 'node:crypto';

function verifyInfraPilotWebhook(rawBody, signatureHeader, secret) {
  if (!signatureHeader?.startsWith('sha256=')) {
    return false;
  }

  const expected = crypto
    .createHmac('sha256', secret)
    .update(rawBody)
    .digest('hex');

  const provided = signatureHeader.slice('sha256='.length);
  const expectedBuffer = Buffer.from(expected, 'utf8');
  const providedBuffer = Buffer.from(provided, 'utf8');

  if (expectedBuffer.length !== providedBuffer.length) {
    return false;
  }

  return crypto.timingSafeEqual(expectedBuffer, providedBuffer);
}`,
    },
    {
      title: 'Verification notes',
      columns: [
        { key: 'item', label: 'Item' },
        { key: 'detail', label: 'Detail' },
      ],
      rows: [
        {
          item: 'Use the raw body',
          detail: 'Verify against the exact raw request body bytes before JSON parsing or reformatting.',
        },
        {
          item: 'Compare safely',
          detail: 'Use constant-time comparison such as `timingSafeEqual` to avoid leaking signature differences.',
        },
        {
          item: 'Store the secret securely',
          detail: 'The secret is shown only once when the subscription is created, so store it immediately in your receiving system.',
        },
        {
          item: 'Trace deliveries',
          detail: 'Log `X-Webhook-Delivery` and `X-Webhook-Event` to correlate retries, failures, and downstream processing.',
        },
      ],
    },
  ],
  'auth-api': [
    {
      title: 'Endpoints',
      columns: defaultEndpointColumns,
      rows: [
        { method: '`POST`', path: '`/api/auth/login`', auth: 'Anonymous', description: 'Authenticate a local user and return a JWT plus normalized user object.' },
        { method: '`GET`', path: '`/api/auth/me`', auth: 'Authenticated user', description: 'Return the current user identity, roles, and admin flag.' },
      ],
    },
    {
      title: 'Login request body',
      columns: bodyFieldColumns,
      rows: [
        { field: '`email`', type: 'string', required: 'Yes', description: 'Local user email.' },
        { field: '`password`', type: 'string', required: 'Yes', description: 'Local user password.' },
      ],
    },
    {
      title: 'Login response example',
      code: `POST /api/auth/login

{
  "email": "admin@example.com",
  "password": "local-dev-password"
}

200 OK
{
  "token": "<jwt>",
  "user": {
    "id": "1",
    "name": "Local Admin",
    "email": "admin@example.com",
    "roles": ["InfraPortal.Admin"],
    "isAdmin": true
  }
}`,
    },
  ],
  'feature-flags-api': [
    {
      title: 'Endpoints',
      columns: defaultEndpointColumns,
      rows: [
        { method: '`GET`', path: '`/api/features`', auth: 'Authenticated user', description: 'List all feature flags stored under keys starting with `features.`.' },
        { method: '`GET`', path: '`/api/features/{key}`', auth: 'Authenticated user', description: 'Return the enabled state for one feature flag.' },
        { method: '`PUT`', path: '`/api/features/{key}`', auth: 'Catalog admin', description: 'Set the enabled state for one feature flag.' },
      ],
    },
    {
      title: 'Set flag body',
      columns: bodyFieldColumns,
      rows: [
        { field: '`enabled`', type: 'boolean', required: 'Yes', description: 'Desired state for the flag.' },
      ],
    },
    {
      title: 'Set flag example',
      code: `PUT /api/features/features.promotions.enabled

{
  "enabled": true
}`,
    },
  ],
  'audit-api': [
    {
      title: 'Endpoints',
      columns: defaultEndpointColumns,
      rows: [
        { method: '`GET`', path: '`/api/audit`', auth: 'AuditViewer policy', description: 'List audit entries with filtering and pagination.' },
      ],
    },
    {
      title: 'Query parameters',
      columns: queryFieldColumns,
      rows: [
        { name: '`correlationId`', type: 'Guid', description: 'Filter by correlation id.' },
        { name: '`entityType`', type: 'string', description: 'Filter by entity type.' },
        { name: '`entityId`', type: 'Guid', description: 'Filter by entity id.' },
        { name: '`actorId`', type: 'string', description: 'Filter by actor id.' },
        { name: '`module`', type: 'string', description: 'Filter by module name.' },
        { name: '`action`', type: 'string', description: 'Filter by action name.' },
        { name: '`from`', type: 'DateTimeOffset', description: 'Filter lower time bound.' },
        { name: '`to`', type: 'DateTimeOffset', description: 'Filter upper time bound.' },
        { name: '`page`', type: 'integer', description: 'Page number. Defaults to 1.' },
        { name: '`pageSize`', type: 'integer', description: 'Page size. Defaults to 50.' },
      ],
    },
    {
      title: 'Response shape',
      columns: deploymentApiResponseColumns,
      rows: [
        { status: '`200 OK`', body: '`{ items, total, page, pageSize }`', notes: 'Returns filtered audit entries sorted by timestamp descending.' },
      ],
    },
  ],
  'release-notes-api': [
    {
      title: 'Endpoints',
      columns: defaultEndpointColumns,
      rows: [
        { method: '`GET`',  path: '`/api/release-notes/preview/raw`', auth: 'CanApprove', description: 'Aggregate deploy events for `(product, environment, from, to)` into the structured services payload. Read-only.' },
        { method: '`GET`',  path: '`/api/release-notes/preview`',     auth: 'CanApprove', description: 'Same aggregation rendered through the resolved Handlebars template. Returns `{ rendered, raw }`. Read-only.' },
        { method: '`GET`',  path: '`/api/release-notes/template`',    auth: 'CanApprove', description: 'Read the saved template at a given scope. Query: `product`, `environment`, `exact` (when `true`, returns only the row at the exact scope without walking the fallback chain).' },
        { method: '`PUT`',  path: '`/api/release-notes/template`',    auth: 'CanApprove', description: 'Save a template at a scope. Body: `{ product?, environment?, template }`.' },
        { method: '`POST`', path: '`/api/release-notes/generate`',    auth: 'CanApprove', description: 'Persist a release note + dispatch `release_note.generated` webhook. Body: `{ product, environment, from?, to?, renderedContent? }`. When `renderedContent` is supplied it is persisted verbatim; otherwise the template is rendered.' },
        { method: '`GET`',  path: '`/api/release-notes`',             auth: 'CanApprove', description: 'List persisted release notes. Query: `product?`, `environment?`, `limit?` (default 100, max 500).' },
        { method: '`GET`',  path: '`/api/release-notes/{id}`',        auth: 'CanApprove', description: 'Detail of a single release note. Returns rendered content + structured `raw` services snapshot.' },
      ],
    },
    {
      title: 'Query parameters — preview / generate',
      columns: queryFieldColumns,
      rows: [
        { name: '`product`',     type: 'string',           description: 'Product name. Required for preview; required for generate.' },
        { name: '`environment`', type: 'string',           description: 'Environment name. Required for preview; required for generate.' },
        { name: '`from`',        type: 'DateTimeOffset',   description: 'Window lower bound (ISO-8601). Required for preview; optional for generate (auto-derived from the `generatedAt` of the last published note for the same `(product, environment)`).' },
        { name: '`to`',          type: 'DateTimeOffset',   description: 'Window upper bound (ISO-8601). Required for preview; optional for generate (defaults to `UtcNow`).' },
      ],
    },
    {
      title: 'Generate request body',
      columns: bodyFieldColumns,
      rows: [
        { field: '`product`',         type: 'string',         required: 'Yes', description: 'Product name.' },
        { field: '`environment`',     type: 'string',         required: 'Yes', description: 'Environment name.' },
        { field: '`from`',            type: 'DateTimeOffset', required: 'No',  description: 'Window lower bound. Defaults to the last published note for `(product, environment)`.' },
        { field: '`to`',              type: 'DateTimeOffset', required: 'No',  description: 'Window upper bound. Defaults to `UtcNow`.' },
        { field: '`renderedContent`', type: 'string',         required: 'No',  description: 'When present, the supplied markdown is persisted verbatim and the template is skipped. Used by the draft → edit → publish UI flow.' },
      ],
    },
    {
      title: 'Template scopes (resolution order)',
      description: 'Templates live in the `platform_settings` table. Resolution picks the most-specific row that exists; when nothing is saved the built-in default template is used.',
      columns: [
        { key: 'scope',    label: 'Scope' },
        { key: 'key',      label: '`platform_settings.Key`' },
        { key: 'effect',   label: 'Effect' },
      ],
      rows: [
        { scope: 'Per (product, environment)', key: '`release-notes.template.{product}.{environment}`', effect: 'Wins over per-product and default for the given pair.' },
        { scope: 'Per product',                key: '`release-notes.template.{product}`',                effect: 'Default for any environment of this product.' },
        { scope: 'Global default',             key: '`release-notes.template.default`',                   effect: 'Fallback for all products. If absent, a hard-coded default ships with the build.' },
      ],
    },
    {
      title: 'Template context — top-level fields',
      columns: bodyFieldColumns,
      rows: [
        { field: '`product`',     type: 'string',        required: '—', description: 'Product name.' },
        { field: '`environment`', type: 'string',        required: '—', description: 'Environment name.' },
        { field: '`date`',        type: 'string',        required: '—', description: 'Date stamp for the rendering (yyyy-MM-dd).' },
        { field: '`from`, `to`',  type: 'string',        required: '—', description: 'Window bounds (ISO-8601 `u` format).' },
        { field: '`services`',    type: 'array',         required: '—', description: 'One entry per service deployed in the window. Use `{{#each services}}`.' },
      ],
    },
    {
      title: 'Template context — per-service fields',
      columns: bodyFieldColumns,
      rows: [
        { field: '`service`',               type: 'string',  required: '—', description: 'Service name.' },
        { field: '`previousVersion`',       type: 'string',  required: '—', description: 'Previous version string, or `—` when this is the first event for the service.' },
        { field: '`currentVersion`',        type: 'string',  required: '—', description: 'Deployed version.' },
        { field: '`isRollback`',            type: 'boolean', required: '—', description: 'Truthy when the underlying deploy event was a rollback.' },
        { field: '`deployedAt`',            type: 'string',  required: '—', description: 'Deploy timestamp (ISO-8601 `u` format).' },
        { field: '`workItems[]`',           type: 'array',   required: '—', description: 'Each: `{ key, title, type, url }`. Use `{{#each workItems}}`.' },
        { field: '`pullRequests[]`',        type: 'array',   required: '—', description: 'Each: `{ key, title, url }`.' },
        { field: '`pipelines[]`',           type: 'array',   required: '—', description: 'Each: `{ key, title, url }`.' },
        { field: '`participants[]`',        type: 'array',   required: '—', description: 'Deduped event + reference-level participants. Each: `{ role, displayName, email }`.' },
        { field: '`pullRequest`',           type: 'object',  required: '—', description: 'First entry of `pullRequests[]`, or `null`. Lets templates avoid `{{#each}}` for the common single-PR case.' },
        { field: '`pipeline`',              type: 'object',  required: '—', description: 'First entry of `pipelines[]`, or `null`.' },
        { field: '`author`, `qa`, `triggeredBy`', type: 'object', required: '—', description: 'Single-best-match participant for each role: `{ displayName, email }` or `null`. Allows `[{{{author.displayName}}}](mailto:{{author.email}})` directly.' },
      ],
    },
    {
      title: 'Built-in default template',
      description: 'Used when no template is stored at any scope. Triple-mustache (`{{{value}}}`) is used for content that should not be HTML-escaped (e.g. names with diacritics, em-dashes, work-item titles).',
      code: `# 🛠️ Release: {{product}} — {{environment}}

**Date:** {{date}} | **Window:** {{from}} → {{to}}

{{#each services}}
* **{{service}}** (\\\`{{{previousVersion}}} → {{currentVersion}}\\\`){{#if isRollback}} ⚠️ rollback{{/if}}
{{#each workItems}}
  * [{{key}}]({{url}}) — {{{title}}}{{#if ../pullRequest}} · PR [#{{../pullRequest.key}}]({{../pullRequest.url}}){{/if}}{{#if ../pipeline}} · Build [{{../pipeline.key}}]({{../pipeline.url}}){{/if}}{{#if ../author}} · author: [{{{../author.displayName}}}](mailto:{{../author.email}}){{/if}}{{#if ../qa}} · qa: [{{{../qa.displayName}}}](mailto:{{../qa.email}}){{/if}}
{{/each}}
{{#unless workItems}}
  * _no work items_{{#if pullRequest}} · PR [#{{pullRequest.key}}]({{pullRequest.url}}){{/if}}{{#if pipeline}} · Build [{{pipeline.key}}]({{pipeline.url}}){{/if}}{{#if author}} · author: [{{{author.displayName}}}](mailto:{{author.email}}){{/if}}{{#if qa}} · qa: [{{{qa.displayName}}}](mailto:{{qa.email}}){{/if}}
{{/unless}}
{{/each}}`,
    },
    {
      title: 'Webhook events',
      description: 'Two events fire on every publish — subscribe to whichever payload matches your consumer. Both honour the standard `Product` / `Environment` subscription filters. The HTML is rendered server-side once via Markdig (advanced pipeline — tables, autolinks, task lists) and reused across all subscribers.',
      columns: [
        { key: 'event',   label: 'Event' },
        { key: 'payload', label: 'Payload' },
        { key: 'use',     label: 'When to use' },
      ],
      rows: [
        { event: '`release_note.generated`',      payload: 'markdown only (`renderedContent`)',                            use: 'Teams incoming webhook (renders markdown natively), Slack, anything that posts markdown verbatim. Smaller payload — preferred default.' },
        { event: '`release_note.generated.html`', payload: 'markdown **and** HTML (`renderedContent` + `renderedHtml`)', use: 'Confluence storage format, HTML email templates, SharePoint pages, anything that can\'t parse markdown.' },
      ],
    },
    {
      title: 'Markdown payload — `release_note.generated`',
      code: `{
  "id": "ae1fa7ef-...",
  "product": "identity-platform",
  "environment": "production",
  "from": "2026-05-06T21:12:17Z",
  "to":   "2026-05-07T14:00:00Z",
  "generatedAt": "2026-05-07T14:05:00Z",
  "renderedContent": "# 🛠️ Release: identity-platform — production\\n...",
  "services": [
    {
      "service": "auth-api",
      "previousVersion": "1.8.5",
      "currentVersion":  "1.10.0",
      "isRollback": false,
      "workItems":    [{ "key": "IDP-2946", "title": "...", "url": "..." }],
      "pullRequests": [{ "key": "888",      "title": "...", "url": "..." }],
      "pipelines":    [{ "key": "build-79588", "url": "..." }],
      "participants": [{ "role": "author", "displayName": "...", "email": "..." }]
    }
  ]
}`,
    },
    {
      title: 'HTML payload — `release_note.generated.html`',
      description: 'Same shape as the markdown event with an extra `renderedHtml` field containing the server-rendered HTML.',
      code: `{
  "id": "ae1fa7ef-...",
  "product": "identity-platform",
  "environment": "production",
  "from": "2026-05-06T21:12:17Z",
  "to":   "2026-05-07T14:00:00Z",
  "generatedAt": "2026-05-07T14:05:00Z",
  "renderedContent": "# 🛠️ Release: identity-platform — production\\n...",
  "renderedHtml":    "<h1>🛠️ Release: identity-platform — production</h1>...",
  "services": [ /* ... same as above ... */ ]
}`,
    },
    {
      title: 'Generate — minimal example',
      description: 'After a release, the simplest pipeline call. The server auto-derives `from` from the most recent published note for the same `(product, environment)`, using `UtcNow` for `to`. The response includes the persisted `renderedContent` so the caller can also post it to Teams without subscribing to the webhook.',
      code: `POST /api/release-notes/generate

{
  "product": "identity-platform",
  "environment": "production"
}`,
    },
    {
      title: 'Responses',
      columns: deploymentApiResponseColumns,
      rows: [
        { status: '`201 Created`',     body: '`{ id, product, environment, from, to, generatedAt, servicesCount, status, renderedContent }`', notes: 'Returned by `POST /generate`. `Location` header points at the new detail URL.' },
        { status: '`200 OK` (list)',   body: '`ReleaseNoteListItem[]`',  notes: 'Returned by `GET /api/release-notes`. One row per persisted note with `id`, `product`, `environment`, window, `generatedAt`, `servicesCount`, `status`.' },
        { status: '`200 OK` (detail)', body: '`ReleaseNoteDetailDto`',   notes: 'Returned by `GET /api/release-notes/{id}`. Includes `renderedContent` and the original `raw` services snapshot.' },
        { status: '`204 No Content`',  body: '—',                         notes: 'Returned by `PUT /api/release-notes/template` on a successful save.' },
        { status: '`400 Bad Request`', body: '`{ error }` or `{ errors[] }`', notes: 'Missing required fields, invalid window (`from > to`), template render failure, or empty window (`code: "no_services"`) when no services were deployed and no `renderedContent` override was supplied.' },
        { status: '`404 Not Found`',   body: '—',                         notes: 'Returned by `GET /api/release-notes/{id}` when the id does not exist.' },
      ],
    },
  ],
};

export const integrations: Integration[] = [
  {
    name: 'Azure DevOps',
    description: 'Run pipeline and repository automation from catalog-backed requests and approvals.',
  },
  {
    name: 'GitHub Actions',
    description: 'Ingest deployments and connect repository-centric workflows without replacing existing CI.',
  },
  {
    name: 'Jira',
    description: 'Turn free-form or structured requests into tracked work items when ticketing remains the right execution layer.',
  },
  {
    name: 'Microsoft Entra ID',
    description: 'Authenticate portal users and enforce admin-facing surfaces through app roles.',
  },
  {
    name: 'Microsoft Graph',
    description: 'Resolve approval groups and approver membership for production routing decisions.',
  },
  {
    name: 'Azure Blob Storage',
    description: 'Store request attachments when workflows need supporting files and evidence.',
  },
  {
    name: 'Azure Service Bus',
    description: 'Decouple execution flow when teams need queue-backed orchestration.',
  },
  {
    name: 'Azure OpenAI',
    description: 'Power assistant-driven catalog discovery, request guidance, and platform query experiences.',
  },
  {
    name: 'Postgres or Azure SQL',
    description: 'Run on either provider with shared application behavior and migrations for each backend.',
  },
];

export const faq = [
  {
    question: 'Who is InfraPilot for?',
    answer:
      'InfraPilot is designed first for platform teams that need a self-service front door with approvals and operational control, while still helping developers, approvers, and release stakeholders work through the same portal.',
  },
  {
    question: 'Does it replace CI/CD and ticketing tools?',
    answer:
      'No. It sits in front of those systems and standardizes the request, approval, and visibility layer while continuing to call Azure DevOps, GitHub, Jira, and webhooks.',
  },
  {
    question: 'What can I evaluate first?',
    answer:
      'Start with Docker Compose, review the catalog examples, ingest a sample deployment event, and walk through one request and one promotion scenario in the portal.',
  },
];
