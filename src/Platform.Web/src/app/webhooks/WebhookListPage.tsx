import { useState, useEffect, useCallback } from 'react';
import { Link } from 'react-router-dom';
import { api } from '@/lib/api';
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
} from 'lucide-react';
import { formatDistanceToNow } from 'date-fns';

const AVAILABLE_EVENTS = [
  'deployment.created',
  'request.status_changed',
  'approval.created',
  'approval.approved',
  'approval.rejected',
  'approval.changesrequested',
  'promotion.approved',
  'promotion.rejected',
  'promotion.deployed',
  'ping',
];

export function WebhookListPage() {
  const [webhooks, setWebhooks] = useState<WebhookSubscription[]>([]);
  const [loading, setLoading] = useState(true);
  const [showCreate, setShowCreate] = useState(false);
  const [createdSecret, setCreatedSecret] = useState<string | null>(null);
  const [secretCopied, setSecretCopied] = useState(false);
  const [error, setError] = useState<string | null>(null);

  // Create form state
  const [name, setName] = useState('');
  const [url, setUrl] = useState('');
  const [selectedEvents, setSelectedEvents] = useState<string[]>([]);
  const [filterProduct, setFilterProduct] = useState('');
  const [filterEnv, setFilterEnv] = useState('');
  const [creating, setCreating] = useState(false);
  const [showGuide, setShowGuide] = useState(false);

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

  useEffect(() => {
    fetchWebhooks();
  }, [fetchWebhooks]);

  const handleCreate = async () => {
    if (!name.trim() || !url.trim() || selectedEvents.length === 0) return;
    setCreating(true);
    setError(null);
    try {
      const filters =
        filterProduct || filterEnv
          ? { product: filterProduct || undefined, environment: filterEnv || undefined }
          : undefined;
      const result = await api.createWebhook({ name, url, events: selectedEvents, filters });
      setCreatedSecret(result.secret ?? null);
      if (result.secret) setShowGuide(true);
      setShowCreate(false);
      setName('');
      setUrl('');
      setSelectedEvents([]);
      setFilterProduct('');
      setFilterEnv('');
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
      setTimeout(fetchWebhooks, 2000);
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
        <button
          onClick={() => setShowCreate(true)}
          className="inline-flex items-center gap-1.5 text-[13px] font-medium px-4 py-2 rounded-lg text-white transition-colors hover:opacity-90"
          style={{ backgroundColor: 'var(--accent)' }}
        >
          <Plus size={15} />
          New Webhook
        </button>
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

          <div className="grid grid-cols-2 gap-4">
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
          </div>

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

          <div className="grid grid-cols-2 gap-4">
            <div className="space-y-1.5">
              <label className="text-[12px] font-medium" style={{ color: 'var(--text-secondary)' }}>
                Filter: Product <span style={{ color: 'var(--text-muted)' }}>(optional)</span>
              </label>
              <input
                type="text"
                value={filterProduct}
                onChange={(e) => setFilterProduct(e.target.value)}
                placeholder="e.g. billing-platform"
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
                Filter: Environment <span style={{ color: 'var(--text-muted)' }}>(optional)</span>
              </label>
              <input
                type="text"
                value={filterEnv}
                onChange={(e) => setFilterEnv(e.target.value)}
                placeholder="e.g. production"
                className="w-full px-3 py-2 rounded-lg border text-[13px] outline-none transition-colors focus:border-[var(--accent)]"
                style={{
                  borderColor: 'var(--border-color)',
                  backgroundColor: 'var(--bg-primary)',
                  color: 'var(--text-primary)',
                }}
              />
            </div>
          </div>

          <div className="flex items-center gap-3 pt-2 border-t" style={{ borderColor: 'var(--border-color)' }}>
            <button
              onClick={handleCreate}
              disabled={creating || !name.trim() || !url.trim() || selectedEvents.length === 0}
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
                    <div className="text-[12px] mt-0.5 flex items-center gap-1" style={{ color: 'var(--text-muted)' }}>
                      <ExternalLink size={11} />
                      <span className="truncate max-w-[250px]">{wh.url}</span>
                    </div>
                    {(wh.filters.product || wh.filters.environment) && (
                      <div className="flex gap-1.5 mt-1">
                        {wh.filters.product && (
                          <span className="text-[11px] px-1.5 py-0.5 rounded" style={{ backgroundColor: 'var(--bg-primary)', color: 'var(--text-muted)' }}>
                            {wh.filters.product}
                          </span>
                        )}
                        {wh.filters.environment && (
                          <span className="text-[11px] px-1.5 py-0.5 rounded" style={{ backgroundColor: 'var(--bg-primary)', color: 'var(--text-muted)' }}>
                            {wh.filters.environment}
                          </span>
                        )}
                      </div>
                    )}
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
            {/* Headers */}
            <div className="pt-4 space-y-2">
              <h3 className="text-[13px] font-semibold" style={{ color: 'var(--text-primary)' }}>
                Request Headers
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
                Verifying Signatures
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
