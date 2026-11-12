import { useEffect, useState } from 'react';
import { Link, useNavigate } from 'react-router-dom';
import { formatDistanceToNow } from 'date-fns';
import { CalendarDays, Milestone, Plus } from 'lucide-react';
import { api } from '@/lib/api';
import type { ReleaseManagers, ReleaseSummary } from '@/lib/api';
import { useDocumentTitle } from '@/lib/pageTitle';
import { useEntityRefresh } from '@/hooks/useEntityEvents';
import { ListEmptyState } from '@/components/ui/ListEmptyState';
import { describeManagers, formatTargetDate, releasePath, releaseStatusStyle } from '@/components/releases/releaseStatus';
import { ReleaseForm } from './ReleaseForm';

/**
 * Releases — the versioned release trains (5.10, 6.0) from version cut to production sign-off.
 * The ones under way come first as cards, each with how far its checklist has got and the step it
 * is on; finished and cancelled ones follow as a table. Everyone can follow them; admins and the
 * release managers (Settings → Releases) open and run them, and the page says who those are.
 */
export function ReleasesPage() {
  useDocumentTitle(['Releases']);
  const navigate = useNavigate();
  const [items, setItems] = useState<ReleaseSummary[]>([]);
  // The API decides who may open a release; the page only mirrors it.
  const [canCreate, setCanCreate] = useState(false);
  const [managers, setManagers] = useState<ReleaseManagers | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [creating, setCreating] = useState(false);
  const tick = useEntityRefresh(['release']);

  useEffect(() => {
    let cancelled = false;
    api
      .listReleases({ limit: 200 })
      .then((res) => { if (!cancelled) { setItems(res.items); setCanCreate(res.canCreate); setError(null); } })
      .catch((e) => { if (!cancelled) setError(e instanceof Error ? e.message : 'Failed to load releases'); })
      .finally(() => { if (!cancelled) setLoading(false); });
    return () => { cancelled = true; };
  }, [tick]);

  useEffect(() => {
    api.getReleaseManagers().then(setManagers).catch(() => setManagers(null));
  }, [tick]);

  const managerText = managers ? describeManagers(managers.gates) : '';

  const active = items.filter((r) => r.status === 'planned' || r.status === 'in_progress');
  const past = items.filter((r) => r.status === 'completed' || r.status === 'cancelled');

  const th = 'text-left font-medium px-2 py-1.5 whitespace-nowrap';
  const td = 'px-2 py-2 whitespace-nowrap';

  return (
    <div className="space-y-4">
      <div className="flex items-start justify-between gap-3 flex-wrap">
        <div className="flex items-center gap-2">
          <Milestone size={20} style={{ color: 'var(--accent)' }} />
          <div>
            <h1 className="text-xl font-semibold">Releases</h1>
            <p className="text-[12px]" style={{ color: 'var(--text-muted)' }}>
              Versioned releases from the version cut to the production sign-off. Each step is moved on by hand;
              the portal checks the builds, promotions, deployments and migrations as they happen.
            </p>
            {managers && (
              <p className="text-[12px] mt-0.5" style={{ color: 'var(--text-secondary)' }}>
                {managerText
                  ? <>Run by admins and the release managers, the approvers of “{managers.requirement}” on the production promotion gates — {managerText}.</>
                  : <>No production promotion gate has a “{managers.requirement}” requirement, so only admins run releases.</>}
                {managers.canEdit && (
                  <> <Link to="/settings/releases" className="underline" style={{ color: 'var(--accent)' }}>Change in Settings → Releases</Link></>
                )}
              </p>
            )}
          </div>
        </div>
        {canCreate && (
          <button
            onClick={() => setCreating(true)}
            data-guide-anchor="releases-new"
            className="inline-flex items-center gap-1.5 px-3 py-1.5 rounded-lg text-[13px] font-medium text-white"
            style={{ backgroundColor: 'var(--accent)' }}
          >
            <Plus size={14} /> New release
          </button>
        )}
      </div>

      {error && (
        <div className="px-4 py-3 rounded-xl border text-[13px]" style={{ borderColor: 'var(--danger)', backgroundColor: 'var(--danger-bg)', color: 'var(--danger)' }}>
          {error}
        </div>
      )}

      {loading ? (
        <div className="skeleton h-40" />
      ) : items.length === 0 ? (
        <ListEmptyState
          icon={Milestone}
          title="No releases yet"
          body={canCreate
            ? 'Open a release with New release when its version line is about to be cut, and follow it here to production.'
            : 'Releases appear here once a release manager opens one.'}
        />
      ) : (
        <>
          {active.length > 0 && (
            <div className="grid gap-3 grid-cols-1 lg:grid-cols-2">
              {active.map((r) => <ActiveReleaseCard key={r.id} release={r} />)}
            </div>
          )}

          {past.length > 0 && (
            <section className="rounded-xl border p-4" style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-primary)' }}>
              <h2 className="text-[13px] font-semibold mb-2">Past releases</h2>
              <div className="overflow-x-auto -mx-2">
                <table className="w-full text-[12px]">
                  <thead style={{ color: 'var(--text-muted)' }}>
                    <tr>
                      <th className={th}>Release</th>
                      <th className={th}>Status</th>
                      <th className={th}>Products</th>
                      <th className={th}>Started</th>
                      <th className={th}>Released</th>
                      <th className={th}>Release manager</th>
                    </tr>
                  </thead>
                  <tbody>
                    {past.map((r) => {
                      const st = releaseStatusStyle(r.status);
                      return (
                        <tr
                          key={r.id}
                          className="border-t cursor-pointer transition-colors hover:bg-[var(--bg-secondary)]"
                          style={{ borderColor: 'var(--border-color)' }}
                          onClick={() => navigate(releasePath(r.id))}
                        >
                          <td className={td}>
                            <Link to={releasePath(r.id)} className="hover:underline font-medium" onClick={(e) => e.stopPropagation()}>
                              {r.title}
                            </Link>
                          </td>
                          <td className={td}><span className="badge" style={{ backgroundColor: st.bg, color: st.fg }}>{st.label}</span></td>
                          <td className={td} style={{ color: 'var(--text-secondary)' }}>{r.products.join(', ')}</td>
                          <td className={td} style={{ color: 'var(--text-secondary)' }}>
                            {r.startedAt ? new Date(r.startedAt).toLocaleDateString() : '—'}
                          </td>
                          <td className={td} style={{ color: 'var(--text-secondary)' }}>
                            {r.completedAt ? new Date(r.completedAt).toLocaleString() : r.cancelledAt ? `Cancelled ${new Date(r.cancelledAt).toLocaleDateString()}` : '—'}
                          </td>
                          <td className={td} style={{ color: 'var(--text-secondary)' }}>{r.releaseManagerName ?? r.releaseManager ?? '—'}</td>
                        </tr>
                      );
                    })}
                  </tbody>
                </table>
              </div>
            </section>
          )}
        </>
      )}

      {creating && (
        <ReleaseForm
          onClose={() => setCreating(false)}
          onSaved={(saved) => { setCreating(false); navigate(releasePath(saved.id)); }}
        />
      )}
    </div>
  );
}

function ActiveReleaseCard({ release: r }: { release: ReleaseSummary }) {
  const st = releaseStatusStyle(r.status);
  const pct = r.stepsTotal === 0 ? 0 : Math.round((r.stepsClosed / r.stepsTotal) * 100);
  const target = formatTargetDate(r.targetDate);
  return (
    <Link
      to={releasePath(r.id)}
      className="block rounded-xl border p-4 transition-colors hover:border-[var(--accent)]"
      style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-primary)' }}
    >
      <div className="flex items-start justify-between gap-2">
        <div>
          <div className="flex items-center gap-2">
            <span className="text-[15px] font-semibold">{r.title}</span>
            <span className="badge" style={{ backgroundColor: st.bg, color: st.fg }}>{st.label}</span>
          </div>
          <p className="text-[12px] mt-0.5" style={{ color: 'var(--text-muted)' }}>{r.products.join(' · ')}</p>
        </div>
        {target && (
          <span className="inline-flex items-center gap-1 text-[12px] whitespace-nowrap" style={{ color: 'var(--text-secondary)' }} title="Release date">
            <CalendarDays size={13} /> {target}
          </span>
        )}
      </div>
      <div className="mt-3">
        <div className="h-1.5 rounded-full overflow-hidden" style={{ backgroundColor: 'var(--bg-secondary)' }}>
          <div className="h-full rounded-full" style={{ width: `${pct}%`, backgroundColor: 'var(--accent)' }} />
        </div>
        <div className="flex justify-between mt-1.5 text-[12px]" style={{ color: 'var(--text-secondary)' }}>
          <span>{r.currentStep ? <>Now: <b style={{ color: 'var(--text-primary)' }}>{r.currentStep}</b></> : 'Every step closed'}</span>
          <span>{r.stepsClosed} of {r.stepsTotal} steps</span>
        </div>
      </div>
      <p className="text-[11px] mt-2" style={{ color: 'var(--text-muted)' }}>
        {r.releaseManagerName ?? r.releaseManager ?? 'No release manager'} · updated {formatDistanceToNow(new Date(r.updatedAt), { addSuffix: true })}
      </p>
    </Link>
  );
}
