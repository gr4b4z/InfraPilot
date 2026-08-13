import { useEffect, useMemo, useState } from 'react';
import { Link, useNavigate, useSearchParams } from 'react-router-dom';
import {
  AlertTriangle,
  ChartColumn,
  CheckCircle2,
  CircleDashed,
  Clock,
  GitCommitHorizontal,
  Rocket,
  Search,
  Ticket,
} from 'lucide-react';
import {
  api,
  type FrequencyResponse,
  type LeadTimeResponse,
  type MatrixCell,
  type PromotionQueueResponse,
  type WorkItemMatrixResponse,
} from '@/lib/api';
import { useSettingsStore } from '@/stores/settingsStore';
import { EnvLabel } from '@/components/environments/EnvBadge';
import { StatTiles, type StatTile } from '@/components/ui/StatTiles';
import { InfoPopover } from '@/components/ui/InfoPopover';
import { ListEmptyState } from '@/components/ui/ListEmptyState';
import { useEntityRefresh } from '@/hooks/useEntityEvents';
import { useDocumentTitle } from '@/lib/pageTitle';
import { resolveProductionEnvs, type ProdSource } from '@/lib/envStage';
import type { EnvironmentConfig } from '@/stores/settingsStore';
import { DeployTrendChart } from './DeployTrendChart';

/** Sentinel product meaning "aggregate layer 1 across every product". */
const ALL_PRODUCTS = 'all';

const PERIODS = [
  { key: '14', label: '14 days' },
  { key: '30', label: '30 days' },
  { key: 'this-month', label: 'This month' },
  { key: 'last-month', label: 'Last month' },
] as const;

/**
 * Rolling windows for day-to-day use; calendar months for reporting. The distinction matters:
 * a rolling "last 30 days" shifts every day, so two looks at "the same report" a few days apart
 * disagree — calendar presets give leadership a window that holds still.
 */
function computeRange(key: string): { from: string; to?: string; days: number } {
  const now = new Date();
  if (key === 'this-month') {
    const from = new Date(now.getFullYear(), now.getMonth(), 1);
    return { from: from.toISOString(), days: (now.getTime() - from.getTime()) / 86_400_000 || 1 };
  }
  if (key === 'last-month') {
    const from = new Date(now.getFullYear(), now.getMonth() - 1, 1);
    const to = new Date(now.getFullYear(), now.getMonth(), 1);
    return {
      from: from.toISOString(),
      to: to.toISOString(),
      days: (to.getTime() - from.getTime()) / 86_400_000,
    };
  }
  const days = key === '30' ? 30 : 14;
  return { from: new Date(now.getTime() - days * 86_400_000).toISOString(), days };
}

/**
 * Analytics: two layers over the same window.
 *
 * Layer 1 is the executive strip — trend chart and KPI tiles with delta vs the previous
 * equal-length period, plus "shipped this period" / "in flight" lists. It fits one screen and
 * supports "All products" for org-level reporting.
 *
 * Layer 2 is the team view: story × environment matrix (searchable, filterable to "not yet on
 * env"), promotion queue, per-service cadence including stale services. Product-scoped — the
 * story matrix has no meaningful cross-product form, so "All products" hides it with a hint.
 *
 * Numbers are live — computed from the transactional tables at request time, no snapshots.
 */
export function AnalyticsPage() {
  useDocumentTitle(['Analytics']);
  const [searchParams, setSearchParams] = useSearchParams();
  const { getDisplayName, getOrderedEnvironments, environments: configuredEnvs } = useSettingsStore();
  const refreshTick = useEntityRefresh(['deployment', 'promotion']);

  const [products, setProducts] = useState<string[]>([]);
  const product = searchParams.get('product') ?? products[0] ?? '';
  const allProducts = product === ALL_PRODUCTS;
  const periodKey = searchParams.get('period') ?? '14';
  const notYetOn = searchParams.get('notYetOn') ?? '';
  const [search, setSearch] = useState('');

  const [frequency, setFrequency] = useState<FrequencyResponse | null>(null);
  const [serviceFrequency, setServiceFrequency] = useState<FrequencyResponse | null>(null);
  const [matrix, setMatrix] = useState<WorkItemMatrixResponse | null>(null);
  const [shipped, setShipped] = useState<WorkItemMatrixResponse | null>(null);
  const [queue, setQueue] = useState<PromotionQueueResponse | null>(null);
  const [leadTime, setLeadTime] = useState<LeadTimeResponse | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    api
      .getDeploymentProducts()
      .then((list) => setProducts(list.map((p) => p.product)))
      .catch(() => setProducts([]));
  }, []);

  const range = useMemo(
    () => computeRange(periodKey),
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [periodKey, refreshTick],
  );
  const tz = useMemo(() => Intl.DateTimeFormat().resolvedOptions().timeZone, []);

  // The production environment SET the executive tiles report on, with its provenance (surfaced
  // in the tiles' ⓘ popovers): explicitly marked in settings → name-based default mapping for
  // unconfigured keys ("prod", "production", "live"…) → last-in-order convention. A product
  // genuinely deployed to several production environments gets ALL of them — tiles aggregate
  // over the set, "shipped" means "landed on every one".
  const { prodEnvs, prodSource } = useMemo(() => {
    const universe = allProducts
      ? (frequency?.series ?? []).map((s) => s.key.environment).filter((e): e is string => !!e)
      : (matrix?.environments ?? []);
    const union = Array.from(
      new Set([...universe, ...(allProducts ? configuredEnvs.map((e) => e.key) : [])]),
    );
    const ordered = getOrderedEnvironments(union);
    const { envs, source } = resolveProductionEnvs(union, configuredEnvs, ordered);
    return { prodEnvs: envs, prodSource: source };
  }, [allProducts, frequency, matrix, configuredEnvs, getOrderedEnvironments]);

  // Loading starts true and flips once: refetches (period/product/filter changes) keep the
  // previous data on screen until the new response swaps in, instead of flashing skeletons.
  useEffect(() => {
    if (!product) return;
    let cancelled = false;

    const productParam = allProducts ? undefined : product;
    const window = { from: range.from, to: range.to };

    const matrixReq = allProducts
      ? Promise.resolve(null)
      : api.getWorkItemMatrix({
          product,
          ...window,
          environment: notYetOn || undefined,
          limit: 200,
        });

    Promise.all([
      api.getDeploymentFrequency({ product: productParam, ...window, groupBy: 'environment', tz }),
      // summaryOnly: the cadence table reads summaries alone, and at hundreds of services the
      // zero-filled bucket arrays would dominate the payload.
      api.getDeploymentFrequency({ product: productParam, ...window, groupBy: 'service', tz, summaryOnly: true }),
      matrixReq,
      api.getPromotionQueueStats({ product: productParam, ...window }),
      api.getLeadTime({ product: productParam, ...window, tz }),
      // "Shipped this period" targets the SAME production set the tiles report on — resolved
      // here from the matrix's own environments so the list can never disagree with the header.
      matrixReq.then((m) => {
        if (!m) return null;
        const { envs } = resolveProductionEnvs(m.environments, configuredEnvs, m.environments);
        return envs.length > 0
          ? api.getWorkItemMatrix({ product, ...window, reachedEnv: envs.join(','), limit: 100 })
          : null;
      }),
    ])
      .then(([freq, svcFreq, m, q, lt, shippedRes]) => {
        if (cancelled) return;
        setError(null);
        setFrequency(freq);
        setServiceFrequency(svcFreq);
        setMatrix(m);
        setQueue(q);
        setLeadTime(lt);
        setShipped(shippedRes);
      })
      .catch((e) => {
        if (!cancelled) setError(e instanceof Error ? e.message : 'Failed to load analytics');
      })
      .finally(() => {
        if (!cancelled) setLoading(false);
      });

    return () => {
      cancelled = true;
    };
  }, [product, allProducts, notYetOn, range.from, range.to, tz, configuredEnvs]);

  const updateParams = (patch: Record<string, string>) => {
    const next = new URLSearchParams(searchParams);
    for (const [k, v] of Object.entries(patch)) {
      if (v) next.set(k, v);
      else next.delete(k);
    }
    setSearchParams(next, { replace: true });
  };

  const tiles = buildTiles(
    frequency, matrix, queue, leadTime,
    prodEnvs, prodSource, configuredEnvs, getDisplayName, Math.round(range.days), allProducts);

  // In flight = missing from AT LEAST ONE production environment, mirroring shipped's
  // ALL-of-them rule: a story live in one region and absent in the other is still in flight.
  const inFlight = (matrix?.items ?? []).filter(
    (i) => prodEnvs.length > 0 && prodEnvs.some((env) => i.envs[env]?.state !== 'deployed'),
  );

  const period = PERIODS.find((p) => p.key === periodKey) ?? PERIODS[0];

  return (
    <div className="space-y-6">
      {/* Header + global controls: one product, one window, both layers obey them. */}
      <div className="flex flex-wrap items-center gap-3">
        <div className="flex items-center gap-2 mr-auto">
          <ChartColumn size={20} style={{ color: 'var(--accent)' }} />
          <h1 className="text-xl font-semibold">Analytics</h1>
        </div>
        <select
          value={product}
          onChange={(e) => updateParams({ product: e.target.value, notYetOn: '' })}
          className="text-[13px] px-3 py-1.5 rounded-lg border"
          style={{
            borderColor: 'var(--border-color)',
            backgroundColor: 'var(--bg-primary)',
            color: 'var(--text-primary)',
          }}
        >
          {products.length === 0 && <option value="">No products</option>}
          {products.length > 1 && <option value={ALL_PRODUCTS}>All products</option>}
          {products.map((p) => (
            <option key={p} value={p}>
              {p}
            </option>
          ))}
        </select>
        <div
          className="flex rounded-lg border overflow-hidden"
          style={{ borderColor: 'var(--border-color)' }}
        >
          {PERIODS.map((p) => (
            <button
              key={p.key}
              onClick={() => updateParams({ period: p.key })}
              className="px-3 py-1.5 text-[13px]"
              style={{
                backgroundColor: p.key === period.key ? 'var(--accent-bg)' : 'var(--bg-primary)',
                color: p.key === period.key ? 'var(--accent)' : 'var(--text-secondary)',
              }}
            >
              {p.label}
            </button>
          ))}
        </div>
      </div>

      {error && (
        <div
          className="px-4 py-3 rounded-xl border text-[13px]"
          style={{ borderColor: 'var(--danger)', backgroundColor: 'var(--danger-bg)', color: 'var(--danger)' }}
        >
          {error}
        </div>
      )}

      {loading && !frequency ? (
        <div className="space-y-3">
          <div className="skeleton h-24" />
          <div className="skeleton h-40" />
          <div className="skeleton h-64" />
        </div>
      ) : (
        <>
          {/* ── Layer 1: executive strip ─────────────────────────────────── */}
          <StatTiles tiles={tiles} />
          <DeployTrendChart frequency={frequency} />

          {!allProducts && (
            <div className="grid grid-cols-1 lg:grid-cols-2 gap-4">
              <ShippedList shipped={shipped} prodEnvs={prodEnvs} getDisplayName={getDisplayName} />
              <InFlightList items={inFlight} prodEnvs={prodEnvs} getDisplayName={getDisplayName} />
            </div>
          )}

          {/* ── Layer 2: team view ───────────────────────────────────────── */}
          {allProducts ? (
            <p className="text-[13px]" style={{ color: 'var(--text-muted)' }}>
              Select a single product to see the story × environment matrix and its shipped /
              in-flight lists — stories don't aggregate meaningfully across products.
            </p>
          ) : (
            <MatrixSection
              matrix={matrix}
              periodLabel={period.label}
              search={search}
              onSearch={setSearch}
              notYetOn={notYetOn}
              onNotYetOn={(env) => updateParams({ notYetOn: env })}
              getDisplayName={getDisplayName}
            />
          )}
          <div className="grid grid-cols-1 lg:grid-cols-2 gap-4">
            <QueueSection queue={queue} showProduct={allProducts} />
            <ServiceFrequencySection frequency={serviceFrequency} showProduct={allProducts} />
          </div>
        </>
      )}
    </div>
  );
}

// ── Layer 1 pieces ─────────────────────────────────────────────────────────

/** Display label for the production set: one env → its name; several → "Production (n envs)". */
function prodLabel(prodEnvs: string[], getDisplayName: (env: string) => string): string {
  if (prodEnvs.length === 0) return '—';
  if (prodEnvs.length === 1) return getDisplayName(prodEnvs[0]);
  return `Production (${prodEnvs.length} envs)`;
}

function buildTiles(
  frequency: FrequencyResponse | null,
  matrix: WorkItemMatrixResponse | null,
  queue: PromotionQueueResponse | null,
  leadTime: LeadTimeResponse | null,
  prodEnvs: string[],
  prodSource: ProdSource,
  configuredEnvs: EnvironmentConfig[],
  getDisplayName: (env: string) => string,
  periodDays: number,
  allProducts: boolean,
): StatTile[] {
  const envName = prodLabel(prodEnvs, getDisplayName);
  const vsPrev = `vs prev ${periodDays}d`;

  // Aggregate over the production SET: sums for counts, bucket sums for failure math (per-series
  // ratios cannot be averaged), worst-environment view for lead time (percentiles cannot be
  // merged client-side — the honest single number is the slowest region's).
  const prodSeries = (frequency?.series ?? []).filter(
    (s) => s.key.environment !== null && prodEnvs.includes(s.key.environment),
  );
  const deploys = prodSeries.reduce((n, s) => n + s.summary.total, 0);
  const prevDeploys = prodSeries.reduce((n, s) => n + s.summary.previousPeriodTotal, 0);
  const failed = prodSeries.reduce(
    (n, s) => n + s.buckets.reduce((m, b) => m + b.failed, 0), 0);
  const rollbacks = prodSeries.reduce(
    (n, s) => n + s.buckets.reduce((m, b) => m + b.rollbacks, 0), 0);
  const attempts = deploys + failed;
  const cfr = attempts > 0 ? (failed + rollbacks) / attempts : null;

  const approval = queue?.approvalLatency;
  const coverage = matrix?.coverage;
  const leadProd = (leadTime?.byEnvironment ?? [])
    .filter((e) => prodEnvs.includes(e.environment) && e.n > 0)
    .sort((a, b) => (b.p50Hours ?? 0) - (a.p50Hours ?? 0))[0];
  const leadCoverage = leadTime?.coverage.ratio ?? 0;
  const leadCovered = leadCoverage > 0;

  // Where the reported environment set came from — quoted in the popovers so "why does the tile
  // say X?" never needs this conversation's history to answer.
  const envList = prodEnvs.map(getDisplayName).join(', ') || '—';
  const envProvenance =
    prodSource === 'marked'
      ? `Environment${prodEnvs.length > 1 ? 's' : ''}: ${envList} — marked as production stage in Settings → Environments.`
      : prodSource === 'default-name'
        ? `Environment${prodEnvs.length > 1 ? 's' : ''}: ${envList} — recognised as production by name (default mapping; add the key in Settings → Environments to override).`
        : `Environment: ${envList} — last environment in order (no production stage marked).`;
  const configuredHint = configuredEnvs.length === 0 ? ' No environments are configured yet.' : '';

  return [
    {
      label: `Deploys · ${envName}`,
      sub: vsPrev,
      value: String(deploys),
      icon: Rocket,
      color: 'var(--accent)',
      bg: 'var(--accent-bg)',
      delta:
        deploys !== prevDeploys
          ? { text: fmtDelta(deploys - prevDeploys), good: deploys > prevDeploys, up: deploys > prevDeploys }
          : undefined,
      info: {
        what: `Successful deployments to ${envName} in the selected ${periodDays}-day window${prodEnvs.length > 1 ? ' — summed across all marked production environments' : ''}.`,
        how: `Deploy events with status "succeeded". Rollbacks and re-deploys of the same version are not counted (they are reported separately). The delta compares the identical window immediately before. ${envProvenance}${configuredHint}`,
        why: 'Delivery tempo. Neither good nor bad on its own — read it together with the change failure rate next to it: rising tempo at a stable CFR is improvement; rising tempo with a rising CFR is speed on borrowed stability.',
      },
    },
    {
      label: 'Change failure rate',
      sub: `${envName} · failed + rollbacks`,
      value: cfr == null ? '—' : `${Math.round(cfr * 100)}%`,
      icon: AlertTriangle,
      color: 'var(--warning)',
      bg: 'var(--warning-bg)',
      info: {
        what: `The share of changes that failed${prodEnvs.length > 1 ? ', computed jointly over all marked production environments' : ''}.`,
        how: `${frequency?.definition.changeFailureRate ?? '(failed + rollbacks) / (succeeded + failed) within the window'} — counts summed across the production set before dividing (ratios can't be averaged). ${envProvenance}`,
        why: `The counterweight to tempo. Beware small numbers: at 10 deploys a single rollback moves this by 10 percentage points — that's why the deploy count is shown beside it.`,
      },
    },
    {
      label: 'Approval p50',
      sub: approval ? `n=${approval.n}` : undefined,
      value: fmtHours(approval?.p50Hours),
      icon: Clock,
      color: 'var(--info)',
      bg: 'var(--info-bg)',
      info: {
        what: 'Median time from a promotion candidate being created to it being approved.',
        how: `createdAt → approvedAt for candidates approved inside the window (n=${approval?.n ?? 0}). A percentile, not an average — one forgotten candidate should not distort the picture.`,
        why: 'Measures waiting on a HUMAN, not on the pipeline (that is "approved→deployed" in the queue section below). If this grows, the approval process is the bottleneck — not CI.',
      },
    },
    {
      label: 'Story coverage',
      sub: allProducts
        ? 'per product — pick one'
        : coverage
          ? `${coverage.withoutWorkItem} deploys w/o story`
          : undefined,
      value: !allProducts && coverage ? `${Math.round(coverage.ratio * 100)}%` : '—',
      icon: Ticket,
      color: 'var(--success)',
      bg: 'var(--success-bg)',
      muted: allProducts,
      info: {
        what: 'The share of deployments that carry a story/ticket reference.',
        how: coverage
          ? `Deployments in the window with at least one work-item reference (${coverage.deployments - coverage.withoutWorkItem} of ${coverage.deployments}).`
          : 'Deployments in the window with at least one work-item reference, over all deployments. Computed per product.',
        why: 'The credibility of every other number here. At 65% coverage, every per-story metric on this page undercounts by about a third. This is a number to push UP — it rises when pipelines consistently send ticket keys.',
      },
    },
    {
      label: `Lead time p50 · ${envName}`,
      sub: leadCovered
        ? `coverage ${Math.round(leadCoverage * 100)}%${prodEnvs.length > 1 && leadProd ? ` · slowest: ${getDisplayName(leadProd.environment)}` : ''}`
        : 'awaiting producer data (occurredAt)',
      value: leadCovered ? fmtHours(leadProd?.p50Hours) : '—',
      icon: GitCommitHorizontal,
      color: 'var(--text-muted)',
      bg: 'var(--bg-secondary)',
      muted: !leadCovered,
      info: {
        what: `Median time from a change being merged to its first successful deployment to ${envName} — cumulative from the commit, so this measures the whole path, not the last hop.`,
        how: `Clock start: ${leadTime?.definition.clockStart ?? 'pull-request.occurredAt'} (fallback: ${leadTime?.definition.clockStartFallback ?? 'commit.occurredAt'}); clock stop: first successful deploy per environment; grain: story × environment. ${prodEnvs.length > 1 ? 'With several production environments the tile shows the SLOWEST one — percentiles cannot be merged; per-environment figures are in the lead-time data. ' : ''}Only stories whose producer sent timestamps are measurable — current coverage ${Math.round(leadCoverage * 100)}%. ${envProvenance}`,
        why: 'How long code waits on its way to users. Do NOT compare periods with materially different coverage — a backfill reaching deeper into one quarter than another manufactures a trend.',
      },
    },
  ];
}

function ShippedList({
  shipped,
  prodEnvs,
  getDisplayName,
}: {
  shipped: WorkItemMatrixResponse | null;
  prodEnvs: string[];
  getDisplayName: (env: string) => string;
}) {
  const items = shipped?.items ?? [];
  const label = prodLabel(prodEnvs, getDisplayName);
  return (
    <section
      className="rounded-xl border p-4"
      style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-primary)' }}
    >
      <h2 className="text-[13px] font-semibold mb-3 flex items-center gap-2">
        <CheckCircle2 size={14} style={{ color: 'var(--success)' }} />
        Shipped this period · {label}
        <span style={{ color: 'var(--text-muted)' }}>({shipped?.totalItems ?? 0})</span>
        {prodEnvs.length > 1 && (
          <InfoPopover
            label="Shipped this period"
            content={{
              what: `Stories that reached EVERY production environment (${prodEnvs.map(getDisplayName).join(', ')}).`,
              how: 'A story counts as shipped when its first successful deploy has landed on all marked production environments, dated by the one that completed the set.',
              why: '"Shipped" in a report means customers have it everywhere — counting from the first region would flatter the number while a rollout is still in progress.',
            }}
          />
        )}
      </h2>
      {items.length === 0 ? (
        <p className="text-[13px]" style={{ color: 'var(--text-muted)' }}>
          No stories reached {label} in this window.
        </p>
      ) : (
        <ul className="space-y-1.5">
          {items.slice(0, 8).map((i) => (
            <li key={i.key} className="text-[13px] flex items-baseline gap-2 min-w-0">
              <WorkItemLink itemKey={i.key} />
              <span className="truncate" style={{ color: 'var(--text-secondary)' }}>
                {i.title}
              </span>
            </li>
          ))}
          {items.length > 8 && (
            <li className="text-[12px]" style={{ color: 'var(--text-muted)' }}>
              +{(shipped?.totalItems ?? items.length) - 8} more
            </li>
          )}
        </ul>
      )}
    </section>
  );
}

function InFlightList({
  items,
  prodEnvs,
  getDisplayName,
}: {
  items: WorkItemMatrixResponse['items'];
  prodEnvs: string[];
  getDisplayName: (env: string) => string;
}) {
  const label = prodLabel(prodEnvs, getDisplayName);
  return (
    <section
      className="rounded-xl border p-4"
      style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-primary)' }}
    >
      <h2 className="text-[13px] font-semibold mb-3 flex items-center gap-2">
        <CircleDashed size={14} style={{ color: 'var(--warning)' }} />
        In flight — not yet on {label}
        <span style={{ color: 'var(--text-muted)' }}>({items.length})</span>
      </h2>
      {items.length === 0 ? (
        <p className="text-[13px]" style={{ color: 'var(--text-muted)' }}>
          Everything with recent activity is fully deployed.
        </p>
      ) : (
        <ul className="space-y-1.5">
          {items.slice(0, 8).map((i) => (
            <li key={i.key} className="text-[13px] flex items-baseline gap-2 min-w-0">
              <WorkItemLink itemKey={i.key} />
              <span className="truncate flex-1" style={{ color: 'var(--text-secondary)' }}>
                {i.title}
              </span>
              <span className="shrink-0" style={{ color: 'var(--text-muted)' }}>
                {prodEnvs.length > 1
                  ? `missing: ${prodEnvs
                      .filter((env) => i.envs[env]?.state !== 'deployed')
                      .map(getDisplayName)
                      .join(', ')}`
                  : i.furthestEnv
                    ? `on ${getDisplayName(i.furthestEnv)}`
                    : 'not deployed'}
              </span>
            </li>
          ))}
          {items.length > 8 && (
            <li className="text-[12px]" style={{ color: 'var(--text-muted)' }}>
              +{items.length - 8} more — see the matrix below
            </li>
          )}
        </ul>
      )}
    </section>
  );
}

// ── Layer 2 pieces ─────────────────────────────────────────────────────────

/** Rows the matrix shows before "Show all" — recent activity first, the rest one click away. */
const MATRIX_TOP_N = 15;

function MatrixSection({
  matrix,
  periodLabel,
  search,
  onSearch,
  notYetOn,
  onNotYetOn,
  getDisplayName,
}: {
  matrix: WorkItemMatrixResponse | null;
  periodLabel: string;
  search: string;
  onSearch: (v: string) => void;
  notYetOn: string;
  onNotYetOn: (env: string) => void;
  getDisplayName: (env: string) => string;
}) {
  const [showAllRows, setShowAllRows] = useState(false);
  if (!matrix) return null;
  const { environments, coverage, totals } = matrix;

  const needle = search.trim().toLowerCase();
  const matching = needle
    ? matrix.items.filter(
        (i) =>
          i.key.toLowerCase().includes(needle) || (i.title ?? '').toLowerCase().includes(needle),
      )
    : matrix.items;
  const filtered = Boolean(needle || notYetOn);
  // Capped by default — at dozens of deploys a week this list runs long, and the newest activity
  // (the sort order) is what people come for. Searching always shows every match.
  const items = needle || showAllRows ? matching : matching.slice(0, MATRIX_TOP_N);
  const hiddenRows = matching.length - items.length;

  return (
    <section className="space-y-2">
      <div className="flex flex-wrap items-center gap-3">
        <div className="flex items-baseline gap-2 mr-auto">
          <h2 className="text-[15px] font-semibold flex items-center gap-1.5">
            Stories × environments
            <InfoPopover
              label="Stories × environments"
              content={{
                what: 'Which stories are deployed — or waiting — in which environment.',
                how: 'The time window selects WHICH stories appear (any deploy or promotion activity inside it, or a currently open candidate); the cells always show full state, including deploys from before the window. A story is linked to a deployment through the work-item references its pipeline sends.',
                why: 'Answers "where is ticket X" and "what has not reached production yet" without opening Jira. Read it alongside the coverage strip: deployments without a story reference are invisible here.',
              }}
            />
          </h2>
          <span className="text-[12px]" style={{ color: 'var(--text-muted)' }}>
            {matrix.totalItems} stories with activity in the last {periodLabel.toLowerCase()}
          </span>
        </div>
        <div className="relative">
          <Search
            size={13}
            className="absolute left-2.5 top-1/2 -translate-y-1/2"
            style={{ color: 'var(--text-muted)' }}
          />
          <input
            value={search}
            onChange={(e) => onSearch(e.target.value)}
            placeholder="Find story…"
            className="text-[13px] pl-8 pr-3 py-1.5 rounded-lg border w-52"
            style={{
              borderColor: 'var(--border-color)',
              backgroundColor: 'var(--bg-primary)',
              color: 'var(--text-primary)',
            }}
          />
        </div>
        <select
          value={notYetOn}
          onChange={(e) => onNotYetOn(e.target.value)}
          className="text-[13px] px-3 py-1.5 rounded-lg border"
          style={{
            borderColor: notYetOn ? 'var(--accent)' : 'var(--border-color)',
            backgroundColor: 'var(--bg-primary)',
            color: notYetOn ? 'var(--accent)' : 'var(--text-primary)',
          }}
        >
          <option value="">All stories</option>
          {environments.map((env) => (
            <option key={env} value={env}>
              Not yet on {getDisplayName(env)}
            </option>
          ))}
        </select>
      </div>

      {/* Legend: the cell states ARE the product here — nobody should need a hover to learn them. */}
      <div className="flex flex-wrap gap-x-4 gap-y-1 text-[12px]" style={{ color: 'var(--text-muted)' }}>
        <span className="inline-flex items-center gap-1.5">
          <CheckCircle2 size={13} style={{ color: 'var(--success)' }} /> deployed
        </span>
        <span className="inline-flex items-center gap-1.5">
          <Clock size={13} style={{ color: 'var(--warning)' }} /> awaiting approval
        </span>
        <span className="inline-flex items-center gap-1.5">
          <CircleDashed size={13} style={{ color: 'var(--info)' }} /> approved · awaiting deploy
        </span>
        <span className="inline-flex items-center gap-1.5">— no activity</span>
      </div>

      {/* Coverage strip — permanent, not dismissible: with a third of deploys carrying no
          story reference, every count on this page must be read alongside this number. */}
      {coverage.withoutWorkItem > 0 && (
        <div
          className="flex items-center gap-2 px-3 py-2 rounded-lg border text-[12px]"
          style={{ borderColor: 'var(--warning)', backgroundColor: 'var(--warning-bg)', color: 'var(--warning)' }}
        >
          <AlertTriangle size={13} />
          {coverage.withoutWorkItem} of {coverage.deployments} deployments in this window carry no story
          reference — these numbers undercount what actually shipped.
        </div>
      )}

      {items.length === 0 ? (
        <ListEmptyState
          icon={Ticket}
          tone={filtered ? 'filtered' : 'neutral'}
          title={filtered ? 'No stories match the filters' : 'No stories in this window'}
          body={
            filtered
              ? 'Loosen the search or the environment filter.'
              : 'No work items were referenced by deployments or open promotions in the selected period.'
          }
          filters={[
            ...(needle ? [{ label: 'Search', value: search, onClear: () => onSearch('') }] : []),
            ...(notYetOn
              ? [{ label: 'Not yet on', value: getDisplayName(notYetOn), onClear: () => onNotYetOn('') }]
              : []),
          ]}
        />
      ) : (
        <div
          className="rounded-xl border overflow-x-auto"
          style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-primary)' }}
        >
          <table className="w-full min-w-max text-[13px]">
            <thead>
              <tr style={{ backgroundColor: 'var(--bg-secondary)' }}>
                <th className="text-left px-4 py-3 font-medium" style={{ color: 'var(--text-muted)' }}>
                  Story
                </th>
                {environments.map((env) => (
                  <th key={env} className="text-center px-4 py-3 font-medium">
                    <EnvLabel env={env} />
                    <div className="text-[11px] font-normal" style={{ color: 'var(--text-muted)' }}>
                      {totals[env] ?? 0} deployed
                    </div>
                  </th>
                ))}
              </tr>
            </thead>
            <tbody>
              {items.map((item) => (
                <tr key={item.key} style={{ borderTop: '1px solid var(--border-color)' }}>
                  <td className="px-4 py-2.5 max-w-105">
                    <div className="flex items-baseline gap-2 min-w-0">
                      <WorkItemLink itemKey={item.key} />
                      <span className="truncate" style={{ color: 'var(--text-secondary)' }}>
                        {item.title}
                      </span>
                    </div>
                  </td>
                  {environments.map((env) => (
                    <td key={env} className="px-4 py-2.5 text-center">
                      <MatrixCellView cell={item.envs[env]} env={getDisplayName(env)} />
                    </td>
                  ))}
                </tr>
              ))}
            </tbody>
          </table>
          {(hiddenRows > 0 || showAllRows) && !needle && (
            <button
              onClick={() => setShowAllRows((v) => !v)}
              className="m-3 text-[12px] underline"
              style={{ color: 'var(--accent)' }}
            >
              {showAllRows
                ? `Show latest ${MATRIX_TOP_N} only`
                : `Show all ${matching.length} stories`}
            </button>
          )}
        </div>
      )}
    </section>
  );
}

/**
 * One checkmark cell. Not a boolean on purpose: "not on prod" splits into "approved, waiting for
 * the pipeline", "waiting for a human", and "nothing staged" — which is exactly the question the
 * matrix exists to answer. Deployed cells link the deploy event; pending ones the candidate.
 */
function MatrixCellView({ cell, env }: { cell?: MatrixCell; env: string }) {
  const navigate = useNavigate();
  if (!cell || cell.state === 'absent') {
    return <span style={{ color: 'var(--text-muted)' }}>—</span>;
  }
  if (cell.state === 'deployed') {
    const label = `Deployed to ${env}${cell.version ? `: ${cell.version}` : ''}`;
    return (
      <button
        onClick={() => cell.deployEventId && navigate(`/deployments/events/${cell.deployEventId}`)}
        title={cell.version ? `${cell.version} · ${fmtDate(cell.at)}` : undefined}
        aria-label={label}
        className="inline-flex"
        style={{ color: 'var(--success)' }}
      >
        <CheckCircle2 size={16} />
      </button>
    );
  }
  const pendingApproval = cell.state === 'awaiting-approval';
  const label = pendingApproval
    ? `Awaiting approval for ${env}`
    : `Approved for ${env} · awaiting deploy`;
  return (
    <button
      onClick={() => cell.candidateId && navigate(`/promotions/${cell.candidateId}`)}
      title={label}
      aria-label={label}
      className="inline-flex items-center gap-1 text-[11px]"
      style={{ color: pendingApproval ? 'var(--warning)' : 'var(--info)' }}
    >
      {pendingApproval ? <Clock size={14} /> : <CircleDashed size={14} />}
    </button>
  );
}

function QueueSection({
  queue,
  showProduct,
}: {
  queue: PromotionQueueResponse | null;
  showProduct: boolean;
}) {
  if (!queue) return null;
  return (
    <section
      className="rounded-xl border p-4"
      style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-primary)' }}
    >
      <h2 className="text-[13px] font-semibold mb-3 flex items-center gap-1.5">
        Promotion queue
        <InfoPopover
          label="Promotion queue"
          content={{
            what: 'What is waiting to move forward right now, per target environment.',
            how: 'Open promotion candidates: "Pending" awaits a human approval, "Awaiting deploy" is approved but not yet landed. "Oldest" is the age of the longest-waiting candidate. The two latencies below are computed over candidates that closed inside the window: approval = createdAt→approvedAt, approved→deployed = approvedAt→deployedAt (p50/p90).',
            why: 'This is the actionable list — a candidate aging in the queue is a decision someone owes today, not a trend to review next quarter. The two latencies split "waiting on a human" from "waiting on the pipeline", which point at different fixes.',
          }}
        />
      </h2>
      {queue.edges.length === 0 ? (
        <p className="text-[13px]" style={{ color: 'var(--text-muted)' }}>
          Nothing is waiting for approval or deploy.
        </p>
      ) : (
        <table className="w-full text-[13px]">
          <thead>
            <tr style={{ color: 'var(--text-muted)' }}>
              {showProduct && <th className="text-left py-1 font-medium">Product</th>}
              <th className="text-left py-1 font-medium">Target env</th>
              <th className="text-right py-1 font-medium">Pending</th>
              <th className="text-right py-1 font-medium">Awaiting deploy</th>
              <th className="text-right py-1 font-medium">Oldest</th>
            </tr>
          </thead>
          <tbody>
            {queue.edges.map((e) => (
              <tr key={`${e.product}/${e.targetEnv}`} style={{ borderTop: '1px solid var(--border-color)' }}>
                {showProduct && <td className="py-1.5 font-medium">{e.product}</td>}
                <td className="py-1.5">
                  <EnvLabel env={e.targetEnv} />
                </td>
                <td className="py-1.5 text-right">{e.pending}</td>
                <td className="py-1.5 text-right">{e.awaitingDeploy}</td>
                <td className="py-1.5 text-right" style={{ color: 'var(--text-muted)' }}>
                  {fmtHours(maxOf(e.oldestPendingHours, e.oldestAwaitingDeployHours))}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
      <div className="mt-3 pt-3 text-[12px] flex flex-wrap gap-x-5 gap-y-1" style={{ borderTop: '1px solid var(--border-color)', color: 'var(--text-muted)' }}>
        <span>
          Approval p50/p90: <b style={{ color: 'var(--text-primary)' }}>{fmtHours(queue.approvalLatency.p50Hours)}</b> /{' '}
          {fmtHours(queue.approvalLatency.p90Hours)} (n={queue.approvalLatency.n})
        </span>
        <span>
          Approved→deployed p50/p90: <b style={{ color: 'var(--text-primary)' }}>{fmtHours(queue.deployLatency.p50Hours)}</b> /{' '}
          {fmtHours(queue.deployLatency.p90Hours)} (n={queue.deployLatency.n})
        </span>
      </div>
      <p className="mt-1 text-[11px]" style={{ color: 'var(--text-muted)' }}>
        Approval = waiting for a human; approved→deployed = waiting for the pipeline.
      </p>
    </section>
  );
}

/**
 * Cadence at fleet scale (hundreds of services): the section shows SIGNAL, not inventory —
 * a one-line summary, the stale services (the alarm this section exists to ring), and the top
 * of the league table. The full list stays one click away, with a search, so the page never
 * renders 400 rows nobody reads.
 */
const CADENCE_TOP_N = 10;

function ServiceFrequencySection({
  frequency,
  showProduct,
}: {
  frequency: FrequencyResponse | null;
  showProduct: boolean;
}) {
  const [showAll, setShowAll] = useState(false);
  const [staleOpen, setStaleOpen] = useState(false);
  const [search, setSearch] = useState('');
  if (!frequency) return null;

  const active = frequency.series
    .filter((s) => s.summary.total > 0)
    .sort((a, b) => b.summary.total - a.summary.total);
  // Oldest-first: the service nobody touched the longest is the headline.
  const stale = frequency.series
    .filter((s) => s.summary.total === 0)
    .sort((a, b) => (a.summary.lastDeployedAt ?? '').localeCompare(b.summary.lastDeployedAt ?? ''));
  const totalDeploys = active.reduce((sum, s) => sum + s.summary.total, 0);

  const needle = search.trim().toLowerCase();
  const listed = showAll
    ? [...active, ...stale].filter(
        (s) => !needle || (s.key.serviceName ?? '').toLowerCase().includes(needle),
      )
    : active.slice(0, CADENCE_TOP_N);

  return (
    <section
      className="rounded-xl border p-4"
      style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-primary)' }}
    >
      <h2 className="text-[13px] font-semibold mb-1 flex items-center gap-1.5">
        Deploy cadence per service · all environments
        <InfoPopover
          label="Deploy cadence per service"
          content={{
            what: 'How often each service ships, over every environment.',
            how: 'Successful, non-rollback deploy events grouped by service. A service with deploy history but nothing in this window is STALE — it is reported explicitly instead of silently dropping out. The delta in parentheses compares the previous equal-length window.',
            why: 'Stale services are the alarm: a service nobody has deployed in weeks is where risk quietly accumulates (unpatched dependencies, rusty pipeline, lost knowledge). The league table is for orientation; the stale list is for action.',
          }}
        />
      </h2>
      <p className="text-[12px] mb-3" style={{ color: 'var(--text-muted)' }}>
        {active.length} active {active.length === 1 ? 'service' : 'services'} · {totalDeploys} deploys
        {stale.length > 0 && <> · {stale.length} stale</>}
      </p>

      {stale.length > 0 && (
        <button
          onClick={() => setStaleOpen((v) => !v)}
          aria-expanded={staleOpen}
          className="w-full text-left flex items-center gap-2 px-3 py-2 rounded-lg border text-[12px] mb-3"
          style={{ borderColor: 'var(--warning)', backgroundColor: 'var(--warning-bg)', color: 'var(--warning)' }}
        >
          <AlertTriangle size={13} className="shrink-0" />
          <span className="mr-auto">
            {stale.length} {stale.length === 1 ? 'service' : 'services'} with no deploy in this window —
            oldest: <b>{stale[0]?.key.serviceName}</b> ({fmtAgo(stale[0]?.summary.lastDeployedAt)})
          </span>
          <span className="shrink-0 underline">{staleOpen ? 'hide' : 'show'}</span>
        </button>
      )}
      {staleOpen && (
        <ul className="mb-3 space-y-0.5 text-[12px]" style={{ color: 'var(--text-secondary)' }}>
          {stale.map((s) => (
            <li key={`${s.key.product}/${s.key.serviceName}`} className="flex justify-between gap-3">
              <span className="truncate">
                {showProduct && <span style={{ color: 'var(--text-muted)' }}>{s.key.product} · </span>}
                {s.key.serviceName}
              </span>
              <span className="shrink-0" style={{ color: 'var(--warning)' }}>
                {fmtAgo(s.summary.lastDeployedAt)}
              </span>
            </li>
          ))}
        </ul>
      )}

      {active.length === 0 && stale.length === 0 ? (
        <p className="text-[13px]" style={{ color: 'var(--text-muted)' }}>
          No deployments in this window.
        </p>
      ) : (
        <>
          {showAll && (
            <div className="relative mb-2">
              <Search
                size={13}
                className="absolute left-2.5 top-1/2 -translate-y-1/2"
                style={{ color: 'var(--text-muted)' }}
              />
              <input
                value={search}
                onChange={(e) => setSearch(e.target.value)}
                placeholder="Find service…"
                className="text-[13px] pl-8 pr-3 py-1.5 rounded-lg border w-full"
                style={{
                  borderColor: 'var(--border-color)',
                  backgroundColor: 'var(--bg-primary)',
                  color: 'var(--text-primary)',
                }}
              />
            </div>
          )}
          <table className="w-full text-[13px]">
            <thead>
              <tr style={{ color: 'var(--text-muted)' }}>
                <th className="text-left py-1 font-medium">Service</th>
                <th className="text-right py-1 font-medium">Deploys</th>
                <th className="text-right py-1 font-medium">/week</th>
                <th className="text-right py-1 font-medium">Median gap</th>
                <th className="text-right py-1 font-medium">Last deploy</th>
              </tr>
            </thead>
            <tbody>
              {listed.map((s) => {
                const isStale = s.summary.total === 0;
                return (
                  <tr
                    key={`${s.key.product}/${s.key.serviceName}`}
                    style={{ borderTop: '1px solid var(--border-color)' }}
                  >
                    <td className="py-1.5 font-medium">
                      {showProduct && (
                        <span style={{ color: 'var(--text-muted)' }}>{s.key.product} · </span>
                      )}
                      {s.key.serviceName}
                      {isStale && (
                        <span
                          className="ml-2 px-1.5 py-0.5 rounded text-[10px] font-medium uppercase"
                          style={{ backgroundColor: 'var(--warning-bg)', color: 'var(--warning)' }}
                        >
                          stale
                        </span>
                      )}
                    </td>
                    <td className="py-1.5 text-right">
                      {s.summary.total}
                      {s.summary.total !== s.summary.previousPeriodTotal && (
                        <span
                          className="ml-1 text-[11px]"
                          style={{
                            color:
                              s.summary.total > s.summary.previousPeriodTotal
                                ? 'var(--success)'
                                : 'var(--danger)',
                          }}
                        >
                          ({fmtDelta(s.summary.total - s.summary.previousPeriodTotal)})
                        </span>
                      )}
                    </td>
                    <td className="py-1.5 text-right">{s.summary.perWeek}</td>
                    <td className="py-1.5 text-right" style={{ color: 'var(--text-muted)' }}>
                      {fmtHours(s.summary.medianIntervalHours)}
                    </td>
                    <td
                      className="py-1.5 text-right"
                      style={{ color: isStale ? 'var(--warning)' : 'var(--text-muted)' }}
                    >
                      {fmtAgo(s.summary.lastDeployedAt)}
                    </td>
                  </tr>
                );
              })}
            </tbody>
          </table>
          {(active.length > CADENCE_TOP_N || stale.length > 0) && (
            <button
              onClick={() => {
                setShowAll((v) => !v);
                setSearch('');
              }}
              className="mt-2 text-[12px] underline"
              style={{ color: 'var(--accent)' }}
            >
              {showAll
                ? `Show top ${CADENCE_TOP_N} only`
                : `Show all ${active.length + stale.length} services`}
            </button>
          )}
        </>
      )}
    </section>
  );
}

// ── Shared bits ────────────────────────────────────────────────────────────

function WorkItemLink({ itemKey }: { itemKey: string }) {
  // Links to the internal work-item detail page (which itself links out to the tracker) —
  // keeping matrix clicks inside the app so env/product context isn't lost.
  return (
    <Link to={`/work-items/${encodeURIComponent(itemKey)}`} className="hover:underline shrink-0">
      <span className="font-medium shrink-0" style={{ color: 'var(--accent)' }}>
        {itemKey}
      </span>
    </Link>
  );
}

function fmtHours(hours: number | null | undefined): string {
  if (hours == null) return '—';
  if (hours < 1) return `${Math.round(hours * 60)}m`;
  if (hours < 48) return `${Math.round(hours)}h`;
  return `${(hours / 24).toFixed(1)}d`;
}

function fmtDelta(n: number): string {
  return n > 0 ? `+${n}` : String(n);
}

function fmtDate(iso: string | null | undefined): string {
  return iso ? new Date(iso).toLocaleString() : '';
}

function fmtAgo(iso: string | null | undefined): string {
  if (!iso) return '—';
  const hours = (Date.now() - new Date(iso).getTime()) / 3600_000;
  if (hours < 1) return 'just now';
  return `${fmtHours(hours)} ago`;
}

function maxOf(a: number | null, b: number | null): number | null {
  if (a == null) return b;
  if (b == null) return a;
  return Math.max(a, b);
}
