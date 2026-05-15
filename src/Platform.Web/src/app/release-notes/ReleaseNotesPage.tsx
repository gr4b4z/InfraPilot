import { useEffect, useMemo, useState } from 'react';
import { useNavigate, useParams } from 'react-router-dom';
import { Loader2, ScrollText, Eye } from 'lucide-react';
import { formatDistanceToNow } from 'date-fns';
import { api, type ReleaseNoteListItem } from '@/lib/api';
import { useDeploymentStore } from '@/stores/deploymentStore';

// `<input type="datetime-local">` consumes/emits "yyyy-MM-ddTHH:mm" in local time.
function toLocalInput(d: Date): string {
  const pad = (n: number) => String(n).padStart(2, '0');
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}T${pad(d.getHours())}:${pad(d.getMinutes())}`;
}

export function ReleaseNotesPage() {
  const { product = '' } = useParams<{ product: string }>();
  const navigate = useNavigate();
  const { products, fetchProducts } = useDeploymentStore();
  const [items, setItems] = useState<ReleaseNoteListItem[]>([]);
  const [environment, setEnvironment] = useState<string>('');
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [from, setFrom] = useState<string | null>(null);
  const [to, setTo] = useState<string | null>(null);

  useEffect(() => { fetchProducts(); }, [fetchProducts]);

  async function load() {
    setLoading(true);
    setError(null);
    try {
      const rows = await api.listReleaseNotes({ product, environment: environment || undefined });
      setItems(rows);
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setLoading(false);
    }
  }

  useEffect(() => {
    load();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [product, environment]);

  const productEntry = products.find((p) => p.product === product);
  const envsFromProduct = productEntry ? Object.keys(productEntry.environments) : [];
  const environments = Array.from(
    new Set([...envsFromProduct, ...items.map((i) => i.environment)])
  ).sort();

  const defaultWindow = useMemo(() => {
    const now = new Date();
    const lastForEnv = environment
      ? items.find((i) => i.environment === environment)
      : items[0];
    const start = lastForEnv ? new Date(lastForEnv.generatedAt) : new Date(now.getTime() - 24 * 60 * 60 * 1000);
    return { from: toLocalInput(start), to: toLocalInput(now) };
  }, [items, environment]);

  const fromValue = from ?? defaultWindow.from;
  const toValue = to ?? defaultWindow.to;

  function openDraft() {
    if (!environment) { setError('Pick an environment before previewing'); return; }
    const qs = new URLSearchParams({
      env: environment,
      from: new Date(fromValue).toISOString(),
      to: new Date(toValue).toISOString(),
    });
    navigate(`/release-notes/${product}/new?${qs.toString()}`);
  }

  return (
    <div className="space-y-6">
      <div>
        <h1 className="text-xl font-semibold tracking-tight" style={{ color: 'var(--text-primary)' }}>
          Release Notes — {product}
        </h1>
        <p className="text-sm mt-1" style={{ color: 'var(--text-muted)' }}>
          Generated release notes aggregated from deployment events
        </p>
      </div>

      <div className="rounded-xl border p-4 space-y-3" style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-secondary)' }}>
        <div className="text-[11px] uppercase tracking-wider" style={{ color: 'var(--text-muted)' }}>
          New release note
        </div>
        <div className="flex flex-wrap items-end gap-3">
          <div className="flex flex-col gap-1">
            <label className="text-[11px]" style={{ color: 'var(--text-muted)' }}>Environment</label>
            <select
              value={environment}
              onChange={(e) => setEnvironment(e.target.value)}
              className="px-2 py-1.5 rounded-md border text-[13px]"
              style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-primary)', color: 'var(--text-primary)' }}
            >
              <option value="">— select —</option>
              {environments.map((env) => <option key={env} value={env}>{env}</option>)}
            </select>
          </div>
          <div className="flex flex-col gap-1">
            <label className="text-[11px]" style={{ color: 'var(--text-muted)' }}>From (since last by default)</label>
            <input
              type="datetime-local"
              value={fromValue}
              onChange={(e) => { setFrom(e.target.value); if (to === null) setTo(defaultWindow.to); }}
              className="px-2 py-1.5 rounded-md border text-[13px]"
              style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-primary)', color: 'var(--text-primary)' }}
            />
          </div>
          <div className="flex flex-col gap-1">
            <label className="text-[11px]" style={{ color: 'var(--text-muted)' }}>To (now by default)</label>
            <input
              type="datetime-local"
              value={toValue}
              onChange={(e) => { setTo(e.target.value); if (from === null) setFrom(defaultWindow.from); }}
              className="px-2 py-1.5 rounded-md border text-[13px]"
              style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-primary)', color: 'var(--text-primary)' }}
            />
          </div>
          {(from !== null || to !== null) && (
            <button
              onClick={() => { setFrom(null); setTo(null); }}
              className="text-[12px] underline"
              style={{ color: 'var(--text-muted)' }}
              title="Reset to auto window (since last note → now)"
            >Reset</button>
          )}
          <div className="ml-auto">
            <button
              onClick={openDraft}
              disabled={!environment}
              className="inline-flex items-center gap-2 px-3 py-2 rounded-lg text-[13px] font-medium disabled:opacity-50"
              style={{ backgroundColor: 'var(--accent)', color: 'white' }}
            >
              <Eye size={14} />
              Preview
            </button>
          </div>
        </div>
      </div>

      {error && (
        <div className="px-3 py-2 rounded-lg text-[13px]" style={{ backgroundColor: 'var(--error-bg)', color: 'var(--error)' }}>
          {error}
        </div>
      )}

      {loading ? (
        <div className="flex items-center justify-center py-20">
          <Loader2 className="animate-spin" size={24} style={{ color: 'var(--text-muted)' }} />
        </div>
      ) : items.length === 0 ? (
        <div className="flex flex-col items-center justify-center py-20 text-center">
          <ScrollText size={40} style={{ color: 'var(--text-muted)' }} />
          <p className="mt-3 text-sm" style={{ color: 'var(--text-muted)' }}>No release notes yet</p>
        </div>
      ) : (
        <div className="rounded-xl border overflow-hidden" style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-secondary)' }}>
          <table className="w-full text-[13px]">
            <thead>
              <tr style={{ borderBottom: '1px solid var(--border-color)' }}>
                <th className="text-left px-4 py-3 font-medium" style={{ color: 'var(--text-muted)' }}>Generated</th>
                <th className="text-left px-4 py-3 font-medium" style={{ color: 'var(--text-muted)' }}>Environment</th>
                <th className="text-left px-4 py-3 font-medium" style={{ color: 'var(--text-muted)' }}>Window</th>
                <th className="text-left px-4 py-3 font-medium" style={{ color: 'var(--text-muted)' }}>Services</th>
                <th className="text-left px-4 py-3 font-medium" style={{ color: 'var(--text-muted)' }}>Status</th>
              </tr>
            </thead>
            <tbody>
              {items.map((row) => (
                <tr
                  key={row.id}
                  className="cursor-pointer transition-colors hover:opacity-80"
                  style={{ borderBottom: '1px solid var(--border-color)' }}
                  onClick={() => navigate(`/release-notes/${product}/${row.id}`)}
                >
                  <td className="px-4 py-3" style={{ color: 'var(--text-primary)' }}>
                    {formatDistanceToNow(new Date(row.generatedAt), { addSuffix: true })}
                  </td>
                  <td className="px-4 py-3" style={{ color: 'var(--text-secondary)' }}>{row.environment}</td>
                  <td className="px-4 py-3 text-[12px] font-mono" style={{ color: 'var(--text-muted)' }}>
                    {new Date(row.from).toLocaleDateString()} → {new Date(row.to).toLocaleDateString()}
                  </td>
                  <td className="px-4 py-3" style={{ color: 'var(--text-secondary)' }}>{row.servicesCount}</td>
                  <td className="px-4 py-3" style={{ color: 'var(--text-secondary)' }}>{row.status}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </div>
  );
}
