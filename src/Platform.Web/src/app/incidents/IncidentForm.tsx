import { useState } from 'react';
import { Siren } from 'lucide-react';
import { api, type Incident, type IncidentInput } from '@/lib/api';
import { useSettingsStore } from '@/stores/settingsStore';
import { inputClass, inputStyle, labelClass, labelStyle } from '@/app/settings/formStyles';

/** ISO → the value a `datetime-local` input wants (local wall clock, minute precision). */
function toLocalInput(iso: string | null | undefined): string {
  if (!iso) return '';
  const d = new Date(iso);
  const pad = (n: number) => String(n).padStart(2, '0');
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}T${pad(d.getHours())}:${pad(d.getMinutes())}`;
}

function fromLocalInput(value: string): string | null {
  return value ? new Date(value).toISOString() : null;
}

/**
 * Record or edit an incident — the same form on the Incidents page and on a deploy's page, where
 * it arrives pre-filled with the deploy's product, service, environment and version (`locked`
 * keeps those read-only, because the point of recording from the deploy is the attribution).
 * Only the pointer fields: the incident system keeps the narrative, the portal keeps a link.
 */
export function IncidentForm({
  initial,
  existing,
  locked = false,
  products = [],
  onSaved,
  onCancel,
}: {
  initial?: Partial<IncidentInput>;
  /** When set, the form edits this incident instead of creating one. */
  existing?: Incident;
  locked?: boolean;
  products?: string[];
  onSaved: (incident: Incident) => void;
  onCancel: () => void;
}) {
  const { activeEnvironments, getDisplayName } = useSettingsStore();
  const seed = existing ?? initial ?? {};
  const [product, setProduct] = useState(seed.product ?? '');
  const [service, setService] = useState(seed.service ?? '');
  const [environment, setEnvironment] = useState(seed.environment ?? '');
  const [startedAt, setStartedAt] = useState(toLocalInput(seed.startedAt ?? new Date().toISOString()));
  const [resolvedAt, setResolvedAt] = useState(toLocalInput(seed.resolvedAt));
  const [version, setVersion] = useState(seed.version ?? '');
  const [url, setUrl] = useState(seed.url ?? '');
  const [source, setSource] = useState(seed.source ?? 'manual');
  const [externalId, setExternalId] = useState(seed.externalId ?? '');
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const envOptions = Array.from(new Set([...activeEnvironments.map((e) => e.key), ...(environment ? [environment] : [])]));
  const canSubmit = product.trim() && service.trim() && environment.trim() && startedAt && !saving;

  const submit = async () => {
    setSaving(true);
    setError(null);
    const payload: IncidentInput = {
      product: product.trim(),
      service: service.trim(),
      environment: environment.trim(),
      startedAt: fromLocalInput(startedAt)!,
      resolvedAt: fromLocalInput(resolvedAt),
      version: version.trim() || null,
      causedByDeployEventId: seed.causedByDeployEventId ?? null,
      source: source.trim() || 'manual',
      externalId: externalId.trim() || null,
      url: url.trim() || null,
    };
    try {
      const saved = existing ? await api.updateIncident(existing.id, payload) : await api.createIncident(payload);
      onSaved(saved);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to save the incident');
    } finally {
      setSaving(false);
    }
  };

  const field = `${inputClass} w-full mt-1`;

  return (
    <div className="rounded-xl border p-4 space-y-3" style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-primary)' }}>
      <div className="flex items-center gap-2">
        <Siren size={15} style={{ color: 'var(--danger)' }} />
        <h3 className="text-[13px] font-semibold">{existing ? 'Edit incident' : 'Record incident'}</h3>
      </div>
      <p className="text-[12px]" style={{ color: 'var(--text-muted)' }}>
        Only what the metrics need: where, when it started, when service was restored, and a link to the
        real incident. The version, when you know it, ties the incident to the deployment that caused it.
      </p>
      <div className="grid grid-cols-1 sm:grid-cols-3 gap-3">
        <label className="block">
          <span className={labelClass} style={labelStyle}>Product</span>
          {locked || products.length === 0 ? (
            <input value={product} onChange={(e) => setProduct(e.target.value)} readOnly={locked} className={field} style={inputStyle} list="incident-products" />
          ) : (
            <select value={product} onChange={(e) => setProduct(e.target.value)} className={field} style={inputStyle}>
              <option value="">Pick a product</option>
              {products.map((p) => <option key={p} value={p}>{p}</option>)}
            </select>
          )}
        </label>
        <label className="block">
          <span className={labelClass} style={labelStyle}>Service</span>
          <input value={service} onChange={(e) => setService(e.target.value)} readOnly={locked} className={field} style={inputStyle} placeholder="orders" />
        </label>
        <label className="block">
          <span className={labelClass} style={labelStyle}>Environment</span>
          {locked ? (
            <input value={getDisplayName(environment)} readOnly className={field} style={inputStyle} />
          ) : (
            <select value={environment} onChange={(e) => setEnvironment(e.target.value)} className={field} style={inputStyle}>
              <option value="">Pick an environment</option>
              {envOptions.map((k) => <option key={k} value={k}>{getDisplayName(k)}</option>)}
            </select>
          )}
        </label>
        <label className="block">
          <span className={labelClass} style={labelStyle}>Started</span>
          <input type="datetime-local" value={startedAt} onChange={(e) => setStartedAt(e.target.value)} className={field} style={inputStyle} />
        </label>
        <label className="block">
          <span className={labelClass} style={labelStyle}>Restored <span className="normal-case font-normal">(leave empty while open)</span></span>
          <input type="datetime-local" value={resolvedAt} onChange={(e) => setResolvedAt(e.target.value)} className={field} style={inputStyle} />
        </label>
        <label className="block">
          <span className={labelClass} style={labelStyle}>Version that was running</span>
          <input value={version} onChange={(e) => setVersion(e.target.value)} className={`${field} font-mono`} style={inputStyle} placeholder="optional" />
        </label>
        <label className="block sm:col-span-2">
          <span className={labelClass} style={labelStyle}>Link to the incident</span>
          <input value={url} onChange={(e) => setUrl(e.target.value)} className={field} style={inputStyle} placeholder="https://… (optional)" />
        </label>
        <div className="grid grid-cols-2 gap-2">
          <label className="block">
            <span className={labelClass} style={labelStyle}>Source</span>
            <input value={source} onChange={(e) => setSource(e.target.value)} className={field} style={inputStyle} placeholder="manual" />
          </label>
          <label className="block">
            <span className={labelClass} style={labelStyle}>External id</span>
            <input value={externalId} onChange={(e) => setExternalId(e.target.value)} className={field} style={inputStyle} placeholder="INC-123" />
          </label>
        </div>
      </div>
      {error && <p className="text-[12px]" style={{ color: 'var(--danger)' }}>{error}</p>}
      <div className="flex gap-2 justify-end">
        <button onClick={onCancel} className="px-3 py-1.5 rounded-lg text-[13px]" style={{ color: 'var(--text-secondary)' }}>Cancel</button>
        <button
          onClick={submit}
          disabled={!canSubmit}
          className="px-3 py-1.5 rounded-lg text-[13px] font-medium text-white disabled:opacity-50"
          style={{ backgroundColor: 'var(--accent)' }}
        >
          {saving ? 'Saving…' : existing ? 'Save changes' : 'Record incident'}
        </button>
      </div>
    </div>
  );
}
