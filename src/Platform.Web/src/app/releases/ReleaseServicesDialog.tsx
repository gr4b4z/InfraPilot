import { useMemo, useState } from 'react';
import { Loader2, Search, X } from 'lucide-react';
import { api } from '@/lib/api';
import type { Release, ReleaseProgress, ReleaseService } from '@/lib/api';
import { Dialog } from '@/components/ui/Dialog';
import { EnvLabel } from '@/components/environments/EnvBadge';
import { inputClass, inputStyle } from '@/app/settings/formStyles';

const keyOf = (s: Pick<ReleaseService, 'product' | 'service'>) => `${s.product}/${s.service}`;

/**
 * Which services ship with the release. Every service of the release's products is in it until a
 * release manager unticks it here — a checklist like Mass approve's, so the call is made seeing each
 * service's staging build, production promotion and what production runs. Unticked services stay on
 * the release, listed as not shipping; the checks stop counting them. Read-only for everyone else.
 */
export function ReleaseServicesDialog({
  release,
  progress,
  onClose,
  onSaved,
}: {
  release: Release;
  progress: ReleaseProgress;
  onClose: () => void;
  onSaved: (saved: Release) => void;
}) {
  const canEdit = release.canManage;
  const [shipping, setShipping] = useState<Set<string>>(
    () => new Set(progress.services.filter((s) => !s.excluded).map(keyOf)),
  );
  const [query, setQuery] = useState('');
  const [onlyOffLine, setOnlyOffLine] = useState(false);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const q = query.trim().toLowerCase();
  const visible = progress.services.filter((s) =>
    (!q || s.service.toLowerCase().includes(q) || s.product.toLowerCase().includes(q))
    && (!onlyOffLine || !s.onLine));
  const byProduct = useMemo(() => {
    const groups = new Map<string, ReleaseService[]>();
    for (const s of visible) groups.set(s.product, [...(groups.get(s.product) ?? []), s]);
    return [...groups.entries()];
  }, [visible]);

  const toggle = (keys: string[], on: boolean) =>
    setShipping((prev) => {
      const next = new Set(prev);
      keys.forEach((k) => (on ? next.add(k) : next.delete(k)));
      return next;
    });

  const total = progress.services.length;
  const count = progress.services.filter((s) => shipping.has(keyOf(s))).length;
  const changed = progress.services.some((s) => shipping.has(keyOf(s)) === s.excluded);

  const save = async () => {
    setSaving(true);
    setError(null);
    try {
      const excluded = progress.services.filter((s) => !shipping.has(keyOf(s))).map(keyOf);
      onSaved(await api.setReleaseServices(release.id, excluded));
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Could not save the services');
      setSaving(false);
    }
  };

  const th = 'text-left font-medium px-2 py-1.5 whitespace-nowrap';
  const td = 'px-2 py-1.5 whitespace-nowrap';
  const muted = { color: 'var(--text-muted)' };

  return (
    <Dialog onClose={onClose} ariaLabel="Services in the release" width={880}>
      <div className="flex items-center justify-between px-5 py-3 border-b" style={{ borderColor: 'var(--border-color)' }}>
        <div>
          <h2 className="text-[15px] font-semibold">Services in {release.title}</h2>
          <p className="text-[12px]" style={muted}>
            Every service of {release.products.join(', ')} ships with the release.{' '}
            {canEdit ? 'Untick the ones that don’t.' : 'Release managers choose which ones don’t.'}
          </p>
        </div>
        <button onClick={onClose} className="p-1 rounded hover:bg-[var(--bg-secondary)]" aria-label="Close">
          <X size={16} />
        </button>
      </div>

      <div className="px-5 py-3 flex items-center gap-3 flex-wrap border-b" style={{ borderColor: 'var(--border-color)' }}>
        <span className="relative flex items-center">
          <Search size={13} className="absolute left-2.5" style={muted} />
          <input value={query} onChange={(e) => setQuery(e.target.value)} placeholder="Filter services" aria-label="Filter services"
            className={`${inputClass} pl-7 w-56`} style={inputStyle} />
        </span>
        <label className="inline-flex items-center gap-1.5 text-[12px]" style={{ color: 'var(--text-secondary)' }}>
          <input type="checkbox" checked={onlyOffLine} onChange={(e) => setOnlyOffLine(e.target.checked)} />
          Only services without a {release.version} build
        </label>
        {canEdit && (
          <span className="ml-auto inline-flex gap-2 text-[12px]">
            <button className="hover:underline" style={{ color: 'var(--accent)' }} onClick={() => toggle(visible.map(keyOf), true)}>Select all</button>
            <button className="hover:underline" style={{ color: 'var(--accent)' }} onClick={() => toggle(visible.map(keyOf), false)}>Select none</button>
          </span>
        )}
      </div>

      <div className="max-h-[60vh] overflow-y-auto px-3 py-2">
        {byProduct.length === 0 ? (
          <p className="text-[13px] px-2 py-4" style={{ color: 'var(--text-secondary)' }}>No service matches.</p>
        ) : byProduct.map(([product, rows]) => {
          const keys = rows.map(keyOf);
          const all = keys.every((k) => shipping.has(k));
          const some = keys.some((k) => shipping.has(k));
          return (
            <table key={product} className="w-full text-[12px] mb-3">
              <thead style={muted}>
                <tr className="border-b" style={{ borderColor: 'var(--border-color)' }}>
                  <th className={`${th} w-8`}>
                    <input
                      type="checkbox"
                      checked={all}
                      ref={(el) => { if (el) el.indeterminate = some && !all; }}
                      disabled={!canEdit}
                      onChange={(e) => toggle(keys, e.target.checked)}
                      aria-label={`All of ${product}`}
                    />
                  </th>
                  <th className={th}>{product} <span className="font-normal">({rows.length})</span></th>
                  <th className={th}><EnvLabel env={release.stagingEnvironment} /></th>
                  <th className={th}>Promotion</th>
                  <th className={th}><EnvLabel env={release.productionEnvironment} /></th>
                </tr>
              </thead>
              <tbody>
                {rows.map((s) => {
                  const k = keyOf(s);
                  const on = shipping.has(k);
                  return (
                    <tr
                      key={k}
                      className={`border-b ${canEdit ? 'cursor-pointer hover:bg-[var(--bg-secondary)]' : ''}`}
                      style={{ borderColor: 'var(--border-color)', opacity: on ? 1 : 0.55 }}
                      onClick={() => canEdit && toggle([k], !on)}
                    >
                      <td className={td}>
                        <input type="checkbox" checked={on} disabled={!canEdit} onChange={(e) => toggle([k], e.target.checked)}
                          onClick={(e) => e.stopPropagation()} aria-label={`${s.service} ships`} />
                      </td>
                      <td className={td}>
                        <span style={{ color: 'var(--text-primary)' }}>{s.service}</span>
                      </td>
                      <td className={`${td} font-mono`} style={s.staging ? undefined : muted}>{s.staging?.version ?? '—'}</td>
                      <td className={td} style={s.promotion ? undefined : muted}>
                        {s.promotion ? <><span className="font-mono">{s.promotion.version}</span> <span style={muted}>{s.promotion.status}</span></> : '—'}
                      </td>
                      <td className={`${td} font-mono`} style={s.production ? undefined : muted}>
                        {s.production ? <>{s.production.version}{s.production.status !== 'succeeded' && <span style={{ color: 'var(--danger)' }}> {s.production.status}</span>}</> : '—'}
                      </td>
                    </tr>
                  );
                })}
              </tbody>
            </table>
          );
        })}
      </div>

      <div className="flex items-center justify-between gap-3 px-5 py-3 border-t" style={{ borderColor: 'var(--border-color)' }}>
        <span className="text-[12px]" style={{ color: 'var(--text-secondary)' }}>
          {count} of {total} {total === 1 ? 'service ships' : 'services ship'}
        </span>
        <div className="flex items-center gap-2">
          {error && <span className="text-[12px]" style={{ color: 'var(--danger)' }}>{error}</span>}
          <button onClick={onClose} className="px-3 py-1.5 rounded-lg text-[13px]" style={{ border: '1px solid var(--border-color)', color: 'var(--text-secondary)' }}>
            {canEdit ? 'Cancel' : 'Close'}
          </button>
          {canEdit && (
            <button
              onClick={() => void save()}
              disabled={saving || !changed}
              className="inline-flex items-center gap-1.5 px-3 py-1.5 rounded-lg text-[13px] font-medium text-white disabled:opacity-50"
              style={{ backgroundColor: 'var(--accent)' }}
            >
              {saving && <Loader2 size={13} className="animate-spin" />} Save
            </button>
          )}
        </div>
      </div>
    </Dialog>
  );
}
