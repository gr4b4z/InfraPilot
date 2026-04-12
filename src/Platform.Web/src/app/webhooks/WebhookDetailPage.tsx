import { useState, useEffect, useCallback } from 'react';
import { useParams, useNavigate } from 'react-router-dom';
import { api } from '@/lib/api';
import type { WebhookSubscription, WebhookDelivery } from '@/lib/types';
import {
  ArrowLeft,
  Send,
  Trash2,
  ToggleLeft,
  ToggleRight,
  RefreshCw,
  ChevronDown,
  ChevronRight,
  Check,
  X,
  AlertCircle,
  Save,
} from 'lucide-react';
import { formatDistanceToNow, format } from 'date-fns';

const AVAILABLE_EVENTS = [
  'deployment.created',
  'request.status_changed',
  'approval.created',
  'approval.approved',
  'approval.rejected',
  'approval.changesrequested',
  'ping',
];

export function WebhookDetailPage() {
  const { id } = useParams<{ id: string }>();
  const navigate = useNavigate();
  const [webhook, setWebhook] = useState<WebhookSubscription | null>(null);
  const [deliveries, setDeliveries] = useState<WebhookDelivery[]>([]);
  const [deliveryTotal, setDeliveryTotal] = useState(0);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [expandedDelivery, setExpandedDelivery] = useState<string | null>(null);

  // Edit state
  const [editing, setEditing] = useState(false);
  const [editName, setEditName] = useState('');
  const [editUrl, setEditUrl] = useState('');
  const [editEvents, setEditEvents] = useState<string[]>([]);
  const [editFilterProduct, setEditFilterProduct] = useState('');
  const [editFilterEnv, setEditFilterEnv] = useState('');
  const [saving, setSaving] = useState(false);

  const fetchData = useCallback(async () => {
    if (!id) return;
    try {
      const [wh, del] = await Promise.all([
        api.getWebhook(id),
        api.getWebhookDeliveries(id, { limit: 50 }),
      ]);
      setWebhook(wh);
      setDeliveries(del.items);
      setDeliveryTotal(del.total);
    } catch {
      setError('Failed to load webhook');
    } finally {
      setLoading(false);
    }
  }, [id]);

  useEffect(() => {
    fetchData();
  }, [fetchData]);

  const startEditing = () => {
    if (!webhook) return;
    setEditName(webhook.name);
    setEditUrl(webhook.url);
    setEditEvents([...webhook.events]);
    setEditFilterProduct(webhook.filters.product ?? '');
    setEditFilterEnv(webhook.filters.environment ?? '');
    setEditing(true);
  };

  const handleSave = async () => {
    if (!id) return;
    setSaving(true);
    try {
      await api.updateWebhook(id, {
        name: editName,
        url: editUrl,
        events: editEvents,
        filters: { product: editFilterProduct || undefined, environment: editFilterEnv || undefined },
      });
      setEditing(false);
      await fetchData();
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Failed to save');
    } finally {
      setSaving(false);
    }
  };

  const toggleActive = async () => {
    if (!id || !webhook) return;
    try {
      await api.updateWebhook(id, { active: !webhook.active });
      await fetchData();
    } catch {
      setError('Failed to toggle');
    }
  };

  const handleDelete = async () => {
    if (!id) return;
    try {
      await api.deleteWebhook(id);
      navigate('/webhooks');
    } catch {
      setError('Failed to delete');
    }
  };

  const handleTest = async () => {
    if (!id) return;
    try {
      await api.testWebhook(id);
      setTimeout(fetchData, 2000);
    } catch {
      setError('Failed to send test');
    }
  };

  const handleRetry = async (deliveryId: string) => {
    try {
      await api.retryWebhookDelivery(deliveryId);
      setTimeout(fetchData, 1000);
    } catch {
      setError('Failed to retry');
    }
  };

  const toggleEvent = (event: string) => {
    setEditEvents((prev) =>
      prev.includes(event) ? prev.filter((e) => e !== event) : [...prev, event]
    );
  };

  const statusColor = (status: string) => {
    switch (status) {
      case 'delivered': return 'var(--success)';
      case 'failed': return 'var(--error)';
      default: return 'var(--warning)';
    }
  };

  const statusBg = (status: string) => {
    switch (status) {
      case 'delivered': return 'var(--success-bg)';
      case 'failed': return 'var(--error-bg)';
      default: return 'var(--warning-bg)';
    }
  };

  if (loading) {
    return (
      <div className="flex items-center justify-center h-64">
        <div
          className="w-6 h-6 border-2 border-t-transparent rounded-full animate-spin"
          style={{ borderColor: 'var(--accent)', borderTopColor: 'transparent' }}
        />
      </div>
    );
  }

  if (!webhook) {
    return (
      <div className="text-center py-12">
        <p style={{ color: 'var(--text-muted)' }}>Webhook not found</p>
      </div>
    );
  }

  return (
    <div className="space-y-5">
      {/* Header */}
      <div className="flex items-center justify-between">
        <div className="flex items-center gap-3">
          <button
            onClick={() => navigate('/webhooks')}
            className="p-1.5 rounded-lg transition-colors hover:bg-[var(--accent-muted)]"
            style={{ color: 'var(--text-muted)' }}
          >
            <ArrowLeft size={18} />
          </button>
          <div>
            <h1 className="text-xl font-semibold tracking-tight" style={{ color: 'var(--text-primary)' }}>
              {webhook.name}
            </h1>
            <p className="text-[13px] mt-0.5" style={{ color: 'var(--text-muted)' }}>
              {webhook.url}
            </p>
          </div>
        </div>
        <div className="flex items-center gap-2">
          <button
            onClick={handleTest}
            className="inline-flex items-center gap-1.5 text-[13px] font-medium px-3 py-1.5 rounded-lg transition-colors hover:opacity-80"
            style={{ color: 'var(--accent)', backgroundColor: 'var(--accent-muted)' }}
          >
            <Send size={13} /> Test
          </button>
          <button
            onClick={toggleActive}
            className="inline-flex items-center gap-1.5 text-[13px] font-medium px-3 py-1.5 rounded-lg transition-colors hover:opacity-80"
            style={{
              color: webhook.active ? 'var(--success)' : 'var(--text-muted)',
              backgroundColor: webhook.active ? 'var(--success-bg)' : 'var(--bg-primary)',
            }}
          >
            {webhook.active ? <ToggleRight size={15} /> : <ToggleLeft size={15} />}
            {webhook.active ? 'Active' : 'Inactive'}
          </button>
          <button
            onClick={handleDelete}
            className="inline-flex items-center gap-1.5 text-[13px] font-medium px-3 py-1.5 rounded-lg transition-colors hover:opacity-80"
            style={{ color: 'var(--error)' }}
          >
            <Trash2 size={13} /> Delete
          </button>
        </div>
      </div>

      {error && (
        <div
          className="flex items-center gap-2 px-4 py-3 rounded-lg text-[13px]"
          style={{ backgroundColor: 'var(--error-bg)', color: 'var(--error)' }}
        >
          <AlertCircle size={15} />
          {error}
          <button onClick={() => setError(null)} className="ml-auto">
            <X size={14} />
          </button>
        </div>
      )}

      {/* Configuration */}
      <div
        className="rounded-xl border p-5 space-y-4"
        style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-secondary)' }}
      >
        <div className="flex items-center justify-between">
          <h2 className="text-[14px] font-semibold" style={{ color: 'var(--text-primary)' }}>
            Configuration
          </h2>
          {!editing ? (
            <button
              onClick={startEditing}
              className="text-[12px] font-medium px-3 py-1 rounded-lg transition-colors hover:opacity-80"
              style={{ color: 'var(--accent)', backgroundColor: 'var(--accent-muted)' }}
            >
              Edit
            </button>
          ) : (
            <div className="flex items-center gap-2">
              <button
                onClick={handleSave}
                disabled={saving}
                className="inline-flex items-center gap-1 text-[12px] font-medium px-3 py-1 rounded-lg text-white transition-colors hover:opacity-90 disabled:opacity-50"
                style={{ backgroundColor: 'var(--accent)' }}
              >
                <Save size={12} /> {saving ? 'Saving...' : 'Save'}
              </button>
              <button
                onClick={() => setEditing(false)}
                className="text-[12px] font-medium px-3 py-1 rounded-lg transition-colors hover:opacity-80"
                style={{ color: 'var(--text-muted)' }}
              >
                Cancel
              </button>
            </div>
          )}
        </div>

        {editing ? (
          <div className="space-y-4">
            <div className="grid grid-cols-2 gap-4">
              <div className="space-y-1.5">
                <label className="text-[12px] font-medium" style={{ color: 'var(--text-secondary)' }}>Name</label>
                <input
                  type="text"
                  value={editName}
                  onChange={(e) => setEditName(e.target.value)}
                  className="w-full px-3 py-2 rounded-lg border text-[13px] outline-none transition-colors focus:border-[var(--accent)]"
                  style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-primary)', color: 'var(--text-primary)' }}
                />
              </div>
              <div className="space-y-1.5">
                <label className="text-[12px] font-medium" style={{ color: 'var(--text-secondary)' }}>URL</label>
                <input
                  type="url"
                  value={editUrl}
                  onChange={(e) => setEditUrl(e.target.value)}
                  className="w-full px-3 py-2 rounded-lg border text-[13px] outline-none transition-colors focus:border-[var(--accent)]"
                  style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-primary)', color: 'var(--text-primary)' }}
                />
              </div>
            </div>
            <div className="space-y-1.5">
              <label className="text-[12px] font-medium" style={{ color: 'var(--text-secondary)' }}>Events</label>
              <div className="flex flex-wrap gap-1.5">
                {AVAILABLE_EVENTS.map((event) => (
                  <button
                    key={event}
                    onClick={() => toggleEvent(event)}
                    className="px-2.5 py-1 rounded-md text-[12px] font-medium transition-all"
                    style={{
                      backgroundColor: editEvents.includes(event) ? 'var(--accent-muted)' : 'var(--bg-primary)',
                      color: editEvents.includes(event) ? 'var(--accent)' : 'var(--text-muted)',
                      border: editEvents.includes(event) ? '1px solid var(--accent)' : '1px solid var(--border-color)',
                    }}
                  >
                    {event}
                  </button>
                ))}
              </div>
            </div>
            <div className="grid grid-cols-2 gap-4">
              <div className="space-y-1.5">
                <label className="text-[12px] font-medium" style={{ color: 'var(--text-secondary)' }}>
                  Filter: Product <span style={{ color: 'var(--text-muted)' }}>(optional)</span>
                </label>
                <input
                  type="text"
                  value={editFilterProduct}
                  onChange={(e) => setEditFilterProduct(e.target.value)}
                  className="w-full px-3 py-2 rounded-lg border text-[13px] outline-none transition-colors focus:border-[var(--accent)]"
                  style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-primary)', color: 'var(--text-primary)' }}
                />
              </div>
              <div className="space-y-1.5">
                <label className="text-[12px] font-medium" style={{ color: 'var(--text-secondary)' }}>
                  Filter: Environment <span style={{ color: 'var(--text-muted)' }}>(optional)</span>
                </label>
                <input
                  type="text"
                  value={editFilterEnv}
                  onChange={(e) => setEditFilterEnv(e.target.value)}
                  className="w-full px-3 py-2 rounded-lg border text-[13px] outline-none transition-colors focus:border-[var(--accent)]"
                  style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-primary)', color: 'var(--text-primary)' }}
                />
              </div>
            </div>
          </div>
        ) : (
          <div className="grid grid-cols-2 gap-x-8 gap-y-3 text-[13px]">
            <div>
              <span style={{ color: 'var(--text-muted)' }}>Events</span>
              <div className="flex flex-wrap gap-1 mt-1">
                {webhook.events.map((e) => (
                  <span
                    key={e}
                    className="text-[11px] px-2 py-0.5 rounded-md font-medium"
                    style={{ backgroundColor: 'var(--accent-muted)', color: 'var(--accent)' }}
                  >
                    {e}
                  </span>
                ))}
              </div>
            </div>
            <div>
              <span style={{ color: 'var(--text-muted)' }}>Filters</span>
              <div className="mt-1" style={{ color: 'var(--text-primary)' }}>
                {webhook.filters.product || webhook.filters.environment ? (
                  <div className="flex gap-2">
                    {webhook.filters.product && <span>Product: {webhook.filters.product}</span>}
                    {webhook.filters.environment && <span>Env: {webhook.filters.environment}</span>}
                  </div>
                ) : (
                  <span style={{ color: 'var(--text-muted)' }}>None</span>
                )}
              </div>
            </div>
            <div>
              <span style={{ color: 'var(--text-muted)' }}>Created</span>
              <div style={{ color: 'var(--text-primary)' }}>
                {format(new Date(webhook.createdAt), 'MMM d, yyyy HH:mm')}
              </div>
            </div>
            {webhook.updatedAt && (
              <div>
                <span style={{ color: 'var(--text-muted)' }}>Updated</span>
                <div style={{ color: 'var(--text-primary)' }}>
                  {format(new Date(webhook.updatedAt), 'MMM d, yyyy HH:mm')}
                </div>
              </div>
            )}
          </div>
        )}
      </div>

      {/* Delivery history */}
      <div
        className="rounded-xl border p-5 space-y-4"
        style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-secondary)' }}
      >
        <div className="flex items-center justify-between">
          <h2 className="text-[14px] font-semibold" style={{ color: 'var(--text-primary)' }}>
            Recent Deliveries
            <span className="ml-2 text-[12px] font-normal" style={{ color: 'var(--text-muted)' }}>
              ({deliveryTotal} total)
            </span>
          </h2>
          <button
            onClick={fetchData}
            className="p-1.5 rounded-lg transition-colors hover:bg-[var(--accent-muted)]"
            style={{ color: 'var(--text-muted)' }}
          >
            <RefreshCw size={14} />
          </button>
        </div>

        {deliveries.length === 0 ? (
          <p className="text-[13px] py-4 text-center" style={{ color: 'var(--text-muted)' }}>
            No deliveries yet
          </p>
        ) : (
          <div className="space-y-1">
            {deliveries.map((d) => (
              <div key={d.id}>
                <button
                  onClick={() => setExpandedDelivery(expandedDelivery === d.id ? null : d.id)}
                  className="w-full flex items-center gap-3 px-3 py-2.5 rounded-lg transition-colors hover:bg-[var(--accent-muted)] text-left"
                >
                  {expandedDelivery === d.id ? (
                    <ChevronDown size={14} style={{ color: 'var(--text-muted)' }} />
                  ) : (
                    <ChevronRight size={14} style={{ color: 'var(--text-muted)' }} />
                  )}
                  <span
                    className="text-[11px] font-medium px-2 py-0.5 rounded-full"
                    style={{ backgroundColor: statusBg(d.status), color: statusColor(d.status) }}
                  >
                    {d.status}
                  </span>
                  <span className="text-[12px] font-medium" style={{ color: 'var(--text-primary)' }}>
                    {d.eventType}
                  </span>
                  {d.httpStatus && (
                    <span className="text-[12px]" style={{ color: 'var(--text-muted)' }}>
                      HTTP {d.httpStatus}
                    </span>
                  )}
                  <span className="text-[12px] ml-auto" style={{ color: 'var(--text-muted)' }}>
                    {d.attempts} attempt{d.attempts !== 1 ? 's' : ''}
                  </span>
                  <span className="text-[12px]" style={{ color: 'var(--text-muted)' }}>
                    {formatDistanceToNow(new Date(d.createdAt), { addSuffix: true })}
                  </span>
                  {d.status === 'failed' && (
                    <button
                      onClick={(e) => {
                        e.stopPropagation();
                        handleRetry(d.id);
                      }}
                      className="p-1 rounded-lg transition-colors hover:bg-[var(--accent-muted)]"
                      style={{ color: 'var(--accent)' }}
                      title="Retry"
                    >
                      <RefreshCw size={13} />
                    </button>
                  )}
                </button>

                {expandedDelivery === d.id && (
                  <div
                    className="ml-8 mr-3 mb-2 p-3 rounded-lg text-[12px] space-y-2"
                    style={{ backgroundColor: 'var(--bg-primary)' }}
                  >
                    <div className="grid grid-cols-2 gap-3">
                      <div>
                        <span style={{ color: 'var(--text-muted)' }}>ID:</span>{' '}
                        <span className="font-mono" style={{ color: 'var(--text-primary)' }}>{d.id}</span>
                      </div>
                      <div>
                        <span style={{ color: 'var(--text-muted)' }}>Created:</span>{' '}
                        <span style={{ color: 'var(--text-primary)' }}>
                          {format(new Date(d.createdAt), 'MMM d, yyyy HH:mm:ss')}
                        </span>
                      </div>
                      {d.deliveredAt && (
                        <div>
                          <span style={{ color: 'var(--text-muted)' }}>Delivered:</span>{' '}
                          <span style={{ color: 'var(--text-primary)' }}>
                            {format(new Date(d.deliveredAt), 'MMM d, yyyy HH:mm:ss')}
                          </span>
                        </div>
                      )}
                      {d.nextRetryAt && d.status === 'pending' && (
                        <div>
                          <span style={{ color: 'var(--text-muted)' }}>Next retry:</span>{' '}
                          <span style={{ color: 'var(--warning)' }}>
                            {formatDistanceToNow(new Date(d.nextRetryAt), { addSuffix: true })}
                          </span>
                        </div>
                      )}
                    </div>
                    {d.errorMessage && (
                      <div>
                        <span style={{ color: 'var(--text-muted)' }}>Error:</span>{' '}
                        <span style={{ color: 'var(--error)' }}>{d.errorMessage}</span>
                      </div>
                    )}
                    {d.responseBody && (
                      <div>
                        <span style={{ color: 'var(--text-muted)' }}>Response:</span>
                        <pre
                          className="mt-1 p-2 rounded text-[11px] overflow-x-auto"
                          style={{ backgroundColor: 'var(--bg-secondary)', color: 'var(--text-secondary)' }}
                        >
                          {d.responseBody}
                        </pre>
                      </div>
                    )}
                    {d.payloadJson && (
                      <div>
                        <span style={{ color: 'var(--text-muted)' }}>Payload:</span>
                        <pre
                          className="mt-1 p-2 rounded text-[11px] overflow-x-auto max-h-48"
                          style={{ backgroundColor: 'var(--bg-secondary)', color: 'var(--text-secondary)' }}
                        >
                          {(() => {
                            try {
                              return JSON.stringify(JSON.parse(d.payloadJson), null, 2);
                            } catch {
                              return d.payloadJson;
                            }
                          })()}
                        </pre>
                      </div>
                    )}
                  </div>
                )}
              </div>
            ))}
          </div>
        )}
      </div>
    </div>
  );
}
