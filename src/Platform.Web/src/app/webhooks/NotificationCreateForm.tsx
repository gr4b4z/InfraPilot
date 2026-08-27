import { useState, useEffect, useMemo, useCallback } from 'react';
import { X, ChevronDown, ChevronRight, RotateCcw, Eye } from 'lucide-react';
import { api } from '@/lib/api';
import {
  AVAILABLE_EVENTS,
  defaultMessageTemplate,
  type NotificationTargetType,
} from './webhookTargets';
import { WebhookFilterFields } from './WebhookFilterFields';
import { EMPTY_FILTERS, toFilterInput } from './webhookFilters';
import type { WebhookFilters } from '@/lib/types';

/**
 * Creating a chat notification, as opposed to a machine-facing webhook. The two forms are separate
 * on purpose: almost nothing they ask for overlaps. A webhook needs a secret, a signature header and
 * an event contract; a notification needs a channel URL and the words to post. Folding both into one
 * form meant most fields were inert whichever target you picked.
 */

const INPUT_CLASS =
  'w-full px-3 py-2 rounded-lg border text-[13px] outline-none transition-colors focus:border-[var(--accent)]';

const INPUT_STYLE = {
  borderColor: 'var(--border-color)',
  backgroundColor: 'var(--bg-primary)',
  color: 'var(--text-primary)',
} as const;

const PLATFORMS: {
  value: NotificationTargetType;
  label: string;
  description: string;
  urlPlaceholder: string;
  urlHint: string;
}[] = [
  {
    value: 'msteams',
    label: 'Microsoft Teams',
    description: 'Posts an Adaptive Card into the channel.',
    urlPlaceholder: 'https://prod-00.westeurope.logic.azure.com:443/workflows/...',
    urlHint:
      'In Teams: channel → ⋯ → Workflows → "Post to a channel when a webhook request is received", then copy the generated URL. Legacy Office 365 connector URLs (webhook.office.com) still work and are detected automatically.',
  },
  {
    value: 'msteams_html',
    label: 'Microsoft Teams (HTML)',
    description: 'Posts HTML to a flow that forwards it. Reads as an ordinary message.',
    urlPlaceholder: 'https://prod-00.westeurope.logic.azure.com:443/workflows/...',
    urlHint:
      'For a Power Automate flow whose action is "Post message in a chat or channel" rather than "Post card" — it takes the HTML itself, so the body is posted raw rather than wrapped in a card. Pick this if you already have such a flow, or if you want tables and headings, which Adaptive Cards drop.',
  },
  {
    value: 'discord',
    label: 'Discord',
    description: 'Posts a message or embed into the channel.',
    urlPlaceholder: 'https://discord.com/api/webhooks/{id}/{token}',
    urlHint:
      'In Discord: Server Settings → Integrations → Webhooks → New Webhook, pick the channel, then Copy Webhook URL.',
  },
];

export interface NotificationCreateFormProps {
  onCancel: () => void;
  onCreated: () => void;
}

export function NotificationCreateForm({ onCancel, onCreated }: NotificationCreateFormProps) {
  const [targetType, setTargetType] = useState<NotificationTargetType>('msteams');
  const [name, setName] = useState('');
  const [url, setUrl] = useState('');
  const [selectedEvents, setSelectedEvents] = useState<string[]>([]);
  const [filters, setFilters] = useState<WebhookFilters>(EMPTY_FILTERS);
  // Null means "still following the prefill": until the operator types, changing the selected
  // events keeps the templates in step with what they picked, and after that the prefill stops
  // fighting them for the cursor. Held as an override rather than synced into state so the boxes
  // stay a pure function of the selection.
  const [titleOverride, setTitleOverride] = useState<string | null>(null);
  const [bodyOverride, setBodyOverride] = useState<string | null>(null);
  const [showPayload, setShowPayload] = useState(false);
  const [creating, setCreating] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const platform = PLATFORMS.find((p) => p.value === targetType)!;

  // Templates are per-notification, not per-event, so the preview has to name which event it is
  // showing. The first selected event is the one most likely being written for.
  const [previewEvent, setPreviewEvent] = useState('ping');
  const effectivePreviewEvent = selectedEvents.includes(previewEvent)
    ? previewEvent
    : (selectedEvents[0] ?? 'ping');

  const prefill = useMemo(
    () => defaultMessageTemplate(selectedEvents[0] ?? 'ping'),
    [selectedEvents]
  );

  const messageTitle = titleOverride ?? prefill.title;
  const messageTemplate = bodyOverride ?? prefill.body;

  const toggleEvent = (event: string) => {
    setSelectedEvents((prev) =>
      prev.includes(event) ? prev.filter((e) => e !== event) : [...prev, event]
    );
  };

  const resetTemplates = () => {
    setTitleOverride(null);
    setBodyOverride(null);
  };

  // ── Live preview ────────────────────────────────────────────────────────
  // Rendered by the API against a sample payload, using the same renderer a real delivery uses, so
  // the preview cannot drift from what actually gets posted.
  const [preview, setPreview] = useState<{
    title: string;
    text: string;
    samplePayload: string;
    requestBody: string;
    contentType: string;
  } | null>(null);
  const [previewError, setPreviewError] = useState<string | null>(null);

  const requestPreview = useCallback(async () => {
    try {
      const result = await api.previewNotificationMessage({
        targetType,
        eventType: effectivePreviewEvent,
        messageTemplate,
        messageTitle,
        url,
      });
      setPreview(result);
      setPreviewError(null);
    } catch (e) {
      setPreview(null);
      setPreviewError(e instanceof Error ? e.message : 'Preview failed');
    }
  }, [targetType, effectivePreviewEvent, messageTemplate, messageTitle, url]);

  useEffect(() => {
    // Debounced so typing a template does not fire a request per keystroke.
    const handle = setTimeout(requestPreview, 300);
    return () => clearTimeout(handle);
  }, [requestPreview]);

  const canCreate = name.trim() !== '' && url.trim() !== '' && selectedEvents.length > 0;

  const handleCreate = async () => {
    if (!canCreate) return;
    setCreating(true);
    setError(null);
    try {
      await api.createWebhook({
        name: name.trim(),
        url: url.trim(),
        events: selectedEvents,
        filters: toFilterInput(filters),
        targetType,
        // Always sent, even when untouched: storing the resolved template is what lets an operator
        // see and edit exactly what gets posted instead of an invisible server-side default.
        messageTemplate,
        messageTitle,
      });
      onCreated();
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Failed to create notification');
    } finally {
      setCreating(false);
    }
  };

  return (
    <div
      className="rounded-xl border p-5 space-y-4"
      style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-secondary)' }}
    >
      <div className="flex items-center justify-between">
        <div>
          <h2 className="text-[14px] font-semibold" style={{ color: 'var(--text-primary)' }}>
            New Notification
          </h2>
          <p className="text-[12px] mt-0.5" style={{ color: 'var(--text-muted)' }}>
            Posts a rendered message straight into a chat channel — no relay function in between.
          </p>
        </div>
        <button onClick={onCancel} style={{ color: 'var(--text-muted)' }} aria-label="Close">
          <X size={16} />
        </button>
      </div>

      {error && (
        <div
          className="px-3 py-2 rounded-lg text-[13px]"
          style={{ backgroundColor: 'var(--error-bg)', color: 'var(--error)' }}
        >
          {error}
        </div>
      )}

      {/* Platform */}
      <div className="space-y-1.5">
        <label className="text-[12px] font-medium" style={{ color: 'var(--text-secondary)' }}>
          Platform
        </label>
        <div className="grid grid-cols-1 sm:grid-cols-2 gap-2">
          {PLATFORMS.map((p) => (
            <button
              key={p.value}
              onClick={() => setTargetType(p.value)}
              className="text-left px-3 py-2.5 rounded-lg transition-all"
              style={{
                backgroundColor: targetType === p.value ? 'var(--accent-muted)' : 'var(--bg-primary)',
                border:
                  targetType === p.value ? '1px solid var(--accent)' : '1px solid var(--border-color)',
              }}
            >
              <div
                className="text-[13px] font-medium"
                style={{ color: targetType === p.value ? 'var(--accent)' : 'var(--text-primary)' }}
              >
                {p.label}
              </div>
              <div className="text-[11px] mt-0.5 leading-snug" style={{ color: 'var(--text-muted)' }}>
                {p.description}
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
            placeholder="e.g. Platform releases channel"
            className={INPUT_CLASS}
            style={INPUT_STYLE}
          />
        </div>
        <div className="space-y-1.5">
          <label className="text-[12px] font-medium" style={{ color: 'var(--text-secondary)' }}>
            Webhook URL
          </label>
          <input
            type="text"
            value={url}
            onChange={(e) => setUrl(e.target.value)}
            placeholder={platform.urlPlaceholder}
            className={INPUT_CLASS}
            style={INPUT_STYLE}
          />
          <p className="text-[11px] leading-snug" style={{ color: 'var(--text-muted)' }}>
            {platform.urlHint}
          </p>
          <p className="text-[11px] leading-snug" style={{ color: 'var(--warning)' }}>
            Treat this URL as a secret — anyone holding it can post to the channel. There is no
            separate secret to configure.
          </p>
        </div>
      </div>

      {/* Events */}
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
                backgroundColor: selectedEvents.includes(event)
                  ? 'var(--accent-muted)'
                  : 'var(--bg-primary)',
                color: selectedEvents.includes(event) ? 'var(--accent)' : 'var(--text-muted)',
                border: selectedEvents.includes(event)
                  ? '1px solid var(--accent)'
                  : '1px solid var(--border-color)',
              }}
            >
              {event}
            </button>
          ))}
        </div>
        {selectedEvents.length > 1 && (
          <p className="text-[11px]" style={{ color: 'var(--text-muted)' }}>
            One template covers every selected event, so reference fields all of them carry —
            <code> {'{{eventType}}'} </code> is always available. For per-event wording, create a
            notification per event.
          </p>
        )}
      </div>

      {/* Message template */}
      <div
        className="space-y-3 pt-3 border-t"
        style={{ borderColor: 'var(--border-color)' }}
      >
        <div className="flex items-center justify-between">
          <label className="text-[12px] font-medium" style={{ color: 'var(--text-secondary)' }}>
            Message
          </label>
          <button
            onClick={resetTemplates}
            className="inline-flex items-center gap-1 text-[11px] font-medium px-2 py-1 rounded-md transition-colors hover:opacity-80"
            style={{ color: 'var(--text-muted)' }}
            title="Restore the default template for the previewed event"
          >
            <RotateCcw size={11} /> Reset to default
          </button>
        </div>

        <div className="space-y-1.5">
          <label className="text-[11px]" style={{ color: 'var(--text-muted)' }}>
            Heading — leave empty to post without one
          </label>
          <input
            type="text"
            value={messageTitle}
            onChange={(e) => setTitleOverride(e.target.value)}
            className={INPUT_CLASS}
            style={INPUT_STYLE}
          />
        </div>

        <div className="space-y-1.5">
          <label className="text-[11px]" style={{ color: 'var(--text-muted)' }}>
            Body — Handlebars over the event payload: <code>{'{{eventType}}'}</code>,{' '}
            <code>{'{{data.product}}'}</code>, <code>{'{{#each data.items}}'}</code>. Release notes
            arrive already rendered as <code>{'{{data.renderedContent}}'}</code>.
          </label>
          <textarea
            value={messageTemplate}
            onChange={(e) => setBodyOverride(e.target.value)}
            rows={8}
            spellCheck={false}
            className={`${INPUT_CLASS} font-mono text-[12px] leading-relaxed resize-y`}
            style={INPUT_STYLE}
          />
        </div>
      </div>

      {/* Preview */}
      <div className="space-y-2">
        <div className="flex items-center gap-2">
          <Eye size={13} style={{ color: 'var(--accent)' }} />
          <span className="text-[12px] font-medium" style={{ color: 'var(--text-secondary)' }}>
            Preview
          </span>
          <select
            value={effectivePreviewEvent}
            onChange={(e) => setPreviewEvent(e.target.value)}
            className="text-[12px] px-2 py-1 rounded-md border outline-none"
            style={INPUT_STYLE}
          >
            {(selectedEvents.length > 0 ? selectedEvents : ['ping']).map((event) => (
              <option key={event} value={event}>
                {event}
              </option>
            ))}
          </select>
        </div>

        {previewError ? (
          <div
            className="px-3 py-2 rounded-lg text-[12px]"
            style={{ backgroundColor: 'var(--error-bg)', color: 'var(--error)' }}
          >
            {previewError}
          </div>
        ) : (
          <div
            className="rounded-lg p-3 space-y-1"
            style={{ backgroundColor: 'var(--bg-primary)', border: '1px solid var(--border-color)' }}
          >
            {preview?.title && (
              <div className="text-[13px] font-semibold" style={{ color: 'var(--text-primary)' }}>
                {preview.title}
              </div>
            )}
            <div
              className="text-[13px] whitespace-pre-wrap break-words"
              style={{ color: 'var(--text-secondary)' }}
            >
              {preview?.text || '—'}
            </div>
          </div>
        )}

        <p className="text-[11px] leading-snug" style={{ color: 'var(--text-muted)' }}>
          {targetType === 'msteams_html'
            ? 'Shown as the raw text that gets posted. It is converted to HTML on the way out, so the full markdown vocabulary survives — tables and headings included. Literal HTML in the template is passed through untouched.'
            : 'Shown as the raw text that gets posted. Both platforms render a markdown subset — bold, italics, links and bullet lists survive; tables do not, and Teams also drops headings.'}
        </p>

        {preview && (
          <div>
            <button
              onClick={() => setShowPayload(!showPayload)}
              className="inline-flex items-center gap-1 text-[11px] font-medium"
              style={{ color: 'var(--text-muted)' }}
            >
              {showPayload ? <ChevronDown size={12} /> : <ChevronRight size={12} />}
              Sample payload and request body
            </button>
            {showPayload && (
              <div className="mt-2 grid grid-cols-1 lg:grid-cols-2 gap-2">
                <div className="space-y-1">
                  <span className="text-[11px]" style={{ color: 'var(--text-muted)' }}>
                    Sample payload the template renders against
                  </span>
                  <pre
                    className="p-2 rounded-lg text-[11px] overflow-auto max-h-56"
                    style={{ backgroundColor: 'var(--bg-primary)', color: 'var(--text-secondary)' }}
                  >
                    {formatJson(preview.samplePayload)}
                  </pre>
                </div>
                <div className="space-y-1">
                  <span className="text-[11px]" style={{ color: 'var(--text-muted)' }}>
                    Body sent to {platform.label}{' '}
                    <code style={{ color: 'var(--text-muted)' }}>
                      ({preview.contentType.split(';')[0]})
                    </code>
                  </span>
                  <pre
                    className="p-2 rounded-lg text-[11px] overflow-auto max-h-56"
                    style={{ backgroundColor: 'var(--bg-primary)', color: 'var(--text-secondary)' }}
                  >
                    {formatJson(preview.requestBody)}
                  </pre>
                </div>
              </div>
            )}
          </div>
        )}
      </div>

      {/* Filters */}
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
          onClick={onCancel}
          className="text-[13px] font-medium px-3 py-2 rounded-lg transition-colors hover:opacity-80"
          style={{ color: 'var(--text-muted)' }}
        >
          Cancel
        </button>
      </div>
    </div>
  );
}

function formatJson(value: string): string {
  try {
    return JSON.stringify(JSON.parse(value), null, 2);
  } catch {
    return value;
  }
}
