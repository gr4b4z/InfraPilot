import { useEffect, useId, useState } from 'react';
import { useNavigate, useSearchParams } from 'react-router-dom';
import { formatDistanceToNow } from 'date-fns';
import { Loader2, PieChart, SearchX } from 'lucide-react';
import { api } from '@/lib/api';
import { useDocumentTitle } from '@/lib/pageTitle';
import { useAuthStore } from '@/stores/authStore';
import { ListEmptyState } from '@/components/ui/ListEmptyState';
import type { CoverageCurrency, InventoryCoverageResponse, InventoryFeedsResponse } from './inventoryTypes';
import { SERIES_DIRECT, SERIES_SHARED, formatAmount, monthLabel } from './inventoryFormat';
import { Freshness, MonthSelect, Swatch } from './InventoryUi';

const pct = (part: number, whole: number) => (whole > 0 ? (part / whole) * 100 : 0);
const fmtPct = (v: number) => `${v > 0 && v < 10 ? v.toFixed(1) : Math.round(v)}%`;

/** The parts a month's total is made of, in the order the bar draws them. */
const PARTS: { key: keyof CoverageCurrency & ('direct' | 'shared' | 'sharedUnsplit' | 'untagged' | 'unknownTag'); label: string; color: string; hatched?: boolean; attributed: boolean }[] = [
  { key: 'direct', label: 'Direct to a service', color: SERIES_DIRECT, attributed: true },
  { key: 'shared', label: 'Shared, split between services', color: SERIES_SHARED, attributed: true },
  { key: 'sharedUnsplit', label: 'Shared, no service bound', color: SERIES_SHARED, hatched: true, attributed: false },
  { key: 'untagged', label: 'No service or shared tag', color: 'var(--border-strong)', attributed: false },
  { key: 'unknownTag', label: 'Tag not in the catalog', color: 'var(--text-muted)', hatched: true, attributed: false },
];

/**
 * /inventory/coverage — how much of a month's cost is attributed to services, why the rest is not,
 * where the largest unattributed amounts sit, and coverage over time. Admins also see the feeds.
 */
export function InventoryCoveragePage() {
  const [params] = useSearchParams();
  const navigate = useNavigate();
  const period = params.get('month') ?? undefined;
  const isAdmin = useAuthStore((s) => s.user?.isAdmin ?? false);

  const [data, setData] = useState<InventoryCoverageResponse | null>(null);
  const [failed, setFailed] = useState(false);
  useDocumentTitle(['Cost coverage']);

  useEffect(() => {
    let cancelled = false;
    api.getInventoryCoverage(period)
      .then((d) => { if (!cancelled) { setData(d); setFailed(false); } })
      .catch(() => { if (!cancelled) { setData(null); setFailed(true); } });
    return () => { cancelled = true; };
  }, [period]);

  if (failed) {
    return <ListEmptyState icon={SearchX} title="Cost coverage could not be loaded" body="Try again in a moment." />;
  }
  if (!data) {
    return (
      <div className="flex items-center justify-center py-20">
        <Loader2 className="animate-spin" size={24} style={{ color: 'var(--text-muted)' }} />
      </div>
    );
  }

  return (
    <div className="space-y-5">
      <div className="flex flex-wrap items-end gap-3">
        <div>
          <h1 className="text-xl font-semibold tracking-tight" style={{ color: 'var(--text-primary)' }}>Cost coverage</h1>
          <p className="text-sm mt-0.5" style={{ color: 'var(--text-muted)' }}>
            How much of the cloud cost can be traced to a service, and where the rest sits
          </p>
        </div>
        <span className="flex-1" />
        {data.period && (
          <MonthSelect
            months={data.months}
            value={data.period}
            onChange={(m) => navigate(`/inventory/coverage?month=${encodeURIComponent(m)}`)}
          />
        )}
      </div>

      <Freshness feeds={[data.freshness]} />

      {!data.period ? (
        <ListEmptyState
          icon={PieChart}
          title="No cost has been read yet"
          body="Coverage appears once the first cost file has been fetched."
        />
      ) : (
        <>
          <p className="text-[12px]" style={{ color: 'var(--text-muted)' }}>
            {monthLabel(data.period)} · {data.complete ? 'closed month' : 'to date'}
            {data.sources && data.sources.length > 0 && ` · sources: ${[...new Set(data.sources.map((s) => s.source))].join(', ')}`}
          </p>

          {/* One block per currency: amounts in different currencies are never added together. */}
          {(data.currencies ?? []).map((c) => (
            <CurrencyBlock key={c.currency} c={c} unsplit={(data.unsplitShared ?? []).filter((s) => (s.unsplit[c.currency] ?? 0) > 0).map((s) => s.id)} />
          ))}

          <div className="grid gap-4 lg:grid-cols-2">
            {(data.currencies ?? []).map((c) => <LargestUnattributed key={c.currency} c={c} />)}
            {[...new Set((data.trend ?? []).map((t) => t.currency))].map((currency) => (
              <CoverageTrend key={currency} currency={currency} rows={(data.trend ?? []).filter((t) => t.currency === currency)} />
            ))}
          </div>

          {data.sources && data.sources.length > 0 && (
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
                    {data.sources.map((s) => (
                      <tr key={`${s.source}:${s.currency}`} className="border-t" style={{ borderColor: 'var(--border-color)' }}>
                        <td className="px-3 py-2 font-mono">{s.source}</td>
                        <td className="px-3 py-2 text-right">
                          {s.rows == null ? '—' : `${s.rows} in ${s.scopes ?? 0} scope${s.scopes === 1 ? '' : 's'}`}
                        </td>
                        <td className="px-3 py-2 text-right">{formatAmount(s.total, s.currency)}</td>
                        <td className="px-3 py-2 text-right">{fmtPct(pct(s.attributed, s.total))}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            </section>
          )}
        </>
      )}

      {isAdmin && <FeedsPanel />}
    </div>
  );
}

function CurrencyBlock({ c, unsplit }: { c: CoverageCurrency; unsplit: string[] }) {
  const coverage = pct(c.direct + c.shared, c.total);
  return (
    <section className="rounded-xl border p-4 space-y-3" style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-primary)' }}>
      <div className="flex flex-wrap items-baseline gap-x-3 gap-y-1">
        <span className="text-3xl font-semibold tabular-nums">{fmtPct(coverage)}</span>
        <span className="text-[13px]" style={{ color: 'var(--text-secondary)' }}>
          of {formatAmount(c.total, c.currency)} attributed to services
        </span>
        <span className="flex-1" />
        <span className="text-[11px]" style={{ color: 'var(--text-muted)' }}>{c.currency}</span>
      </div>
      {/* Every part is named with its amount in the table below, so identity is never colour alone. */}
      <div className="flex h-3 w-full gap-[2px] rounded overflow-hidden" role="img" aria-label={`${fmtPct(coverage)} attributed`}>
        {PARTS.map((p) => c[p.key] > 0 && (
          <span
            key={p.key}
            title={`${p.label}: ${formatAmount(c[p.key], c.currency)}`}
            style={{
              width: `${pct(c[p.key], c.total)}%`,
              background: p.hatched ? `repeating-linear-gradient(135deg, ${p.color} 0 2px, transparent 2px 5px)` : p.color,
              border: p.hatched ? `1px solid ${p.color}` : undefined,
            }}
          />
        ))}
      </div>
      <table className="w-full text-[13px] tabular-nums">
        <tbody>
          {PARTS.map((p) => (
            <tr key={p.key} className="border-t" style={{ borderColor: 'var(--border-color)', color: c[p.key] ? undefined : 'var(--text-muted)' }}>
              <td className="py-1.5">
                <span className="inline-flex flex-wrap items-center gap-2">
                  <Swatch color={p.color} hatched={p.hatched} />
                  {p.label}
                  {!p.attributed && (
                    <span className="text-[10px] uppercase tracking-wide" style={{ color: 'var(--text-muted)' }}>not attributed</span>
                  )}
                </span>
              </td>
              <td className="py-1.5 text-right">{formatAmount(c[p.key], c.currency)}</td>
              <td className="py-1.5 text-right w-16" style={{ color: 'var(--text-muted)' }}>{fmtPct(pct(c[p.key], c.total))}</td>
            </tr>
          ))}
        </tbody>
      </table>
      {unsplit.length > 0 && (
        <p className="text-[12px]" style={{ color: 'var(--text-muted)' }}>
          Shared resources no service is bound to: {unsplit.join(', ')}
        </p>
      )}
    </section>
  );
}

function LargestUnattributed({ c }: { c: CoverageCurrency }) {
  const [by, setBy] = useState<'group' | 'scope'>('group');
  const rows = by === 'group'
    ? c.largestGroups.map((r) => ({ key: `${r.source}/${r.scope}/${r.group}`, title: r.group || '(no group)', sub: `${r.source} · ${r.scope}`, amount: r.amount }))
    : c.largestScopes.map((r) => ({ key: `${r.source}/${r.scope}`, title: r.scope, sub: r.source, amount: r.amount }));
  const max = Math.max(1, ...rows.map((r) => r.amount));

  return (
    <section className="rounded-xl border p-4" style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-primary)' }}>
      <div className="flex flex-wrap items-center gap-2 mb-3">
        <h2 className="text-[13px] font-semibold">Largest unattributed · {c.currency}</h2>
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
      {rows.length === 0 ? (
        <p className="text-[13px]" style={{ color: 'var(--text-muted)' }}>Nothing unattributed in {c.currency}.</p>
      ) : (
        <ul className="space-y-2.5">
          {rows.map((r) => (
            <li key={r.key} className="text-[12px]">
              <div className="flex items-baseline gap-2">
                <span className="font-mono truncate min-w-0" style={{ color: 'var(--text-primary)' }} title={r.title}>{r.title}</span>
                <span className="flex-1" />
                <span className="tabular-nums font-medium">{formatAmount(r.amount, c.currency)}</span>
              </div>
              <div className="mt-1 h-1.5 rounded-full" style={{ backgroundColor: 'var(--bg-secondary)' }}>
                <div className="h-1.5 rounded-full" style={{ width: `${(r.amount / max) * 100}%`, backgroundColor: 'var(--border-strong)' }} />
              </div>
              <span className="text-[10px] truncate block" style={{ color: 'var(--text-muted)' }}>{r.sub}</span>
            </li>
          ))}
        </ul>
      )}
    </section>
  );
}

function CoverageTrend({ currency, rows }: {
  currency: string;
  rows: { period: string; complete: boolean; total: number; attributed: number }[];
}) {
  const [hover, setHover] = useState<number | null>(null);
  const id = useId().replace(/:/g, '');
  const H = 120;
  const h = hover != null ? rows[hover] : null;
  return (
    <section className="rounded-xl border p-4" style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-primary)' }}>
      <div className="flex flex-wrap items-baseline gap-2 mb-1">
        <h2 className="text-[13px] font-semibold">Coverage per month · {currency}</h2>
        <span className="flex-1" />
        <span className="text-[12px]" style={{ color: 'var(--text-muted)' }}>
          {h ? (
            <>
              {monthLabel(h.period)}{h.complete ? '' : ' (to date)'}{' '}
              <b style={{ color: 'var(--text-primary)' }}>{fmtPct(pct(h.attributed, h.total))}</b>
              {' '}· {formatAmount(h.attributed, currency)} of {formatAmount(h.total, currency)}
            </>
          ) : 'share attributed to services'}
        </span>
      </div>
      <svg width="100%" height={H} role="img" aria-label={`Coverage per month in ${currency}`} onMouseLeave={() => setHover(null)} style={{ display: 'block' }}>
        <defs>
          <pattern id={`${id}-c`} width="5" height="5" patternUnits="userSpaceOnUse" patternTransform="rotate(45)">
            <rect width="5" height="5" fill={SERIES_DIRECT} opacity="0.25" />
            <rect width="2" height="5" fill={SERIES_DIRECT} />
          </pattern>
        </defs>
        {[0, 50, 100].map((g) => (
          <g key={g}>
            <line x1="28" x2="100%" y1={H - 1 - (g / 100) * (H - 14)} y2={H - 1 - (g / 100) * (H - 14)} stroke="var(--border-color)" strokeDasharray={g ? '2 3' : undefined} />
            {g > 0 && <text x="0" y={H + 2 - (g / 100) * (H - 14)} fontSize="9" fill="var(--text-muted)">{g}%</text>}
          </g>
        ))}
        {rows.map((m, i) => {
          const slot = 100 / rows.length;
          const v = pct(m.attributed, m.total);
          const bh = (v / 100) * (H - 14);
          return (
            <g key={m.period}>
              <rect x={`${i * slot}%`} y={0} width={`${slot}%`} height={H} fill={hover === i ? 'var(--bg-secondary)' : 'transparent'} onMouseEnter={() => setHover(i)} />
              {bh > 0 ? (
                <rect
                  x={`${i * slot + slot * 0.25}%`} y={H - 1 - bh} width={`${slot * 0.5}%`} height={bh} rx={3}
                  fill={m.complete ? SERIES_DIRECT : `url(#${id}-c)`}
                  stroke={m.complete ? undefined : SERIES_DIRECT} strokeDasharray={m.complete ? undefined : '3 2'}
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
        {rows.map((m) => (
          <span key={m.period} className="flex-1 text-center">
            {monthLabel(m.period, true)}{!m.complete && ' · to date'}
          </span>
        ))}
      </div>
    </section>
  );
}

const ago = (iso: string | null) => (iso ? formatDistanceToNow(new Date(iso), { addSuffix: true }) : 'never');

/** "generated <timestamp>" reads better as "generated 12 minutes ago". */
function storedLabel(version: string): string {
  const at = version.replace(/^generated\s*/, '');
  return version.startsWith('generated ') && !Number.isNaN(Date.parse(at)) ? `generated ${ago(at)}` : version;
}

/** Admins only: the feeds behind these pages, when each was last refreshed, and why an attempt failed. */
function FeedsPanel() {
  const [feeds, setFeeds] = useState<InventoryFeedsResponse | null>(null);
  useEffect(() => {
    let cancelled = false;
    api.getInventoryFeeds().then((f) => { if (!cancelled) setFeeds(f); }).catch(() => undefined);
    return () => { cancelled = true; };
  }, []);
  if (!feeds) return null;

  const rows = feeds.state.filter((s) => s.configured);
  return (
    <section>
      <h2 className="text-sm font-semibold mb-1">Feeds</h2>
      <p className="text-[12px] mb-2" style={{ color: 'var(--text-muted)' }}>
        Admins only. The portal keeps the last good copy of each feed; a failed refresh never removes data.
      </p>
      {feeds.configurationErrors.length > 0 && (
        <div className="rounded-lg border px-3 py-2 mb-2 text-[12px]" style={{ borderColor: 'var(--warning)', backgroundColor: 'var(--warning-bg)', color: 'var(--text-secondary)' }}>
          <b style={{ color: 'var(--text-primary)' }}>The feed configuration is invalid, so nothing is refreshed:</b>
          <ul className="list-disc ml-5 mt-1">{feeds.configurationErrors.map((e) => <li key={e}>{e}</li>)}</ul>
        </div>
      )}
      {feeds.feeds.length === 0 ? (
        <p className="text-[13px]" style={{ color: 'var(--text-muted)' }}>No feeds are configured.</p>
      ) : (
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
              {feeds.feeds.map((f) => {
                const state = rows.filter((s) => s.feed === f.name);
                const own = state.find((s) => s.period === null);
                const months = state.filter((s) => s.period !== null);
                const failing = [own, ...months].filter((s) => s?.lastError);
                return (
                  <tr key={f.name} className="border-t align-top" style={{ borderColor: 'var(--border-color)' }}>
                    <td className="px-3 py-2 font-medium">
                      {f.name}
                      <span className="block text-[11px] font-normal" style={{ color: 'var(--text-muted)' }}>
                        {f.type} · every {f.pollMinutes} min
                      </span>
                    </td>
                    <td className="px-3 py-2 font-mono break-all" style={{ color: 'var(--text-secondary)' }}>
                      {f.sourceKind} · {f.location ?? '—'}
                    </td>
                    <td className="px-3 py-2">
                      {own?.stored ? (
                        <>
                          {storedLabel(own.stored.version)}
                          <span className="block text-[11px]" style={{ color: 'var(--text-muted)' }}>fetched {ago(own.stored.fetchedAt)}</span>
                        </>
                      ) : <span style={{ color: 'var(--text-muted)' }}>nothing yet</span>}
                      {months.length > 0 && (
                        <span className="block text-[11px]" style={{ color: 'var(--text-muted)' }}>
                          {months.filter((m) => m.stored).length} month{months.filter((m) => m.stored).length === 1 ? '' : 's'} stored
                        </span>
                      )}
                    </td>
                    <td className="px-3 py-2">
                      {failing.length === 0 ? (
                        <span style={{ color: 'var(--success)' }}>OK {ago(own?.lastAttemptAt ?? null)}</span>
                      ) : (
                        failing.map((s) => (
                          <span key={s!.period ?? ''} className="block">
                            <span style={{ color: 'var(--warning)' }}>
                              Failed {ago(s!.lastAttemptAt)}{s!.period ? ` (${s!.period})` : ''}
                            </span>
                            <span className="block text-[11px]" style={{ color: 'var(--text-secondary)' }}>{s!.lastError}</span>
                          </span>
                        ))
                      )}
                    </td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        </div>
      )}
    </section>
  );
}
