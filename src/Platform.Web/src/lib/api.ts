import { acquireToken, isMsalEnabled } from './auth';
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

export const api = new ApiClient();
