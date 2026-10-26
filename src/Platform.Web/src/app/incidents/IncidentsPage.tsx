import { useCallback, useEffect, useState } from 'react';
import { Link } from 'react-router-dom';
import { ExternalLink, Pencil, Plus, Siren, Trash2 } from 'lucide-react';
import { api, type Incident } from '@/lib/api';
import { useDocumentTitle } from '@/lib/pageTitle';
import { useSettingsStore } from '@/stores/settingsStore';
import { EnvLabel } from '@/components/environments/EnvBadge';
import { ConfirmDialog } from '@/components/ui/ConfirmDialog';
import { ListEmptyState } from '@/components/ui/ListEmptyState';
import { HelpButton } from '@/components/guide/HelpButton';
import { inputClass, inputStyle } from '@/app/settings/formStyles';
import { IncidentForm } from './IncidentForm';

function fmtDuration(hours: number | null): string {
  if (hours == null) return 'open';
  if (hours < 1) return `${Math.round(hours * 60)}m`;
  if (hours < 48) return `${Math.round(hours * 10) / 10}h`;
  return `${(hours / 24).toFixed(1)}d`;
}

/**
 * Incidents — admin-only records behind change failure rate and time to restore. A list with the
 * few fields the metrics read and a link to the real incident, plus a form. Deliberately not an
 * incident tool: no timeline, no severity narrative, no responders. The portal holds the pointer.
 */
export function IncidentsPage() {
  useDocumentTitle(['Incidents']);
  const { activeEnvironments, getDisplayName } = useSettingsStore();
  const [products, setProducts] = useState<string[]>([]);
  const [items, setItems] = useState<Incident[]>([]);
  const [total, setTotal] = useState(0);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  const [product, setProduct] = useState('');
  const [environment, setEnvironment] = useState('');
  const [openOnly, setOpenOnly] = useState(false);

  const [creating, setCreating] = useState(false);
  const [editing, setEditing] = useState<Incident | null>(null);
  const [deleting, setDeleting] = useState<Incident | null>(null);
  const [deleteBusy, setDeleteBusy] = useState(false);

  useEffect(() => {
    api.getDeploymentProducts().then((l) => setProducts(l.map((p) => p.product))).catch(() => setProducts([]));
  }, []);

  const load = useCallback(async () => {
    try {
      const res = await api.listIncidents({
        product: product || undefined,
        environment: environment || undefined,
        open: openOnly ? true : undefined,
        limit: 200,
      });
      setItems(res.items);
      setTotal(res.total);
      setError(null);
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Failed to load incidents');
    } finally {
      setLoading(false);
    }
  }, [product, environment, openOnly]);

  useEffect(() => { void load(); }, [load]);

  const confirmDelete = async () => {
    if (!deleting) return;
    setDeleteBusy(true);
    try {
      await api.deleteIncident(deleting.id);
      setDeleting(null);
      await load();
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Failed to delete the incident');
    } finally {
      setDeleteBusy(false);
    }
  };

  const th = 'text-left font-medium px-2 py-1.5 whitespace-nowrap';
  const td = 'px-2 py-2 whitespace-nowrap';

  return (
    <div className="space-y-4">
      <div className="flex flex-wrap items-center gap-3">
        <div className="flex items-center gap-2 mr-auto">
          <Siren size={20} style={{ color: 'var(--danger)' }} />
          <div>
            <h1 className="text-xl font-semibold flex items-center gap-2">
              Incidents
              <HelpButton page="Incidents" size="sm" context={{ product: product || 'all', openOnly }} />
            </h1>
            <p className="text-[12px]" style={{ color: 'var(--text-muted)' }}>
              Production incidents as the <Link to="/analytics/delivery" className="underline">Delivery metrics</Link> need them — change failure rate and time to restore.
            </p>
          </div>
        </div>
        <select value={product} onChange={(e) => setProduct(e.target.value)} className={inputClass} style={inputStyle} aria-label="Product">
          <option value="">All products</option>
          {products.map((p) => <option key={p} value={p}>{p}</option>)}
        </select>
        <select value={environment} onChange={(e) => setEnvironment(e.target.value)} className={inputClass} style={inputStyle} aria-label="Environment">
          <option value="">All environments</option>
          {activeEnvironments.map((e) => <option key={e.key} value={e.key}>{e.displayName}</option>)}
        </select>
        <label className="inline-flex items-center gap-1.5 text-[13px]" style={{ color: 'var(--text-secondary)' }}>
          <input type="checkbox" checked={openOnly} onChange={(e) => setOpenOnly(e.target.checked)} /> open only
        </label>
        <button
          onClick={() => { setEditing(null); setCreating((v) => !v); }}
          className="inline-flex items-center gap-1.5 px-3 py-1.5 rounded-lg text-[13px] font-medium text-white"
          style={{ backgroundColor: 'var(--accent)' }}
        >
          <Plus size={14} /> Record incident
        </button>
      </div>

      {creating && (
        <IncidentForm
          products={products}
          initial={{ product: product || undefined, environment: environment || undefined }}
          onSaved={() => { setCreating(false); void load(); }}
          onCancel={() => setCreating(false)}
        />
      )}
      {editing && (
        <IncidentForm
          existing={editing}
          products={products}
          onSaved={() => { setEditing(null); void load(); }}
          onCancel={() => setEditing(null)}
        />
      )}

      {error && (
        <div className="px-4 py-3 rounded-xl border text-[13px]" style={{ borderColor: 'var(--danger)', backgroundColor: 'var(--danger-bg)', color: 'var(--danger)' }}>
          {error}
        </div>
      )}

      {loading ? (
        <div className="skeleton h-40" />
      ) : items.length === 0 ? (
        <ListEmptyState
          icon={Siren}
          title="No incidents recorded"
          body="Record one here, or from a deployment's page — that pre-fills the service, environment and version, which is what ties the incident to the deploy that caused it."
          filters={[
            ...(product ? [{ label: 'Product', value: product, onClear: () => setProduct('') }] : []),
            ...(environment ? [{ label: 'Environment', value: getDisplayName(environment), onClear: () => setEnvironment('') }] : []),
            ...(openOnly ? [{ label: 'State', value: 'open only', onClear: () => setOpenOnly(false) }] : []),
          ]}
          onClearFilters={() => { setProduct(''); setEnvironment(''); setOpenOnly(false); }}
        />
      ) : (
        <section className="rounded-xl border p-4" style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-primary)' }}>
          <p className="text-[12px] mb-2" style={{ color: 'var(--text-muted)' }}>{total} {total === 1 ? 'incident' : 'incidents'}</p>
          <div className="overflow-x-auto -mx-2">
            <table className="w-full text-[12px]">
              <thead style={{ color: 'var(--text-muted)' }}>
                <tr>
                  <th className={th}>Started</th>
                  <th className={th}>Service</th>
                  <th className={th}>Environment</th>
                  <th className={th}>Restored after</th>
                  <th className={th}>Version</th>
                  <th className={th}>Caused by</th>
                  <th className={th}>Source</th>
                  <th className={th}></th>
                </tr>
              </thead>
              <tbody>
                {items.map((i) => (
                  <tr key={i.id} className="border-t" style={{ borderColor: 'var(--border-color)' }}>
                    <td className={td} title={new Date(i.startedAt).toLocaleString()}>{new Date(i.startedAt).toLocaleString()}</td>
                    <td className={td}>
                      <Link to={`/deployments/${encodeURIComponent(i.product)}/${encodeURIComponent(i.service)}`} className="hover:underline">
                        <span style={{ color: 'var(--text-muted)' }}>{i.product} · </span>{i.service}
                      </Link>
                    </td>
                    <td className={td}><EnvLabel env={i.environment} /></td>
                    <td className={td} style={{ color: i.resolvedAt ? undefined : 'var(--danger)' }}>{fmtDuration(i.durationHours)}</td>
                    <td className={`${td} font-mono`}>{i.version ?? '—'}</td>
                    <td className={td}>
                      {i.causedByDeployEventId
                        ? <Link to={`/deployments/events/${i.causedByDeployEventId}`} className="underline">deploy</Link>
                        : i.version ? <span style={{ color: 'var(--text-muted)' }}>by version</span> : <span style={{ color: 'var(--text-muted)' }}>inferred</span>}
                    </td>
                    <td className={td}>
                      {i.url ? (
                        <a href={i.url} target="_blank" rel="noreferrer" className="inline-flex items-center gap-1 underline">
                          {i.source}{i.externalId ? ` ${i.externalId}` : ''} <ExternalLink size={11} />
                        </a>
                      ) : (
                        <span>{i.source}{i.externalId ? ` ${i.externalId}` : ''}</span>
                      )}
                    </td>
                    <td className={`${td} text-right`}>
                      <button onClick={() => { setCreating(false); setEditing(i); }} className="p-1 rounded hover:opacity-70" aria-label="Edit incident" style={{ color: 'var(--text-secondary)' }}><Pencil size={13} /></button>
                      <button onClick={() => setDeleting(i)} className="p-1 rounded hover:opacity-70" aria-label="Delete incident" style={{ color: 'var(--danger)' }}><Trash2 size={13} /></button>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </section>
      )}

      {deleting && (
        <ConfirmDialog
          title="Delete this incident?"
          body={<>The record for <b>{deleting.service}</b> on <b>{getDisplayName(deleting.environment)}</b> started {new Date(deleting.startedAt).toLocaleString()} is removed from the metrics. The incident itself, in its own system, is not touched.</>}
          confirmLabel="Delete"
          confirmTone="danger"
          busy={deleteBusy}
          onConfirm={confirmDelete}
          onCancel={() => setDeleting(null)}
        />
      )}
    </div>
  );
}
