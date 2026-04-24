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
}

export interface DeployEvent extends DeploymentStateEntry {
  id: string;
  metadata: Record<string, unknown>;
}

export interface DeployReference {
  type: string;
  url?: string;
  provider?: string;
  key?: string;
  revision?: string;
  title?: string;
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

// Webhook types

export interface WebhookSubscription {
  id: string;
  name: string;
  url: string;
  secret?: string; // only returned on create
  events: string[];
  filters: { product: string | null; environment: string | null };
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
  status: 'pending' | 'delivered' | 'failed';
  attempts: number;
  httpStatus: number | null;
  responseBody: string | null;
  errorMessage: string | null;
  payloadJson?: string;
  createdAt: string;
  deliveredAt: string | null;
  nextRetryAt: string | null;
}
