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
  type FrequencySeries,
  type LeadTimeResponse,
  type MatrixCell,
  type PromotionQueueResponse,
  type WorkItemMatrixResponse,
} from '@/lib/api';
import { useSettingsStore } from '@/stores/settingsStore';
import { EnvLabel } from '@/components/environments/EnvBadge';
import { StatTiles, type StatTile } from '@/components/ui/StatTiles';
import { ListEmptyState } from '@/components/ui/ListEmptyState';
import { useEntityRefresh } from '@/hooks/useEntityEvents';
import { useDocumentTitle } from '@/lib/pageTitle';
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

  // The final env the executive tiles report on. Prefer the last CONFIGURED environment
  // (settings order) — that is the org's declared "end of the pipeline" even in a window where
  // nothing deployed there ("0 deploys to production" is the honest headline, "1 deploy to dev"
  // is not). Unconfigured env keys only win when nothing configured appears in the data at all.
  const finalEnv = useMemo(() => {
    const universe = allProducts
      ? (frequency?.series ?? []).map((s) => s.key.environment).filter((e): e is string => !!e)
      : (matrix?.environments ?? []);
    const configured = configuredEnvs.map((e) => e.key);
    const union = Array.from(new Set([...universe, ...(allProducts ? configured : [])]));
    const ordered = getOrderedEnvironments(union);
    const lastConfigured = [...ordered].reverse().find((e) => configured.includes(e));
    return lastConfigured ?? ordered[ordered.length - 1] ?? '';
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
      api.getDeploymentFrequency({ product: productParam, ...window, groupBy: 'service', tz }),
      matrixReq,
      api.getPromotionQueueStats({ product: productParam, ...window }),
      api.getLeadTime({ product: productParam, ...window, tz }),
      // "Shipped this period" needs the final env, known only after the matrix answers.
      matrixReq.then((m) => {
        if (!m) return null;
        const final = m.environments[m.environments.length - 1];
        return final
          ? api.getWorkItemMatrix({ product, ...window, reachedEnv: final, limit: 100 })
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
  }, [product, allProducts, notYetOn, range.from, range.to, tz]);

  const updateParams = (patch: Record<string, string>) => {
    const next = new URLSearchParams(searchParams);
    for (const [k, v] of Object.entries(patch)) {
      if (v) next.set(k, v);
      else next.delete(k);
    }
    setSearchParams(next, { replace: true });
  };

  const finalSeries = frequency?.series.find((s) => s.key.environment === finalEnv) ?? null;
  const tiles = buildTiles(
    finalSeries, matrix, queue, leadTime, finalEnv, getDisplayName, Math.round(range.days), allProducts);

  const inFlight = (matrix?.items ?? []).filter(
    (i) => finalEnv && i.envs[finalEnv]?.state !== 'deployed',
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
              <ShippedList shipped={shipped} finalEnv={finalEnv} getDisplayName={getDisplayName} />
              <InFlightList items={inFlight} finalEnv={finalEnv} getDisplayName={getDisplayName} />
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

function buildTiles(
  finalSeries: FrequencySeries | null,
  matrix: WorkItemMatrixResponse | null,
  queue: PromotionQueueResponse | null,
  leadTime: LeadTimeResponse | null,
  finalEnv: string,
  getDisplayName: (env: string) => string,
  periodDays: number,
  allProducts: boolean,
): StatTile[] {
  const envName = finalEnv ? getDisplayName(finalEnv) : '—';
  const vsPrev = `vs prev ${periodDays}d`;

  const deploys = finalSeries?.summary.total ?? 0;
  const prevDeploys = finalSeries?.summary.previousPeriodTotal ?? 0;
  const cfr = finalSeries?.summary.changeFailureRate;
  const approval = queue?.approvalLatency;
  const coverage = matrix?.coverage;
  const leadFinal = leadTime?.byEnvironment.find((e) => e.environment === finalEnv);
  const leadCovered = (leadTime?.coverage.ratio ?? 0) > 0;

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
    },
    {
      label: 'Change failure rate',
      sub: `${envName} · failed + rollbacks`,
      value: cfr == null ? '—' : `${Math.round(cfr * 100)}%`,
      icon: AlertTriangle,
      color: 'var(--warning)',
      bg: 'var(--warning-bg)',
    },
    {
      label: 'Approval p50',
      sub: approval ? `n=${approval.n}` : undefined,
      value: fmtHours(approval?.p50Hours),
      icon: Clock,
      color: 'var(--info)',
      bg: 'var(--info-bg)',
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
    },
    {
      label: `Lead time p50 · ${envName}`,
      sub: leadCovered
        ? `coverage ${Math.round((leadTime?.coverage.ratio ?? 0) * 100)}%`
        : 'awaiting producer data (occurredAt)',
      value: leadCovered ? fmtHours(leadFinal?.p50Hours) : '—',
      icon: GitCommitHorizontal,
      color: 'var(--text-muted)',
      bg: 'var(--bg-secondary)',
      muted: !leadCovered,
    },
  ];
}

function ShippedList({
  shipped,
  finalEnv,
  getDisplayName,
}: {
  shipped: WorkItemMatrixResponse | null;
  finalEnv: string;
  getDisplayName: (env: string) => string;
}) {
  const items = shipped?.items ?? [];
  return (
    <section
      className="rounded-xl border p-4"
      style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-primary)' }}
    >
      <h2 className="text-[13px] font-semibold mb-3 flex items-center gap-2">
        <CheckCircle2 size={14} style={{ color: 'var(--success)' }} />
        Shipped this period · {finalEnv ? getDisplayName(finalEnv) : '—'}
        <span style={{ color: 'var(--text-muted)' }}>({shipped?.totalItems ?? 0})</span>
      </h2>
      {items.length === 0 ? (
        <p className="text-[13px]" style={{ color: 'var(--text-muted)' }}>
          No stories reached {finalEnv ? getDisplayName(finalEnv) : 'the final environment'} in this window.
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
  finalEnv,
  getDisplayName,
}: {
  items: WorkItemMatrixResponse['items'];
  finalEnv: string;
  getDisplayName: (env: string) => string;
}) {
  return (
    <section
      className="rounded-xl border p-4"
      style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-primary)' }}
    >
      <h2 className="text-[13px] font-semibold mb-3 flex items-center gap-2">
        <CircleDashed size={14} style={{ color: 'var(--warning)' }} />
        In flight — not yet on {finalEnv ? getDisplayName(finalEnv) : '—'}
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
                {i.furthestEnv ? `on ${getDisplayName(i.furthestEnv)}` : 'not deployed'}
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
  if (!matrix) return null;
  const { environments, coverage, totals } = matrix;

  const needle = search.trim().toLowerCase();
  const items = needle
    ? matrix.items.filter(
        (i) =>
          i.key.toLowerCase().includes(needle) || (i.title ?? '').toLowerCase().includes(needle),
      )
    : matrix.items;
  const filtered = Boolean(needle || notYetOn);

  return (
    <section className="space-y-2">
      <div className="flex flex-wrap items-center gap-3">
        <div className="flex items-baseline gap-2 mr-auto">
          <h2 className="text-[15px] font-semibold">Stories × environments</h2>
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
      <h2 className="text-[13px] font-semibold mb-3">Promotion queue</h2>
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

function ServiceFrequencySection({
  frequency,
  showProduct,
}: {
  frequency: FrequencyResponse | null;
  showProduct: boolean;
}) {
  if (!frequency) return null;
  // Stale services (zero deploys in the window) surface FIRST — they are the alarm, and sorted
  // to the bottom of a busy table nobody would ever see them.
  const series = [...frequency.series].sort(
    (a, b) => Number(a.summary.total > 0) - Number(b.summary.total > 0) || b.summary.total - a.summary.total,
  );
  return (
    <section
      className="rounded-xl border p-4"
      style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-primary)' }}
    >
      <h2 className="text-[13px] font-semibold mb-3">Deploy cadence per service · all environments</h2>
      {series.length === 0 ? (
        <p className="text-[13px]" style={{ color: 'var(--text-muted)' }}>
          No deployments in this window.
        </p>
      ) : (
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
            {series.map((s) => {
              const stale = s.summary.total === 0;
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
                    {stale && (
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
                    style={{ color: stale ? 'var(--warning)' : 'var(--text-muted)' }}
                  >
                    {fmtAgo(s.summary.lastDeployedAt)}
                  </td>
                </tr>
              );
            })}
          </tbody>
        </table>
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
