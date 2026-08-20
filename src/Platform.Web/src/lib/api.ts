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

  // Build registry
  /**
   * Registered builds, newest first. `branch` is a substring match, so "MPT-1234" finds the
   * feature branch without spelling out the full ref; `version` is exact, so product + service +
   * version identifies exactly one build.
   */
  listBuilds(params?: { product?: string; service?: string; branch?: string; version?: string; limit?: number }) {
    const entries = Object.entries(params ?? {})
      .filter(([, v]) => v !== undefined && v !== '')
      .map(([k, v]) => [k, String(v)] as [string, string]);
    const query = entries.length ? '?' + new URLSearchParams(entries).toString() : '';
    return this.request<{ results: import('./types').BuildSummary[] }>(`/builds${query}`);
  }

  /**
   * Target environments a registered build can be promoted to for this service — the edges with a
   * resolving `build → *` policy. Empty means the product isn't enrolled in build promotions.
   */
  getBuildTargets(product: string, service: string) {
    const query = new URLSearchParams([['product', product], ['service', service]]).toString();
    return this.request<{ targets: import('./types').BuildTarget[] }>(
      `/promotions/build-targets?${query}`,
    );
  }

  /**
   * "Deploy this build": creates a promotion candidate from a registered build. The server builds
   * the change set from the stored manifest and stamps the caller as triggered-by.
   */
  createPromotionFromBuild(buildId: string, targetEnv: string) {
    return this.request<{ id: string; status: string }>('/promotions/from-build', {
      method: 'POST',
      body: JSON.stringify({ buildId, targetEnv }),
    });
  }

  /**
   * Cross-product service search — find a service without knowing which product it lives in.
   * Case-insensitive substring match on the service name; the same name under two products
   * returns two hits.
   */
  searchDeploymentServices(q: string, limit?: number) {
    const entries: [string, string][] = [['q', q]];
    if (limit) entries.push(['limit', String(limit)]);
    return this.request<{ results: import('./types').ServiceSearchResult[] }>(
      `/deployments/services/search?${new URLSearchParams(entries).toString()}`,
    );
  }

  /**
   * Everything the service detail page shows, in one call: current state per environment, the
   * last distinct versions and which environments each reached, and the service's promotions.
   */
  getServiceDetail(product: string, service: string, params?: { versionsLimit?: number }) {
    const query = params?.versionsLimit ? `?versionsLimit=${params.versionsLimit}` : '';
    return this.request<import('./types').ServiceDetail>(
      `/deployments/services/${encodeURIComponent(product)}/${encodeURIComponent(service)}${query}`,
    );
  }

  getDeploymentHistory(product: string, service: string, params?: { environment?: string; limit?: number }) {
    const entries: [string, string][] = [];
    if (params?.environment) entries.push(['environment', params.environment]);
    if (params?.limit) entries.push(['limit', String(params.limit)]);
    const query = entries.length ? '?' + new URLSearchParams(entries).toString() : '';
    return this.request<import('./types').DeployEvent[]>(`/deployments/history/${product}/${service}${query}`);
  }

  /**
   * Everything the deployment detail page shows, in one call: the event with its CI run, the log
   * blocks it captured (as summaries — text comes from {@link getDeploymentLog}), the neighbouring
   * deployments of the same service, and the promotions and work items it connects to.
   */
  getDeploymentEvent(id: string, params?: { historyLimit?: number }) {
    const query = params?.historyLimit ? `?historyLimit=${params.historyLimit}` : '';
    return this.request<import('./types').DeploymentDetail>(`/deployments/events/${id}${query}`);
  }

  /** One log block's text. Separate from the detail call because a Helm printout is large. */
  getDeploymentLog(eventId: string, logId: string) {
    return this.request<import('./types').DeployLogContent>(`/deployments/events/${eventId}/logs/${logId}`);
  }

  getRecentDeployments(product: string, environment: string, since?: string) {
    const query = since ? '?since=' + since : '';
    return this.request<import('./types').DeployEvent[]>(`/deployments/recent/${product}/${environment}${query}`);
  }

  getRecentProductDeployments(product: string, since?: string) {
    const query = since ? '?since=' + since : '';
    return this.request<import('./types').DeployEvent[]>(`/deployments/recent/${product}${query}`);
  }

  /**
   * Manual deployment entry: create a new deploy based on the latest one for
   * (product, service, environment), changing version/status. Server stamps Source="manual"
   * and triggered-by = the caller. `note` is required. Admin-only for human callers.
   */
  createManualDeploy(body: {
    product: string;
    service: string;
    environment: string;
    version: string;
    note: string;
    status?: string;
  }) {
    return this.request<{ id: string; version: string; previousVersion: string | null; status: string; source: string }>(
      '/deployments/manual',
      { method: 'POST', body: JSON.stringify(body) },
    );
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

  /**
   * Deploy-event log retention: captured pipeline output for deploys older than the cutoff. The
   * events themselves are never touched — only their stored log blocks.
   */
  getDeploymentLogRetentionPreview(olderThanDays: number) {
    return this.request<{ logs: number; bytes: number }>(
      `/deployments/admin/logs?olderThanDays=${olderThanDays}`,
    );
  }

  removeOldDeploymentLogs(olderThanDays: number) {
    return this.request<{ logs: number; bytes: number }>(
      `/deployments/admin/logs?olderThanDays=${olderThanDays}`,
      { method: 'DELETE' },
    );
  }

  /**
   * Retired services — the admin soft delete. A retired `(product, service)` disappears from the
   * deployment matrix, from promotions and from the work-item queue; nothing is erased, and a new
   * deployment for the service un-retires it on its own. This endpoint is the only way to see the
   * retired ones, which is why the restore UI calls it directly rather than deriving the list.
   */
  listDeletedServices(product?: string) {
    const query = product ? `?product=${encodeURIComponent(product)}` : '';
    return this.request<DeletedService[]>(`/deployments/admin/deleted-services${query}`);
  }

  deleteService(body: { product: string; service: string; reason?: string }) {
    return this.request<DeleteServiceResult>('/deployments/admin/deleted-services', {
      method: 'POST',
      body: JSON.stringify(body),
    });
  }

  restoreService(product: string, service: string) {
    return this.request<void>(
      `/deployments/admin/deleted-services?product=${encodeURIComponent(product)}&serviceName=${encodeURIComponent(service)}`,
      { method: 'DELETE' },
    );
  }

  /**
   * Service→product overrides. Product arrives as free text on every deploy, build and external
   * promotion, and a pipeline mid-migration keeps sending the product it was configured with years
   * ago; a row here decides where the service's entities actually land, whatever was posted.
   * `fromProduct` narrows a row to one sending product — omit it for a catch-all.
   */
  listServiceProductOverrides() {
    return this.request<ServiceProductOverride[]>('/deployments/admin/product-overrides');
  }

  saveServiceProductOverride(body: {
    service: string;
    product: string;
    fromProduct?: string | null;
    reason?: string | null;
  }) {
    return this.request<ServiceProductOverride>('/deployments/admin/product-overrides', {
      method: 'POST',
      body: JSON.stringify(body),
    });
  }

  deleteServiceProductOverride(id: string) {
    return this.request<void>(`/deployments/admin/product-overrides/${encodeURIComponent(id)}`, {
      method: 'DELETE',
    });
  }

  /**
   * Overrides only affect what arrives next. These two move the history that was stored before the
   * override existed: GET counts what would move, POST moves it. Same payload from both, so the
   * confirmation the admin approved is the report they get back.
   */
  previewServiceProductRemap(id: string) {
    return this.request<ServiceProductRemap>(
      `/deployments/admin/product-overrides/${encodeURIComponent(id)}/remap`,
    );
  }

  applyServiceProductRemap(id: string) {
    return this.request<ServiceProductRemap>(
      `/deployments/admin/product-overrides/${encodeURIComponent(id)}/remap`,
      { method: 'POST' },
    );
  }

  /**
   * Webhook delivery maintenance: `failed` is the whole failed set (bulk-retryable), `purgeable`
   * counts settled rows (delivered or failed) older than the cutoff. Pending rows are never counted
   * or purged — they are still owed to a receiver.
   */
  getWebhookDeliveryMaintenanceStats(olderThanDays: number) {
    return this.request<{ failed: number; purgeable: number; oldestFailedAt: string | null }>(
      `/webhooks/maintenance/deliveries?olderThanDays=${olderThanDays}`,
    );
  }

  retryFailedWebhookDeliveries() {
    return this.request<{ retried: number }>('/webhooks/maintenance/deliveries/retry-failed', {
      method: 'POST',
    });
  }

  purgeWebhookDeliveries(olderThanDays: number) {
    return this.request<{ removed: number }>(
      `/webhooks/maintenance/deliveries?olderThanDays=${olderThanDays}`,
      { method: 'DELETE' },
    );
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

  /**
   * `secret` is required for the azure_devops and github targets — those reuse a credential the
   * receiving system already holds. Generic targets ignore it and mint their own, returned once.
   * The msteams and discord targets reject it outright: their URL is the credential. Those two take
   * `messageTemplate` / `messageTitle` instead, and fall back to per-event defaults without them.
   */
  createWebhook(data: { name: string; url: string; events: string[]; filters?: { product?: string; environment?: string }; targetType?: string; secret?: string; signatureHeader?: string; gitHubEventType?: string; messageTemplate?: string; messageTitle?: string }) {
    return this.request<import('./types').WebhookSubscription>('/webhooks', {
      method: 'POST',
      body: JSON.stringify(data),
    });
  }

  /** `secret` rotates the stored credential; omit it to keep the current one. */
  updateWebhook(id: string, data: { name?: string; url?: string; events?: string[]; filters?: { product?: string; environment?: string }; active?: boolean; secret?: string; signatureHeader?: string; gitHubEventType?: string; messageTemplate?: string; messageTitle?: string }) {
    return this.request<import('./types').WebhookSubscription>(`/webhooks/${id}`, {
      method: 'PUT',
      body: JSON.stringify(data),
    });
  }

  /**
   * Renders a notification template against a sample payload for the given event. Nothing is stored
   * and nothing is posted, so this is safe to call while the operator types.
   */
  previewNotificationMessage(data: {
    targetType: string;
    eventType: string;
    messageTemplate?: string;
    messageTitle?: string;
    url?: string;
  }) {
    return this.request<{
      eventType: string;
      targetType: string;
      title: string;
      text: string;
      samplePayload: string;
      requestBody: string;
      /** Not always JSON — the Teams HTML target posts an HTML fragment. */
      contentType: string;
    }>('/webhooks/preview-message', {
      method: 'POST',
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
      bypass: { byName: string; at: string; reason: string | null } | null;
      /** Whether the current user may undo this promotion's approval right now. */
      canCancelApproval: boolean;
    }>(`/promotions/${id}`);
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

  /**
   * Assign / reassign / clear a participant on a specific work-item reference of a promotion
   * candidate. This is what the work-items queue uses (candidates are self-contained — there is no
   * deploy event to override). Pass `assignee: null` to clear the role on that reference.
   */
  assignPromotionReferenceParticipant(
    candidateId: string,
    referenceKey: string,
    role: string,
    assignee: { email: string; displayName: string } | null,
  ) {
    return this.request<{ participants: PromotionSourceEventParticipant[] }>(
      `/promotions/${candidateId}/references/${encodeURIComponent(referenceKey)}/participants`,
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

  /**
   * Undo an approval: an Approved promotion that hasn't been dispatched goes back to Pending and its
   * recorded sign-offs are cleared. `approvedWebhookStopped` reports whether the promotion.approved
   * webhook was caught inside its 10s hold — i.e. whether downstream ever heard about the approval.
   */
  cancelPromotionApproval(id: string, comment?: string) {
    return this.request<{
      candidate: PromotionCandidate;
      clearedApprovals: number;
      approvedWebhookStopped: boolean;
    }>(`/promotions/${id}/cancel-approval`, {
      method: 'POST',
      body: JSON.stringify({ comment }),
    });
  }

  /**
   * Admin escape hatch: force a Pending promotion to Approved without satisfying its gate.
   * Requires a reason. Hits the admin-only endpoint (CatalogAdmin); the backend still fires the
   * existing promotion.approved webhook so downstream automation is unchanged.
   */
  bypassPromotion(id: string, reason: string) {
    return this.request<{ id: string; status: string }>(
      `/promotions/admin/candidates/${id}/bypass`,
      { method: 'POST', body: JSON.stringify({ reason }) },
    );
  }

  /**
   * Settles open promotions that deploy history has already decided: closes the ones whose version
   * shipped, supersedes the ones a newer version overtook, leaves everything ambiguous alone.
   * Admin-only. `dryRun` reports what would happen without writing — the UI always runs it first so
   * the admin applies a reviewed list, never a surprise.
   */
  reconcilePromotionCompletions(dryRun: boolean) {
    return this.request<PromotionReconcileResult>(
      `/promotions/admin/candidates/reconcile-completions`,
      { method: 'POST', body: JSON.stringify({ dryRun }) },
    );
  }

  /**
   * Duplicate promotion candidates — residue of the pre-fix create path that minted a new row per
   * external POST instead of reusing the natural key. Same scan/remove contract as the deploy-event
   * duplicates pair; the backend excludes legitimate re-promote history from what it calls a duplicate.
   */
  getPromotionDuplicatesPreview() {
    return this.request<{ groups: number; rows: number }>('/promotions/admin/duplicates');
  }

  removePromotionDuplicates() {
    return this.request<{ groups: number; rows: number }>('/promotions/admin/duplicates', {
      method: 'DELETE',
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

  /**
   * Flag a problem on the work item. The promotion stays Pending and the same user can call
   * `approveWorkItem` later to release the item.
   */
  raiseWorkItemIssue(key: string, product: string, targetEnv: string, comment?: string) {
    return this.request<WorkItemApproval>(
      `/work-items/${encodeURIComponent(key)}/issues`,
      { method: 'POST', body: JSON.stringify({ product, targetEnv, comment }) },
    );
  }

  /**
   * Hold the work item back. Says more than `raiseWorkItemIssue` and does the same thing: the
   * promotion stays Pending, nothing is vetoed, and the decision can be changed later. Vetoing is a
   * promotion-level action (`rejectPromotion`), never something done to one work item.
   */
  blockWorkItem(key: string, product: string, targetEnv: string, comment?: string) {
    return this.request<WorkItemApproval>(
      `/work-items/${encodeURIComponent(key)}/blocks`,
      { method: 'POST', body: JSON.stringify({ product, targetEnv, comment }) },
    );
  }

  /**
   * Everything the work-item detail page renders in one call: display fields, assigned people,
   * decision trail, comment thread, and every promotion candidate carrying the ticket. 404s when
   * the platform has never seen the key for that (product, targetEnv).
   */
  getWorkItemDetail(key: string, product: string, targetEnv: string) {
    const params = new URLSearchParams({ product, targetEnv });
    return this.request<WorkItemDetail>(
      `/work-items/${encodeURIComponent(key)}/detail?${params.toString()}`,
    );
  }

  // ── Work-item comments ────────────────────────────────────────────────
  // Threads key on (key, product, targetEnv) — the same grain as the decisions — so they survive
  // a superseded candidate. Edit/delete address the comment by id alone.

  listWorkItemComments(key: string, product: string, targetEnv: string) {
    const params = new URLSearchParams({ product, targetEnv });
    return this.request<{ comments: WorkItemComment[] }>(
      `/work-items/${encodeURIComponent(key)}/comments?${params.toString()}`,
    );
  }

  addWorkItemComment(key: string, product: string, targetEnv: string, body: string) {
    return this.request<WorkItemComment>(
      `/work-items/${encodeURIComponent(key)}/comments`,
      { method: 'POST', body: JSON.stringify({ product, targetEnv, body }) },
    );
  }

  updateWorkItemComment(commentId: string, body: string) {
    return this.request<WorkItemComment>(`/work-items/comments/${commentId}`, {
      method: 'PATCH',
      body: JSON.stringify({ body }),
    });
  }

  deleteWorkItemComment(commentId: string) {
    return this.request<void>(`/work-items/comments/${commentId}`, { method: 'DELETE' });
  }

  // The current user's pending work items across all (product, targetEnv) pairs.
  // Powers the /me/work-items queue page.
  //
  // Optional `assignee` narrows the list (display only — server-side authorisation is
  // unchanged):
  //   - null                 → full authorized list (no narrowing).
  //   - assignee=email       → that email holds a role on the work item (any role, or the
  //                            policy-required roles when roleRequirement=assigned — the queue's
  //                            person filter).
  //   - assignee=unassigned  → nobody in any role that counts as an assignment.
  // Response also carries the (email, required-role) → count rollup, so the page can populate
  // its person dropdown in the same call.
  getMyPendingWorkItems(args?: {
    assignee?: string;
    /**
     * Status mode — "pending" (default, the inbox awaiting decision) or "decided"
     * (combined approved + rejected history). On "decided" `assignee` narrows by the decider
     * (WorkItemApproval.ApproverEmail) rather than by work-item participant; pass `since` to
     * narrow the time window (omit for all time).
     */
    status?: 'pending' | 'decided';
    /** ISO timestamp lower bound on the decision time. Only used when status === 'decided'. */
    since?: string;
    /**
     * Narrows by the promotion policy's work-item role requirement — the roles that make somebody
     * answerable for a work item:
     *   - `assigned` → `assignee` must hold a role the item's own policy REQUIRES. Items whose policy
     *                  requires no role never match. This is what the "Assigned to me" tab and the
     *                  queue's person filter mean.
     *   - `missing`  → items where at least one policy-required role has nobody in it ("Not assigned").
     * Ignored on the "decided" view.
     */
    roleRequirement?: 'assigned' | 'missing';
  }) {
    const params = new URLSearchParams();
    const assignee = args?.assignee?.trim();
    const status = args?.status;
    if (assignee) params.set('assignee', assignee);
    if (status && status !== 'pending') params.set('status', status);
    if (args?.since) params.set('since', args.since);
    if (args?.roleRequirement) params.set('roleRequirement', args.roleRequirement);
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
    return this.request<RollbackDetail>(`/rollbacks/${id}`);
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

  // Admin-only: force a pending rollback past its approval gate. The reason is mandatory and is
  // recorded on the resulting approval row, which is flagged as an override.
  overrideRollbackApproval(id: string, reason: string) {
    return this.request<RollbackRequest>(`/rollbacks/${id}/override-approval`, {
      method: 'POST',
      body: JSON.stringify({ reason }),
    });
  }

  // Products the caller may actually raise a rollback for (their hidden products removed).
  getRollbackEnabledProducts() {
    return this.request<{ products: string[] }>(`/rollbacks/enabled-products`);
  }

  // Whether the caller may create a rollback for this (product, env), and the server's reason if not.
  canCreateRollback(product: string, targetEnv: string) {
    const qs = new URLSearchParams({ product, targetEnv }).toString();
    return this.request<{ allowed: boolean; reason: string | null }>(`/rollbacks/can-create?${qs}`);
  }

  // ── Rollback policy admin ──────────────────────────────────────────────
  // A policy says who may create rollbacks for a product and who must approve them. Its existence is
  // also the product's rollback enrollment — there is no separate enabled-products list.

  listRollbackPolicies() {
    return this.request<{ policies: RollbackPolicy[] }>(`/rollbacks/admin/policies`);
  }

  upsertRollbackPolicy(policy: UpsertRollbackPolicyPayload, id?: string) {
    return id
      ? this.request<RollbackPolicy>(`/rollbacks/admin/policies/${id}`, {
          method: 'PUT',
          body: JSON.stringify(policy),
        })
      : this.request<RollbackPolicy>(`/rollbacks/admin/policies`, {
          method: 'POST',
          body: JSON.stringify(policy),
        });
  }

  deleteRollbackPolicy(id: string) {
    return this.request<void>(`/rollbacks/admin/policies/${id}`, { method: 'DELETE' });
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

  // ── The signed-in user's own preferences ───────────────────────────────────

  /**
   * Vocabulary for the promotions list filters. Unfiltered by design — dropdown options built from
   * a filtered result set collapse to whatever is already selected.
   */
  getPromotionFilterOptions() {
    return this.request<{ products: string[]; targetEnvs: string[] }>(`/promotions/filter-options`);
  }

  /**
   * The promotions activity feed — one request backs the whole audit page: the rows, the counts its
   * tabs are badged with, and the actors its dropdown offers.
   *
   * `from`/`to` are absolute instants. A calendar day is the reader's, not the server's, so the page
   * resolves its own midnight before calling (see `promotionAuditFilterParams`).
   */
  getPromotionAudit(params: {
    from?: string;
    to?: string;
    category?: string;
    action?: string;
    actor?: string;
    product?: string;
    service?: string;
    targetEnv?: string;
    page?: number;
    pageSize?: number;
  }) {
    const query = new URLSearchParams();
    for (const [key, value] of Object.entries(params)) {
      if (value === undefined || value === '') continue;
      query.set(key, String(value));
    }
    const suffix = query.toString();
    return this.request<PromotionAuditResponse>(`/promotions/audit${suffix ? `?${suffix}` : ''}`);
  }

  getMyPreferences() {
    return this.request<UserPreferencesPayload>(`/me/preferences`);
  }

  /**
   * Everything the hidden-products control needs. `products` is deliberately the UNFILTERED list —
   * every other product-bearing endpoint already has the hidden set applied, so this is the only
   * call that can still see a hidden product and therefore the only one that can offer to unhide it.
   */
  getMyProductVisibility() {
    return this.request<{ products: string[]; hiddenProducts: string[] }>(`/me/preferences/products`);
  }

  setMyHiddenProducts(products: string[]) {
    return this.request<UserPreferencesPayload>(`/me/preferences/hidden-products`, {
      method: 'PUT',
      body: JSON.stringify({ products }),
    });
  }

  // ── Analytics ──────────────────────────────────────────────────────────

  getDeploymentFrequency(params?: {
    product?: string;
    serviceName?: string;
    environment?: string;
    from?: string;
    to?: string;
    bucket?: 'day' | 'week';
    groupBy?: 'none' | 'service' | 'environment' | 'product';
    tz?: string;
    includeRollbacks?: boolean;
    includeRedeploys?: boolean;
    summaryOnly?: boolean;
  }) {
    const entries: [string, string][] = [];
    if (params?.product) entries.push(['product', params.product]);
    if (params?.serviceName) entries.push(['serviceName', params.serviceName]);
    if (params?.environment) entries.push(['environment', params.environment]);
    if (params?.from) entries.push(['from', params.from]);
    if (params?.to) entries.push(['to', params.to]);
    if (params?.bucket) entries.push(['bucket', params.bucket]);
    if (params?.groupBy) entries.push(['groupBy', params.groupBy]);
    if (params?.tz) entries.push(['tz', params.tz]);
    if (params?.includeRollbacks) entries.push(['includeRollbacks', 'true']);
    if (params?.includeRedeploys) entries.push(['includeRedeploys', 'true']);
    if (params?.summaryOnly) entries.push(['summaryOnly', 'true']);
    const query = entries.length ? '?' + new URLSearchParams(entries).toString() : '';
    return this.request<FrequencyResponse>(`/analytics/deployments/frequency${query}`);
  }

  getWorkItemMatrix(params: {
    product: string;
    environment?: string;
    reachedEnv?: string;
    from?: string;
    to?: string;
    limit?: number;
    offset?: number;
  }) {
    const entries: [string, string][] = [['product', params.product]];
    if (params.environment) entries.push(['environment', params.environment]);
    if (params.reachedEnv) entries.push(['reachedEnv', params.reachedEnv]);
    if (params.from) entries.push(['from', params.from]);
    if (params.to) entries.push(['to', params.to]);
    if (params.limit) entries.push(['limit', String(params.limit)]);
    if (params.offset) entries.push(['offset', String(params.offset)]);
    return this.request<WorkItemMatrixResponse>(
      `/analytics/work-items/matrix?` + new URLSearchParams(entries).toString());
  }

  getPromotionQueueStats(params?: { product?: string; from?: string; to?: string }) {
    const entries: [string, string][] = [];
    if (params?.product) entries.push(['product', params.product]);
    if (params?.from) entries.push(['from', params.from]);
    if (params?.to) entries.push(['to', params.to]);
    const query = entries.length ? '?' + new URLSearchParams(entries).toString() : '';
    return this.request<PromotionQueueResponse>(`/analytics/promotions/queue${query}`);
  }

  getLeadTime(params?: {
    product?: string;
    serviceName?: string;
    environment?: string;
    from?: string;
    to?: string;
    bucket?: 'day' | 'week';
    tz?: string;
  }) {
    const entries: [string, string][] = [];
    if (params?.product) entries.push(['product', params.product]);
    if (params?.serviceName) entries.push(['serviceName', params.serviceName]);
    if (params?.environment) entries.push(['environment', params.environment]);
    if (params?.from) entries.push(['from', params.from]);
    if (params?.to) entries.push(['to', params.to]);
    if (params?.bucket) entries.push(['bucket', params.bucket]);
    if (params?.tz) entries.push(['tz', params.tz]);
    const query = entries.length ? '?' + new URLSearchParams(entries).toString() : '';
    return this.request<LeadTimeResponse>(`/analytics/lead-time${query}`);
  }
}

export interface UserPreferencesPayload {
  hiddenProducts: string[];
}

export interface AppSettingsPayload {
  /** `color` is `#rrggbb` or null/absent — the server normalises and drops unparseable values. */
  environments: { key: string; displayName: string; color?: string | null; isProduction?: boolean }[];
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
  /**
   * Display/traceability revisions: the SHA the target env currently runs and the SHA being
   * promoted. Supplied by the external creator; either may be null. Together with the candidate's
   * `repository` reference they let the UI link to the provider's commit-diff view.
   */
  fromRevision?: string | null;
  toRevision?: string | null;
  /** Version currently deployed in `targetEnv` (what this promotion would replace). Null for first deploy. */
  targetCurrentVersion: string | null;
  /**
   * Git ref this version was built from (`refs/heads/…`). Only candidates whose source is the
   * synthetic `build` env carry one — everywhere else the source environment is the provenance.
   * Absent on responses from an older API.
   */
  sourceBranch?: string | null;
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
  /**
   * Whether this candidate's edge creates work items at all. False for dev-only edges (e.g. dev → test)
   * whose policy opts out: the change set still lists its work-item references, but there is nothing to
   * sign off, no queue entry, and no required roles. Absent on responses from an older API — treat
   * `undefined` as true.
   */
  tracksWorkItems?: boolean;
  /**
   * Participant roles the resolved promotion policy requires somebody in on every work item of this
   * candidate (canonical keys, e.g. `qa-owner`). Empty when the policy asks for none — the common case.
   */
  requiredWorkItemRoles?: string[];
  /**
   * The candidate's work items that are missing somebody in at least one required role. Derived
   * server-side from the candidate's policy snapshot and its current participants, so it stays correct
   * when a work item is attached later, someone is reassigned, or the policy changes.
   */
  workItemRoleGaps?: WorkItemRoleGap[];
}

// ── Promotions audit ──────────────────────────────────────────────────────
/**
 * The kind of action an audit row records, as the server groups them. Categories rather than raw
 * action names because that grouping is domain knowledge the API owns (see
 * `PromotionAuditCategories`): the same fact decides what a tab shows and what a saved link means.
 *
 * `other` is what an action the server's map hasn't heard of falls back to, so a new audit action
 * appears in the feed the day it ships rather than silently vanishing.
 */
export type PromotionAuditCategory =
  | 'approved'
  | 'approval-step'
  | 'rejected'
  | 'cancelled'
  | 'created'
  | 'updated'
  | 'deployed'
  | 'work-item'
  | 'comment'
  | 'people'
  | 'other';

/** Somebody named on a row other than as its actor — see {@link PromotionAuditEntry.approvedBy}. */
export interface PromotionAuditActor {
  id: string;
  name: string;
}

/** One recorded action on one promotion. */
export interface PromotionAuditEntry {
  id: string;
  timestamp: string;
  /** Groups the rows one request wrote — an approval and the gate it opened share one. */
  correlationId: string;
  /** The raw audit action, e.g. `promotion.approved`. */
  action: string;
  category: PromotionAuditCategory;
  actorId: string;
  actorName: string;
  /** `user` or `system`. Gate evaluation, ingest and auto-approval are the system's. */
  actorType: string;

  // The promotion this happened to. Always present: the feed only carries rows it could join.
  candidateId: string;
  product: string;
  service: string;
  sourceEnv: string;
  targetEnv: string;
  version: string;
  /** The promotion's status **now**, not at the time of the action. */
  candidateStatus: PromotionStatus;

  /** What the actor typed, on the actions that take a comment. */
  comment: string | null;
  /** Required on a bypass — why the gate was skipped. */
  reason: string | null;
  /** The ticket a work-item action was about. */
  workItemKey: string | null;
  role: string | null;
  referenceKey: string | null;
  /** How an approval came about, e.g. `gate-evaluator`, `administrator-bypass`. */
  trigger: string | null;
  /**
   * On a gate-opening row: the people whose approvals opened it. The row's own actor is the evaluator
   * that noticed, so this is where "who approved it" actually lives. Null for an auto-approval, which
   * nobody decided.
   */
  approvedBy: PromotionAuditActor[] | null;
  /** The action's whole recorded payload, for the row's details expansion. Shape varies by action. */
  details: Record<string, unknown> | null;
}

export interface PromotionAuditResponse {
  entries: PromotionAuditEntry[];
  /** Rows matching every filter — not the page. */
  total: number;
  page: number;
  pageSize: number;
  range: { from: string | null; to: string | null };
  /**
   * Per-action counts under every filter **except** the action/category one, so a tab badge says what
   * selecting it would show rather than what is already selected.
   */
  actions: { action: string; category: PromotionAuditCategory; count: number }[];
  /** Likewise for actors: counted under every filter except the actor filter itself. */
  actors: { id: string; name: string; type: string; count: number }[];
}

/** One work item that has nobody in a role its promotion policy requires. */
export interface WorkItemRoleGap {
  workItemKey: string;
  title: string | null;
  /** Canonical role keys nobody holds on this work item. Always non-empty. */
  missingRoles: string[];
}

/**
 * A work-item sign-off outcome. Neither `Issue` nor `Blocked` touches the promotion — both leave the
 * item unresolved, which stalls the gate without terminating the candidate, and both are reversible.
 * They differ only in what the reviewer is saying: "something's wrong here" versus "this isn't going
 * out". Vetoing belongs to the promotion, not to a single work item.
 */
export type WorkItemDecision = 'Approved' | 'Issue' | 'Blocked';

export interface WorkItemApproval {
  id: string;
  workItemKey: string;
  product: string;
  targetEnv: string;
  approverEmail: string;
  approverName: string;
  decision: WorkItemDecision;
  comment: string | null;
  createdAt: string;
  /** Set when the approver later changed their decision; the row is updated in place. */
  updatedAt?: string | null;
}

export interface WorkItemContext {
  workItemKey: string;
  product: string;
  targetEnv: string;
  /** Null when no live promotion carries the item — orphaned, but still signable. */
  pendingCandidateId: string | null;
  /** Whether the current user may record — or change — a decision right now. */
  canApprove: boolean;
  blockedReason: string | null;
  /** The current user's own decision, if they already made one. */
  myDecision?: WorkItemDecision | null;
  approvals: WorkItemApproval[];
}

/** One entry in a work item's thread. Keyed by (workItemKey, product, targetEnv). */
export interface WorkItemComment {
  id: string;
  workItemKey: string;
  product: string;
  targetEnv: string;
  authorEmail: string;
  authorName: string;
  body: string;
  /**
   * Set when this entry records a sign-off rather than free-text discussion (written automatically
   * on Approve / Block / Reject). Those entries — and anything authored by `system` — are immutable,
   * so the UI shows no edit/delete on them.
   */
  decision?: WorkItemDecision | null;
  createdAt: string;
  updatedAt: string | null;
}

/** One promotion candidate carrying a work item, as listed on the detail page. */
export interface WorkItemCandidateRef {
  id: string;
  service: string;
  version: string;
  sourceEnv: string;
  targetEnv: string;
  status: PromotionStatus;
  createdAt: string;
  /** The candidate participant assignments write to (newest Pending, else newest overall). */
  isPrimary: boolean;
}

/**
 * One environment a work item's change is deployed to — resolved server-side from the deploy events
 * that shipped the carrying version, so it answers "where can I test this?" rather than "where is
 * this promotion headed?". `deployedAt` is the most recent succeeded deploy of that version there.
 */
export interface WorkItemEnvironment {
  environment: string;
  service: string;
  version: string;
  deployedAt: string;
}

/** Full response shape for `GET /api/work-items/{key}/detail`. */
export interface WorkItemDetail {
  workItemKey: string;
  product: string;
  /**
   * The promotion edge this sign-off gates. Identity (it keys the decisions and comments), not a
   * property of the work item to present — `environments` is what says where the change is live.
   */
  targetEnv: string;
  /** Environments the change is deployed to, newest deploy first. */
  environments: WorkItemEnvironment[];
  title: string | null;
  /**
   * Secondary display line: the tracker's own summary (e.g. the Jira ticket title) when `title`
   * carries the commit subject. Null when the producer sent a single name.
   */
  subTitle: string | null;
  /**
   * The work item's body, copied verbatim from the source system — a Jira description, a PR
   * description, a commit message body. Where `title` is the one-line summary, this is the prose
   * under it. Null when the producer sent none; the server blanks-to-null, so a non-null value
   * always has something in it.
   */
  content: string | null;
  url: string | null;
  provider: string | null;
  pendingCandidateId: string | null;
  /** Target for participant writes — null only if the ticket has no candidates at all. */
  primaryCandidateId: string | null;
  canApprove: boolean;
  /** Whether the caller may assign / remove people (QA or Admin). */
  canManage: boolean;
  blockedReason: string | null;
  myDecision: WorkItemDecision | null;
  participants: PromotionSourceEventParticipant[];
  /** Roles the carrying promotion's policy requires somebody in on this work item. */
  requiredRoles?: string[];
  /** The subset of `requiredRoles` nobody holds — non-empty means the work item is incomplete. */
  missingRoles?: string[];
  approvals: WorkItemApproval[];
  comments: WorkItemComment[];
  /** Commits whose messages referenced this work item. Empty when the producer declared none. */
  commits: WorkItemCommitRef[];
  /** Pull requests those commits merged — derived server-side via commit hash → PR revision. */
  pullRequests: WorkItemPullRequestRef[];
  candidates: WorkItemCandidateRef[];
}

/**
 * One commit that carried a work item. `hash` is always present (it's what the producer declared);
 * the rest is hydrated from the matching `commit` reference and is null when the payload had none —
 * such a row renders as a bare hash with no link.
 */
export interface WorkItemCommitRef {
  hash: string;
  title: string | null;
  url: string | null;
  provider: string | null;
  participants: PromotionSourceEventParticipant[];
}

/** One pull request behind a work item, reached through the commit it merged. */
export interface WorkItemPullRequestRef {
  key: string;
  title: string | null;
  url: string | null;
  provider: string | null;
  /** The merge commit that tied this PR to the work item. */
  revision: string | null;
  participants: PromotionSourceEventParticipant[];
}

/** One row from `GET /api/work-items/me/pending`. */
export interface PendingTicket {
  workItemKey: string;
  product: string;
  /** The promotion edge the sign-off gates — identity, not display. See `WorkItemDetail.targetEnv`. */
  targetEnv: string;
  provider: string | null;
  url: string | null;
  title: string | null;
  /** Secondary display line — see `WorkItemDetail.subTitle`. */
  subTitle?: string | null;
  candidateId: string;
  service: string;
  version: string;
  /** Environments this version is deployed to, newest deploy first — where the item can be tested. */
  environments: WorkItemEnvironment[];
  blockingPromotions: number;
  /** Participants on this specific work-item reference (overrides applied). */
  participants: PromotionSourceEventParticipant[];
  /** Roles the carrying promotion's policy requires somebody in on this work item. */
  requiredRoles?: string[];
  /** The subset of `requiredRoles` nobody holds — non-empty means the work item needs attention. */
  missingRoles?: string[];
  /**
   * Status of the candidate this row represents. "Pending" for a live promotion; anything else means
   * the row is either decision history or an orphan — a work item whose promotion died (Superseded /
   * Rejected) and which nobody has signed off, kept in the queue so the work isn't lost.
   * "Unknown" when no candidate could be linked to the row.
   */
  candidateStatus?: string;
  /**
   * The decision recorded on this ticket — null on the pending inbox; populated on the
   * "decided" view. Decisions can come from any approver in the candidate's authorised group.
   */
  decision?: WorkItemDecision | null;
  decidedAt?: string | null;
  decidedByEmail?: string | null;
  decidedByName?: string | null;
  decisionComment?: string | null;
}

/**
 * One row of the (email, role) assignee summary returned alongside the queue. Counts come from
 * the user's authorized list <i>before</i> the person filter is applied, so the queue page can
 * render every choice the user could narrow to. On the pending path the role is always one the
 * item's policy requires — the only assignments the person filter matches. Aggregated
 * server-side per (email, role) pair — a single person on multiple roles produces multiple rows.
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
  /**
   * (email, required-role) rollup of the unfiltered authorized list — the person dropdown's
   * contents. Sorted by count desc, displayName asc. On the "decided" view this is the decider
   * rollup instead (role is empty there).
   */
  assignees: PendingAssignee[];
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
   * Secondary display line under `title`. Set on `work-item` references when the title carries the
   * commit subject and the tracker has its own summary (e.g. the Jira ticket title). Absent when
   * the producer has only one name for the thing.
   */
  subTitle?: string | null;
  /**
   * Commit hashes this reference was derived from — set by the producer on `work-item` references
   * to record which commit messages mentioned the ticket. The server uses it to resolve the
   * ticket's `commits` and `pullRequests` on the detail projection.
   */
  commits?: string[] | null;
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
  /**
   * True when the policy holds human approval back until every work item is signed off. Together
   * with `satisfied === false` this is exactly what makes the approve call fail, so it is what the
   * detail page disables its Approve button on. Distinct from `autoApprove`, which never blocks.
   */
  required: boolean;
  total: number;
  approved: number;
  /** Work items carrying an Issue — counted apart from `approved` so a shortfall is explained. */
  issues?: number;
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

/**
 * Result of the admin reconcile pass over open promotions. `examined − closed − superseded` is the
 * count left open on purpose — promotions history says nothing about — and surfacing that gap is as
 * much the point of the report as the repairs are.
 */
export interface PromotionReconcileResult {
  examined: number;
  closed: number;
  superseded: number;
  leftOpen: number;
  dryRun: boolean;
  candidates: PromotionReconcileCandidate[];
}

export interface PromotionReconcileCandidate {
  id: string;
  product: string;
  service: string;
  sourceEnv: string;
  targetEnv: string;
  version: string;
  previousStatus: string;
  /** "closed" (its version shipped) or "superseded" (a newer version overtook it). */
  action: 'closed' | 'superseded';
  /** When the deciding deploy landed. */
  at: string;
  /** For a supersede, the newer version now in the target. Null for a close. */
  landedVersion: string | null;
}

/** A service an admin retired. See {@link ApiClient.listDeletedServices}. */
export interface DeletedService {
  id: string;
  product: string;
  service: string;
  deletedAt: string;
  deletedByName: string;
  reason: string | null;
}

/**
 * What a retirement took out of view. The counts are reported so the admin can see the size of what
 * they hid — `hiddenOpenPromotions` especially, since a promotion somebody was waiting to approve
 * disappearing is the one surprising consequence of retiring a service.
 */
export interface DeleteServiceResult {
  service: DeletedService;
  hiddenDeployments: number;
  hiddenOpenPromotions: number;
}

/**
 * One configured service→product mapping. `fromProduct` null means the row applies whatever product
 * the sender posted; a row naming a specific `fromProduct` wins over the catch-all for that sender.
 *
 * `storedEntities` / `strandedEntities` are the at-a-glance health of the mapping: how many deploy
 * events and builds for this service already sit under `product`, and how many are still filed
 * somewhere else. A row stuck at `storedEntities: 0` is usually spelling the service differently
 * than the pipeline does.
 */
export interface ServiceProductOverride {
  id: string;
  service: string;
  fromProduct: string | null;
  product: string;
  reason: string | null;
  createdAt: string;
  updatedAt: string | null;
  updatedByName: string;
  storedEntities: number;
  strandedEntities: number;
}

/**
 * What remapping an override's history involves. Identical shape from the preview and the apply —
 * `applied` is what tells them apart.
 *
 * `buildConflicts` are builds left where they are because the target product already has that
 * service+version. `strandedTicketApprovals` are recorded ticket approvals that do NOT move: they
 * key on (ticket, product, target env) with no service, so a ticket spanning two services can't be
 * attributed to one. Together with `openPromotions` that is the reason to prefer remapping when
 * nothing is in flight.
 */
export interface ServiceProductRemap {
  overrideId: string;
  service: string;
  product: string;
  fromProducts: string[];
  applied: boolean;
  deployments: number;
  deployWorkItems: number;
  builds: number;
  buildConflicts: number;
  promotions: number;
  openPromotions: number;
  promotionWorkItems: number;
  retirements: number;
  retirementMerges: number;
  strandedTicketApprovals: number;
}

export interface PromotionPolicy {
  id: string;
  product: string;
  service: string | null;
  sourceEnv: string;
  targetEnv: string;
  steps: PromotionPolicyStep[];
  /**
   * Whether promotions on this edge create work items. False for edges whose target isn't ready for QA
   * (a dev integration environment, a CI test ring): no work items are created, so every other
   * work-item setting on the policy is inert.
   */
  tracksWorkItems: boolean;
  /**
   * Participant roles every work item on this edge must have somebody in (canonical keys). A work item
   * missing one is flagged as incomplete wherever it renders. Not part of the approval gate.
   */
  requiredWorkItemRoles: string[];
  escalationGroup: string | null;
  requireAllWorkItemsApproved: boolean;
  autoApproveOnAllWorkItemsApproved: boolean;
  autoApproveWhenNoWorkItems: boolean;
  sourceRequiresDeploy: boolean;
  /** Branch patterns (full refs, `*` wildcards) that auto-create candidates from registered builds. */
  autoCreateFromBranches: string[];
  /** Per-edge override (seconds) of the approval → promotion.approved delivery delay; null = default. */
  approvedWebhookDelaySeconds: number | null;
  createdAt: string;
  updatedAt: string;
  // Set only on create/update responses: how many pending promotions were re-gated under the saved
  // settings. Null when reading policies back.
  reappliedCandidates: number | null;
}

export interface UpsertPromotionPolicyPayload {
  product: string;
  service: string | null;
  sourceEnv: string;
  targetEnv: string;
  steps: PromotionPolicyStep[];
  /** Whether promotions on this edge create work items at all. */
  tracksWorkItems: boolean;
  /** Canonical participant roles every work item on this edge must have somebody in. */
  requiredWorkItemRoles: string[];
  escalationGroup: string | null;
  requireAllWorkItemsApproved: boolean;
  autoApproveOnAllWorkItemsApproved: boolean;
  autoApproveWhenNoWorkItems: boolean;
  sourceRequiresDeploy: boolean;
  autoCreateFromBranches: string[];
  approvedWebhookDelaySeconds: number | null;
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
  /** Admin, still pending, and gated — i.e. the override action would succeed. */
  canOverride: boolean;
  /** An admin forced this request past its gate rather than satisfying it. */
  approvalOverridden: boolean;
  items: RollbackItem[];
}

export interface RollbackApprovalEntry {
  approverEmail: string;
  approverName: string;
  decision: 'Approved' | 'Rejected';
  comment: string | null;
  createdAt: string;
  /** True for a gate override; `comment` then holds the mandatory reason. */
  isOverride: boolean;
}

/** One approver requirement plus its progress, as returned on the rollback detail. */
export interface RollbackGateRequirement {
  name: string;
  groups: PromotionPolicyGroupRef[];
  users: string[];
  matched: number;
  required: number;
  satisfied: boolean;
}

/** Detail shape: the list fields plus the gate, its progress, and the decision history. */
export interface RollbackDetail extends RollbackRequest {
  /** No rollback policy governs this environment: only an admin override can move it. */
  unconfigured: boolean;
  gate: RollbackGateRequirement[];
  approvals: RollbackApprovalEntry[];
}

// ── Rollback policies ───────────────────────────────────────────────────────

/** A group ∪ user set. Empty grants nobody — it never means "everyone". */
export interface RollbackPrincipalSet {
  groups: PromotionPolicyGroupRef[];
  users: string[];
}

export interface RollbackPolicy {
  id: string;
  product: string;
  /** null ⇒ the product default, covering every environment without its own row. */
  targetEnv: string | null;
  creators: RollbackPrincipalSet;
  steps: PromotionPolicyStep[];
  escalationGroup: string | null;
  /** False ⇒ only admins can create rollbacks in this scope. */
  hasCreators: boolean;
  /** True ⇒ no approval required in this scope (distinct from having no policy at all). */
  isAutoApprove: boolean;
  createdAt: string;
  updatedAt: string;
  updatedBy: string | null;
}

export interface UpsertRollbackPolicyPayload {
  product: string;
  targetEnv: string | null;
  creators: RollbackPrincipalSet;
  steps: PromotionPolicyStep[];
  escalationGroup: string | null;
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

// ── Analytics ─────────────────────────────────────────────────────────────

export interface AnalyticsRange {
  from: string;
  to: string;
}

export interface FrequencyBucket {
  start: string;
  count: number;
  failed: number;
  rollbacks: number;
}

export interface FrequencySeries {
  key: { product: string | null; serviceName: string | null; environment: string | null };
  buckets: FrequencyBucket[];
  summary: {
    total: number;
    perWeek: number;
    medianIntervalHours: number | null;
    longestGapHours: number | null;
    lastDeployedAt: string | null;
    changeFailureRate: number | null;
    previousPeriodTotal: number;
    batchSizeP50: number | null;
  };
}

export interface FrequencyResponse {
  definition: {
    bucket: string;
    groupBy: string;
    tz: string;
    includeRollbacks: boolean;
    includeRedeploys: boolean;
    changeFailureRate: string;
  };
  range: AnalyticsRange;
  series: FrequencySeries[];
}

export type MatrixCellState =
  | 'deployed'
  | 'approved-awaiting-deploy'
  | 'awaiting-approval'
  | 'absent';

export interface MatrixCell {
  state: MatrixCellState;
  version?: string | null;
  at?: string | null;
  deployEventId?: string | null;
  candidateId?: string | null;
}

export interface MatrixItem {
  key: string;
  title: string | null;
  url: string | null;
  furthestEnv: string | null;
  envs: Record<string, MatrixCell>;
  lastActivityAt: string;
}

export interface WorkItemMatrixResponse {
  environments: string[];
  coverage: { deployments: number; withoutWorkItem: number; ratio: number };
  totals: Record<string, number>;
  totalItems: number;
  items: MatrixItem[];
  range: AnalyticsRange;
}

export interface PromotionQueueEdge {
  product: string;
  targetEnv: string;
  pending: number;
  awaitingDeploy: number;
  oldestPendingHours: number | null;
  oldestAwaitingDeployHours: number | null;
}

export interface LatencyStats {
  n: number;
  p50Hours: number | null;
  p90Hours: number | null;
}

export interface PromotionQueueResponse {
  edges: PromotionQueueEdge[];
  approvalLatency: LatencyStats;
  deployLatency: LatencyStats;
  range: AnalyticsRange;
}

export interface LeadTimeEnvStats {
  environment: string;
  n: number;
  p50Hours: number | null;
  p75Hours: number | null;
  p90Hours: number | null;
}

export interface LeadTimeResponse {
  definition: {
    clockStart: string;
    clockStartFallback: string;
    clockStop: string;
    grain: string;
  };
  coverage: { workItems: number; withClockStart: number; ratio: number };
  byEnvironment: LeadTimeEnvStats[];
  buckets: { start: string; environment: string; n: number; p50Hours: number | null }[];
  slowest: { workItemKey: string; environment: string; hours: number; deployEventId: string }[];
  range: AnalyticsRange;
}

export const api = new ApiClient();
