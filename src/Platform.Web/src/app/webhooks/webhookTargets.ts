/**
 * Shared vocabulary for the two webhook screens. The event list used to be duplicated verbatim in
 * WebhookListPage and WebhookDetailPage, which meant a new event type had to be added twice or the
 * two pickers silently disagreed.
 */

export const AVAILABLE_EVENTS = [
  'deployment.created',
  'request.status_changed',
  'approval.created',
  'approval.approved',
  'approval.rejected',
  'approval.changesrequested',
  'promotion.created',
  'promotion.approved',
  'promotion.approval.cancelled',
  'promotion.rejected',
  'promotion.deploying',
  'promotion.deployed',
  'promotion.superseded',
  'promotion.updated',
  'promotion.ticket.approved',
  'promotion.ticket.issue-raised',
  'promotion.ticket.blocked',
  'rollback.approved',
  'rollback.rejected',
  'rollback.deployed',
  'rollback.cancelled',
  'release_note.generated',
  'release_note.generated.html',
  'ping',
];

export type WebhookTargetType =
  | 'generic'
  | 'azure_devops'
  | 'github'
  | 'msteams'
  | 'msteams_html'
  | 'discord';

/** Chat targets — they post a rendered message instead of the event envelope. Matches WebhookTargetTypes.Messaging. */
export type NotificationTargetType = 'msteams' | 'msteams_html' | 'discord';

export const NOTIFICATION_TARGET_TYPES: NotificationTargetType[] = [
  'msteams',
  'msteams_html',
  'discord',
];

export function isNotificationTarget(value: string | undefined): value is NotificationTargetType {
  return NOTIFICATION_TARGET_TYPES.includes(value as NotificationTargetType);
}

/** Matches WebhookRequestBuilder.DefaultAzureDevOpsSignatureHeader on the API side. */
export const DEFAULT_ADO_SIGNATURE_HEADER = 'X-Hub-Signature';

export const TARGET_TYPES: {
  value: WebhookTargetType;
  label: string;
  description: string;
}[] = [
  {
    value: 'generic',
    label: 'Generic',
    description: 'Signed JSON POST to any endpoint. HMAC-SHA256 in X-Hub-Signature-256.',
  },
  {
    value: 'azure_devops',
    label: 'Azure DevOps',
    description: 'Triggers a pipeline through an Incoming WebHook service connection.',
  },
  {
    value: 'github',
    label: 'GitHub',
    description: 'Triggers a workflow through the repository_dispatch API.',
  },
  {
    value: 'msteams',
    label: 'Microsoft Teams',
    description: 'Posts a message straight into a Teams channel. No relay needed.',
  },
  {
    value: 'msteams_html',
    label: 'Microsoft Teams (HTML)',
    description: 'Posts HTML to a Power Automate flow that forwards it into the channel.',
  },
  {
    value: 'discord',
    label: 'Discord',
    description: 'Posts a message straight into a Discord channel.',
  },
];

/**
 * The targets the "New Webhook" form offers. The chat targets are deliberately absent: they need a
 * message template and no secret, which is a different form, reached from its own button.
 */
export const WEBHOOK_TARGET_TYPES = TARGET_TYPES.filter((t) => !isNotificationTarget(t.value));

export function targetLabel(value: string | undefined): string {
  return TARGET_TYPES.find((t) => t.value === value)?.label ?? 'Generic';
}

/**
 * Mirrors NotificationTemplates on the API side: what a notification posts when it has no template
 * of its own. Duplicated here only as editor prefill — the API is what actually renders, so a
 * subscription left untouched keeps using the server's default rather than a copy of it.
 *
 * Keyed by event prefix, longest match first, matching the server's resolution order.
 */
const DEFAULT_MESSAGE_TEMPLATES: { match: string; title: string; body: string }[] = [
  {
    match: 'release_note.generated',
    title: 'Release notes — {{data.product}} / {{data.environment}}',
    body: '{{data.renderedContent}}',
  },
  {
    match: 'deployment.created',
    title: '{{data.product}}/{{data.service}} → {{data.environment}}',
    body: `**{{data.service}}** \`{{data.version}}\` deployed to **{{data.environment}}**{{#if data.previousVersion}} (was \`{{data.previousVersion}}\`){{/if}}
Status: {{data.status}}{{#if data.isRollback}} · rollback{{/if}}{{#if data.failureReason}}
Failure: {{data.failureReason}}{{/if}}{{#if data.runUrl}}
[View run]({{data.runUrl}}){{/if}}`,
  },
  {
    match: 'promotion.ticket.',
    title: '{{eventType}} — {{data.workItemKey}}',
    body: `**{{data.workItemKey}}** · {{data.product}} → {{data.targetEnv}}
{{eventType}} by {{data.approver}}{{#if data.comment}}
> {{data.comment}}{{/if}}`,
  },
  {
    match: 'promotion.',
    title: '{{data.product}}/{{data.service}} {{data.sourceEnv}} → {{data.targetEnv}}',
    body: `**{{data.service}}** \`{{data.version}}\` · {{data.sourceEnv}} → **{{data.targetEnv}}**
{{eventType}} (status: {{data.status}}){{#if data.approvedBy}}
Approved by:{{#each data.approvedBy}}
- {{this.name}}{{#if this.reason}} (bypass: {{this.reason}}){{/if}}{{/each}}{{/if}}`,
  },
  {
    match: 'rollback.',
    title: 'Rollback — {{data.product}} / {{data.targetEnv}}',
    body: `**{{data.product}}** rollback in **{{data.targetEnv}}** — {{eventType}} (status: {{data.status}}){{#if data.reason}}
Reason: {{data.reason}}{{/if}}{{#if data.items}}
{{#each data.items}}
- {{this.service}}: \`{{this.fromVersion}}\` → \`{{this.toVersion}}\`{{/each}}{{/if}}`,
  },
  {
    match: 'approval.',
    title: 'Approval — {{eventType}}',
    body: `Request \`{{data.serviceRequestId}}\` — {{eventType}}{{#if data.decidedBy}} by {{data.decidedBy}}{{/if}}
Status: {{data.status}}{{#if data.comment}}
> {{data.comment}}{{/if}}`,
  },
  {
    match: 'request.status_changed',
    title: 'Request {{data.newStatus}}',
    body: 'Request `{{data.requestId}}`: {{data.previousStatus}} → **{{data.newStatus}}**{{#if data.actorName}} by {{data.actorName}}{{/if}}',
  },
  {
    match: 'ping',
    title: 'InfraPilot test notification',
    body: 'This channel is wired up correctly — the notification was delivered by InfraPilot.',
  },
];

export const FALLBACK_MESSAGE_TEMPLATE = {
  title: '{{eventType}}',
  body: `**{{eventType}}**
_No message template is configured for this event. Set one on the notification to control what gets posted._`,
};

export function defaultMessageTemplate(eventType: string): { title: string; body: string } {
  let best = FALLBACK_MESSAGE_TEMPLATE;
  let bestLength = -1;
  for (const candidate of DEFAULT_MESSAGE_TEMPLATES) {
    if (!eventType.startsWith(candidate.match)) continue;
    if (candidate.match.length <= bestLength) continue;
    best = { title: candidate.title, body: candidate.body };
    bestLength = candidate.match.length;
  }
  return best;
}

/**
 * Hides the path of a chat webhook URL, which is a bearer credential in disguise — anyone who reads
 * it off a shoulder or a screenshot can post to the channel. The host still identifies the target.
 */
export function maskNotificationUrl(url: string): string {
  try {
    return `${new URL(url).host}/…`;
  } catch {
    return 'configured webhook URL';
  }
}

/** The endpoint an Incoming WebHook service connection listens on. */
export function azureDevOpsUrl(organization: string, webhookName: string): string {
  const org = encodeURIComponent(organization.trim());
  const name = encodeURIComponent(webhookName.trim());
  return `https://dev.azure.com/${org}/_apis/public/distributedtask/webhooks/${name}?api-version=6.0-preview`;
}

export function githubDispatchUrl(owner: string, repo: string): string {
  return `https://api.github.com/repos/${encodeURIComponent(owner.trim())}/${encodeURIComponent(
    repo.trim()
  )}/dispatches`;
}
