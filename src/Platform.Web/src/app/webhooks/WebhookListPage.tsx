import { useState, useEffect, useCallback } from 'react';
import { Link } from 'react-router-dom';
import { api } from '@/lib/api';
import { useDocumentTitle } from '@/lib/pageTitle';
import type { WebhookSubscription } from '@/lib/types';
import {
  Plus,
  ExternalLink,
  Trash2,
  ToggleLeft,
  ToggleRight,
  Send,
  X,
  Copy,
  Check,
  AlertCircle,
  ChevronDown,
  ChevronRight,
  BookOpen,
  MessageSquare,
} from 'lucide-react';
import { useEntityRefresh } from '@/hooks/useEntityEvents';
import { formatDistanceToNow } from 'date-fns';
import {
  AVAILABLE_EVENTS,
  DEFAULT_ADO_SIGNATURE_HEADER,
  WEBHOOK_TARGET_TYPES,
  azureDevOpsUrl,
  githubDispatchUrl,
  isNotificationTarget,
  maskNotificationUrl,
  targetLabel,
  type WebhookTargetType,
} from './webhookTargets';
import { NotificationCreateForm } from './NotificationCreateForm';
import { WebhookFilterFields, WebhookFilterSummary } from './WebhookFilterFields';
import { EMPTY_FILTERS, toFilterInput } from './webhookFilters';
import type { WebhookFilters } from '@/lib/types';

export function WebhookListPage() {
  const [webhooks, setWebhooks] = useState<WebhookSubscription[]>([]);
  const [loading, setLoading] = useState(true);
  const [showCreate, setShowCreate] = useState(false);
  const [showCreateNotification, setShowCreateNotification] = useState(false);
  const [createdSecret, setCreatedSecret] = useState<string | null>(null);
  const [secretCopied, setSecretCopied] = useState(false);

  useDocumentTitle(['Webhooks']);
  const [error, setError] = useState<string | null>(null);

  // Create form state
  const [name, setName] = useState('');
  const [url, setUrl] = useState('');
  const [selectedEvents, setSelectedEvents] = useState<string[]>([]);
  const [filters, setFilters] = useState<WebhookFilters>(EMPTY_FILTERS);
  const [creating, setCreating] = useState(false);
  const [showGuide, setShowGuide] = useState(false);

  // Target-specific create state. The composed URL still lands in `url`, so a target that needs a
  // shape the builders don't cover can always be typed in raw.
  const [targetType, setTargetType] = useState<WebhookTargetType>('generic');
  const [secret, setSecret] = useState('');
  const [signatureHeader, setSignatureHeader] = useState(DEFAULT_ADO_SIGNATURE_HEADER);
  const [gitHubEventType, setGitHubEventType] = useState('');
  const [adoOrg, setAdoOrg] = useState('');
  const [adoWebhookName, setAdoWebhookName] = useState('');
  const [ghOwner, setGhOwner] = useState('');
  const [ghRepo, setGhRepo] = useState('');
  const [rawUrlMode, setRawUrlMode] = useState(false);

  const composedUrl =
    rawUrlMode || targetType === 'generic'
      ? url
      : targetType === 'azure_devops'
        ? adoOrg && adoWebhookName
          ? azureDevOpsUrl(adoOrg, adoWebhookName)
          : ''
        : ghOwner && ghRepo
          ? githubDispatchUrl(ghOwner, ghRepo)
          : '';

  const secretRequired = targetType !== 'generic';
  const canCreate =
    name.trim() !== '' &&
    composedUrl.trim() !== '' &&
    selectedEvents.length > 0 &&
    (!secretRequired || secret.trim() !== '');

  const resetCreateForm = () => {
    setName('');
    setUrl('');
    setSelectedEvents([]);
    setFilters(EMPTY_FILTERS);
    setTargetType('generic');
    setSecret('');
    setSignatureHeader(DEFAULT_ADO_SIGNATURE_HEADER);
    setGitHubEventType('');
    setAdoOrg('');
    setAdoWebhookName('');
    setGhOwner('');
    setGhRepo('');
    setRawUrlMode(false);
  };

  const fetchWebhooks = useCallback(async () => {
    try {
      const data = await api.getWebhooks();
      setWebhooks(data);
    } catch {
      setError('Failed to load webhooks');
    } finally {
      setLoading(false);
    }
  }, []);

  // The delivery worker announces processed deliveries, so test sends and retries show their
  // outcome when it actually lands instead of after a guessed delay.
  const deliveriesTick = useEntityRefresh(['webhook-delivery']);

  useEffect(() => {
    fetchWebhooks();
  }, [fetchWebhooks, deliveriesTick]);

  const handleCreate = async () => {
    if (!canCreate) return;
    setCreating(true);
    setError(null);
    try {
      const result = await api.createWebhook({
        name,
        url: composedUrl,
        events: selectedEvents,
        filters: toFilterInput(filters),
        targetType,
        // Only generic mints its own secret; the others reuse what the receiver already holds.
        secret: secretRequired ? secret.trim() : undefined,
        signatureHeader: targetType === 'azure_devops' ? signatureHeader.trim() || undefined : undefined,
        gitHubEventType: targetType === 'github' ? gitHubEventType.trim() || undefined : undefined,
      });
      setCreatedSecret(result.secret ?? null);
      if (result.secret) setShowGuide(true);
      setShowCreate(false);
      resetCreateForm();
      await fetchWebhooks();
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Failed to create webhook');
    } finally {
      setCreating(false);
    }
  };

  const toggleActive = async (wh: WebhookSubscription) => {
    try {
      await api.updateWebhook(wh.id, { active: !wh.active });
      await fetchWebhooks();
    } catch {
      setError('Failed to toggle webhook');
    }
  };

  const handleDelete = async (id: string) => {
    try {
      await api.deleteWebhook(id);
      await fetchWebhooks();
    } catch {
      setError('Failed to delete webhook');
    }
  };

  const handleTest = async (id: string) => {
    try {
      await api.testWebhook(id);
      // No refetch here — the delivery worker's webhook-delivery event triggers it on completion.
    } catch {
      setError('Failed to send test');
    }
  };

  const toggleEvent = (event: string) => {
    setSelectedEvents((prev) =>
      prev.includes(event) ? prev.filter((e) => e !== event) : [...prev, event]
    );
  };

  const copySecret = () => {
    if (!createdSecret) return;
    navigator.clipboard.writeText(createdSecret);
    setSecretCopied(true);
    setTimeout(() => setSecretCopied(false), 2000);
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

  return (
    <div className="space-y-5">
      {/* Header */}
      <div className="flex items-center justify-between">
        <div>
          <h1 className="text-xl font-semibold tracking-tight" style={{ color: 'var(--text-primary)' }}>
            Webhooks
          </h1>
          <p className="text-sm mt-1" style={{ color: 'var(--text-muted)' }}>
            Manage webhook subscriptions for platform events
          </p>
        </div>
        {/* Two entry points, because the two things ask for almost nothing in common: a webhook hands
            an event envelope to a system, a notification posts words into a channel. */}
        <div className="flex items-center gap-2">
          <button
            onClick={() => {
              setShowCreateNotification(true);
              setShowCreate(false);
            }}
            className="inline-flex items-center gap-1.5 text-[13px] font-medium px-3 py-2 rounded-lg transition-colors hover:opacity-80"
            style={{ color: 'var(--accent)', backgroundColor: 'var(--accent-muted)' }}
          >
            <MessageSquare size={15} />
            New Notification
          </button>
          <button
            onClick={() => {
              setShowCreate(true);
              setShowCreateNotification(false);
            }}
            className="inline-flex items-center gap-1.5 text-[13px] font-medium px-4 py-2 rounded-lg text-white transition-colors hover:opacity-90"
            style={{ backgroundColor: 'var(--accent)' }}
          >
            <Plus size={15} />
            New Webhook
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

      {/* Secret banner */}
      {createdSecret && (
        <div
          className="rounded-xl border p-4 space-y-2"
          style={{ borderColor: 'var(--warning)', backgroundColor: 'var(--warning-bg)' }}
        >
          <div className="flex items-center justify-between">
            <span className="text-[13px] font-semibold" style={{ color: 'var(--warning)' }}>
              Webhook secret — copy now, it won't be shown again
            </span>
            <button onClick={() => setCreatedSecret(null)}>
              <X size={14} style={{ color: 'var(--warning)' }} />
            </button>
          </div>
          <div className="flex items-center gap-2">
            <code
              className="flex-1 text-[13px] px-3 py-2 rounded-lg font-mono"
              style={{ backgroundColor: 'var(--bg-primary)', color: 'var(--text-primary)' }}
            >
              {createdSecret}
            </code>
            <button
              onClick={copySecret}
              className="p-2 rounded-lg transition-colors hover:opacity-80"
              style={{ color: 'var(--warning)' }}
            >
              {secretCopied ? <Check size={16} /> : <Copy size={16} />}
            </button>
          </div>
        </div>
      )}

      {/* Create notification */}
      {showCreateNotification && (
        <NotificationCreateForm
          onCancel={() => setShowCreateNotification(false)}
          onCreated={async () => {
            setShowCreateNotification(false);
            await fetchWebhooks();
          }}
        />
      )}

      {/* Create modal */}
      {showCreate && (
        <div
          className="rounded-xl border p-5 space-y-4"
          style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-secondary)' }}
        >
          <div className="flex items-center justify-between">
            <h2 className="text-[14px] font-semibold" style={{ color: 'var(--text-primary)' }}>
              Create Webhook
            </h2>
            <button onClick={() => setShowCreate(false)} style={{ color: 'var(--text-muted)' }}>
              <X size={16} />
            </button>
          </div>

          {/* Target — chosen first, because it decides which fields below apply */}
          <div className="space-y-1.5">
            <label className="text-[12px] font-medium" style={{ color: 'var(--text-secondary)' }}>
              Target
            </label>
            <div className="grid grid-cols-1 sm:grid-cols-3 gap-2">
              {WEBHOOK_TARGET_TYPES.map((t) => (
                <button
                  key={t.value}
                  onClick={() => setTargetType(t.value)}
                  className="text-left px-3 py-2.5 rounded-lg transition-all"
                  style={{
                    backgroundColor: targetType === t.value ? 'var(--accent-muted)' : 'var(--bg-primary)',
                    border:
                      targetType === t.value
                        ? '1px solid var(--accent)'
                        : '1px solid var(--border-color)',
                  }}
                >
                  <div
                    className="text-[13px] font-medium"
                    style={{ color: targetType === t.value ? 'var(--accent)' : 'var(--text-primary)' }}
                  >
                    {t.label}
                  </div>
                  <div className="text-[11px] mt-0.5 leading-snug" style={{ color: 'var(--text-muted)' }}>
                    {t.description}
                  </div>
                </button>
              ))}
            </div>
          </div>

          <div className="grid grid-cols-1 sm:grid-cols-2 gap-4">
            <div className="space-y-1.5">
              <label className="text-[12px] font-medium" style={{ color: 'var(--text-secondary)' }}>
                Name
              </label>
              <input
                type="text"
                value={name}
                onChange={(e) => setName(e.target.value)}
                placeholder="e.g. Slack notifications"
                className="w-full px-3 py-2 rounded-lg border text-[13px] outline-none transition-colors focus:border-[var(--accent)]"
                style={{
                  borderColor: 'var(--border-color)',
                  backgroundColor: 'var(--bg-primary)',
                  color: 'var(--text-primary)',
                }}
              />
            </div>

            {(targetType === 'generic' || rawUrlMode) && (
              <div className="space-y-1.5">
                <label className="text-[12px] font-medium" style={{ color: 'var(--text-secondary)' }}>
                  URL
                </label>
                <input
                  type="url"
                  value={url}
                  onChange={(e) => setUrl(e.target.value)}
                  placeholder="https://example.com/webhook"
                  className="w-full px-3 py-2 rounded-lg border text-[13px] outline-none transition-colors focus:border-[var(--accent)]"
                  style={{
                    borderColor: 'var(--border-color)',
                    backgroundColor: 'var(--bg-primary)',
                    color: 'var(--text-primary)',
                  }}
                />
              </div>
            )}

            {targetType === 'azure_devops' && !rawUrlMode && (
              <>
                <div className="space-y-1.5">
                  <label className="text-[12px] font-medium" style={{ color: 'var(--text-secondary)' }}>
                    Organization
                  </label>
                  <input
                    type="text"
                    value={adoOrg}
                    onChange={(e) => setAdoOrg(e.target.value)}
                    placeholder="e.g. contoso"
                    className="w-full px-3 py-2 rounded-lg border text-[13px] outline-none transition-colors focus:border-[var(--accent)]"
                    style={{
                      borderColor: 'var(--border-color)',
                      backgroundColor: 'var(--bg-primary)',
                      color: 'var(--text-primary)',
                    }}
                  />
                </div>
                <div className="space-y-1.5">
                  <label className="text-[12px] font-medium" style={{ color: 'var(--text-secondary)' }}>
                    Webhook name
                  </label>
                  <input
                    type="text"
                    value={adoWebhookName}
                    onChange={(e) => setAdoWebhookName(e.target.value)}
                    placeholder="matches the service connection"
                    className="w-full px-3 py-2 rounded-lg border text-[13px] outline-none transition-colors focus:border-[var(--accent)]"
                    style={{
                      borderColor: 'var(--border-color)',
                      backgroundColor: 'var(--bg-primary)',
                      color: 'var(--text-primary)',
                    }}
                  />
                </div>
              </>
            )}

            {targetType === 'github' && !rawUrlMode && (
              <>
                <div className="space-y-1.5">
                  <label className="text-[12px] font-medium" style={{ color: 'var(--text-secondary)' }}>
                    Owner
                  </label>
                  <input
                    type="text"
                    value={ghOwner}
                    onChange={(e) => setGhOwner(e.target.value)}
                    placeholder="e.g. contoso"
                    className="w-full px-3 py-2 rounded-lg border text-[13px] outline-none transition-colors focus:border-[var(--accent)]"
                    style={{
                      borderColor: 'var(--border-color)',
                      backgroundColor: 'var(--bg-primary)',
                      color: 'var(--text-primary)',
                    }}
                  />
                </div>
                <div className="space-y-1.5">
                  <label className="text-[12px] font-medium" style={{ color: 'var(--text-secondary)' }}>
                    Repository
                  </label>
                  <input
                    type="text"
                    value={ghRepo}
                    onChange={(e) => setGhRepo(e.target.value)}
                    placeholder="e.g. infrastructure"
                    className="w-full px-3 py-2 rounded-lg border text-[13px] outline-none transition-colors focus:border-[var(--accent)]"
                    style={{
                      borderColor: 'var(--border-color)',
                      backgroundColor: 'var(--bg-primary)',
                      color: 'var(--text-primary)',
                    }}
                  />
                </div>
              </>
            )}
          </div>

          {/* Target-specific credential + tuning */}
          {targetType !== 'generic' && (
            <div className="grid grid-cols-1 sm:grid-cols-2 gap-4">
              <div className="space-y-1.5">
                <label className="text-[12px] font-medium" style={{ color: 'var(--text-secondary)' }}>
                  {targetType === 'github' ? 'Token' : 'Secret'}
                </label>
                <input
                  type="password"
                  value={secret}
                  onChange={(e) => setSecret(e.target.value)}
                  autoComplete="new-password"
                  placeholder={
                    targetType === 'github'
                      ? 'token with repository dispatch permission'
                      : 'the service connection secret'
                  }
                  className="w-full px-3 py-2 rounded-lg border text-[13px] outline-none transition-colors focus:border-[var(--accent)]"
                  style={{
                    borderColor: 'var(--border-color)',
                    backgroundColor: 'var(--bg-primary)',
                    color: 'var(--text-primary)',
                  }}
                />
                <p className="text-[11px]" style={{ color: 'var(--text-muted)' }}>
                  {targetType === 'github'
                    ? 'Sent as a bearer token. Stored encrypted and never shown again.'
                    : 'Must match the Incoming WebHook service connection. Stored encrypted and never shown again.'}
                </p>
              </div>

              {targetType === 'azure_devops' && (
                <div className="space-y-1.5">
                  <label className="text-[12px] font-medium" style={{ color: 'var(--text-secondary)' }}>
                    Signature header
                  </label>
                  <input
                    type="text"
                    value={signatureHeader}
                    onChange={(e) => setSignatureHeader(e.target.value)}
                    placeholder={DEFAULT_ADO_SIGNATURE_HEADER}
                    className="w-full px-3 py-2 rounded-lg border text-[13px] outline-none transition-colors focus:border-[var(--accent)]"
                    style={{
                      borderColor: 'var(--border-color)',
                      backgroundColor: 'var(--bg-primary)',
                      color: 'var(--text-primary)',
                    }}
                  />
                  <p className="text-[11px]" style={{ color: 'var(--text-muted)' }}>
                    Must match the "Http Header" field of the service connection. Carries{' '}
                    <code>sha1=&lt;hex&gt;</code>.
                  </p>
                </div>
              )}

              {targetType === 'github' && (
                <div className="space-y-1.5">
                  <label className="text-[12px] font-medium" style={{ color: 'var(--text-secondary)' }}>
                    Event type <span style={{ color: 'var(--text-muted)' }}>(optional)</span>
                  </label>
                  <input
                    type="text"
                    value={gitHubEventType}
                    onChange={(e) => setGitHubEventType(e.target.value)}
                    placeholder="defaults to the InfraPilot event name"
                    className="w-full px-3 py-2 rounded-lg border text-[13px] outline-none transition-colors focus:border-[var(--accent)]"
                    style={{
                      borderColor: 'var(--border-color)',
                      backgroundColor: 'var(--bg-primary)',
                      color: 'var(--text-primary)',
                    }}
                  />
                  <p className="text-[11px]" style={{ color: 'var(--text-muted)' }}>
                    The <code>event_type</code> your workflow filters on under{' '}
                    <code>repository_dispatch</code>.
                  </p>
                </div>
              )}
            </div>
          )}

          {targetType !== 'generic' && (
            <div className="flex items-center gap-2">
              <button
                onClick={() => setRawUrlMode(!rawUrlMode)}
                className="text-[12px] underline"
                style={{ color: 'var(--text-muted)' }}
              >
                {rawUrlMode ? 'Compose the URL from fields' : 'Enter the URL manually instead'}
              </button>
              {!rawUrlMode && composedUrl && (
                <code className="text-[11px] truncate" style={{ color: 'var(--text-muted)' }}>
                  {composedUrl}
                </code>
              )}
            </div>
          )}

          <div className="space-y-1.5">
            <label className="text-[12px] font-medium" style={{ color: 'var(--text-secondary)' }}>
              Events
            </label>
            <div className="flex flex-wrap gap-1.5">
              {AVAILABLE_EVENTS.map((event) => (
                <button
                  key={event}
                  onClick={() => toggleEvent(event)}
                  className="px-2.5 py-1 rounded-md text-[12px] font-medium transition-all"
                  style={{
                    backgroundColor: selectedEvents.includes(event) ? 'var(--accent-muted)' : 'var(--bg-primary)',
                    color: selectedEvents.includes(event) ? 'var(--accent)' : 'var(--text-muted)',
                    border: selectedEvents.includes(event) ? '1px solid var(--accent)' : '1px solid var(--border-color)',
                  }}
                >
                  {event}
                </button>
              ))}
            </div>
          </div>

          <WebhookFilterFields filters={filters} onChange={setFilters} />

          <div className="flex items-center gap-3 pt-2 border-t" style={{ borderColor: 'var(--border-color)' }}>
            <button
              onClick={handleCreate}
              disabled={creating || !canCreate}
              className="inline-flex items-center gap-1.5 text-[13px] font-medium px-4 py-2 rounded-lg text-white transition-colors hover:opacity-90 disabled:opacity-50"
              style={{ backgroundColor: 'var(--accent)' }}
            >
              {creating ? 'Creating...' : 'Create'}
            </button>
            <button
              onClick={() => setShowCreate(false)}
              className="text-[13px] font-medium px-3 py-2 rounded-lg transition-colors hover:opacity-80"
              style={{ color: 'var(--text-muted)' }}
            >
              Cancel
            </button>
          </div>
        </div>
      )}

      {/* Webhook table */}
      {webhooks.length === 0 ? (
        <div
          className="rounded-xl border p-8 text-center"
          style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-secondary)' }}
        >
          <p className="text-[14px]" style={{ color: 'var(--text-muted)' }}>
            No webhooks configured. Create one to get started.
          </p>
        </div>
      ) : (
        <div
          className="rounded-xl border overflow-hidden"
          style={{ borderColor: 'var(--border-color)' }}
        >
          <table className="w-full text-[13px]">
            <thead>
              <tr style={{ backgroundColor: 'var(--bg-secondary)' }}>
                <th className="text-left px-4 py-3 font-medium" style={{ color: 'var(--text-muted)', borderBottom: '1px solid var(--border-color)' }}>
                  Name
                </th>
                <th className="text-left px-4 py-3 font-medium" style={{ color: 'var(--text-muted)', borderBottom: '1px solid var(--border-color)' }}>
                  Events
                </th>
                <th className="text-left px-4 py-3 font-medium" style={{ color: 'var(--text-muted)', borderBottom: '1px solid var(--border-color)' }}>
                  Deliveries
                </th>
                <th className="text-left px-4 py-3 font-medium" style={{ color: 'var(--text-muted)', borderBottom: '1px solid var(--border-color)' }}>
                  Status
                </th>
                <th className="text-right px-4 py-3 font-medium" style={{ color: 'var(--text-muted)', borderBottom: '1px solid var(--border-color)' }}>
                  Actions
                </th>
              </tr>
            </thead>
            <tbody>
              {webhooks.map((wh) => (
                <tr
                  key={wh.id}
                  className="transition-colors hover:bg-[var(--accent-muted)]"
                  style={{ borderBottom: '1px solid var(--border-color)' }}
                >
                  <td className="px-4 py-3" style={{ borderBottom: '1px solid var(--border-color)' }}>
                    <Link
                      to={`/webhooks/${wh.id}`}
                      className="font-medium hover:underline"
                      style={{ color: 'var(--accent)' }}
                    >
                      {wh.name}
                    </Link>
                    {/* A chat webhook URL is a bearer credential — its path is what lets anyone post
                        to the channel, so only the host is shown here. */}
                    <div className="text-[12px] mt-0.5 flex items-center gap-1" style={{ color: 'var(--text-muted)' }}>
                      <ExternalLink size={11} />
                      <span className="truncate max-w-[250px]">
                        {isNotificationTarget(wh.targetType) ? maskNotificationUrl(wh.url) : wh.url}
                      </span>
                    </div>
                    {wh.targetType && wh.targetType !== 'generic' && (
                      <span
                        className="inline-block text-[11px] px-1.5 py-0.5 rounded mt-1 font-medium"
                        style={{ backgroundColor: 'var(--accent-muted)', color: 'var(--accent)' }}
                      >
                        {targetLabel(wh.targetType)}
                      </span>
                    )}
                    <WebhookFilterSummary
                      filters={wh.filters}
                      className="flex flex-wrap gap-1.5 mt-1"
                    />
                  </td>
                  <td className="px-4 py-3" style={{ borderBottom: '1px solid var(--border-color)' }}>
                    <div className="flex flex-wrap gap-1">
                      {wh.events.map((e) => (
                        <span
                          key={e}
                          className="text-[11px] px-2 py-0.5 rounded-md font-medium"
                          style={{ backgroundColor: 'var(--accent-muted)', color: 'var(--accent)' }}
                        >
                          {e}
                        </span>
                      ))}
                    </div>
                  </td>
                  <td className="px-4 py-3" style={{ borderBottom: '1px solid var(--border-color)' }}>
                    {wh.deliveryStats ? (
                      <div className="space-y-0.5">
                        <div className="flex items-center gap-3 text-[12px]">
                          <span style={{ color: 'var(--success)' }}>{wh.deliveryStats.delivered} delivered</span>
                          {wh.deliveryStats.failed > 0 && (
                            <span style={{ color: 'var(--error)' }}>{wh.deliveryStats.failed} failed</span>
                          )}
                          {wh.deliveryStats.pending > 0 && (
                            <span style={{ color: 'var(--warning)' }}>{wh.deliveryStats.pending} pending</span>
                          )}
                        </div>
                        {wh.deliveryStats.lastDeliveryAt && (
                          <div className="text-[11px]" style={{ color: 'var(--text-muted)' }}>
                            Last: {formatDistanceToNow(new Date(wh.deliveryStats.lastDeliveryAt), { addSuffix: true })}
                          </div>
                        )}
                      </div>
                    ) : (
                      <span className="text-[12px]" style={{ color: 'var(--text-muted)' }}>No deliveries</span>
                    )}
                  </td>
                  <td className="px-4 py-3" style={{ borderBottom: '1px solid var(--border-color)' }}>
                    <span
                      className="inline-flex items-center gap-1 text-[12px] font-medium px-2 py-0.5 rounded-full"
                      style={{
                        backgroundColor: wh.active ? 'var(--success-bg)' : 'var(--bg-primary)',
                        color: wh.active ? 'var(--success)' : 'var(--text-muted)',
                      }}
                    >
                      <span
                        className="w-1.5 h-1.5 rounded-full"
                        style={{ backgroundColor: wh.active ? 'var(--success)' : 'var(--text-muted)' }}
                      />
                      {wh.active ? 'Active' : 'Inactive'}
                    </span>
                  </td>
                  <td className="px-4 py-3 text-right" style={{ borderBottom: '1px solid var(--border-color)' }}>
                    <div className="flex items-center justify-end gap-1">
                      <button
                        onClick={() => handleTest(wh.id)}
                        className="p-1.5 rounded-lg transition-colors hover:bg-[var(--accent-muted)]"
                        style={{ color: 'var(--text-muted)' }}
                        title="Send test ping"
                      >
                        <Send size={14} />
                      </button>
                      <button
                        onClick={() => toggleActive(wh)}
                        className="p-1.5 rounded-lg transition-colors hover:bg-[var(--accent-muted)]"
                        style={{ color: wh.active ? 'var(--success)' : 'var(--text-muted)' }}
                        title={wh.active ? 'Deactivate' : 'Activate'}
                      >
                        {wh.active ? <ToggleRight size={16} /> : <ToggleLeft size={16} />}
                      </button>
                      <button
                        onClick={() => handleDelete(wh.id)}
                        className="p-1.5 rounded-lg transition-colors hover:bg-[var(--error-bg)]"
                        style={{ color: 'var(--text-muted)' }}
                        title="Delete"
                      >
                        <Trash2 size={14} />
                      </button>
                    </div>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}

      {/* Integration Guide */}
      <div
        className="rounded-xl border overflow-hidden"
        style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-secondary)' }}
      >
        <button
          onClick={() => setShowGuide(!showGuide)}
          className="w-full flex items-center gap-2.5 px-5 py-4 text-left transition-colors hover:bg-[var(--accent-muted)]"
        >
          <BookOpen size={16} style={{ color: 'var(--accent)' }} />
          <span className="text-[14px] font-semibold flex-1" style={{ color: 'var(--text-primary)' }}>
            Integration Guide
          </span>
          {showGuide ? (
            <ChevronDown size={16} style={{ color: 'var(--text-muted)' }} />
          ) : (
            <ChevronRight size={16} style={{ color: 'var(--text-muted)' }} />
          )}
        </button>

        {showGuide && (
          <div
            className="px-5 pb-5 space-y-5 border-t"
            style={{ borderColor: 'var(--border-color)' }}
          >
            {/* Targets */}
            <div className="pt-4 space-y-2">
              <h3 className="text-[13px] font-semibold" style={{ color: 'var(--text-primary)' }}>
                Targets
              </h3>
              <p className="text-[13px]" style={{ color: 'var(--text-secondary)' }}>
                A subscription's target decides how the delivery is framed. Event filtering, retries
                and delivery history work the same way for all of them. The target is fixed at
                creation — to change it, delete the subscription and create a new one.
              </p>
              <div
                className="rounded-lg overflow-hidden text-[12px]"
                style={{ backgroundColor: 'var(--bg-primary)' }}
              >
                <table className="w-full">
                  <tbody>
                    {[
                      ['Generic', 'Signed JSON POST to any URL', 'HMAC-SHA256 in X-Hub-Signature-256'],
                      ['Azure DevOps', 'Incoming WebHook service connection', 'HMAC-SHA1 in a header you choose'],
                      ['GitHub', 'repository_dispatch REST call', 'Bearer token, no signature'],
                      ['Microsoft Teams', 'Adaptive Card posted to a channel', 'The webhook URL is the credential'],
                      ['Microsoft Teams (HTML)', 'HTML posted to a Power Automate flow', 'The webhook URL is the credential'],
                      ['Discord', 'Message or embed posted to a channel', 'The webhook URL is the credential'],
                    ].map(([label, what, auth]) => (
                      <tr key={label} style={{ borderBottom: '1px solid var(--border-color)' }}>
                        <td className="px-3 py-2 font-semibold whitespace-nowrap" style={{ color: 'var(--accent)' }}>
                          {label}
                        </td>
                        <td className="px-3 py-2" style={{ color: 'var(--text-secondary)' }}>
                          {what}
                        </td>
                        <td className="px-3 py-2" style={{ color: 'var(--text-muted)' }}>
                          {auth}
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            </div>

            {/* Headers */}
            <div className="space-y-2">
              <h3 className="text-[13px] font-semibold" style={{ color: 'var(--text-primary)' }}>
                Generic target — request headers
              </h3>
              <p className="text-[13px]" style={{ color: 'var(--text-secondary)' }}>
                Each webhook delivery includes these HTTP headers:
              </p>
              <div
                className="rounded-lg overflow-hidden text-[12px] font-mono"
                style={{ backgroundColor: 'var(--bg-primary)' }}
              >
                <table className="w-full">
                  <tbody>
                    {[
                      ['X-Hub-Signature-256', 'sha256=<hex>', 'HMAC-SHA256 hex digest of the request body, computed with your webhook secret'],
                      ['X-Webhook-Event', 'deployment.created', 'The event type that triggered this delivery'],
                      ['X-Webhook-Delivery', '<uuid>', 'Unique ID for this delivery attempt (for idempotency)'],
                      ['Content-Type', 'application/json', 'Payload is always JSON'],
                    ].map(([header, example, desc]) => (
                      <tr key={header} style={{ borderBottom: '1px solid var(--border-color)' }}>
                        <td className="px-3 py-2 font-semibold whitespace-nowrap" style={{ color: 'var(--accent)' }}>
                          {header}
                        </td>
                        <td className="px-3 py-2 whitespace-nowrap" style={{ color: 'var(--text-muted)' }}>
                          {example}
                        </td>
                        <td className="px-3 py-2 font-sans text-[12px]" style={{ color: 'var(--text-secondary)' }}>
                          {desc}
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            </div>

            {/* Verification steps */}
            <div className="space-y-2">
              <h3 className="text-[13px] font-semibold" style={{ color: 'var(--text-primary)' }}>
                Generic target — verifying signatures
              </h3>
              <p className="text-[13px]" style={{ color: 'var(--text-secondary)' }}>
                To verify that a webhook delivery is authentic, compute an HMAC-SHA256 of the raw
                request body using your secret, then compare it to the value in the{' '}
                <code
                  className="text-[12px] px-1.5 py-0.5 rounded"
                  style={{ backgroundColor: 'var(--bg-primary)', color: 'var(--accent)' }}
                >
                  X-Hub-Signature-256
                </code>{' '}
                header. Always use a constant-time comparison to prevent timing attacks.
              </p>
            </div>

            {/* Node.js example */}
            <div className="space-y-2">
              <h4 className="text-[12px] font-semibold" style={{ color: 'var(--text-secondary)' }}>
                Node.js / TypeScript
              </h4>
              <pre
                className="rounded-lg p-4 text-[12px] leading-relaxed overflow-x-auto"
                style={{ backgroundColor: 'var(--bg-primary)', color: 'var(--text-primary)' }}
              >
{`import crypto from 'node:crypto';

function verifySignature(
  payload: string | Buffer,
  secret: string,
  signatureHeader: string
): boolean {
  const expected = 'sha256=' + crypto
    .createHmac('sha256', secret)
    .update(payload)
    .digest('hex');

  return crypto.timingSafeEqual(
    Buffer.from(expected),
    Buffer.from(signatureHeader)
  );
}

// Express middleware example
app.post('/webhook', express.raw({ type: 'application/json' }), (req, res) => {
  const signature = req.headers['x-hub-signature-256'] as string;
  if (!verifySignature(req.body, process.env.WEBHOOK_SECRET!, signature)) {
    return res.status(401).send('Invalid signature');
  }

  const event = req.headers['x-webhook-event'] as string;
  const deliveryId = req.headers['x-webhook-delivery'] as string;
  const payload = JSON.parse(req.body.toString());

  // Process the event...
  console.log(\`Received \${event} (delivery: \${deliveryId})\`);
  res.status(200).json({ ok: true });
});`}
              </pre>
            </div>

            {/* Python example */}
            <div className="space-y-2">
              <h4 className="text-[12px] font-semibold" style={{ color: 'var(--text-secondary)' }}>
                Python (Flask)
              </h4>
              <pre
                className="rounded-lg p-4 text-[12px] leading-relaxed overflow-x-auto"
                style={{ backgroundColor: 'var(--bg-primary)', color: 'var(--text-primary)' }}
              >
{`import hmac, hashlib

def verify_signature(payload: bytes, secret: str, signature_header: str) -> bool:
    expected = 'sha256=' + hmac.new(
        secret.encode(), payload, hashlib.sha256
    ).hexdigest()
    return hmac.compare_digest(expected, signature_header)

@app.route('/webhook', methods=['POST'])
def handle_webhook():
    signature = request.headers.get('X-Hub-Signature-256', '')
    if not verify_signature(request.data, WEBHOOK_SECRET, signature):
        abort(401, 'Invalid signature')

    event = request.headers.get('X-Webhook-Event')
    delivery_id = request.headers.get('X-Webhook-Delivery')
    payload = request.get_json()

    # Process the event...
    print(f"Received {event} (delivery: {delivery_id})")
    return jsonify(ok=True), 200`}
              </pre>
            </div>

            {/* C# / .NET example */}
            <div className="space-y-2">
              <h4 className="text-[12px] font-semibold" style={{ color: 'var(--text-secondary)' }}>
                C# / ASP.NET Core
              </h4>
              <pre
                className="rounded-lg p-4 text-[12px] leading-relaxed overflow-x-auto"
                style={{ backgroundColor: 'var(--bg-primary)', color: 'var(--text-primary)' }}
              >
{`using System.Security.Cryptography;
using System.Text;

bool VerifySignature(byte[] payload, string secret, string signatureHeader)
{
    using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
    var hash = hmac.ComputeHash(payload);
    var expected = "sha256=" + Convert.ToHexStringLower(hash);
    return CryptographicOperations.FixedTimeEquals(
        Encoding.UTF8.GetBytes(expected),
        Encoding.UTF8.GetBytes(signatureHeader));
}

// Minimal API endpoint
app.MapPost("/webhook", async (HttpContext ctx) =>
{
    using var reader = new StreamReader(ctx.Request.Body);
    var body = await reader.ReadToEndAsync();
    var signature = ctx.Request.Headers["X-Hub-Signature-256"].ToString();

    if (!VerifySignature(Encoding.UTF8.GetBytes(body), webhookSecret, signature))
        return Results.Unauthorized();

    var eventType = ctx.Request.Headers["X-Webhook-Event"].ToString();
    var deliveryId = ctx.Request.Headers["X-Webhook-Delivery"].ToString();

    // Process the event...
    return Results.Ok(new { ok = true });
});`}
              </pre>
            </div>

            {/* Azure DevOps */}
            <div className="space-y-2 pt-1 border-t" style={{ borderColor: 'var(--border-color)' }}>
              <h3 className="text-[13px] font-semibold pt-4" style={{ color: 'var(--text-primary)' }}>
                Azure DevOps target
              </h3>
              <p className="text-[13px]" style={{ color: 'var(--text-secondary)' }}>
                Triggers a pipeline through an Incoming WebHook service connection. The body is the
                same InfraPilot envelope as the generic target, but it is signed with{' '}
                <strong>HMAC-SHA1</strong> and the digest goes in whichever header the service
                connection is configured to read.
              </p>
              <ol className="text-[13px] space-y-1.5 list-decimal list-inside" style={{ color: 'var(--text-secondary)' }}>
                <li>
                  In Azure DevOps, go to <strong>Project Settings → Service connections</strong> and
                  create an <strong>Incoming WebHook</strong> connection.
                </li>
                <li>
                  Set <strong>WebHook Name</strong>, a <strong>Secret</strong>, and an{' '}
                  <strong>Http Header</strong> (e.g.{' '}
                  <code style={{ color: 'var(--accent)' }}>X-Hub-Signature</code>).
                </li>
                <li>
                  Create the subscription here with the same webhook name, secret, and header.
                </li>
                <li>
                  Add the webhook resource to the pipeline that should run, then use{' '}
                  <strong>Send test ping</strong> to confirm it fires.
                </li>
              </ol>
              <pre
                className="rounded-lg p-4 text-[12px] leading-relaxed overflow-x-auto"
                style={{ backgroundColor: 'var(--bg-primary)', color: 'var(--text-primary)' }}
              >
{`# azure-pipelines.yml
resources:
  webhooks:
    - webhook: infrapilot            # must match the service connection name
      connection: infrapilot

trigger: none

steps:
  - script: |
      echo "event:   \${{ parameters.infrapilot.eventType }}"
      echo "product: \${{ parameters.infrapilot.data.product }}"
      echo "version: \${{ parameters.infrapilot.data.version }}"`}
              </pre>
              <p className="text-[13px]" style={{ color: 'var(--text-muted)' }}>
                The whole envelope is addressable as{' '}
                <code style={{ color: 'var(--accent)' }}>{'${{ parameters.<webhook>.<path> }}'}</code>.
                If deliveries come back 400 or 403, the header name is almost always the cause —
                check it matches the service connection exactly.
              </p>
            </div>

            {/* GitHub */}
            <div className="space-y-2 pt-1 border-t" style={{ borderColor: 'var(--border-color)' }}>
              <h3 className="text-[13px] font-semibold pt-4" style={{ color: 'var(--text-primary)' }}>
                GitHub target
              </h3>
              <p className="text-[13px]" style={{ color: 'var(--text-secondary)' }}>
                GitHub has no inbound webhook receiver, so this target calls the{' '}
                <code style={{ color: 'var(--accent)' }}>repository_dispatch</code> API instead. It
                authenticates with a token rather than a signature, and the envelope rides along as{' '}
                <code style={{ color: 'var(--accent)' }}>client_payload</code>. A successful dispatch
                answers <strong>204 No Content</strong>.
              </p>
              <p className="text-[13px]" style={{ color: 'var(--text-secondary)' }}>
                The token needs permission to dispatch repository events: a fine-grained token with{' '}
                <strong>Contents: read and write</strong> on the target repository, or a classic
                token with the <strong>repo</strong> scope.
              </p>
              <pre
                className="rounded-lg p-4 text-[12px] leading-relaxed overflow-x-auto"
                style={{ backgroundColor: 'var(--bg-primary)', color: 'var(--text-primary)' }}
              >
{`# .github/workflows/deploy.yml
on:
  repository_dispatch:
    types: [deployment.created]     # or your Event type override

jobs:
  handle:
    runs-on: ubuntu-latest
    steps:
      - run: |
          echo "event:   \${{ github.event.client_payload.eventType }}"
          echo "product: \${{ github.event.client_payload.data.product }}"

# What InfraPilot sends:
# POST https://api.github.com/repos/{owner}/{repo}/dispatches
# Authorization: Bearer <token>
# {"event_type":"deployment.created","client_payload":{ ...envelope... }}`}
              </pre>
              <p className="text-[13px]" style={{ color: 'var(--text-muted)' }}>
                Leave <strong>Event type</strong> blank to dispatch under the InfraPilot event name,
                so one workflow can filter several events by <code>types:</code>. Set it to collapse
                every event onto a single dispatch name instead.
              </p>
            </div>

            {/* Chat notifications */}
            <div className="space-y-2 pt-1 border-t" style={{ borderColor: 'var(--border-color)' }}>
              <h3 className="text-[13px] font-semibold pt-4" style={{ color: 'var(--text-primary)' }}>
                Microsoft Teams and Discord — notifications
              </h3>
              <p className="text-[13px]" style={{ color: 'var(--text-secondary)' }}>
                Created from <strong>New Notification</strong> rather than New Webhook. These targets
                post a message a person reads, so the body is rendered from a Handlebars template
                instead of being the event envelope. There is no secret and no signature: the webhook
                URL is itself the capability to post, which is why an https URL is required and why
                the URL is masked everywhere it is displayed.
              </p>
              <ul className="text-[13px] space-y-1.5 list-disc list-inside" style={{ color: 'var(--text-secondary)' }}>
                <li>
                  <strong>Teams:</strong> channel → <strong>⋯ → Workflows</strong> →{' '}
                  <em>Post to a channel when a webhook request is received</em>, then copy the URL.
                  InfraPilot sends an Adaptive Card. Legacy Office 365 connector URLs
                  (<code>webhook.office.com</code>) are detected from the host and sent a MessageCard
                  instead, so existing connectors keep working until Microsoft retires them.
                </li>
                <li>
                  <strong>Teams (HTML):</strong> the same kind of URL, for a Power Automate flow whose
                  action is <em>Post message in a chat or channel</em> rather than <em>Post card</em>.
                  That action takes HTML, so the message is converted and POSTed raw instead of being
                  wrapped in a card. Nothing in the URL says which flow you have, so pick this one
                  deliberately — it reads as an ordinary message rather than a card attributed to the
                  Workflows app, and it keeps tables and headings.
                </li>
                <li>
                  <strong>Discord:</strong> Server Settings → <strong>Integrations → Webhooks</strong>{' '}
                  → New Webhook. With a heading the message is posted as an embed; without one, as
                  plain content.
                </li>
              </ul>
              <p className="text-[13px]" style={{ color: 'var(--text-secondary)' }}>
                Every event has a default message, so a notification works before you write anything.
                Release notes arrive already rendered and their default forwards{' '}
                <code style={{ color: 'var(--accent)' }}>{'{{data.renderedContent}}'}</code> — that is
                the path that replaces a relay function reformatting the note on the way through.
              </p>
              <p className="text-[13px]" style={{ color: 'var(--text-secondary)' }}>
                Each notification also carries{' '}
                <code style={{ color: 'var(--accent)' }}>X-Webhook-Delivery</code> and{' '}
                <code style={{ color: 'var(--accent)' }}>X-Webhook-Event</code>. Neither Teams nor
                Discord reads them, but a Power Automate flow in front of Teams can: chat platforms
                have no idempotency of their own, so if the same message is posted twice, the delivery
                id is what tells you whether InfraPilot sent it twice or the flow ran twice on one
                send. Compare it against <strong>Recent Deliveries</strong> on the notification — one
                delivery with one attempt means the duplicate came from the flow, not from here.
              </p>
              <pre
                className="rounded-lg p-4 text-[12px] leading-relaxed overflow-x-auto"
                style={{ backgroundColor: 'var(--bg-primary)', color: 'var(--text-primary)' }}
              >
{`# Templates are Handlebars over the delivery envelope:
{{eventType}}                     the event that fired
{{data.product}} {{data.service}} the event's own fields, camelCase
{{data.renderedContent}}          release notes, already rendered
{{#if data.failureReason}}...{{/if}}
{{#each data.items}}- {{this.service}}{{/each}}

# A missing field renders empty rather than failing, so an
# optional value needs no guard unless the wording around it does.`}
              </pre>
              <p className="text-[13px]" style={{ color: 'var(--text-muted)' }}>
                The card and embed targets render only a markdown subset — bold, italics, links and
                bullet lists survive; tables do not, and Teams also drops headings. Teams (HTML) is
                the exception: the message is converted to HTML on the way out, so the full markdown
                vocabulary survives and literal HTML in a template passes through untouched. Over-long
                messages are trimmed to the platform limit rather than being rejected. Use the live
                preview in the create form to see the exact text and request body before saving.
              </p>
            </div>

            {/* Retry behaviour */}
            <div className="space-y-2">
              <h3 className="text-[13px] font-semibold" style={{ color: 'var(--text-primary)' }}>
                Retry Behaviour
              </h3>
              <p className="text-[13px]" style={{ color: 'var(--text-secondary)' }}>
                If your endpoint returns a non-2xx status code or the request times out (10s), delivery
                is retried with exponential backoff:
              </p>
              <div className="flex gap-2 flex-wrap">
                {['30s', '2 min', '10 min', '1 hour', '4 hours'].map((delay, i) => (
                  <span
                    key={i}
                    className="text-[12px] font-medium px-2.5 py-1 rounded-lg"
                    style={{ backgroundColor: 'var(--bg-primary)', color: 'var(--text-secondary)' }}
                  >
                    Attempt {i + 2}: +{delay}
                  </span>
                ))}
              </div>
              <p className="text-[13px]" style={{ color: 'var(--text-muted)' }}>
                After 5 failed attempts the delivery is marked as permanently failed. You can manually
                retry failed deliveries from the webhook detail page.
              </p>
            </div>

            {/* Best practices */}
            <div className="space-y-2">
              <h3 className="text-[13px] font-semibold" style={{ color: 'var(--text-primary)' }}>
                Best Practices
              </h3>
              <ul className="text-[13px] space-y-1.5 list-disc list-inside" style={{ color: 'var(--text-secondary)' }}>
                <li>
                  <strong>Always verify signatures</strong> — reject requests where the HMAC doesn't match.
                </li>
                <li>
                  <strong>Respond quickly</strong> — return 200 within a few seconds. Process heavy work asynchronously.
                </li>
                <li>
                  <strong>Use the delivery ID for idempotency</strong> — the same event may be delivered more than
                  once on retry. De-duplicate using{' '}
                  <code className="text-[12px]" style={{ color: 'var(--accent)' }}>X-Webhook-Delivery</code>.
                </li>
                <li>
                  <strong>Keep your secret safe</strong> — treat it like a password. Rotate it if compromised.
                </li>
              </ul>
            </div>
          </div>
        )}
      </div>
    </div>
  );
}
