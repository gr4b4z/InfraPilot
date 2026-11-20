/**
 * PROTOTYPE — small building blocks shared by the service inventory views.
 */
import { Link, useLocation, useNavigate } from 'react-router-dom';
import { AlertTriangle, CircleHelp, Clock, FlaskConical } from 'lucide-react';
import { ago, useScenario } from './inventoryHooks';
import {
  PROTOTYPE_NOW,
  SCENARIOS,
  STALE_AFTER_MIN,
  currencies,
  formatAmount,
  type FeedKey,
  type FeedState,
  type Money,
  type Scenario,
} from './inventoryData';
import './prototype.css';

/**
 * The dashed strip at the top of every prototype view: says the data is static and lets a reviewer
 * flip between the states. Not part of the proposed UI.
 */
export function PrototypeBar({ note }: { note?: string }) {
  const scenario = useScenario();
  const navigate = useNavigate();
  const { pathname, search } = useLocation();
  const set = (key: Scenario) => {
    const p = new URLSearchParams(search);
    if (key === 'ok') p.delete('inv');
    else p.set('inv', key);
    const q = p.toString();
    navigate(`${pathname}${q ? `?${q}` : ''}`, { replace: true });
  };
  return (
    <div
      className="rounded-lg px-3 py-2 flex flex-wrap items-center gap-x-3 gap-y-1.5 text-[12px]"
      style={{ border: '1px dashed var(--warning)', backgroundColor: 'var(--warning-bg)', color: 'var(--text-secondary)' }}
    >
      <span className="inline-flex items-center gap-1.5 font-semibold" style={{ color: 'var(--warning)' }}>
        <FlaskConical size={13} /> Prototype
      </span>
      <span>Static fixtures from the artifact examples{note ? ` · ${note}` : ''}. Not part of the design.</span>
      <span className="flex-1" />
      <span className="inline-flex flex-wrap gap-1" role="group" aria-label="Prototype state">
        {SCENARIOS.map((s) => (
          <button
            key={s.key}
            type="button"
            onClick={() => set(s.key)}
            aria-pressed={scenario === s.key}
            className="px-2 py-0.5 rounded-full text-[11px] font-medium border"
            style={
              scenario === s.key
                ? { backgroundColor: 'var(--accent)', color: 'var(--accent-fg)', borderColor: 'var(--accent)' }
                : { borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-primary)', color: 'var(--text-secondary)' }
            }
          >
            {s.label}
          </button>
        ))}
      </span>
    </div>
  );
}

/** Marks numbers that are derived for the prototype rather than taken from a real artifact. */
export function SampleTag({ title = 'Sample data derived from the November example, not real cost' }: { title?: string }) {
  return (
    <span
      title={title}
      className="inline-flex items-center px-1.5 py-0.5 rounded text-[9px] font-semibold uppercase tracking-wide leading-none"
      style={{ border: '1px dashed var(--text-muted)', color: 'var(--text-muted)' }}
    >
      Sample
    </span>
  );
}

function isStale(f: FeedState) {
  return (PROTOTYPE_NOW.getTime() - new Date(f.refreshedAt).getTime()) / 60_000 > STALE_AFTER_MIN[f.feed];
}

/**
 * How old the data on this view is. One quiet line when every feed it reads is fresh; a warning
 * banner naming the feed, the age of the copy still shown and why the refresh failed when one is
 * not. The copy is never hidden — failure only changes how it is labelled.
 */
export function Freshness({ feeds, only }: { feeds: FeedState[]; only: FeedKey[] }) {
  const shown = feeds.filter((f) => only.includes(f.feed));
  const troubled = shown.filter((f) => f.error || isStale(f));
  return (
    <div className="space-y-2">
      {troubled.map((f) => (
        <div
          key={f.feed}
          role="status"
          className="rounded-lg border px-3 py-2 flex items-start gap-2 text-[12px]"
          style={{ borderColor: 'color-mix(in srgb, var(--warning) 40%, transparent)', backgroundColor: 'var(--warning-bg)' }}
        >
          <AlertTriangle size={14} className="mt-0.5 shrink-0" style={{ color: 'var(--warning)' }} />
          <div style={{ color: 'var(--text-secondary)' }}>
            <span className="font-semibold" style={{ color: 'var(--text-primary)' }}>
              {f.label} data is out of date
            </span>{' '}
            — showing the copy from {ago(f.refreshedAt)} ({f.version}).
            {f.error && (
              <>
                {' '}Last refresh {ago(f.attemptedAt)}: {f.error}
              </>
            )}
          </div>
        </div>
      ))}
      <p className="inline-flex flex-wrap items-center gap-x-3 gap-y-1 text-[11px]" style={{ color: 'var(--text-muted)' }}>
        <Clock size={11} />
        {shown.map((f) => (
          <span
            key={f.feed}
            title={`Refreshed ${new Date(f.refreshedAt).toUTCString()} · checked every ${f.pollEvery}`}
            style={{ color: f.error || isStale(f) ? 'var(--warning)' : undefined }}
          >
            {f.label} {f.feed === 'catalog' ? `${f.version} · ` : ''}
            {ago(f.refreshedAt)}
          </span>
        ))}
      </p>
    </div>
  );
}

/**
 * An amount per currency, one line each. Currencies are never added together; an empty object is
 * zero and is printed as such in the view's currency (`zeroIn`), or as "—" when it has none.
 */
export function MoneyLines({ money, zeroIn, compact, className }: {
  money: Money;
  zeroIn?: string[];
  compact?: boolean;
  className?: string;
}) {
  const curs = currencies(money, Object.fromEntries((zeroIn ?? []).map((c) => [c, 0])));
  if (curs.length === 0) return <span className={className} style={{ color: 'var(--text-muted)' }}>—</span>;
  return (
    <span className={`inline-flex flex-col items-end tabular-nums ${className ?? ''}`}>
      {curs.map((c) => (
        <span key={c}>{formatAmount(money?.[c] ?? 0, c, compact)}</span>
      ))}
    </span>
  );
}

/**
 * Observed states. "Unknown" and "unresolved" mean nobody could look — grey with a question mark,
 * never red, so they can't be read as a failure.
 */
const STATE_STYLE: Record<string, { label: string; fg: string; bg: string; hint: string }> = {
  present: { label: 'Present', fg: 'var(--success)', bg: 'var(--success-bg)', hint: 'Found in the last Azure scan.' },
  linked: { label: 'Linked', fg: 'var(--success)', bg: 'var(--success-bg)', hint: 'Found and owned by this service.' },
  ok: { label: 'Found', fg: 'var(--success)', bg: 'var(--success-bg)', hint: 'Resolved to one resource.' },
  done: { label: 'Done', fg: 'var(--success)', bg: 'var(--success-bg)', hint: '' },
  absent: { label: 'Not found', fg: 'var(--warning)', bg: 'var(--warning-bg)', hint: 'Declared in the catalog, but the scan did not find it.' },
  ambiguous: { label: 'Ambiguous', fg: 'var(--warning)', bg: 'var(--warning-bg)', hint: 'More than one resource matches.' },
  pending: { label: 'Pending', fg: 'var(--warning)', bg: 'var(--warning-bg)', hint: '' },
  unowned: { label: 'No owner', fg: 'var(--warning)', bg: 'var(--warning-bg)', hint: 'No service claims ownership.' },
  multiple_owners: { label: 'Several owners', fg: 'var(--warning)', bg: 'var(--warning-bg)', hint: '' },
  unresolved: { label: 'Not observed', fg: 'var(--text-muted)', bg: 'var(--bg-secondary)', hint: 'The server was not visible to the scan, so nothing is known.' },
  unknown: { label: 'Not observed', fg: 'var(--text-muted)', bg: 'var(--bg-secondary)', hint: 'Not checked yet.' },
};

export function StateChip({ state, detail }: { state: string; detail?: string }) {
  const s = STATE_STYLE[state] ?? STATE_STYLE.unknown;
  const unknown = state === 'unknown' || state === 'unresolved';
  return (
    <span
      title={detail ?? s.hint}
      className="inline-flex items-center gap-1 px-1.5 py-0.5 rounded text-[10px] font-semibold uppercase tracking-wide leading-none whitespace-nowrap"
      style={{ backgroundColor: s.bg, color: s.fg, border: unknown ? '1px dashed var(--border-strong)' : undefined }}
    >
      {unknown ? <CircleHelp size={10} /> : <span className="inline-block w-1.5 h-1.5 rounded-full" style={{ backgroundColor: s.fg }} />}
      {s.label}
    </span>
  );
}

export function SectionCard({ title, aside, children, className }: {
  title: string;
  aside?: React.ReactNode;
  children: React.ReactNode;
  className?: string;
}) {
  return (
    <div
      className={`rounded-xl border p-3.5 flex flex-col gap-2.5 min-w-0 ${className ?? ''}`}
      style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-primary)' }}
    >
      <div className="flex items-center gap-2">
        <h3 className="text-[12px] font-semibold uppercase tracking-wide" style={{ color: 'var(--text-muted)' }}>
          {title}
        </h3>
        <span className="flex-1" />
        {aside}
      </div>
      {children}
    </div>
  );
}

export function InventoryLink({ to, children }: { to: string; children: React.ReactNode }) {
  const { search } = useLocation();
  const inv = new URLSearchParams(search).get('inv');
  const join = to.includes('?') ? '&' : '?';
  return (
    <Link to={inv ? `${to}${join}inv=${inv}` : to} className="hover:underline" style={{ color: 'var(--accent)' }}>
      {children}
    </Link>
  );
}
