export type RequestStatus =
  | 'Draft'
  | 'Validating'
  | 'ValidationFailed'
  | 'AwaitingApproval'
  | 'Executing'
  | 'Completed'
  | 'Failed'
  | 'Retrying'
  | 'Rejected'
  | 'ChangesRequested'
  | 'TimedOut'
  | 'ManuallyResolved'
  | 'Cancelled';

export interface CatalogItem {
  id: string;
  slug: string;
  name: string;
  description?: string;
  category: string;
  icon?: string;
  isActive: boolean;
}

export interface ExecutionResult {
  id: string;
  serviceRequestId: string;
  attempt: number;
  status: 'Completed' | 'Failed' | 'InProgress';
  outputJson?: string;
  errorMessage?: string;
  startedAt: string;
  completedAt?: string;
}

export interface ServiceRequest {
  id: string;
  correlationId: string;
  catalogItemId: string;
  requesterId: string;
  requesterName: string;
  status: RequestStatus;
  inputsJson: Record<string, unknown>;
  externalTicketKey?: string;
  externalTicketUrl?: string;
  createdAt: string;
  updatedAt: string;
  catalogItem?: CatalogItem;
  executionResults?: ExecutionResult[];
  approvalRequest?: ApprovalRequest;
}

export interface ApprovalRequest {
  id: string;
  serviceRequestId: string;
  strategy: 'Any' | 'All' | 'Quorum';
  quorumCount?: number;
  status: string;
  timeoutAt?: string;
  escalated: boolean;
  createdAt: string;
  serviceRequest?: ServiceRequest;
  decisions: ApprovalDecision[];
}

export interface ApprovalDecision {
  id: string;
  approvalRequestId: string;
  approverId: string;
  approverName: string;
  decision: 'Approved' | 'Rejected' | 'ChangesRequested';
  comment?: string;
  decidedAt: string;
}

export interface AuditEntry {
  id: string;
  timestamp: string;
  correlationId: string;
  module: string;
  action: string;
  actorId: string;
  actorName: string;
  actorType: string;
  entityType: string;
  entityId?: string;
  beforeState?: Record<string, unknown>;
  afterState?: Record<string, unknown>;
  metadata?: Record<string, unknown>;
}

export interface AgentCard {
  type: 'deployment-list' | 'request-detail' | 'summary' | 'timeline' | 'deployment-state' | 'deployment-activity';
  title?: string;
  data: unknown;
}

export interface A2UIComponent {
  type: string;
  id: string;
  label?: string;
  placeholder?: string;
  required?: boolean;
  dataKey: string;
  options?: Array<{ id: string; label: string }>;
  defaultValue?: unknown;
  source?: string;
  visibleWhen?: { field: string; equals: unknown };
  children?: A2UIComponent[];
  min?: number;
  max?: number;
  step?: number;
  accept?: string[];
  maxSizeMb?: number;
  maxFiles?: number;
  language?: string;
  content?: string;
  severity?: 'info' | 'warning' | 'error';
  fields?: Array<{ label: string; value: string }>;
}

// Deployment tracking types

export interface ProductSummary {
  product: string;
  environments: Record<string, EnvironmentSummary>;
}

export interface EnvironmentSummary {
  totalServices: number;
  deployedServices: number;
  lastDeployedAt: string | null;
}

export interface DeploymentStateEntry {
  /** The deploy event behind this entry — the key for linking to its detail page. */
  id: string;
  product: string;
  service: string;
  environment: string;
  version: string;
  previousVersion: string | null;
  isRollback?: boolean;
  status: string;
  source: string;
  deployedAt: string;
  references: DeployReference[];
  participants: DeployParticipant[];
  enrichment: DeployEnrichment | null;
  run?: DeployRun | null;
}

export interface DeployEvent extends DeploymentStateEntry {
  metadata: Record<string, unknown>;
}

/**
 * The CI run that performed the deployment. Distinct from the `pipeline` reference, which points at
 * the run that *built* the artifact — a component is built once and deployed many times, so only
 * this can explain a particular deployment's outcome. `failureReason` is the one line the pipeline
 * itself named as the cause, so the UI never has to guess which log line matters.
 */
export interface DeployRun {
  provider?: string | null;
  runId?: string | null;
  runNumber?: string | null;
  attempt?: number | null;
  workflowName?: string | null;
  jobName?: string | null;
  runUrl?: string | null;
  /** Deep link to this component's job inside the run; falls back to `runUrl` when unresolved. */
  jobUrl?: string | null;
  triggeredBy?: string | null;
  startedAt?: string | null;
  completedAt?: string | null;
  failureReason?: string | null;
}

export interface DeployReference {
  type: string;
  url?: string;
  provider?: string;
  key?: string;
  revision?: string;
  title?: string;
  participants?: DeployParticipant[];
}

export interface DeployParticipant {
  role: string;
  displayName?: string;
  email?: string;
}

export interface DeployEnrichment {
  labels: Record<string, string>;
  participants: DeployParticipant[];
  enrichedAt: string;
}

/**
 * Collects all participants from a deploy event: reference-level (highest priority,
 * most specific), event-level, and enrichment. Deduplication is intentionally NOT
 * performed — callers may want to show the same person in multiple roles across
 * different references.
 */
export function collectParticipants(evt: DeploymentStateEntry): DeployParticipant[] {
  return [
    ...evt.references.flatMap(r => r.participants ?? []),
    ...evt.participants,
    ...(evt.enrichment?.participants ?? []),
  ];
}

// Deployment detail types

/** A block of captured pipeline output, without its text — fetched separately, on expand. */
export interface DeployLogSummary {
  id: string;
  name: string;
  source?: string | null;
  sequence: number;
  byteCount: number;
  lineCount: number;
  /** True when only the tail was kept, either by the producer or by the ingest cap. */
  truncated: boolean;
  createdAt: string;
}

export interface DeployLogContent {
  id: string;
  name: string;
  source?: string | null;
  content: string;
  truncated: boolean;
  originalByteCount: number;
}

export interface DeployEventHistoryEntry {
  id: string;
  environment: string;
  version: string;
  previousVersion: string | null;
  isRollback: boolean;
  status: string;
  source: string;
  deployedAt: string;
  failureReason: string | null;
}

/**
 * A promotion carrying this deployment's version. `outbound` — this environment is the promotion's
 * source, so this deploy is what may move forward. `inbound` — it is the target, so this deploy is
 * what the promotion delivered.
 */
export interface RelatedPromotion {
  id: string;
  sourceEnv: string;
  targetEnv: string;
  version: string;
  status: string;
  direction: 'outbound' | 'inbound';
  createdAt: string;
  approvedAt: string | null;
  deployedAt: string | null;
}

export interface RelatedWorkItem {
  key: string;
  provider?: string | null;
  url?: string | null;
  title?: string | null;
  /** Environments the ticket is gated for; a work-item link needs one of them. */
  signOffTargetEnvs: string[];
}

export interface DeploymentDetail {
  event: DeployEvent;
  logs: DeployLogSummary[];
  history: DeployEventHistoryEntry[];
  promotions: RelatedPromotion[];
  workItems: RelatedWorkItem[];
}

// Service search & detail types

/**
 * One hit from the cross-product service search. Identity is the (product, service) pair — the
 * same name under two products is two hits — so the product always rides along.
 */
export interface ServiceSearchResult {
  product: string;
  service: string;
  environments: ServiceSearchEnvironment[];
  lastDeployedAt: string;
}

export interface ServiceSearchEnvironment {
  environment: string;
  lastDeployedAt: string;
}

/** One distinct version of a service and the environments it was deployed to. */
export interface ServiceVersion {
  version: string;
  lastDeployedAt: string;
  environments: ServiceVersionEnvironment[];
}

export interface ServiceVersionEnvironment {
  /** The deploy event behind this entry — the key for linking to its detail page. */
  eventId: string;
  environment: string;
  status: string;
  isRollback: boolean;
  deployedAt: string;
}

/** A promotion of this service, regardless of version — the service page's promotion feed. */
export interface ServicePromotion {
  id: string;
  sourceEnv: string;
  targetEnv: string;
  version: string;
  status: string;
  createdAt: string;
  approvedAt: string | null;
  deployedAt: string | null;
}

export interface ServiceDetail {
  product: string;
  service: string;
  environments: DeploymentStateEntry[];
  recentVersions: ServiceVersion[];
  promotions: ServicePromotion[];
}

// Webhook types

export interface WebhookSubscription {
  id: string;
  name: string;
  url: string;
  secret?: string; // only returned on create, and only for generic targets
  events: string[];
  filters: { product: string | null; environment: string | null };
  /** How the delivery is framed on the wire. Fixed at creation time. */
  targetType: 'generic' | 'azure_devops' | 'github' | 'msteams' | 'msteams_html' | 'discord';
  /** azure_devops only — the header carrying the HMAC-SHA1 checksum. */
  signatureHeader?: string | null;
  /** github only — overrides the repository_dispatch event_type. */
  githubEventType?: string | null;
  /** Messaging targets only — Handlebars template for the message body. Null uses the per-event default. */
  messageTemplate?: string | null;
  /** Messaging targets only — heading template. Null uses the per-event default; empty means no heading. */
  messageTitle?: string | null;
  active: boolean;
  createdAt: string;
  updatedAt?: string;
  deliveryStats?: {
    total: number;
    delivered: number;
    failed: number;
    pending: number;
    lastDeliveryAt: string | null;
    lastStatus: string | null;
  };
  recentDeliveries?: WebhookDelivery[];
}

export interface WebhookDelivery {
  id: string;
  eventType: string;
  /** `cancelled` — the source event was retracted while the delivery was still held. */
  status: 'pending' | 'delivered' | 'failed' | 'cancelled';
  attempts: number;
  httpStatus: number | null;
  responseBody: string | null;
  errorMessage: string | null;
  payloadJson?: string;
  createdAt: string;
  deliveredAt: string | null;
  nextRetryAt: string | null;
}

/** One registered build — a row in the build registry (all published builds, any branch). */
export interface BuildSummary {
  id: string;
  product: string;
  service: string;
  version: string;
  /** Full git ref, e.g. `refs/heads/feature/MPT-1234-x`. */
  branch: string;
  commitSha: string | null;
  buildId: string | null;
  buildUrl: string | null;
  artifactRef: string | null;
  artifactDigest: string | null;
  createdAt: string;
  updatedAt: string | null;
}
