import { acquireToken, isMsalEnabled, reauthenticate } from './auth';
import { buildApiUrl } from './runtimeConfig';
import { isLocalAuthEnabled } from './authConfig';
import { getStoredToken } from './localAuth';

class ApiClient {
  private token: string | null = null;

  setToken(token: string) {
    this.token = token;
  }

  private async request<T>(path: string, options: RequestInit = {}): Promise<T> {
    const headers: Record<string, string> = {
      'Content-Type': 'application/json',
      ...((options.headers as Record<string, string>) || {}),
    };

    if (isMsalEnabled()) {
      const token = await acquireToken();
      if (token) {
        headers['Authorization'] = `Bearer ${token}`;
      }
    } else if (isLocalAuthEnabled()) {
      const token = getStoredToken();
      if (token) {
        headers['Authorization'] = `Bearer ${token}`;
      }
    } else if (this.token) {
      headers['Authorization'] = `Bearer ${this.token}`;
    }

    const response = await fetch(buildApiUrl(path), {
      ...options,
      headers,
    });

    if (!response.ok) {
      // A 401 under MSAL means the session expired or was revoked. Silent renewal
      // can't recover and a reload won't either (the stale account lingers in
      // sessionStorage), so force an interactive redirect instead of leaving the
      // UI stuck with no data. Local-auth 401s keep their existing behaviour.
      if (response.status === 401 && isMsalEnabled()) {
        await reauthenticate();
      }
      const error = await response.json().catch(() => ({ error: response.statusText }));
      throw new Error(error.error || `API error: ${response.status}`);
    }

    if (response.status === 204) return undefined as T;
    return response.json();
  }

  // Catalog
  getCatalog() {
    return this.request<CatalogListResponse>('/catalog');
  }

  getCatalogItem(slug: string) {
    return this.request<CatalogItemResponse>(`/catalog/${slug}`);
  }

  // Requests
  getRequests(params?: Record<string, string>) {
    const query = params ? '?' + new URLSearchParams(params).toString() : '';
    return this.request<RequestListResponse>(`/requests${query}`);
  }

  getRequest(id: string) {
    return this.request<RequestDetailResponse>(`/requests/${id}`);
  }

  createRequest(data: CreateRequestPayload) {
    return this.request<{ id: string }>('/requests', {
      method: 'POST',
      body: JSON.stringify(data),
    });
  }

  submitRequest(id: string) {
    return this.request<{ message: string }>(`/requests/${id}/submit`, {
      method: 'POST',
    });
  }

  retryRequest(id: string) {
    return this.request<{ message: string }>(`/requests/${id}/retry`, {
      method: 'POST',
    });
  }

  cancelRequest(id: string) {
    return this.request<{ message: string }>(`/requests/${id}/cancel`, {
      method: 'POST',
    });
  }

  // Approvals
  getApprovals(params?: Record<string, string>) {
    const query = params ? '?' + new URLSearchParams(params).toString() : '';
    return this.request<ApprovalListResponse>(`/approvals${query}`);
  }

  getApproval(id: string) {
    return this.request<ApprovalDetailResponse>(`/approvals/${id}`);
  }

  approveRequest(id: string, comment?: string) {
    return this.request(`/approvals/${id}/approve`, {
      method: 'POST',
      body: JSON.stringify({ comment }),
    });
  }

  rejectRequest(id: string, comment: string) {
    return this.request(`/approvals/${id}/reject`, {
      method: 'POST',
      body: JSON.stringify({ comment }),
    });
  }

  requestChanges(id: string, comment: string) {
    return this.request(`/approvals/${id}/request-changes`, {
      method: 'POST',
      body: JSON.stringify({ comment }),
    });
  }

  // Audit
  getAuditLog(params: Record<string, string>) {
    const query = '?' + new URLSearchParams(params).toString();
    return this.request<AuditLogResponse>(`/audit${query}`);
  }

  // Deployments
  getDeploymentProducts() {
    return this.request<import('./types').ProductSummary[]>('/deployments/products');
  }

  getDeploymentState(params?: { product?: string; environment?: string; serviceName?: string }) {
    const query = params ? '?' + new URLSearchParams(Object.entries(params).filter(([, v]) => v) as [string, string][]).toString() : '';
    return this.request<import('./types').DeploymentStateEntry[]>(`/deployments/state${query}`);
  }

  getDeploymentHistory(product: string, service: string, params?: { environment?: string; limit?: number }) {
    const entries: [string, string][] = [];
    if (params?.environment) entries.push(['environment', params.environment]);
    if (params?.limit) entries.push(['limit', String(params.limit)]);
    const query = entries.length ? '?' + new URLSearchParams(entries).toString() : '';
    return this.request<import('./types').DeployEvent[]>(`/deployments/history/${product}/${service}${query}`);
  }

  getRecentDeployments(product: string, environment: string, since?: string) {
    const query = since ? '?since=' + since : '';
    return this.request<import('./types').DeployEvent[]>(`/deployments/recent/${product}/${environment}${query}`);
  }

  getRecentProductDeployments(product: string, since?: string) {
    const query = since ? '?since=' + since : '';
    return this.request<import('./types').DeployEvent[]>(`/deployments/recent/${product}${query}`);
  }

  // Deployment admin — duplicate cleanup (admin only)
  getDeploymentDuplicatesPreview() {
    return this.request<{ groups: number; rows: number }>('/deployments/admin/duplicates');
  }

  removeDeploymentDuplicates() {
    return this.request<{ groups: number; rows: number }>('/deployments/admin/duplicates', {
      method: 'DELETE',
    });
  }

  // Catalog Admin
  getCatalogAdmin() {
    return this.request<CatalogAdminResponse>('/catalog/admin');
  }

  createCatalogItem(yamlContent: string) {
    return this.request<{ item: { id: string; slug: string; name: string } }>('/catalog/admin', {
      method: 'POST',
      body: JSON.stringify({ yamlContent }),
    });
  }

  updateCatalogItem(slug: string, yamlContent: string) {
    return this.request<{ item: { id: string; slug: string; name: string } }>(`/catalog/admin/${slug}`, {
      method: 'PUT',
      body: JSON.stringify({ yamlContent }),
    });
  }

  deleteCatalogItem(slug: string) {
    return this.request<void>(`/catalog/admin/${slug}`, { method: 'DELETE' });
  }

  toggleCatalogItem(slug: string, isActive: boolean) {
    return this.request<{ slug: string; isActive: boolean }>(`/catalog/admin/${slug}/active`, {
      method: 'PATCH',
      body: JSON.stringify({ isActive }),
    });
  }

  getCatalogItemYaml(slug: string) {
    return this.request<{ yamlContent: string }>(`/catalog/admin/${slug}/yaml`);
  }

  validateCatalogYaml(yamlContent: string) {
    return this.request<{ isValid: boolean; errors: string[] }>('/catalog/admin/validate', {
      method: 'POST',
      body: JSON.stringify({ yamlContent }),
    });
  }

  // Webhooks
  getWebhooks() {
    return this.request<import('./types').WebhookSubscription[]>('/webhooks');
  }

  getWebhook(id: string) {
    return this.request<import('./types').WebhookSubscription>(`/webhooks/${id}`);
  }

  createWebhook(data: { name: string; url: string; events: string[]; filters?: { product?: string; environment?: string } }) {
    return this.request<import('./types').WebhookSubscription>('/webhooks', {
      method: 'POST',
      body: JSON.stringify(data),
    });
  }

  updateWebhook(id: string, data: { name?: string; url?: string; events?: string[]; filters?: { product?: string; environment?: string }; active?: boolean }) {
    return this.request<import('./types').WebhookSubscription>(`/webhooks/${id}`, {
      method: 'PUT',
      body: JSON.stringify(data),
    });
  }

  deleteWebhook(id: string) {
    return this.request<void>(`/webhooks/${id}`, { method: 'DELETE' });
  }

  getWebhookDeliveries(id: string, params?: { limit?: number; offset?: number }) {
    const entries: [string, string][] = [];
    if (params?.limit) entries.push(['limit', String(params.limit)]);
    if (params?.offset) entries.push(['offset', String(params.offset)]);
    const query = entries.length ? '?' + new URLSearchParams(entries).toString() : '';
    return this.request<{ items: import('./types').WebhookDelivery[]; total: number }>(`/webhooks/${id}/deliveries${query}`);
  }

  retryWebhookDelivery(deliveryId: string) {
    return this.request<{ message: string }>(`/webhooks/deliveries/${deliveryId}/retry`, { method: 'POST' });
  }

  testWebhook(id: string) {
    return this.request<{ message: string; deliveryId: string }>(`/webhooks/${id}/test`, { method: 'POST' });
  }

  // ── Promotions ─────────────────────────────────────────────────────────

  listPromotions(params?: {
    status?: string;
    product?: string;
    service?: string;
    targetEnv?: string;
    reference?: string;
    limit?: number;
  }) {
    const entries: [string, string][] = [];
    if (params?.status) entries.push(['status', params.status]);
    if (params?.product) entries.push(['product', params.product]);
    if (params?.service) entries.push(['service', params.service]);
    if (params?.targetEnv) entries.push(['targetEnv', params.targetEnv]);
    if (params?.reference) entries.push(['reference', params.reference]);
    if (params?.limit) entries.push(['limit', String(params.limit)]);
    const query = entries.length ? '?' + new URLSearchParams(entries).toString() : '';
    return this.request<{ candidates: PromotionCandidate[] }>(`/promotions/${query}`);
  }

  getPromotion(id: string) {
    return this.request<{
      candidate: PromotionCandidate;
      approvals: PromotionApprovalEntry[];
      sourceEvent: PromotionSourceEvent | null;
      comments: PromotionComment[];
      approvalProgress: PromotionApprovalProgress;
      eligibleRequirements: EligibleRequirement[];
    }>(`/promotions/${id}`);
  }

  listPromotionRoles() {
    return this.request<{ roles: string[] }>(`/promotions/roles`);
  }

  searchPromotionUsers(q: string) {
    return this.request<{
      users: Array<{ id: string; displayName: string; email: string }>;
    }>(`/promotions/users/search?q=${encodeURIComponent(q)}`);
  }

  searchPromotionGroups(q: string) {
    return this.request<{
      groups: Array<{ id: string; displayName: string }>;
    }>(`/promotions/groups/search?q=${encodeURIComponent(q)}`);
  }

  /**
   * Operator routing override on a deploy event's reference. Pass `assignee: null` to
   * tombstone the slot (suppresses the Jira-supplied participant on the read path so the
   * UI sees an empty slot). The PATCH returns the merged participant list for the target
   * reference so callers can re-render without a follow-up GET.
   */
  assignReferenceParticipant(
    eventId: string,
    referenceKey: string,
    role: string,
    assignee: { email: string; displayName: string } | null,
  ) {
    return this.request<{
      participants: PromotionSourceEventParticipant[];
      tombstone: boolean;
      override: PromotionSourceEventParticipant | null;
    }>(
      `/deployments/${eventId}/references/${encodeURIComponent(referenceKey)}/participants`,
      { method: 'PATCH', body: JSON.stringify({ role, assignee }) },
    );
  }

  upsertPromotionParticipant(
    id: string,
    body: {
      role: string;
      displayName?: string | null;
      email?: string | null;
    },
  ) {
    return this.request<{ participants: PromotionParticipant[] }>(
      `/promotions/${id}/participants`,
      { method: 'POST', body: JSON.stringify(body) },
    );
  }

  removePromotionParticipant(id: string, role: string) {
    return this.request<{ participants: PromotionParticipant[] }>(
      `/promotions/${id}/participants/${encodeURIComponent(role)}`,
      { method: 'DELETE' },
    );
  }

  listPromotionComments(id: string) {
    return this.request<{ comments: PromotionComment[] }>(`/promotions/${id}/comments`);
  }

  addPromotionComment(id: string, body: string) {
    return this.request<PromotionComment>(`/promotions/${id}/comments`, {
      method: 'POST',
      body: JSON.stringify({ body }),
    });
  }

  updatePromotionComment(commentId: string, body: string) {
    return this.request<PromotionComment>(`/promotions/comments/${commentId}`, {
      method: 'PATCH',
      body: JSON.stringify({ body }),
    });
  }

  deletePromotionComment(commentId: string) {
    return this.request<void>(`/promotions/comments/${commentId}`, { method: 'DELETE' });
  }

  approvePromotion(
    id: string,
    comment?: string,
    target?: { stepName: string; requirementName: string },
  ) {
    return this.request<PromotionCandidate>(`/promotions/${id}/approve`, {
      method: 'POST',
      body: JSON.stringify({
        comment,
        stepName: target?.stepName,
        requirementName: target?.requirementName,
      }),
    });
  }

  rejectPromotion(id: string, comment?: string) {
    return this.request<PromotionCandidate>(`/promotions/${id}/reject`, {
      method: 'POST',
      body: JSON.stringify({ comment }),
    });
  }

  bulkApprovePromotions(ids: string[], comment?: string) {
    return this.request<{ results: Array<{ id: string; ok: boolean; status?: string; error?: string }> }>(
      `/promotions/bulk/approve`,
      { method: 'POST', body: JSON.stringify({ ids, comment }) },
    );
  }

  // ── Work-item (ticket) approvals ───────────────────────────────────────

  // Authority + decision history for a single (key, product, env). Drives the
  // TicketsCard row state on the promotion detail page. Returns canApprove +
  // blockedReason mirroring the throwing decision path so the UI surfaces the
  // same wording the user would see on a failed POST.
  getWorkItemContext(key: string, product: string, targetEnv: string) {
    const params = new URLSearchParams({ product, targetEnv });
    return this.request<WorkItemContext>(
      `/work-items/${encodeURIComponent(key)}?${params.toString()}`,
    );
  }

  approveWorkItem(key: string, product: string, targetEnv: string, comment?: string) {
    return this.request<WorkItemApproval>(
      `/work-items/${encodeURIComponent(key)}/approvals`,
      { method: 'POST', body: JSON.stringify({ product, targetEnv, comment }) },
    );
  }

  rejectWorkItem(key: string, product: string, targetEnv: string, comment?: string) {
    return this.request<WorkItemApproval>(
      `/work-items/${encodeURIComponent(key)}/rejections`,
      { method: 'POST', body: JSON.stringify({ product, targetEnv, comment }) },
    );
  }

  // The current user's pending work items across all (product, targetEnv) pairs.
  // Powers the /me/work-items queue page.
  //
  // Optional `role` and `assignee` narrow the list (display only — server-side authorisation
  // is unchanged). The matrix:
  //   - both null            → full authorized list (no narrowing).
  //   - role only            → at least one participant in that role.
  //   - assignee=email       → that email holds a role in the assignee set (or the role-filter
  //                            when set).
  //   - assignee=unassigned  → no participant in the effective role set.
  // Response also carries the (email, role) → count rollup and the canonical role set so the
  // queue page can populate its dropdowns without a second call.
  getMyPendingWorkItems(args?: {
    role?: string;
    assignee?: string;
    /**
     * Status mode — "pending" (default, the inbox awaiting decision) or "decided"
     * (combined approved + rejected history). On "decided" the role/assignee filters are
     * ignored; pass `since` to narrow the time window (server defaults to last 24h).
     */
    status?: 'pending' | 'decided';
    /** ISO timestamp lower bound on the decision time. Only used when status === 'decided'. */
    since?: string;
  }) {
    const params = new URLSearchParams();
    const role = args?.role?.trim();
    const assignee = args?.assignee?.trim();
    const status = args?.status;
    if (role) params.set('role', role);
    if (assignee) params.set('assignee', assignee);
    if (status && status !== 'pending') params.set('status', status);
    if (args?.since) params.set('since', args.since);
    const qs = params.toString();
    const suffix = qs.length > 0 ? `?${qs}` : '';
    return this.request<MyPendingWorkItemsResponse>(`/work-items/me/pending${suffix}`);
  }

  // ── Promotion admin ────────────────────────────────────────────────────

  listPromotionPolicies() {
    return this.request<{ policies: PromotionPolicy[] }>(`/promotions/admin/policies`);
  }

  upsertPromotionPolicy(policy: UpsertPromotionPolicyPayload, id?: string) {
    return id
      ? this.request<PromotionPolicy>(`/promotions/admin/policies/${id}`, {
          method: 'PUT',
          body: JSON.stringify(policy),
        })
      : this.request<PromotionPolicy>(`/promotions/admin/policies`, {
          method: 'POST',
          body: JSON.stringify(policy),
        });
  }

  deletePromotionPolicy(id: string) {
    return this.request<void>(`/promotions/admin/policies/${id}`, { method: 'DELETE' });
  }

  // ── Rollbacks ──────────────────────────────────────────────────────────

  listRollbacks(params?: {
    status?: string;
    product?: string;
    targetEnv?: string;
    limit?: number;
  }) {
    const entries: [string, string][] = [];
    if (params?.status) entries.push(['status', params.status]);
    if (params?.product) entries.push(['product', params.product]);
    if (params?.targetEnv) entries.push(['targetEnv', params.targetEnv]);
    if (params?.limit) entries.push(['limit', String(params.limit)]);
    const query = entries.length ? '?' + new URLSearchParams(entries).toString() : '';
    return this.request<{ requests: RollbackRequest[] }>(`/rollbacks${query}`);
  }

  getRollback(id: string) {
    return this.request<RollbackRequest & { approvals: RollbackApprovalEntry[] }>(
      `/rollbacks/${id}`,
    );
  }

  previewRollback(body: RollbackInput) {
    return this.request<RollbackPreview>(`/rollbacks/preview`, {
      method: 'POST',
      body: JSON.stringify(body),
    });
  }

  createRollback(body: RollbackInput) {
    return this.request<RollbackRequest>(`/rollbacks`, {
      method: 'POST',
      body: JSON.stringify(body),
    });
  }

  approveRollback(id: string, comment?: string) {
    return this.request<RollbackRequest>(`/rollbacks/${id}/approve`, {
      method: 'POST',
      body: JSON.stringify({ comment }),
    });
  }

  rejectRollback(id: string, comment?: string) {
    return this.request<RollbackRequest>(`/rollbacks/${id}/reject`, {
      method: 'POST',
      body: JSON.stringify({ comment }),
    });
  }

  cancelRollback(id: string) {
    return this.request<RollbackRequest>(`/rollbacks/${id}/cancel`, {
      method: 'POST',
    });
  }

  getRollbackEnabledProducts() {
    return this.request<{ products: string[] }>(`/rollbacks/enabled-products`);
  }

  // Admin: set the full set of products enrolled in rollbacks.
  setRollbackEnabledProducts(products: string[]) {
    return this.request<{ products: string[] }>(`/rollbacks/admin/enabled-products`, {
      method: 'PUT',
      body: JSON.stringify({ products }),
    });
  }

  // ── Feature flags ──────────────────────────────────────────────────────

  listFeatureFlags() {
    return this.request<{ flags: FeatureFlag[] }>(`/features`);
  }

  setFeatureFlag(key: string, enabled: boolean) {
    return this.request<{ key: string; enabled: boolean }>(`/features/${encodeURIComponent(key)}`, {
      method: 'PUT',
      body: JSON.stringify({ enabled }),
    });
  }

  // ── Deployment versions (for rollback picker) ──────────────────────────

  getDeploymentVersions(params: { product: string; environment: string; service?: string; limit?: number }) {
    const entries: [string, string][] = [
      ['product', params.product],
      ['environment', params.environment],
    ];
    if (params.service) entries.push(['serviceName', params.service]);
    if (params.limit) entries.push(['limit', String(params.limit)]);
    const query = '?' + new URLSearchParams(entries).toString();
    return this.request<{ versions: DeploymentVersion[] }>(`/deployments/versions${query}`);
  }

  // ── Release Notes ──────────────────────────────────────────────────────

  getReleaseNotePreviewRaw(params: { product: string; environment: string; from: string; to: string }) {
    const q = new URLSearchParams(params as Record<string, string>).toString();
    return this.request<RawPreview>(`/release-notes/preview/raw?${q}`);
  }

  getReleaseNotePreview(params: { product: string; environment: string; from: string; to: string }) {
    const q = new URLSearchParams(params as Record<string, string>).toString();
    return this.request<{ rendered: string; raw: RawPreview }>(`/release-notes/preview?${q}`);
  }

  getReleaseNoteTemplate(opts: { product?: string; environment?: string; exact?: boolean } = {}) {
    const entries: [string, string][] = [];
    if (opts.product) entries.push(['product', opts.product]);
    if (opts.environment) entries.push(['environment', opts.environment]);
    if (opts.exact) entries.push(['exact', 'true']);
    const q = entries.length ? '?' + new URLSearchParams(entries).toString() : '';
    return this.request<{ product: string; environment: string; template: string }>(`/release-notes/template${q}`);
  }

  saveReleaseNoteTemplate(template: string, opts: { product?: string; environment?: string } = {}) {
    return this.request<void>(`/release-notes/template`, {
      method: 'PUT',
      body: JSON.stringify({
        product: opts.product ?? null,
        environment: opts.environment ?? null,
        template,
      }),
    });
  }

  generateReleaseNote(payload: { product: string; environment: string; from?: string; to?: string; renderedContent?: string }) {
    return this.request<ReleaseNoteListItem & { renderedContent: string }>(`/release-notes/generate`, {
      method: 'POST',
      body: JSON.stringify(payload),
    });
  }

  listReleaseNotes(params: { product?: string; environment?: string; page?: number; pageSize?: number } = {}) {
    const entries: [string, string][] = [];
    if (params.product) entries.push(['product', params.product]);
    if (params.environment) entries.push(['environment', params.environment]);
    if (params.page) entries.push(['page', String(params.page)]);
    if (params.pageSize) entries.push(['pageSize', String(params.pageSize)]);
    const q = entries.length ? '?' + new URLSearchParams(entries).toString() : '';
    return this.request<PagedResult<ReleaseNoteFeedItem>>(`/release-notes${q}`);
  }

  getReleaseNote(id: string) {
    return this.request<ReleaseNoteDetail>(`/release-notes/${id}`);
  }

  // ── Shared UI settings (environments, roles, activity template) ────────────

  getAppSettings() {
    return this.request<AppSettingsPayload>(`/settings`);
  }

  saveAppSettings(payload: AppSettingsPayload) {
    return this.request<void>(`/settings`, {
      method: 'PUT',
      body: JSON.stringify(payload),
    });
  }
}

export interface AppSettingsPayload {
  environments: { key: string; displayName: string }[];
  roles: { key: string; displayName: string }[];
  activityTemplate: { template: string; style: 'primary' | 'secondary' | 'muted' }[];
}

export interface RawPreview {
  product: string;
  environment: string;
  from: string;
  to: string;
  generatedAt: string;
  services: Array<{
    service: string;
    previousVersion: string | null;
    currentVersion: string;
    isRollback: boolean;
    deployedAt: string;
    workItems: Array<{ key: string; title: string | null; type: string | null; url: string | null }>;
    pullRequests: Array<{ key: string | null; title: string | null; url: string | null }>;
    participants: Array<{ role: string; displayName: string | null; email: string | null }>;
  }>;
}

export interface ReleaseNoteListItem {
  id: string;
  product: string;
  environment: string;
  from: string;
  to: string;
  generatedAt: string;
  servicesCount: number;
  status: string;
}

export interface ReleaseNoteDetail extends ReleaseNoteListItem {
  renderedContent: string;
  raw: RawPreview;
}

export interface ReleaseNoteFeedItem extends ReleaseNoteListItem {
  renderedContent: string;
}

export interface PagedResult<T> {
  items: T[];
  total: number;
  page: number;
  pageSize: number;
}

// Response types
interface CatalogListResponse {
  items: import('./types').CatalogItem[];
}

interface CatalogItemResponse {
  item: import('./types').CatalogItem;
  inputs: Array<{
    id: string;
    component: string;
    label: string;
    placeholder?: string;
    validation?: string;
    required: boolean;
    default?: unknown;
    source?: string;
    options?: Array<{ id: string; label: string }>;
    visibleWhen?: { field: string; equals: unknown };
    min?: number;
    max?: number;
    step?: number;
  }>;
  validations?: unknown[];
  approval?: { required: boolean; type?: string };
  executor?: { type: string };
}

interface CatalogAdminResponse {
  items: Array<{
    id: string;
    slug: string;
    name: string;
    description?: string;
    category: string;
    icon?: string;
    isActive: boolean;
    createdAt: string;
    updatedAt: string;
  }>;
}

interface RequestListResponse {
  items: import('./types').ServiceRequest[];
  total: number;
}

interface RequestDetailResponse {
  request: import('./types').ServiceRequest;
}

interface ApprovalListResponse {
  items: import('./types').ApprovalRequest[];
  total: number;
}

interface ApprovalDetailResponse {
  approval: import('./types').ApprovalRequest;
}

interface AuditLogResponse {
  items: import('./types').AuditEntry[];
  total: number;
}

interface CreateRequestPayload {
  catalogItemId: string;
  inputs: Record<string, unknown>;
}

// ── Promotions ────────────────────────────────────────────────────────────
export type PromotionStatus =
  | 'Pending'
  | 'Approved'
  | 'Deploying'
  | 'Deployed'
  | 'Superseded'
  | 'Rejected';

export interface PromotionCandidate {
  id: string;
  product: string;
  service: string;
  sourceEnv: string;
  targetEnv: string;
  version: string;
  /** Version currently deployed in `targetEnv` (what this promotion would replace). Null for first deploy. */
  targetCurrentVersion: string | null;
  status: PromotionStatus;
  externalRunUrl: string | null;
  createdAt: string;
  approvedAt: string | null;
  deployedAt: string | null;
  supersededById: string | null;
  participants: PromotionParticipant[];
  sourceEventParticipants: PromotionParticipant[];
  sourceEventReferences: PromotionSourceEventReference[];
  canApprove: boolean;
}

export interface WorkItemApproval {
  id: string;
  workItemKey: string;
  product: string;
  targetEnv: string;
  approverEmail: string;
  approverName: string;
  decision: 'Approved' | 'Rejected';
  comment: string | null;
  createdAt: string;
}

export interface WorkItemContext {
  workItemKey: string;
  product: string;
  targetEnv: string;
  pendingCandidateId: string | null;
  canApprove: boolean;
  blockedReason: string | null;
  approvals: WorkItemApproval[];
}

/** One row from `GET /api/work-items/me/pending`. */
export interface PendingTicket {
  workItemKey: string;
  product: string;
  targetEnv: string;
  provider: string | null;
  url: string | null;
  title: string | null;
  candidateId: string;
  service: string;
  version: string;
  sourceEnv: string;
  blockingPromotions: number;
  /** Source deploy event id — used to PATCH reference participants. */
  sourceDeployEventId: string;
  /** Participants on this specific work-item reference (overrides applied). */
  participants: PromotionSourceEventParticipant[];
  /**
   * Status of the candidate this row represents. "Pending" on the inbox; for decision-history
   * views the candidate may have moved on (Approved / Deploying / Deployed / Rejected /
   * Superseded). "Unknown" when no candidate could be linked to the row.
   */
  candidateStatus?: string;
  /**
   * The decision recorded on this ticket — null on the pending inbox; populated on the
   * "decided" view. Decisions can come from any approver in the candidate's authorised group.
   */
  decision?: 'Approved' | 'Rejected' | null;
  decidedAt?: string | null;
  decidedByEmail?: string | null;
  decidedByName?: string | null;
  decisionComment?: string | null;
}

/**
 * One row of the (email, role) assignee summary returned alongside the queue. Counts come from
 * the user's authorized list <i>before</i> the role/person filter is applied, so the queue page
 * can render every choice the user could narrow to. Aggregated server-side per (email, role)
 * pair — a single person on multiple roles produces multiple rows.
 */
export interface PendingAssignee {
  email: string;
  displayName: string;
  role: string;
  count: number;
}

/** Full response shape for `GET /api/work-items/me/pending`. */
export interface MyPendingWorkItemsResponse {
  tickets: PendingTicket[];
  /** (email, role) rollup of the unfiltered authorized list. Sorted by count desc, displayName asc. */
  assignees: PendingAssignee[];
  /** Canonical assignee-role set from PromotionAssigneeRoleSettings — feeds the role dropdown. */
  roles: string[];
}

export interface PromotionParticipant {
  role: string;
  displayName: string | null;
  email: string | null;
}

export interface PromotionComment {
  id: string;
  candidateId: string;
  authorEmail: string;
  authorName: string;
  body: string;
  createdAt: string;
  updatedAt: string | null;
}

export interface PromotionSourceEventReference {
  type: string;
  url?: string | null;
  provider?: string | null;
  key?: string | null;
  revision?: string | null;
  title?: string | null;
  /**
   * Reference-scoped participants. Optional and may be absent on legacy payloads —
   * always treat as `participants ?? []`. Same shape as event-level participants.
   * The reference-level layer is the more specific signal for excluded-role checks
   * (a QA on a ticket, an author on a PR, etc.).
   */
  participants?: PromotionSourceEventParticipant[];
}

export interface PromotionSourceEventParticipant {
  role: string;
  displayName?: string | null;
  email?: string | null;
  /**
   * True when this participant came from an operator-supplied override that displaced
   * (or filled in) the original Jira/event payload. Server-owned: clients should treat
   * this as a read-only tag for rendering an "(overridden by …)" hint.
   */
  isOverride?: boolean;
  /** Display name of the user who made the override. Null on non-overridden entries. */
  assignedBy?: string | null;
}

export interface PromotionSourceEventEnrichment {
  labels: Record<string, string>;
  participants: PromotionSourceEventParticipant[];
  enrichedAt: string;
}

export interface PromotionSourceEvent {
  id: string;
  deployedAt: string;
  source: string;
  references: PromotionSourceEventReference[];
  participants: PromotionSourceEventParticipant[];
  enrichment: PromotionSourceEventEnrichment | null;
}

export interface PromotionApprovalEntry {
  id: string;
  approverEmail: string;
  approverName: string;
  comment: string | null;
  decision: 'Approved' | 'Rejected';
  /** Which requirement the approver was recorded against (null on legacy/auto rows). */
  stepName: string | null;
  requirementName: string | null;
  createdAt: string;
}

/**
 * One requirement the current user is eligible to approve as, identified by its unique
 * (stepName, requirementName) pair. When more than one is returned the UI prompts the approver
 * to choose which one they approve as.
 */
export interface EligibleRequirement {
  stepName: string;
  requirementName: string;
}

/** Live approval gate progress for the detail view (mirrors the backend `ApprovalProgress`). */
export interface PromotionApprovalProgress {
  /** False for auto-approve candidates / no requirements — the UI hides the panel. */
  requiresApproval: boolean;
  allSatisfied: boolean;
  totalRequired: number;
  totalApproved: number;
  steps: PromotionStepProgress[];
  /** The "all work items resolved" gate condition, when the policy gates on it; null otherwise. */
  workItems: PromotionWorkItemGate | null;
}

export interface PromotionWorkItemGate {
  required: boolean;
  total: number;
  approved: number;
  satisfied: boolean;
  /** When true, resolving all work items auto-approves the promotion (no manual sign-off needed). */
  autoApprove: boolean;
}

export interface PromotionStepProgress {
  name: string;
  satisfied: boolean;
  requirements: PromotionRequirementProgress[];
}

export interface PromotionRequirementProgress {
  name: string;
  required: number;
  approved: number;
  satisfied: boolean;
  // Who can satisfy this requirement — configured groups (id+name) + explicitly listed user emails.
  groups: PromotionPolicyGroupRef[];
  users: string[];
}

export interface PromotionPolicyGroupRef {
  id: string;
  name: string;
}

export interface PromotionPolicyRequirement {
  name: string;
  groups: PromotionPolicyGroupRef[];
  users: string[];
  minApprovers: number;
}

export interface PromotionPolicyStep {
  name: string;
  requirements: PromotionPolicyRequirement[];
}

export interface PromotionPolicy {
  id: string;
  product: string;
  service: string | null;
  sourceEnv: string;
  targetEnv: string;
  steps: PromotionPolicyStep[];
  timeoutHours: number;
  escalationGroup: string | null;
  requireAllWorkItemsApproved: boolean;
  autoApproveOnAllWorkItemsApproved: boolean;
  autoApproveWhenNoWorkItems: boolean;
  createdAt: string;
  updatedAt: string;
}

export interface UpsertPromotionPolicyPayload {
  product: string;
  service: string | null;
  sourceEnv: string;
  targetEnv: string;
  steps: PromotionPolicyStep[];
  timeoutHours: number;
  escalationGroup: string | null;
  requireAllWorkItemsApproved: boolean;
  autoApproveOnAllWorkItemsApproved: boolean;
  autoApproveWhenNoWorkItems: boolean;
}

export interface FeatureFlag {
  key: string;
  enabled: boolean;
  updatedAt: string;
  updatedBy: string;
}

export interface DeploymentVersion {
  id: string;
  service: string;
  version: string;
  deployedAt: string;
  deployerEmail: string | null;
  isRollback: boolean;
}

// ── Rollbacks ───────────────────────────────────────────────────────────────
export type RollbackStatus =
  | 'Pending'
  | 'Approved'
  | 'RollingBack'
  | 'RolledBack'
  | 'Rejected'
  | 'Cancelled';

export type RollbackMode = 'Manual' | 'Align';

export type RollbackItemStatus =
  | 'Pending'
  | 'RollingBack'
  | 'RolledBack'
  | 'Failed'
  | 'Skipped';

export interface RollbackItem {
  id: string;
  service: string;
  fromVersion: string;
  toVersion: string;
  status: RollbackItemStatus;
  completedDeployEventId: string | null;
  externalRunUrl: string | null;
  completedAt: string | null;
}

export interface RollbackRequest {
  id: string;
  product: string;
  targetEnv: string;
  status: RollbackStatus;
  mode: RollbackMode;
  referenceEnv: string | null;
  exclusions: string[];
  reason: string | null;
  createdBy: string;
  createdByName: string;
  createdAt: string;
  approvedAt: string | null;
  completedAt: string | null;
  canApprove: boolean;
  items: RollbackItem[];
}

export interface RollbackApprovalEntry {
  approverEmail: string;
  approverName: string;
  decision: 'Approved' | 'Rejected';
  comment: string | null;
  createdAt: string;
}

/** Body shared by previewRollback / createRollback. */
export interface RollbackInput {
  product: string;
  targetEnv: string;
  mode: RollbackMode;
  referenceEnv?: string;
  exclude?: string[];
  items?: { service: string; toVersion: string }[];
  reason?: string;
}

export interface ResolvedItem {
  service: string;
  fromVersion: string;
  toVersion: string;
  eligible: boolean;
  skipReason: string | null;
}

export interface RollbackPreview {
  product: string;
  targetEnv: string;
  mode: RollbackMode;
  referenceEnv: string | null;
  items: ResolvedItem[];
}

export const api = new ApiClient();
