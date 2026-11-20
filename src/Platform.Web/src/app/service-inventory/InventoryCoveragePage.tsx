/**
 * PROTOTYPE — /inventory/coverage: how much of the month's cost is attributed to services, why the
 * rest is not, where the largest unattributed amounts sit, and coverage over time. Plus, for
 * admins, the state of the three feeds.
 */
import { useState } from 'react';
import { useLocation, useNavigate } from 'react-router-dom';
import { useDocumentTitle } from '@/lib/pageTitle';
import { useAuthStore } from '@/stores/authStore';
import {
  costMonths,
  currencies,
  feedStates,
  formatAmount,
  monthLabel,
  type CostMonth,
} from './inventoryData';
import { Freshness, PrototypeBar, SampleTag } from './InventoryUi';
import { ago, useScenario } from './inventoryHooks';
import { Swatch } from './ServiceInventorySection';

const pct = (part: number, whole: number) => (whole > 0 ? (part / whole) * 100 : 0);
const fmtPct = (v: number) => `${v < 10 && v > 0 ? v.toFixed(1) : Math.round(v)}%`;

/** The parts `totals.total` is made of, in the order the bar draws them. */
const PARTS = [
  { key: 'direct', label: 'Direct to a service', color: 'var(--series-direct)', attributed: true },
  { key: 'shared', label: 'Shared, split between services', color: 'var(--series-shared)', attributed: true },
  { key: 'shared_unsplit', label: 'Shared, no service bound', color: 'var(--series-shared)', hatched: true, attributed: false },
  { key: 'untagged', label: 'No service or shared tag', color: 'var(--border-strong)', attributed: false },
  { key: 'unknown_tag', label: 'Tag not in the catalog', color: 'var(--text-muted)', hatched: true, attributed: false },
] as const;

function partAmount(m: CostMonth, key: (typeof PARTS)[number]['key'], cur: string): number {
  if (key === 'untagged' || key === 'unknown_tag') return m.unallocated[key][cur] ?? 0;
  return m.totals[key][cur] ?? 0;
}

function coverage(m: CostMonth, cur: string) {
  const total = m.totals.total[cur] ?? 0;
  return pct((m.totals.direct[cur] ?? 0) + (m.totals.shared[cur] ?? 0), total);
}

export function InventoryCoveragePage() {
  const scenario = useScenario();
  const feeds = feedStates(scenario);
  const isAdmin = useAuthStore((s) => s.user?.isAdmin ?? false);
  const { search } = useLocation();
  const navigate = useNavigate();
  const params = new URLSearchParams(search);
  const month = costMonths.find((m) => m.period === params.get('month')) ?? costMonths[costMonths.length - 1];
  const curs = currencies(month.totals.total);

  useDocumentTitle(['Cost coverage']);

  return (
    <div className="space-y-5">
      <PrototypeBar note="3 earlier months are sample data; the unallocated list is cut to 3 rows in the example" />
      <div className="flex flex-wrap items-end gap-3">
        <div>
          <h1 className="text-xl font-semibold tracking-tight" style={{ color: 'var(--text-primary)' }}>Cost coverage</h1>
          <p className="text-sm mt-0.5" style={{ color: 'var(--text-muted)' }}>
            How much of the cloud cost can be traced to a service, and where the rest sits
          </p>
        </div>
        <span className="flex-1" />
        <label className="flex flex-col gap-1 text-[11px]" style={{ color: 'var(--text-muted)' }}>
          Month
          <select
            value={month.period}
            onChange={(e) => {
              const p = new URLSearchParams(search);
              p.set('month', e.target.value);
              navigate(`/inventory/coverage?${p.toString()}`);
            }}
            className="rounded-lg border px-2.5 py-1.5 text-[13px]"
            style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-primary)', color: 'var(--text-primary)' }}
          >
            {[...costMonths].reverse().map((m) => (
              <option key={m.period} value={m.period}>
                {monthLabel(m.period)}{m.complete ? '' : ' (to date)'}{m._sample ? ' — sample' : ''}
              </option>
            ))}
          </select>
        </label>
      </div>

      <Freshness feeds={feeds} only={['cost']} />
      <p className="flex items-center gap-2 text-[12px]" style={{ color: 'var(--text-muted)' }}>
        {monthLabel(month.period)} · {month.complete ? 'closed month' : 'to date'} · sources: {month.sources.map((s) => s.id).join(', ')}
        {month._sample && <SampleTag />}
      </p>

      {/* ── Share attributed, one block per currency ── */}
      {curs.map((cur) => {
        const total = month.totals.total[cur] ?? 0;
        const cov = coverage(month, cur);
        return (
          <section key={cur} className="rounded-xl border p-4 space-y-3" style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-primary)' }}>
            <div className="flex flex-wrap items-baseline gap-x-3 gap-y-1">
              <span className="text-3xl font-semibold tabular-nums">{fmtPct(cov)}</span>
              <span className="text-[13px]" style={{ color: 'var(--text-secondary)' }}>
                of {formatAmount(total, cur)} attributed to services
              </span>
              <span className="flex-1" />
              <span className="text-[11px]" style={{ color: 'var(--text-muted)' }}>{cur}</span>
            </div>
            {/* Proportion bar — every part named in the table below, so identity is never colour alone. */}
            <div className="flex h-3 w-full gap-[2px] rounded overflow-hidden" role="img" aria-label={`${fmtPct(cov)} attributed`}>
              {PARTS.map((p) => {
                const v = partAmount(month, p.key, cur);
                if (v <= 0) return null;
                return (
                  <span
                    key={p.key}
                    title={`${p.label}: ${formatAmount(v, cur)}`}
                    style={{
                      width: `${pct(v, total)}%`,
                      background: 'hatched' in p && p.hatched
                        ? `repeating-linear-gradient(135deg, ${p.color} 0 2px, transparent 2px 5px)`
                        : p.color,
                      border: 'hatched' in p && p.hatched ? `1px solid ${p.color}` : undefined,
                    }}
                  />
                );
              })}
            </div>
            <table className="w-full text-[13px] tabular-nums">
              <tbody>
                {PARTS.map((p) => {
                  const v = partAmount(month, p.key, cur);
                  return (
                    <tr key={p.key} className="border-t" style={{ borderColor: 'var(--border-color)', color: v ? undefined : 'var(--text-muted)' }}>
                      <td className="py-1.5">
                        <span className="inline-flex items-center gap-2">
                          <Swatch color={p.color} hatched={'hatched' in p && p.hatched} />
                          {p.label}
                          {!p.attributed && <span className="text-[10px] uppercase tracking-wide" style={{ color: 'var(--text-muted)' }}>not attributed</span>}
                        </span>
                      </td>
                      <td className="py-1.5 text-right">{formatAmount(v, cur)}</td>
                      <td className="py-1.5 text-right w-16" style={{ color: 'var(--text-muted)' }}>{fmtPct(pct(v, total))}</td>
                    </tr>
                  );
                })}
              </tbody>
            </table>
            {month.shared.some((s) => s.unsplit && (s.unsplit[cur] ?? 0) > 0) && (
              <p className="text-[12px]" style={{ color: 'var(--text-muted)' }}>
                Shared resources no service is bound to:{' '}
                {month.shared.filter((s) => (s.unsplit?.[cur] ?? 0) > 0).map((s) => s.id).join(', ')}
              </p>
            )}
          </section>
        );
      })}

      <div className="grid gap-4 lg:grid-cols-2">
        <LargestUnallocated month={month} />
        <CoverageTrend />
      </div>

      {/* ── Sources ── per-source totals as published. */}
      <section>
        <h2 className="text-sm font-semibold mb-2">By cost source</h2>
        <div className="rounded-xl border overflow-x-auto" style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-primary)' }}>
          <table className="w-full text-[13px] tabular-nums min-w-[520px]">
            <thead>
              <tr className="text-left text-[11px] uppercase tracking-wide" style={{ color: 'var(--text-muted)' }}>
                <th className="font-medium px-3 py-2">Source</th>
                <th className="font-medium px-3 py-2 text-right">Resources read</th>
                <th className="font-medium px-3 py-2 text-right">Total</th>
                <th className="font-medium px-3 py-2 text-right">Attributed</th>
              </tr>
            </thead>
            <tbody>
              {month.sources.flatMap((s) =>
                currencies(s.totals.total).map((cur) => {
                  const t = s.totals.total[cur] ?? 0;
                  const a = (s.totals.direct[cur] ?? 0) + (s.totals.shared[cur] ?? 0);
                  return (
                    <tr key={`${s.id}:${cur}`} className="border-t" style={{ borderColor: 'var(--border-color)' }}>
                      <td className="px-3 py-2 font-mono">{s.id}</td>
                      <td className="px-3 py-2 text-right">{s.rows} in {s.scopes} scope{s.scopes === 1 ? '' : 's'}</td>
                      <td className="px-3 py-2 text-right">{formatAmount(t, cur)}</td>
                      <td className="px-3 py-2 text-right">{fmtPct(pct(a, t))}</td>
                    </tr>
                  );
                }),
              )}
            </tbody>
          </table>
        </div>
      </section>

      {isAdmin && <FeedsPanel scenario={scenario} />}
    </div>
  );
}

function LargestUnallocated({ month }: { month: CostMonth }) {
  const [by, setBy] = useState<'group' | 'scope'>('group');
  const curs = currencies(month.totals.unallocated);
  const cur = curs[0] ?? 'USD';
  const rows = (() => {
    if (by === 'group') {
      return month.unallocated.by_scope
        .filter((r) => (r.cost[cur] ?? 0) > 0)
        .map((r) => ({ key: `${r.scope}/${r.group}`, title: r.group ?? '(no group)', sub: `${r.source} · ${r.scope}`, v: r.cost[cur] ?? 0 }));
    }
    const sums = new Map<string, { source: string; v: number }>();
    for (const r of month.unallocated.by_scope) {
      const e = sums.get(r.scope) ?? { source: r.source, v: 0 };
      e.v += r.cost[cur] ?? 0;
      sums.set(r.scope, e);
    }
    return [...sums].filter(([, e]) => e.v > 0).map(([scope, e]) => ({ key: scope, title: scope, sub: e.source, v: e.v }));
  })().sort((a, b) => b.v - a.v).slice(0, 10);
  const max = Math.max(1, ...rows.map((r) => r.v));

  return (
    <section className="rounded-xl border p-4" style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-primary)' }}>
      <div className="flex items-center gap-2 mb-3">
        <h2 className="text-[13px] font-semibold">Largest unattributed · {cur}</h2>
        <span className="flex-1" />
        <div className="inline-flex rounded-lg border text-[11px] overflow-hidden" style={{ borderColor: 'var(--border-color)' }}>
          {(['group', 'scope'] as const).map((k) => (
            <button
              key={k}
              type="button"
              onClick={() => setBy(k)}
              aria-pressed={by === k}
              className="px-2 py-1"
              style={by === k ? { backgroundColor: 'var(--accent-bg)', color: 'var(--accent)' } : { color: 'var(--text-muted)' }}
            >
              {k === 'group' ? 'Resource groups' : 'Subscriptions'}
            </button>
          ))}
        </div>
      </div>
      <ul className="space-y-2.5">
        {rows.map((r) => (
          <li key={r.key} className="text-[12px]">
            <div className="flex items-baseline gap-2">
              <span className="font-mono truncate min-w-0" style={{ color: 'var(--text-primary)' }} title={r.sub}>{r.title}</span>
              <span className="flex-1" />
              <span className="tabular-nums font-medium">{formatAmount(r.v, cur)}</span>
            </div>
            <div className="mt-1 h-1.5 rounded-full" style={{ backgroundColor: 'var(--bg-secondary)' }}>
              <div className="h-1.5 rounded-full" style={{ width: `${(r.v / max) * 100}%`, backgroundColor: 'var(--border-strong)' }} />
            </div>
            <span className="text-[10px] truncate block" style={{ color: 'var(--text-muted)' }}>{r.sub}</span>
          </li>
        ))}
      </ul>
    </section>
  );
}

function CoverageTrend() {
  const cur = 'USD';
  const H = 120;
  const bars = costMonths.filter((m) => (m.totals.total[cur] ?? 0) > 0);
  const [hover, setHover] = useState<number | null>(null);
  const h = hover != null ? bars[hover] : null;
  return (
    <section className="rounded-xl border p-4" style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-primary)' }}>
      <div className="flex flex-wrap items-baseline gap-2 mb-1">
        <h2 className="text-[13px] font-semibold">Coverage per month · {cur}</h2>
        <span className="flex-1" />
        <span className="text-[12px]" style={{ color: 'var(--text-muted)' }}>
          {h ? (
            <span className="inline-flex items-center gap-2">
              {monthLabel(h.period)}{h.complete ? '' : ' (to date)'} <b style={{ color: 'var(--text-primary)' }}>{fmtPct(coverage(h, cur))}</b>
              {h._sample && <SampleTag />}
            </span>
          ) : 'share attributed to services'}
        </span>
      </div>
      <svg width="100%" height={H} role="img" aria-label="Coverage per month" onMouseLeave={() => setHover(null)} style={{ display: 'block' }}>
        <defs>
          <pattern id="inv-hatch-cov" width="5" height="5" patternUnits="userSpaceOnUse" patternTransform="rotate(45)">
            <rect width="5" height="5" fill="var(--series-direct)" opacity="0.25" />
            <rect width="2" height="5" fill="var(--series-direct)" />
          </pattern>
        </defs>
        {[0, 50, 100].map((g) => (
          <g key={g}>
            <line x1="28" x2="100%" y1={H - 1 - (g / 100) * (H - 14)} y2={H - 1 - (g / 100) * (H - 14)} stroke="var(--border-color)" strokeDasharray={g ? '2 3' : undefined} />
            {g > 0 && (
              <text x="0" y={H + 2 - (g / 100) * (H - 14)} fontSize="9" fill="var(--text-muted)">{g}%</text>
            )}
          </g>
        ))}
        {bars.map((m, i) => {
          const slot = 100 / bars.length;
          const v = coverage(m, cur);
          const bh = (v / 100) * (H - 14);
          return (
            <g key={m.period}>
              <rect x={`${i * slot}%`} y={0} width={`${slot}%`} height={H} fill={hover === i ? 'var(--bg-secondary)' : 'transparent'} onMouseEnter={() => setHover(i)} />
              {bh > 0 ? (
                <rect
                  x={`${i * slot + slot * 0.25}%`} y={H - 1 - bh} width={`${slot * 0.5}%`} height={bh} rx={3}
                  fill={m.complete ? 'var(--series-direct)' : 'url(#inv-hatch-cov)'}
                  stroke={m.complete ? undefined : 'var(--series-direct)'} strokeDasharray={m.complete ? undefined : '3 2'}
                  pointerEvents="none"
                />
              ) : (
                <rect x={`${i * slot + slot * 0.25}%`} y={H - 3} width={`${slot * 0.5}%`} height={2} fill="var(--border-strong)" pointerEvents="none" />
              )}
              <text x={`${i * slot + slot / 2}%`} y={Math.max(11, H - 1 - bh - 5)} textAnchor="middle" fontSize="10" fill="var(--text-muted)" pointerEvents="none">
                {fmtPct(v)}
              </text>
            </g>
          );
        })}
      </svg>
      <div className="flex text-[10px] mt-1" style={{ color: 'var(--text-muted)' }}>
        {bars.map((m) => (
          <span key={m.period} className="flex-1 text-center">
            {monthLabel(m.period, true)}{!m.complete && ' · to date'}
            {m._sample && <span className="block">sample</span>}
          </span>
        ))}
      </div>
    </section>
  );
}

/** Admin-only: the feeds behind these pages, when each was last refreshed, and why an attempt failed. */
function FeedsPanel({ scenario }: { scenario: ReturnType<typeof useScenario> }) {
  const feeds = feedStates(scenario);
  return (
    <section>
      <h2 className="text-sm font-semibold mb-1">Feeds</h2>
      <p className="text-[12px] mb-2" style={{ color: 'var(--text-muted)' }}>
        Admins only. The portal keeps the last good copy of each feed; a failed refresh never removes data.
      </p>
      <div className="rounded-xl border overflow-x-auto" style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-primary)' }}>
        <table className="w-full text-[12px] min-w-[720px]">
          <thead>
            <tr className="text-left text-[11px] uppercase tracking-wide" style={{ color: 'var(--text-muted)' }}>
              <th className="font-medium px-3 py-2">Feed</th>
              <th className="font-medium px-3 py-2">Source</th>
              <th className="font-medium px-3 py-2">Stored copy</th>
              <th className="font-medium px-3 py-2">Last attempt</th>
            </tr>
          </thead>
          <tbody>
            {feeds.map((f) => (
              <tr key={f.feed} className="border-t align-top" style={{ borderColor: 'var(--border-color)' }}>
                <td className="px-3 py-2 font-medium">
                  {f.label}
                  <span className="block text-[11px] font-normal" style={{ color: 'var(--text-muted)' }}>every {f.pollEvery}</span>
                </td>
                <td className="px-3 py-2 font-mono break-all" style={{ color: 'var(--text-secondary)' }}>{f.source}</td>
                <td className="px-3 py-2">
                  {f.version}
                  <span className="block text-[11px]" style={{ color: 'var(--text-muted)' }}>fetched {ago(f.refreshedAt)}</span>
                </td>
                <td className="px-3 py-2">
                  {f.error ? (
                    <span style={{ color: 'var(--warning)' }}>Failed {ago(f.attemptedAt)}</span>
                  ) : (
                    <span style={{ color: 'var(--success)' }}>OK {ago(f.attemptedAt)}</span>
                  )}
                  {f.error && <span className="block text-[11px]" style={{ color: 'var(--text-secondary)' }}>{f.error}</span>}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </section>
  );
}
