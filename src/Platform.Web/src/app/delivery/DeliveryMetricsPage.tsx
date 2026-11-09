import { useEffect, useMemo, useRef, useState } from 'react';
import { Link, useSearchParams } from 'react-router-dom';
import { AlertTriangle, Check, ChevronDown, Download, Gauge, Search } from 'lucide-react';
import { api, type DeliveryIncident, type DeliveryMetricsResponse, type DeliverySection, type DeliveryServiceRow } from '@/lib/api';
import { useDocumentTitle } from '@/lib/pageTitle';
import { useSettingsStore } from '@/stores/settingsStore';
import { AnchoredPopover } from '@/components/ui/AnchoredPopover';
import { InfoPopover } from '@/components/ui/InfoPopover';
import { HelpButton } from '@/components/guide/HelpButton';
import { EnvLabel } from '@/components/environments/EnvBadge';
import { MetricCard } from './MetricCard';
import { TrendChart } from './TrendChart';
import { OVERLAY_COLORS, type TrendSeries } from './trendStyle';
import { DELIVERY_PERIODS, computeDeliveryRange } from './deliveryPeriods';
import { csvLine, fmtAgo, fmtCount, fmtDateTime, fmtHours, fmtRatio } from './deliveryFormat';

const SERVICE_TOP_N = 15;

/**
 * Delivery metrics — the report, as opposed to the Analytics page's operational view.
 *
 * One product selection pooled as one population, one fixed window, every number from one API
 * response so they all measure the same production set. The DORA four are the headline; the
 * flow/quality/cadence/data-quality cards below say how to read them; the tables say which product
 * or service moves them. Deliberately no live refresh: a report that redraws while someone reads
 * it to a room is worse than one that is a minute old, and the as-of line says how old.
 */
export function DeliveryMetricsPage() {
  useDocumentTitle(['Delivery metrics']);
  const [searchParams, setSearchParams] = useSearchParams();
  const { getDisplayName } = useSettingsStore();

  const [allProducts, setAllProducts] = useState<string[]>([]);
  const selected = useMemo(
    () => (searchParams.get('products') ?? '').split(',').map((p) => p.trim()).filter(Boolean),
    [searchParams],
  );
  const periodKey = searchParams.get('period') ?? '30';
  const period = DELIVERY_PERIODS.find((p) => p.key === periodKey) ?? DELIVERY_PERIODS[0];
  const range = useMemo(() => computeDeliveryRange(period.key), [period.key]);
  const tz = useMemo(() => Intl.DateTimeFormat().resolvedOptions().timeZone, []);

  const [report, setReport] = useState<DeliveryMetricsResponse | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    api
      .getDeploymentProducts()
      .then((list) => setAllProducts(list.map((p) => p.product)))
      .catch(() => setAllProducts([]));
  }, []);

  useEffect(() => {
    let cancelled = false;
    api
      .getDeliveryMetrics({ products: selected, from: range.from, to: range.to, bucket: range.bucket, tz })
      .then((r) => {
        if (cancelled) return;
        setError(null);
        setReport(r);
      })
      .catch((e) => {
        if (!cancelled) setError(e instanceof Error ? e.message : 'Failed to load delivery metrics');
      })
      .finally(() => {
        if (!cancelled) setLoading(false);
      });
    return () => {
      cancelled = true;
    };
  }, [selected, range.from, range.to, range.bucket, tz]);

  const updateParams = (patch: Record<string, string>) => {
    const next = new URLSearchParams(searchParams);
    for (const [k, v] of Object.entries(patch)) {
      if (v) next.set(k, v);
      else next.delete(k);
    }
    setSearchParams(next, { replace: true });
  };

  const sections = report?.sections ?? [];
  const dora = sections.find((s) => s.key === 'dora');
  const secondary = sections.filter((s) => s.key !== 'dora');
  const periodDays = Math.round(range.days);
  const vsPrev = `vs previous ${periodDays} days`;
  const scopeLabel = selected.length === 0
    ? `All products (${report?.products.length ?? allProducts.length})`
    : selected.length === 1
      ? selected[0]
      : `${selected.length} products`;

  return (
    <div className="space-y-6">
      <div className="flex flex-wrap items-center gap-3">
        <div className="flex items-center gap-2 mr-auto">
          <Gauge size={20} style={{ color: 'var(--accent)' }} />
          <div>
            <h1 className="text-xl font-semibold flex items-center gap-2">
              Delivery metrics
              <HelpButton
                page="Delivery metrics"
                size="sm"
                context={{ products: selected.length ? selected.join(', ') : 'all', period: period.label }}
              />
            </h1>
            <p className="text-[12px]" style={{ color: 'var(--text-muted)' }}>
              DORA and delivery performance across products
            </p>
          </div>
        </div>
        <ProductPicker all={allProducts} selected={selected} onChange={(next) => updateParams({ products: next.join(',') })} />
        <div className="flex rounded-lg border overflow-hidden" style={{ borderColor: 'var(--border-color)' }}>
          {DELIVERY_PERIODS.map((p) => (
            <button
              key={p.key}
              onClick={() => updateParams({ period: p.key })}
              className="px-2.5 py-1.5 text-[12px] whitespace-nowrap"
              style={{
                backgroundColor: p.key === period.key ? 'var(--accent-bg)' : 'var(--bg-primary)',
                color: p.key === period.key ? 'var(--accent)' : 'var(--text-secondary)',
              }}
            >
              {p.label}
            </button>
          ))}
        </div>
        <button
          onClick={() => report && downloadCsv(report, scopeLabel, period.label)}
          disabled={!report}
          className="inline-flex items-center gap-1.5 px-3 py-1.5 rounded-lg border text-[12px] font-medium disabled:opacity-50"
          style={{ borderColor: 'var(--border-color)', color: 'var(--text-secondary)', backgroundColor: 'var(--bg-primary)' }}
        >
          <Download size={13} /> CSV
        </button>
      </div>

      {error && (
        <div
          className="px-4 py-3 rounded-xl border text-[13px]"
          style={{ borderColor: 'var(--danger)', backgroundColor: 'var(--danger-bg)', color: 'var(--danger)' }}
        >
          {error}
        </div>
      )}

      {loading && !report ? (
        <div className="space-y-3">
          <div className="skeleton h-6" />
          <div className="skeleton h-36" />
          <div className="skeleton h-28" />
          <div className="skeleton h-64" />
        </div>
      ) : report && (
        <>
          <ScopeLine report={report} scopeLabel={scopeLabel} getDisplayName={getDisplayName} />

          {dora && (
            <section>
              <SectionHeading section={dora} hint="The four DORA metrics, over the production set above." />
              <div className="grid grid-cols-1 sm:grid-cols-2 xl:grid-cols-4 gap-3">
                {dora.metrics.map((m) => (
                  <MetricCard key={m.key} metric={m} size="primary" periodLabel={vsPrev} />
                ))}
              </div>
            </section>
          )}

          <div className="grid grid-cols-1 lg:grid-cols-2 gap-4">
            {secondary.map((s) => (
              <section key={s.key}>
                <SectionHeading section={s} />
                <div className="grid grid-cols-2 sm:grid-cols-3 gap-3">
                  {s.metrics.map((m) => (
                    <MetricCard key={m.key} metric={m} periodLabel={vsPrev} />
                  ))}
                </div>
              </section>
            ))}
          </div>

          <TrendsSection report={report} periodLabel={`by ${report.definition.bucket} · ${period.label}`} />

          <LeadTimeByEnvironment report={report} getDisplayName={getDisplayName} />

          {report.byProduct.length > 1 && <ProductTable report={report} />}

          <ServiceTable rows={report.byService} showProduct={report.products.length > 1} />

          {report.incidents && <IncidentTable incidents={report.incidents} showProduct={report.products.length > 1} />}

          <p className="text-[11px]" style={{ color: 'var(--text-muted)' }}>
            Computed live from deploy events and promotions at {fmtDateTime(report.asOf)}; a later backfill (commit times,
            late deploy events) moves these numbers. {report.definition.deploy}{' '}
            Operational views — the story matrix, the queue — are on the{' '}
            <Link to="/analytics" className="underline">Analytics</Link> page.
          </p>
        </>
      )}
    </div>
  );
}

// ── Header pieces ──────────────────────────────────────────────────────────

function ScopeLine({
  report,
  scopeLabel,
  getDisplayName,
}: {
  report: DeliveryMetricsResponse;
  scopeLabel: string;
  getDisplayName: (env: string) => string;
}) {
  const prod = report.production;
  return (
    <div
      className="flex flex-wrap items-center gap-x-4 gap-y-1 px-4 py-2.5 rounded-xl border text-[12px]"
      style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-secondary)', color: 'var(--text-secondary)' }}
    >
      <span><span style={{ color: 'var(--text-muted)' }}>Scope</span> <b>{scopeLabel}</b></span>
      <span className="inline-flex items-center gap-1.5">
        <span style={{ color: 'var(--text-muted)' }}>Production</span>
        {prod.environments.length === 0 ? (
          <b>—</b>
        ) : (
          prod.environments.map((env) => <EnvLabel key={env} env={env} />)
        )}
        <InfoPopover
          label="Production environments"
          content={{
            what: `The environments every production figure on this page is measured against: ${prod.environments.map(getDisplayName).join(', ') || 'none resolved'}.`,
            how: prod.explanation,
            why: 'Marking production stages in Settings → Environments is what makes this choice yours rather than a guess from the names.',
          }}
        />
      </span>
      {prod.ignoredProductionLike.length > 0 && (
        <span className="inline-flex items-center gap-1" style={{ color: 'var(--warning)' }} title={prod.explanation}>
          <AlertTriangle size={12} /> not counted: {prod.ignoredProductionLike.map(getDisplayName).join(', ')}
        </span>
      )}
      <span>
        <span style={{ color: 'var(--text-muted)' }}>Window</span>{' '}
        {new Date(report.range.from).toLocaleDateString()} – {new Date(report.range.to).toLocaleDateString()}
        <span style={{ color: 'var(--text-muted)' }}> · by {report.definition.bucket}</span>
      </span>
      <span className="ml-auto" style={{ color: 'var(--text-muted)' }}>as of {fmtDateTime(report.asOf)}</span>
    </div>
  );
}

function SectionHeading({ section, hint }: { section: DeliverySection; hint?: string }) {
  return (
    <h2 className="text-[13px] font-semibold mb-2 flex items-center gap-2">
      {section.title}
      {hint && <span className="font-normal text-[12px]" style={{ color: 'var(--text-muted)' }}>{hint}</span>}
    </h2>
  );
}

/**
 * Multi-select of products: "All" or any subset. A popover with checkboxes rather than a
 * chip-input — there are tens of products at most, and seeing the whole list is the point.
 */
function ProductPicker({
  all,
  selected,
  onChange,
}: {
  all: string[];
  selected: string[];
  onChange: (next: string[]) => void;
}) {
  const [open, setOpen] = useState(false);
  const [filter, setFilter] = useState('');
  const anchorRef = useRef<HTMLButtonElement>(null);
  const options = useMemo(() => {
    const set = new Set(all);
    for (const p of selected) set.add(p);
    return [...set].sort((a, b) => a.localeCompare(b));
  }, [all, selected]);
  const needle = filter.trim().toLowerCase();
  const listed = options.filter((p) => !needle || p.toLowerCase().includes(needle));
  const label = selected.length === 0 ? 'All products' : selected.length === 1 ? selected[0] : `${selected.length} products`;

  const toggle = (p: string) =>
    onChange(selected.includes(p) ? selected.filter((x) => x !== p) : [...selected, p]);

  return (
    <>
      <button
        ref={anchorRef}
        onClick={() => setOpen((v) => !v)}
        aria-haspopup="listbox"
        aria-expanded={open}
        className="inline-flex items-center gap-1.5 text-[13px] px-3 py-1.5 rounded-lg border max-w-[16rem]"
        style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-primary)', color: 'var(--text-primary)' }}
      >
        <span className="truncate">{label}</span>
        <ChevronDown size={13} className="shrink-0" style={{ color: 'var(--text-muted)' }} />
      </button>
      {open && (
        <AnchoredPopover anchorRef={anchorRef} onClose={() => setOpen(false)} width={280}>
          <div className="p-2 space-y-1" role="listbox" aria-multiselectable>
            {options.length > 8 && (
              <label className="flex items-center gap-2 px-2 py-1 text-[12px]" style={{ color: 'var(--text-muted)' }}>
                <Search size={12} />
                <input
                  autoFocus
                  value={filter}
                  onChange={(e) => setFilter(e.target.value)}
                  placeholder="Filter products"
                  className="flex-1 bg-transparent outline-none"
                  style={{ color: 'var(--text-primary)' }}
                />
              </label>
            )}
            <button
              role="option"
              aria-selected={selected.length === 0}
              onClick={() => { onChange([]); setOpen(false); }}
              className="w-full flex items-center gap-2 px-2 py-1.5 rounded-lg text-[13px] text-left hover:opacity-80"
              style={{ backgroundColor: selected.length === 0 ? 'var(--accent-bg)' : 'transparent', color: 'var(--text-primary)' }}
            >
              <span className="w-4 inline-flex justify-center">{selected.length === 0 && <Check size={13} />}</span>
              All products
            </button>
            <div className="max-h-64 overflow-y-auto">
              {listed.map((p) => {
                const on = selected.includes(p);
                return (
                  <button
                    key={p}
                    role="option"
                    aria-selected={on}
                    onClick={() => toggle(p)}
                    className="w-full flex items-center gap-2 px-2 py-1.5 rounded-lg text-[13px] text-left hover:opacity-80"
                    style={{ color: 'var(--text-primary)' }}
                  >
                    <span
                      className="w-4 h-4 inline-flex items-center justify-center rounded border"
                      style={{
                        borderColor: on ? 'var(--accent)' : 'var(--border-color)',
                        backgroundColor: on ? 'var(--accent)' : 'transparent',
                        color: 'white',
                      }}
                    >
                      {on && <Check size={11} />}
                    </span>
                    <span className="truncate">{p}</span>
                  </button>
                );
              })}
              {listed.length === 0 && (
                <p className="px-2 py-1.5 text-[12px]" style={{ color: 'var(--text-muted)' }}>No product matches.</p>
              )}
            </div>
          </div>
        </AnchoredPopover>
      )}
    </>
  );
}

// ── Trends ─────────────────────────────────────────────────────────────────

const DORA_KEYS = ['deploymentFrequency', 'leadTime', 'changeFailureRate', 'timeToRestore'];

/**
 * DORA is a direction, not a snapshot: the four metrics as full charts, with the previous window
 * as a ghost line, per-product overlays to see who moves the portfolio line, and any secondary
 * metric added on demand. Thin-coverage buckets are drawn hollow (see TrendChart).
 */
function TrendsSection({ report, periodLabel }: { report: DeliveryMetricsResponse; periodLabel: string }) {
  const [showPrevious, setShowPrevious] = useState(true);
  const [perProduct, setPerProduct] = useState(false);
  const [extra, setExtra] = useState<string[]>([]);
  const metrics = report.sections.flatMap((s) => s.metrics);
  const byKey = new Map(metrics.map((m) => [m.key, m]));
  const dora = DORA_KEYS.map((k) => byKey.get(k)).filter((m): m is NonNullable<typeof m> => !!m);
  const secondary = metrics.filter((m) => !DORA_KEYS.includes(m.key) && m.trend.length > 0);
  const canOverlay = report.byProduct.length > 1 && report.byProduct.length <= OVERLAY_COLORS.length;

  const overlaysFor = (key: string): TrendSeries[] =>
    perProduct && canOverlay
      ? report.byProduct.map((r, i) => ({ name: r.product, points: r.trends?.[key] ?? [], color: OVERLAY_COLORS[i % OVERLAY_COLORS.length] }))
      : [];

  return (
    <section>
      <div className="flex flex-wrap items-center gap-3 mb-2">
        <h2 className="text-[13px] font-semibold mr-auto flex items-center gap-1.5">
          Trends
          <InfoPopover
            label="Trends"
            content={{
              what: 'The DORA metrics over time, one point per bucket of the selected window.',
              how: 'Solid is this window; the dashed ghost is the equal-length window before, aligned bucket for bucket. Hollow points rest on thin coverage (under half of the bucket\'s stories or incidents measurable) and should not be read as a change. Per product overlays each product\'s own series; the portfolio line is not their average.',
              why: 'DORA is a direction, not a level: a figure only means something next to where it came from. Compare the slope and the gap to the ghost, and distrust any move that coincides with a coverage dip.',
            }}
          />
        </h2>
        <label className="inline-flex items-center gap-1.5 text-[12px]" style={{ color: 'var(--text-secondary)' }}>
          <input type="checkbox" checked={showPrevious} onChange={(e) => setShowPrevious(e.target.checked)} /> previous period
        </label>
        {report.byProduct.length > 1 && (
          <label className="inline-flex items-center gap-1.5 text-[12px]" style={{ color: 'var(--text-secondary)' }} title={canOverlay ? undefined : `Overlay supports up to ${OVERLAY_COLORS.length} products`}>
            <input type="checkbox" checked={perProduct && canOverlay} disabled={!canOverlay} onChange={(e) => setPerProduct(e.target.checked)} /> per product
          </label>
        )}
        <select
          value=""
          onChange={(e) => { if (e.target.value) setExtra((x) => [...x, e.target.value]); }}
          className="text-[12px] px-2 py-1 rounded-lg border"
          style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-primary)', color: 'var(--text-secondary)' }}
          aria-label="Add a metric chart"
        >
          <option value="">Add metric…</option>
          {secondary.filter((m) => !extra.includes(m.key)).map((m) => <option key={m.key} value={m.key}>{m.title}</option>)}
        </select>
      </div>
      {perProduct && canOverlay && (
        <div className="flex flex-wrap gap-x-3 gap-y-1 mb-2 text-[11px]" style={{ color: 'var(--text-secondary)' }}>
          <span className="inline-flex items-center gap-1"><span className="inline-block w-3 h-0.5" style={{ backgroundColor: 'var(--accent)' }} /> all selected</span>
          {report.byProduct.map((r, i) => (
            <span key={r.product} className="inline-flex items-center gap-1">
              <span className="inline-block w-2 h-2 rounded-full" style={{ backgroundColor: OVERLAY_COLORS[i % OVERLAY_COLORS.length] }} /> {r.product}
            </span>
          ))}
        </div>
      )}
      <div className="grid grid-cols-1 lg:grid-cols-2 gap-3">
        {dora.map((m) => (
          <TrendChart key={m.key} metric={m} showPrevious={showPrevious} overlays={overlaysFor(m.key)} periodLabel={periodLabel} />
        ))}
        {extra.map((k) => byKey.get(k)).filter((m): m is NonNullable<typeof m> => !!m).map((m) => (
          <div key={m.key} className="relative">
            <TrendChart metric={m} showPrevious={showPrevious} periodLabel={periodLabel} />
            <button
              onClick={() => setExtra((x) => x.filter((e) => e !== m.key))}
              className="absolute top-3 right-3 text-[11px] underline"
              style={{ color: 'var(--text-muted)' }}
              aria-label={`Remove ${m.title} chart`}
            >
              remove
            </button>
          </div>
        ))}
      </div>
    </section>
  );
}

// ── Breakdowns ─────────────────────────────────────────────────────────────

function LeadTimeByEnvironment({
  report,
  getDisplayName,
}: {
  report: DeliveryMetricsResponse;
  getDisplayName: (env: string) => string;
}) {
  const rows = report.leadTimeByEnvironment.filter((r) => r.n > 0);
  if (rows.length === 0) return null;
  const max = Math.max(1, ...rows.map((r) => r.p90Hours ?? r.p50Hours ?? 0));
  return (
    <section className="rounded-xl border p-4" style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-primary)' }}>
      <h2 className="text-[13px] font-semibold mb-1 flex items-center gap-1.5">
        Lead time by environment
        <InfoPopover
          label="Lead time by environment"
          content={{
            what: 'Hours from commit to a story first landing on each environment, cumulative from the commit.',
            how: `${report.definition.leadTime} Each environment is measured from the same clock start, so production minus test is the time the change waited between the two.`,
            why: 'Shows where the time goes. A big step between the last pre-production stage and production is approval and deploy wait, not engineering.',
          }}
        />
      </h2>
      <p className="text-[12px] mb-3" style={{ color: 'var(--text-muted)' }}>p50 bar, p90 whisker · n per environment</p>
      <div className="space-y-2">
        {rows.map((r) => (
          <div key={r.environment} className="grid grid-cols-[8rem_1fr_7rem] items-center gap-3 text-[12px]">
            <span className="truncate"><EnvLabel env={r.environment} /></span>
            <div className="relative h-3 rounded" style={{ backgroundColor: 'var(--bg-secondary)' }} aria-hidden>
              {r.p90Hours != null && (
                <div className="absolute top-1/2 -translate-y-1/2 h-px" style={{ left: 0, width: `${Math.min(100, (r.p90Hours / max) * 100)}%`, backgroundColor: 'var(--text-muted)' }} />
              )}
              <div className="absolute inset-y-0 left-0 rounded" style={{ width: `${Math.min(100, ((r.p50Hours ?? 0) / max) * 100)}%`, backgroundColor: 'var(--accent)' }} />
            </div>
            <span className="text-right tabular-nums" title={`${getDisplayName(r.environment)}: p50 ${fmtHours(r.p50Hours)}, p75 ${fmtHours(r.p75Hours)}, p90 ${fmtHours(r.p90Hours)}`}>
              <b>{fmtHours(r.p50Hours)}</b> <span style={{ color: 'var(--text-muted)' }}>· n={r.n}</span>
            </span>
          </div>
        ))}
      </div>
    </section>
  );
}

function ProductTable({ report }: { report: DeliveryMetricsResponse }) {
  const rows = [...report.byProduct].sort((a, b) => b.deploys - a.deploys || a.product.localeCompare(b.product));
  const th = 'text-left font-medium px-2 py-1.5 whitespace-nowrap';
  const td = 'px-2 py-1.5 whitespace-nowrap tabular-nums';
  return (
    <section className="rounded-xl border p-4" style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-primary)' }}>
      <h2 className="text-[13px] font-semibold mb-1 flex items-center gap-1.5">
        Per product
        <InfoPopover
          label="Per product"
          content={{
            what: 'The same numbers, one row per product in scope.',
            how: 'Each row is computed over that product alone, with the production set shown at the top. The headline figures above pool these populations — they are not the average of the rows.',
            why: 'Which product drives the portfolio number, and which one is pulling coverage down.',
          }}
        />
      </h2>
      <div className="overflow-x-auto -mx-2">
        <table className="w-full text-[12px]">
          <thead style={{ color: 'var(--text-muted)' }}>
            <tr>
              <th className={th}>Product</th>
              <th className={th}>Deploys</th>
              <th className={th}>Stories shipped</th>
              <th className={th}>Lead time p50</th>
              <th className={th}>Deploy failure</th>
              <th className={th}>Rollbacks</th>
              <th className={th}>Story coverage</th>
              <th className={th}>Lead-time coverage</th>
              <th className={th}>Stale services</th>
              <th className={th}>Incidents</th>
              <th className={th}>Change failure</th>
              <th className={th}>Restore p50</th>
            </tr>
          </thead>
          <tbody>
            {rows.map((r) => (
              <tr key={r.product} className="border-t" style={{ borderColor: 'var(--border-color)' }}>
                <td className={`${td} font-medium`}>
                  <Link to={`/analytics?product=${encodeURIComponent(r.product)}`} className="hover:underline">{r.product}</Link>
                </td>
                <td className={td}>
                  {r.deploys}
                  {r.previousDeploys !== r.deploys && (
                    <span style={{ color: 'var(--text-muted)' }}> ({r.deploys - r.previousDeploys > 0 ? '+' : ''}{r.deploys - r.previousDeploys})</span>
                  )}
                </td>
                <td className={td}>{r.storiesShipped}</td>
                <td className={td}>{fmtHours(r.leadTimeP50Hours)}</td>
                <td className={td}>{fmtRatio(r.deployFailureRate)}</td>
                <td className={td}>{fmtRatio(r.rollbackRate)}</td>
                <td className={td} style={{ color: (r.storyCoverage ?? 1) < 0.5 ? 'var(--warning)' : undefined }}>{fmtRatio(r.storyCoverage)}</td>
                <td className={td} style={{ color: (r.leadTimeCoverage ?? 1) < 0.5 ? 'var(--warning)' : undefined }}>{fmtRatio(r.leadTimeCoverage)}</td>
                <td className={td} style={{ color: r.staleServices > 0 ? 'var(--warning)' : undefined }}>{r.staleServices} / {r.services}</td>
                <td className={td} style={{ color: r.incidents > 0 ? 'var(--danger)' : undefined }}>{r.incidents}</td>
                <td className={td}>{fmtRatio(r.changeFailureRate)}</td>
                <td className={td}>{fmtHours(r.timeToRestoreP50Hours)}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </section>
  );
}

function ServiceTable({ rows, showProduct }: { rows: DeliveryServiceRow[]; showProduct: boolean }) {
  const [showAll, setShowAll] = useState(false);
  const [search, setSearch] = useState('');
  if (rows.length === 0) return null;
  const stale = rows.filter((r) => r.deploys === 0).length;
  const needle = search.trim().toLowerCase();
  const listed = (showAll || needle ? rows : rows.slice(0, SERVICE_TOP_N))
    .filter((r) => !needle || r.service.toLowerCase().includes(needle) || r.product.toLowerCase().includes(needle));
  const th = 'text-left font-medium px-2 py-1.5 whitespace-nowrap';
  const td = 'px-2 py-1.5 whitespace-nowrap tabular-nums';
  return (
    <section className="rounded-xl border p-4" style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-primary)' }}>
      <div className="flex flex-wrap items-center gap-2 mb-1">
        <h2 className="text-[13px] font-semibold flex items-center gap-1.5 mr-auto">
          Per service · all environments
          <InfoPopover
            label="Per service"
            content={{
              what: 'Cadence and quality per service, over every environment.',
              how: 'Successful, non-rollback deploys per service in the window; failed attempts and rollbacks apart; batch size is work items per deploy; lead time is commit → first production landing of the stories this service shipped. A service with history but no deploy in the window is STALE and listed with its true last deploy.',
              why: 'DORA is measured per service and aggregated up. The stale list is the action item: a service nobody deploys is where risk accumulates.',
            }}
          />
        </h2>
        <label className="inline-flex items-center gap-1.5 text-[12px] px-2 py-1 rounded-lg border" style={{ borderColor: 'var(--border-color)', color: 'var(--text-muted)' }}>
          <Search size={12} />
          <input value={search} onChange={(e) => setSearch(e.target.value)} placeholder="Find a service" className="bg-transparent outline-none w-36" style={{ color: 'var(--text-primary)' }} />
        </label>
      </div>
      <p className="text-[12px] mb-3" style={{ color: 'var(--text-muted)' }}>
        {rows.length} services{stale > 0 && <> · <span style={{ color: 'var(--warning)' }}>{stale} stale</span></>}
      </p>
      <div className="overflow-x-auto -mx-2">
        <table className="w-full text-[12px]">
          <thead style={{ color: 'var(--text-muted)' }}>
            <tr>
              <th className={th}>Service</th>
              <th className={th}>Deploys</th>
              <th className={th}>/ week</th>
              <th className={th}>Last deploy</th>
              <th className={th}>Median interval</th>
              <th className={th}>Failed</th>
              <th className={th}>Rollbacks</th>
              <th className={th}>Batch p50</th>
              <th className={th}>Lead time p50</th>
              <th className={th}>Incidents</th>
            </tr>
          </thead>
          <tbody>
            {listed.map((r) => (
              <tr key={`${r.product}/${r.service}`} className="border-t" style={{ borderColor: 'var(--border-color)', color: r.deploys === 0 ? 'var(--text-muted)' : undefined }}>
                <td className={`${td} font-medium`}>
                  <Link to={`/deployments/${encodeURIComponent(r.product)}/${encodeURIComponent(r.service)}`} className="hover:underline">
                    {showProduct && <span style={{ color: 'var(--text-muted)' }}>{r.product} · </span>}
                    {r.service}
                  </Link>
                  {r.deploys === 0 && <span className="ml-1.5 text-[10px] uppercase" style={{ color: 'var(--warning)' }}>stale</span>}
                </td>
                <td className={td}>
                  {r.deploys}
                  {r.previousDeploys !== r.deploys && (
                    <span style={{ color: 'var(--text-muted)' }}> ({r.deploys - r.previousDeploys > 0 ? '+' : ''}{r.deploys - r.previousDeploys})</span>
                  )}
                </td>
                <td className={td}>{fmtCount(r.perWeek)}</td>
                <td className={td} title={fmtDateTime(r.lastDeployedAt)}>{fmtAgo(r.lastDeployedAt)}</td>
                <td className={td}>{fmtHours(r.medianIntervalHours)}</td>
                <td className={td} style={{ color: r.failed > 0 ? 'var(--danger)' : undefined }}>{r.failed}</td>
                <td className={td} style={{ color: r.rollbacks > 0 ? 'var(--warning)' : undefined }}>{r.rollbacks}</td>
                <td className={td}>{fmtCount(r.batchSizeP50)}</td>
                <td className={td}>{fmtHours(r.leadTimeP50Hours)}</td>
                <td className={td} style={{ color: r.incidents > 0 ? 'var(--danger)' : undefined }}>{r.incidents}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
      {rows.length > SERVICE_TOP_N && !needle && (
        <button onClick={() => setShowAll((v) => !v)} className="mt-2 text-[12px] underline" style={{ color: 'var(--text-secondary)' }}>
          {showAll ? `Show top ${SERVICE_TOP_N}` : `Show all ${rows.length}`}
        </button>
      )}
    </section>
  );
}

const ATTRIBUTION_LABEL: Record<DeliveryIncident['attribution'], string> = {
  explicit: 'deploy named',
  version: 'by version',
  inferred: 'live at start',
  'outside-window': 'outside window',
  none: 'unattributed',
};

/**
 * The incidents behind change failure rate and time to restore, with how each was tied to a deploy.
 * Admin-only: the API leaves the list out for everyone else, so this never renders for them.
 */
function IncidentTable({ incidents, showProduct }: { incidents: DeliveryIncident[]; showProduct: boolean }) {
  if (incidents.length === 0) return null;
  const th = 'text-left font-medium px-2 py-1.5 whitespace-nowrap';
  const td = 'px-2 py-1.5 whitespace-nowrap tabular-nums';
  return (
    <section className="rounded-xl border p-4" style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-primary)' }}>
      <h2 className="text-[13px] font-semibold mb-1 flex items-center gap-1.5">
        Incidents in this window
        <InfoPopover
          label="Incidents in this window"
          content={{
            what: 'The production incidents behind change failure rate and time to restore, and how each was tied to a deployment.',
            how: '"deploy named" and "by version" are explicit; "live at start" is the deploy that was running when the incident began, within the rollout tolerance; "outside window" means a deploy was found but older than the attribution window, so it is not counted as a change failure; "unattributed" found no prior deploy. Settings → Delivery Metrics holds the two knobs.',
            why: 'Push producers and responders towards naming the version or the deploy: inferred attribution is a guess that gets worse the busier a service is.',
          }}
        />
        <span className="font-normal text-[11px] ml-1" style={{ color: 'var(--text-muted)' }}>admins only</span>
      </h2>
      <div className="overflow-x-auto -mx-2">
        <table className="w-full text-[12px]">
          <thead style={{ color: 'var(--text-muted)' }}>
            <tr>
              <th className={th}>Started</th>
              <th className={th}>Service</th>
              <th className={th}>Environment</th>
              <th className={th}>Restored after</th>
              <th className={th}>Attribution</th>
              <th className={th}>Deploy</th>
              <th className={th}>Link</th>
            </tr>
          </thead>
          <tbody>
            {incidents.map((i) => (
              <tr key={i.id} className="border-t" style={{ borderColor: 'var(--border-color)' }}>
                <td className={td}>{fmtDateTime(i.startedAt)}</td>
                <td className={td}>
                  {showProduct && <span style={{ color: 'var(--text-muted)' }}>{i.product} · </span>}{i.service}
                </td>
                <td className={td}><EnvLabel env={i.environment} /></td>
                <td className={td} style={{ color: i.resolvedAt ? undefined : 'var(--danger)' }}>{i.resolvedAt ? fmtHours(i.durationHours) : 'open'}</td>
                <td className={td} style={{ color: i.attribution === 'none' || i.attribution === 'outside-window' ? 'var(--warning)' : undefined }}>{ATTRIBUTION_LABEL[i.attribution]}</td>
                <td className={`${td} font-mono`}>
                  {i.deployEventId ? <Link to={`/deployments/events/${i.deployEventId}`} className="underline">{i.version ?? 'deploy'}</Link> : (i.version ?? '—')}
                </td>
                <td className={td}>{i.url ? <a href={i.url} target="_blank" rel="noreferrer" className="underline">open</a> : '—'}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
      <p className="text-[11px] mt-2" style={{ color: 'var(--text-muted)' }}>
        Manage records under <Link to="/incidents" className="underline">Incidents</Link>.
      </p>
    </section>
  );
}

// ── Export ─────────────────────────────────────────────────────────────────

/**
 * The response as a spreadsheet: metrics, then products, then services, each block with its own
 * header. Definitions ride along so a pasted number keeps saying how it was counted.
 */
function downloadCsv(report: DeliveryMetricsResponse, scopeLabel: string, periodLabel: string) {
  const lines: string[] = [];
  lines.push(csvLine(['Delivery metrics', scopeLabel, periodLabel, `as of ${report.asOf}`]));
  lines.push(csvLine(['Production', report.production.environments.join(' '), report.production.source, report.production.explanation]));
  lines.push(csvLine(['Window', report.range.from, report.range.to, `previous ${report.previousRange.from} – ${report.previousRange.to}`]));
  lines.push('');
  lines.push(csvLine(['section', 'metric', 'available', 'value', 'unit', 'n', 'p50', 'p75', 'p90', 'previous', 'change', 'coverage', 'coverageLabel', 'definition', 'reason']));
  for (const s of report.sections) {
    for (const m of s.metrics) {
      lines.push(csvLine([
        s.title, m.title, m.available ? 'yes' : 'no', m.value, m.unit, m.n,
        m.percentiles?.p50, m.percentiles?.p75, m.percentiles?.p90,
        m.delta?.previous, m.delta?.change, m.coverage?.ratio, m.coverage?.label, m.definition, m.reason,
      ]));
    }
  }
  lines.push('');
  lines.push(csvLine(['product', 'services', 'deploys', 'previousDeploys', 'storiesShipped', 'leadTimeP50Hours', 'leadTimeCoverage', 'deployFailureRate', 'rollbackRate', 'storyCoverage', 'staleServices']));
  for (const r of report.byProduct) {
    lines.push(csvLine([r.product, r.services, r.deploys, r.previousDeploys, r.storiesShipped, r.leadTimeP50Hours, r.leadTimeCoverage, r.deployFailureRate, r.rollbackRate, r.storyCoverage, r.staleServices]));
  }
  lines.push('');
  lines.push(csvLine(['product', 'service', 'deploys', 'previousDeploys', 'perWeek', 'lastDeployedAt', 'medianIntervalHours', 'failed', 'rollbacks', 'batchSizeP50', 'leadTimeP50Hours']));
  for (const r of report.byService) {
    lines.push(csvLine([r.product, r.service, r.deploys, r.previousDeploys, r.perWeek, r.lastDeployedAt, r.medianIntervalHours, r.failed, r.rollbacks, r.batchSizeP50, r.leadTimeP50Hours]));
  }

  const blob = new Blob(['﻿' + lines.join('\r\n')], { type: 'text/csv;charset=utf-8' });
  const url = URL.createObjectURL(blob);
  const a = document.createElement('a');
  a.href = url;
  a.download = `delivery-metrics-${report.range.from.slice(0, 10)}-${report.range.to.slice(0, 10)}.csv`;
  document.body.appendChild(a);
  a.click();
  a.remove();
  URL.revokeObjectURL(url);
}
