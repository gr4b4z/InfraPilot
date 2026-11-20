import { Link } from 'react-router-dom';
import { formatDistanceToNow } from 'date-fns';
import { AlertTriangle, CircleHelp, Clock } from 'lucide-react';
import type { Freshness as FreshnessInfo, Money } from './inventoryTypes';
import { FEED_LABEL, currencies, formatAmount, isOutOfDate } from './inventoryFormat';
import './service-inventory.css';

const ago = (iso: string) => formatDistanceToNow(new Date(iso), { addSuffix: true });

/** What the copy is, in words: "v5", "generated 12 minutes ago", "2026-11 to date". */
function versionText(f: FreshnessInfo): string | null {
  if (!f.version) return null;
  if (f.feed === 'catalog') return f.version.replace(/^catalog\s*/, '');
  if (f.feed === 'status') {
    const at = f.version.replace(/^generated\s*/, '');
    return Number.isNaN(Date.parse(at)) ? null : `generated ${ago(at)}`;
  }
  return f.version;
}

/**
 * How old the data on this view is. One quiet line while every feed it reads is fresh; a warning
 * that names the feed, the age of the copy still shown and why when one is not. The copy is never
 * hidden: a failed refresh only changes how it is labelled.
 */
export function Freshness({ feeds }: { feeds: (FreshnessInfo | null | undefined)[] }) {
  const shown = feeds.filter((f): f is FreshnessInfo => !!f);
  if (shown.length === 0) return null;
  const troubled = shown.filter((f) => isOutOfDate(f));
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
              {FEED_LABEL[f.feed]} data is out of date
            </span>
            {f.fetchedAt ? <> — showing the copy from {ago(f.fetchedAt)}.</> : <> — nothing has been read yet.</>}
            {f.lastError
              ? <> Last attempt{f.lastAttemptAt ? ` ${ago(f.lastAttemptAt)}` : ''}: {f.lastError}</>
              : f.refreshedAt && <> Last checked {ago(f.refreshedAt)}.</>}
          </div>
        </div>
      ))}
      <p className="inline-flex flex-wrap items-center gap-x-3 gap-y-1 text-[11px]" style={{ color: 'var(--text-muted)' }}>
        <Clock size={11} />
        {shown.map((f) => (
          <span
            key={f.feed}
            title={f.refreshedAt ? `Last checked ${new Date(f.refreshedAt).toLocaleString()}` : undefined}
            style={{ color: isOutOfDate(f) ? 'var(--warning)' : undefined }}
          >
            {FEED_LABEL[f.feed]}
            {versionText(f) ? ` ${versionText(f)}` : ''}
            {f.refreshedAt ? ` · checked ${ago(f.refreshedAt)}` : ' · not read yet'}
          </span>
        ))}
      </p>
    </div>
  );
}

/**
 * An amount per currency, one line each. Currencies are never added together. `—` when there is
 * no amount at all (a product without services), which is different from a zero.
 */
export function MoneyLines({ money, compact, className }: { money: Money | undefined; compact?: boolean; className?: string }) {
  const curs = currencies(money);
  if (curs.length === 0) return <span className={className} style={{ color: 'var(--text-muted)' }}>—</span>;
  return (
    <span className={`inline-flex flex-col items-end tabular-nums ${className ?? ''}`}>
      {curs.map((c) => (
        <span key={c}>{formatAmount(money![c], c, compact)}</span>
      ))}
    </span>
  );
}

/**
 * Observed states. "Unknown" and "unresolved" mean nobody could look: grey with a question mark,
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
  unresolved: { label: 'Not observed', fg: 'var(--text-muted)', bg: 'var(--bg-secondary)', hint: 'The server was not visible to the scan, so nothing is known.' },
  unknown: { label: 'Not observed', fg: 'var(--text-muted)', bg: 'var(--bg-secondary)', hint: 'Not checked yet.' },
};

export function StateChip({ state, detail }: { state: string | null | undefined; detail?: string | null }) {
  const key = state && STATE_STYLE[state] ? state : 'unknown';
  const s = STATE_STYLE[key];
  const unknown = key === 'unknown' || key === 'unresolved';
  return (
    <span
      title={detail || s.hint || undefined}
      className="inline-flex items-center gap-1 px-1.5 py-0.5 rounded text-[10px] font-semibold uppercase tracking-wide leading-none whitespace-nowrap"
      style={{ backgroundColor: s.bg, color: s.fg, border: unknown ? '1px dashed var(--border-strong)' : undefined }}
    >
      {unknown
        ? <CircleHelp size={10} />
        : <span className="inline-block w-1.5 h-1.5 rounded-full" style={{ backgroundColor: s.fg }} />}
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

/** A colour key for a series; hatched for the month to date, so "incomplete" is never colour alone. */
export function Swatch({ color, hatched }: { color: string; hatched?: boolean }) {
  return (
    <span
      aria-hidden
      className="inline-block w-2.5 h-2.5 rounded-sm shrink-0"
      style={
        hatched
          ? { border: `1.5px solid ${color}`, background: `repeating-linear-gradient(135deg, ${color} 0 2px, transparent 2px 4px)` }
          : { backgroundColor: color }
      }
    />
  );
}

export function InventoryLink({ to, children }: { to: string; children: React.ReactNode }) {
  return (
    <Link to={to} className="hover:underline" style={{ color: 'var(--accent)' }}>
      {children}
    </Link>
  );
}

export function MonthSelect({ months, value, onChange }: {
  months: { period: string; complete: boolean }[];
  value: string;
  onChange: (period: string) => void;
}) {
  return (
    <label className="flex flex-col gap-1 text-[11px]" style={{ color: 'var(--text-muted)' }}>
      Month
      <select
        value={value}
        onChange={(e) => onChange(e.target.value)}
        className="rounded-lg border px-2.5 py-1.5 text-[13px]"
        style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-primary)', color: 'var(--text-primary)' }}
      >
        {[...months].reverse().map((m) => (
          <option key={m.period} value={m.period}>
            {new Date(`${m.period}-01T00:00:00Z`).toLocaleDateString('en-US', { month: 'long', year: 'numeric', timeZone: 'UTC' })}
            {m.complete ? '' : ' (to date)'}
          </option>
        ))}
      </select>
    </label>
  );
}
