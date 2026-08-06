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

export type WebhookTargetType = 'generic' | 'azure_devops' | 'github';

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
];

export function targetLabel(value: string | undefined): string {
  return TARGET_TYPES.find((t) => t.value === value)?.label ?? 'Generic';
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
