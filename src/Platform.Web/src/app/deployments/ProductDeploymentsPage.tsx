import { useEffect, useState, useMemo, useCallback } from 'react';
import { useParams, Link, useSearchParams } from 'react-router-dom';
import { useDeploymentStore } from '@/stores/deploymentStore';
import { useSettingsStore, resolveTemplate } from '@/stores/settingsStore';
import { DeployEventDetail } from '@/components/deployments/DeployEventDetail';
import { formatDistanceToNow } from 'date-fns';
import {
  ArrowLeft,
  ArrowUp,
  Loader2,
  ExternalLink,
  GitBranch,
  GitPullRequest,
  Ticket,
  Workflow,
  Download,
  Undo2,
  ChevronUp,
  ChevronDown,
  ChevronsUpDown,
} from 'lucide-react';
import type { DeploymentStateEntry, DeployEvent } from '@/lib/types';
import { resolveReferenceHref } from '@/lib/refUrl';

type ViewTab = 'state' | 'activity' | 'compare';
type TimeFilter = 'all' | 'today' | '24h' | '7d' | 'custom';

const VIEW_TABS: ViewTab[] = ['state', 'activity', 'compare'];
const TIME_FILTERS: TimeFilter[] = ['all', 'today', '24h', '7d', 'custom'];

const TIME_PRESETS: { key: TimeFilter; label: string }[] = [
  { key: 'all', label: 'All' },
  { key: 'today', label: 'Today' },
  { key: '24h', label: '24h' },
  { key: '7d', label: '7 days' },
  { key: 'custom', label: 'Custom' },
];

const ACTIVITY_TIME_PRESETS = TIME_PRESETS.filter((p) => p.key !== 'all');

function isViewTab(v: string | null): v is ViewTab {
  return v !== null && VIEW_TABS.includes(v as ViewTab);
}
function isTimeFilter(v: string | null): v is TimeFilter {
  return v !== null && TIME_FILTERS.includes(v as TimeFilter);
}

function computeSince(filter: TimeFilter, customDate?: string): string {
  if (filter === 'custom' && customDate) {
    return new Date(customDate).toISOString();
  }
  const now = new Date();
  switch (filter) {
    case 'today': {
      const start = new Date(now);
      start.setHours(0, 0, 0, 0);
      return start.toISOString();
    }
    case '24h':
      return new Date(now.getTime() - 24 * 60 * 60 * 1000).toISOString();
    case '7d':
      return new Date(now.getTime() - 7 * 24 * 60 * 60 * 1000).toISOString();
    default:
      return new Date(0).toISOString();
  }
}

function getSubtitle(filter: TimeFilter, customDate?: string): string {
  if (filter === 'custom' && customDate) {
    return `Deployed since ${new Date(customDate).toLocaleString()}`;
  }
  const map: Record<string, string> = {
    all: 'Service \u00d7 Environment matrix',
    today: 'Deployed today',
    '24h': 'Deployed in the last 24 hours',
    '7d': 'Deployed in the last 7 days',
    custom: 'Select a date and time',
  };
  return map[filter] ?? '';
}

export function ProductDeploymentsPage() {
  const { product } = useParams<{ product: string }>();
  const [searchParams, setSearchParams] = useSearchParams();
  const { stateMatrix, recentActivity, loading, fetchState, fetchRecentByProduct } =
    useDeploymentStore();
  const { getDisplayName, getOrderedEnvironments } = useSettingsStore();
  const [selected, setSelected] = useState<DeploymentStateEntry | null>(null);

  // Read filter state from URL search params
  const tab: ViewTab = isViewTab(searchParams.get('tab')) ? searchParams.get('tab') as ViewTab : 'state';
  const timeFilter: TimeFilter = isTimeFilter(searchParams.get('time')) ? searchParams.get('time') as TimeFilter : 'all';
  const activityTimeFilter: TimeFilter = isTimeFilter(searchParams.get('atime')) ? searchParams.get('atime') as TimeFilter : 'today';
  const customDate = searchParams.get('since') ?? '';
  const activityCustomDate = searchParams.get('asince') ?? '';
  const envFilter = searchParams.get('env') ?? 'all';
  const sortBy = searchParams.get('sort') ?? 'service'; // 'service' or an env key
  const sortDir: 'asc' | 'desc' =
    searchParams.get('dir') === 'desc' ? 'desc' : 'asc';
  const fromEnv = searchParams.get('from') ?? '';
  const toEnv = searchParams.get('to') ?? '';
  // Compare view mode: 'diff' (default, only services where versions differ) or 'all'.
  const compareMode: 'diff' | 'all' = searchParams.get('all') === '1' ? 'all' : 'diff';

  // Write filter state to URL search params
  const updateParams = useCallback(
    (updates: Record<string, string | null>) => {
      setSearchParams((prev) => {
        const next = new URLSearchParams(prev);
        for (const [key, value] of Object.entries(updates)) {
          if (value === null || value === '') {
            next.delete(key);
          } else {
            next.set(key, value);
          }
        }
        return next;
      }, { replace: true });
    },
    [setSearchParams]
  );

  const setTab = useCallback(
    (v: ViewTab) => {
      // When switching tabs, drop params that don't apply to the destination.
      // Keep `env` across tabs; keep `from`/`to` so the compare view is remembered.
      // Drop `all` (compare-only) when leaving the compare tab.
      if (v === 'state') {
        updateParams({ tab: null, atime: null, asince: null, all: null });
      } else if (v === 'compare') {
        updateParams({ tab: 'compare', time: null, since: null, atime: null, asince: null });
      } else {
        updateParams({ tab: 'activity', time: null, since: null, all: null });
      }
    },
    [updateParams]
  );

  const setFromEnv = useCallback(
    (v: string) => updateParams({ from: v || null }),
    [updateParams]
  );

  const setToEnv = useCallback(
    (v: string) => updateParams({ to: v || null }),
    [updateParams]
  );

  const setCompareMode = useCallback(
    (v: 'diff' | 'all') => updateParams({ all: v === 'all' ? '1' : null }),
    [updateParams]
  );

  const setTimeFilter = useCallback(
    (v: TimeFilter) => {
      const updates: Record<string, string | null> = {
        time: v === 'all' ? null : v,
      };
      if (v !== 'custom') updates.since = null;
      updateParams(updates);
    },
    [updateParams]
  );

  const setActivityTimeFilter = useCallback(
    (v: TimeFilter) => {
      const updates: Record<string, string | null> = {
        atime: v === 'today' ? null : v,
      };
      if (v !== 'custom') updates.asince = null;
      updateParams(updates);
    },
    [updateParams]
  );

  const setCustomDate = useCallback(
    (v: string) => updateParams({ since: v || null }),
    [updateParams]
  );

  const setActivityCustomDate = useCallback(
    (v: string) => updateParams({ asince: v || null }),
    [updateParams]
  );

  const setEnvFilter = useCallback(
    (v: string) => updateParams({ env: v === 'all' ? null : v }),
    [updateParams]
  );

  // Fetch state matrix (always needed for state tab)
  useEffect(() => {
    if (product) fetchState(product);
  }, [product, fetchState]);

  // Fetch recent activity when state tab has a time filter
  useEffect(() => {
    if (product && tab === 'state' && timeFilter !== 'all') {
      if (timeFilter === 'custom' && !customDate) return;
      fetchRecentByProduct(product, computeSince(timeFilter, customDate));
    }
  }, [product, tab, timeFilter, customDate, fetchRecentByProduct]);

  // Fetch recent activity for activity tab
  useEffect(() => {
    if (product && tab === 'activity') {
      if (activityTimeFilter === 'custom' && !activityCustomDate) return;
      fetchRecentByProduct(product, computeSince(activityTimeFilter, activityCustomDate));
    }
  }, [product, tab, activityTimeFilter, activityCustomDate, fetchRecentByProduct]);

  // Build a set of recently-changed (service, env) keys for highlighting
  const recentKeys = useMemo(() => {
    const keys = new Set<string>();
    for (const evt of recentActivity) {
      keys.add(`${evt.service}::${evt.environment}`);
    }
    return keys;
  }, [recentActivity]);

  // State matrix helpers
  const environments = getOrderedEnvironments(
    Array.from(new Set(stateMatrix.map((s) => s.environment)))
  );
  const getCell = useCallback(
    (service: string, env: string) =>
      stateMatrix.find((s) => s.service === service && s.environment === env),
    [stateMatrix]
  );

  // Sorted service list — "service" sorts alphabetically, env-key sort sorts by
  // that environment's last deploy time (services with no deploy in that env
  // sink to the bottom regardless of direction).
  const services = useMemo(() => {
    const names = Array.from(new Set(stateMatrix.map((s) => s.service)));
    const dirMul = sortDir === 'asc' ? 1 : -1;

    if (sortBy === 'service') {
      return names.sort((a, b) => a.localeCompare(b) * dirMul);
    }

    // Sort by deploy time for the given environment
    return names.sort((a, b) => {
      const ca = getCell(a, sortBy);
      const cb = getCell(b, sortBy);
      if (!ca && !cb) return a.localeCompare(b);
      if (!ca) return 1; // missing always at bottom
      if (!cb) return -1;
      const ta = new Date(ca.deployedAt).getTime();
      const tb = new Date(cb.deployedAt).getTime();
      return (ta - tb) * dirMul;
    });
  }, [stateMatrix, sortBy, sortDir, getCell]);

  const toggleSort = useCallback(
    (key: string) => {
      const updates: Record<string, string | null> = {};
      if (sortBy === key) {
        // Same column — flip direction
        updates.dir = sortDir === 'asc' ? 'desc' : null; // omit when back to default asc
      } else {
        updates.sort = key === 'service' ? null : key; // omit default
        // New column: service defaults to asc, env defaults to desc (latest first)
        updates.dir = key === 'service' ? null : 'desc';
      }
      updateParams(updates);
    },
    [sortBy, sortDir, updateParams]
  );
  const isBehind = (service: string, envIndex: number) => {
    if (envIndex === 0) return false;
    const current = getCell(service, environments[envIndex]);
    const prev = getCell(service, environments[envIndex - 1]);
    return current && prev && current.version !== prev.version;
  };

  // ── Compare helpers ──
  // If the user hasn't picked envs, default to the last two ordered envs —
  // usually staging -> production. Keep these resolved values out of the URL
  // so the URL stays clean when defaults are in use.
  const resolvedFrom = fromEnv || environments[environments.length - 2] || '';
  const resolvedTo = toEnv || environments[environments.length - 1] || '';

  const compareRows = useMemo(() => {
    if (!resolvedFrom || !resolvedTo || resolvedFrom === resolvedTo) return [];
    const names = Array.from(new Set(stateMatrix.map((s) => s.service))).sort((a, b) =>
      a.localeCompare(b)
    );
    const mapped = names.map((service) => ({
      service,
      from: getCell(service, resolvedFrom),
      to: getCell(service, resolvedTo),
    }));
    if (compareMode === 'all') return mapped;
    return mapped.filter(({ from, to }) => {
      if (!from) return false; // nothing in source — not "ready"
      if (!to) return true; // built in source, never shipped to target
      return from.version !== to.version; // drift
    });
  }, [stateMatrix, getCell, resolvedFrom, resolvedTo, compareMode]);

  // Activity helpers
  const allActivityEnvs = getOrderedEnvironments(
    Array.from(new Set(recentActivity.map((e) => e.environment)))
  );
  const filteredActivity =
    envFilter === 'all'
      ? recentActivity
      : recentActivity.filter((e) => e.environment === envFilter);

  // Group activity by environment for section headers
  const activityByEnv = useMemo(() => {
    const map = new Map<string, DeployEvent[]>();
    for (const evt of filteredActivity) {
      if (!map.has(evt.environment)) map.set(evt.environment, []);
      map.get(evt.environment)!.push(evt);
    }
    // Sort environments by settings order
    const ordered = getOrderedEnvironments(Array.from(map.keys()));
    return ordered.map((env) => ({ env, events: map.get(env)! }));
  }, [filteredActivity, getOrderedEnvironments]);

  // ── Export helpers ──

  const downloadFile = useCallback((content: string, filename: string, mime: string) => {
    const blob = new Blob([content], { type: mime });
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = filename;
    a.click();
    URL.revokeObjectURL(url);
  }, []);

  const exportStateCSV = useCallback(() => {
    const header = ['Service', ...environments.map((e) => `${getDisplayName(e)} (version)`), ...environments.map((e) => `${getDisplayName(e)} (status)`), ...environments.map((e) => `${getDisplayName(e)} (deployed)`)];
    const rows = services.map((service) => {
      const versions = environments.map((env) => {
        const cell = getCell(service, env);
        return cell ? `v${cell.version}` : '';
      });
      const statuses = environments.map((env) => {
        const cell = getCell(service, env);
        return cell?.status ?? '';
      });
      const dates = environments.map((env) => {
        const cell = getCell(service, env);
        return cell ? cell.deployedAt : '';
      });
      return [service, ...versions, ...statuses, ...dates];
    });
    const csv = [header, ...rows].map((r) => r.map((c) => `"${c}"`).join(',')).join('\n');
    downloadFile(csv, `${product}-state.csv`, 'text/csv');
  }, [services, environments, getCell, getDisplayName, product, downloadFile]);

  const exportStateJSON = useCallback(() => {
    const data = services.map((service) => {
      const envData: Record<string, { version: string; status: string; deployedAt: string }> = {};
      for (const env of environments) {
        const cell = getCell(service, env);
        if (cell) envData[env] = { version: cell.version, status: cell.status ?? 'succeeded', deployedAt: cell.deployedAt };
      }
      return { service, environments: envData };
    });
    downloadFile(JSON.stringify({ product, exportedAt: new Date().toISOString(), data }, null, 2), `${product}-state.json`, 'application/json');
  }, [services, environments, getCell, product, downloadFile]);

  const exportActivityCSV = useCallback(() => {
    const header = ['Service', 'Environment', 'Version', 'Previous Version', 'Status', 'Deployed At', 'Source', 'Work Item', 'Work Item Title', 'PR Author', 'QA'];
    const rows = filteredActivity.map((evt) => {
      const workItem = evt.references.find((r) => r.type === 'work-item');
      const allP = [...evt.participants, ...(evt.enrichment?.participants ?? [])];
      const prAuthor = allP.find((p) => p.role === 'PR Author');
      const qa = allP.find((p) => p.role === 'QA');
      return [
        evt.service,
        evt.environment,
        evt.version,
        evt.previousVersion ?? '',
        evt.status ?? 'succeeded',
        evt.deployedAt,
        evt.source,
        workItem?.key ?? '',
        evt.enrichment?.labels?.workItemTitle ?? '',
        prAuthor?.displayName ?? prAuthor?.email ?? '',
        qa?.displayName ?? qa?.email ?? '',
      ];
    });
    const csv = [header, ...rows].map((r) => r.map((c) => `"${String(c).replace(/"/g, '""')}"`).join(',')).join('\n');
    downloadFile(csv, `${product}-activity.csv`, 'text/csv');
  }, [filteredActivity, product, downloadFile]);

  const exportActivityJSON = useCallback(() => {
    downloadFile(
      JSON.stringify({ product, exportedAt: new Date().toISOString(), events: filteredActivity }, null, 2),
      `${product}-activity.json`,
      'application/json',
    );
  }, [filteredActivity, product, downloadFile]);

  const exportCompareCSV = useCallback(() => {
    const fromLabel = getDisplayName(resolvedFrom);
    const toLabel = getDisplayName(resolvedTo);
    const header = [
      'Service',
      `${fromLabel} (version)`,
      `${fromLabel} (deployed)`,
      `${toLabel} (version)`,
      `${toLabel} (deployed)`,
    ];
    const rows = compareRows.map(({ service, from, to }) => [
      service,
      from ? `v${from.version}` : '',
      from ? from.deployedAt : '',
      to ? `v${to.version}` : '',
      to ? to.deployedAt : '',
    ]);
    const csv = [header, ...rows].map((r) => r.map((c) => `"${c}"`).join(',')).join('\n');
    downloadFile(csv, `${product}-compare-${resolvedFrom}-${resolvedTo}.csv`, 'text/csv');
  }, [compareRows, resolvedFrom, resolvedTo, getDisplayName, product, downloadFile]);

  const exportCompareJSON = useCallback(() => {
    const data = {
      product,
      from: resolvedFrom,
      to: resolvedTo,
      exportedAt: new Date().toISOString(),
      services: compareRows.map(({ service, from, to }) => ({
        service,
        from: from
          ? { version: from.version, deployedAt: from.deployedAt, status: from.status ?? 'succeeded' }
          : null,
        to: to
          ? { version: to.version, deployedAt: to.deployedAt, status: to.status ?? 'succeeded' }
          : null,
      })),
    };
    downloadFile(
      JSON.stringify(data, null, 2),
      `${product}-compare-${resolvedFrom}-${resolvedTo}.json`,
      'application/json',
    );
  }, [compareRows, resolvedFrom, resolvedTo, product, downloadFile]);

  return (
    <div className="space-y-6">
      {/* Header */}
      <div className="flex items-center gap-3">
        <Link
          to="/deployments"
          className="p-1.5 rounded-lg transition-colors hover:opacity-80"
          style={{ color: 'var(--text-muted)' }}
        >
          <ArrowLeft size={18} />
        </Link>
        <div>
          <h1
            className="text-xl font-semibold tracking-tight"
            style={{ color: 'var(--text-primary)' }}
          >
            {product}
          </h1>
          <p className="text-sm mt-0.5" style={{ color: 'var(--text-muted)' }}>
            {tab === 'state'
              ? getSubtitle(timeFilter, customDate)
              : tab === 'activity'
              ? getSubtitle(activityTimeFilter, activityCustomDate)
              : 'Compare versions between two environments'}
          </p>
        </div>
      </div>

      {/* Tab switcher + filters */}
      <div className="flex items-center gap-4 flex-wrap">
        {/* State / Activity / Compare tabs — Compare only when 2+ environments */}
        <SegmentedControl
          options={[
            { key: 'state', label: 'State' },
            { key: 'activity', label: 'Activity' },
            ...(environments.length >= 2 ? [{ key: 'compare', label: 'Compare' }] : []),
          ]}
          value={tab}
          onChange={(v) => setTab(v as ViewTab)}
        />

        {/* Time filter for state tab */}
        {tab === 'state' && (
          <>
            <SegmentedControl
              options={TIME_PRESETS}
              value={timeFilter}
              onChange={(v) => setTimeFilter(v as TimeFilter)}
            />
            {timeFilter === 'custom' && (
              <DateTimePicker value={customDate} onChange={setCustomDate} />
            )}
          </>
        )}

        {/* Time filter for activity tab */}
        {tab === 'activity' && (
          <>
            <SegmentedControl
              options={ACTIVITY_TIME_PRESETS}
              value={activityTimeFilter}
              onChange={(v) => setActivityTimeFilter(v as TimeFilter)}
            />
            {activityTimeFilter === 'custom' && (
              <DateTimePicker value={activityCustomDate} onChange={setActivityCustomDate} />
            )}
            {/* Environment filter pills */}
            <div className="flex items-center gap-1.5">
              <EnvPill
                label="All"
                active={envFilter === 'all'}
                onClick={() => setEnvFilter('all')}
              />
              {allActivityEnvs.map((env) => (
                <EnvPill
                  key={env}
                  label={getDisplayName(env)}
                  active={envFilter === env}
                  onClick={() => setEnvFilter(env)}
                />
              ))}
            </div>
            <span className="text-[12px]" style={{ color: 'var(--text-muted)' }}>
              {filteredActivity.length} deployment
              {filteredActivity.length !== 1 ? 's' : ''}
            </span>
          </>
        )}

        {/* Highlight indicator on state tab */}
        {tab === 'state' && timeFilter !== 'all' && !(timeFilter === 'custom' && !customDate) && (
          <span className="text-[12px]" style={{ color: 'var(--text-muted)' }}>
            {recentKeys.size} changed cell{recentKeys.size !== 1 ? 's' : ''}
          </span>
        )}

        {/* Compare tab — From / To env selectors + row-mode toggle */}
        {tab === 'compare' && (
          <>
            <div className="inline-flex items-center gap-2">
              <span className="text-[12px]" style={{ color: 'var(--text-muted)' }}>From</span>
              <EnvSelect
                value={resolvedFrom}
                options={environments}
                getLabel={getDisplayName}
                onChange={setFromEnv}
              />
              <span className="text-[12px]" style={{ color: 'var(--text-muted)' }}>→</span>
              <span className="text-[12px]" style={{ color: 'var(--text-muted)' }}>To</span>
              <EnvSelect
                value={resolvedTo}
                options={environments}
                getLabel={getDisplayName}
                onChange={setToEnv}
              />
            </div>
            <SegmentedControl
              options={[
                { key: 'diff', label: 'Diffs only' },
                { key: 'all', label: 'All services' },
              ]}
              value={compareMode}
              onChange={(v) => setCompareMode(v as 'diff' | 'all')}
            />
            <span className="text-[12px]" style={{ color: 'var(--text-muted)' }}>
              {resolvedFrom === resolvedTo
                ? 'Pick two different environments'
                : compareMode === 'all'
                ? `${compareRows.length} service${compareRows.length === 1 ? '' : 's'}`
                : `${compareRows.length} service${compareRows.length === 1 ? '' : 's'} pending promotion`}
            </span>
          </>
        )}

        {/* Export dropdown — pushed to the right */}
        <div className="flex-1" />
        <ExportMenu
          onCSV={
            tab === 'state'
              ? exportStateCSV
              : tab === 'compare'
              ? exportCompareCSV
              : exportActivityCSV
          }
          onJSON={
            tab === 'state'
              ? exportStateJSON
              : tab === 'compare'
              ? exportCompareJSON
              : exportActivityJSON
          }
          disabled={
            tab === 'state'
              ? services.length === 0
              : tab === 'compare'
              ? compareRows.length === 0
              : filteredActivity.length === 0
          }
        />
      </div>

      {/* Content */}
      {loading ? (
        <div className="flex items-center justify-center py-20">
          <Loader2
            className="animate-spin"
            size={24}
            style={{ color: 'var(--text-muted)' }}
          />
        </div>
      ) : tab === 'state' ? (
        /* ── State Matrix ── */
        <div
          className="rounded-xl border overflow-hidden"
          style={{
            borderColor: 'var(--border-color)',
            backgroundColor: 'var(--bg-secondary)',
          }}
        >
          <table className="w-full text-[13px]">
            <thead>
              <tr style={{ borderBottom: '1px solid var(--border-color)' }}>
                <th className="text-left px-4 py-3 font-medium" style={{ color: 'var(--text-muted)' }}>
                  <SortHeader
                    label="Service"
                    active={sortBy === 'service'}
                    dir={sortDir}
                    onClick={() => toggleSort('service')}
                    align="left"
                  />
                </th>
                {environments.map((env) => (
                  <th
                    key={env}
                    className="text-center px-4 py-3 font-medium"
                    style={{ color: 'var(--text-muted)' }}
                  >
                    <SortHeader
                      label={getDisplayName(env)}
                      active={sortBy === env}
                      dir={sortDir}
                      onClick={() => toggleSort(env)}
                      align="center"
                    />
                  </th>
                ))}
              </tr>
            </thead>
            <tbody>
              {services.map((service) => (
                <tr
                  key={service}
                  style={{ borderBottom: '1px solid var(--border-color)' }}
                >
                  <td
                    className="px-4 py-3 font-medium"
                    style={{ color: 'var(--text-primary)' }}
                  >
                    <Link
                      to={`/deployments/${product}/${service}/history`}
                      className="hover:underline"
                      style={{ color: 'var(--text-primary)' }}
                    >
                      {service}
                    </Link>
                  </td>
                  {environments.map((env, envIdx) => {
                    const cell = getCell(service, env);
                    const behind = isBehind(service, envIdx);
                    const highlighted =
                      timeFilter !== 'all' &&
                      recentKeys.has(`${service}::${env}`);
                    if (!cell) {
                      return (
                        <td
                          key={env}
                          className="text-center px-4 py-3"
                          style={{ color: 'var(--text-muted)' }}
                        >
                          —
                        </td>
                      );
                    }
                    return (
                      <td
                        key={env}
                        className="text-center px-4 py-2 cursor-pointer transition-colors hover:opacity-80"
                        style={{
                          borderLeft: highlighted
                            ? '3px solid var(--accent)'
                            : '3px solid transparent',
                          backgroundColor: highlighted
                            ? 'var(--accent-muted)'
                            : undefined,
                        }}
                        onClick={() => setSelected(cell)}
                      >
                        <div className="inline-flex flex-col items-center gap-0.5">
                          <span className="inline-flex items-center gap-1">
                            <span
                              className="font-mono text-[12px] font-medium"
                              style={{
                                color: behind
                                  ? 'var(--warning)'
                                  : statusColor(cell.status),
                              }}
                            >
                              {behind && '\u26a0 '}v{cell.version}
                            </span>
                            <RollbackIndicator
                              isRollback={cell.isRollback}
                              previousVersion={cell.previousVersion}
                            />
                          </span>
                          {cell.status && cell.status !== 'succeeded' && (
                            <StatusBadge status={cell.status} />
                          )}
                          <span
                            className="text-[11px]"
                            style={{ color: 'var(--text-muted)' }}
                          >
                            {formatDistanceToNow(new Date(cell.deployedAt), {
                              addSuffix: true,
                            })}
                          </span>
                        </div>
                      </td>
                    );
                  })}
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      ) : tab === 'compare' ? (
        /* ── Compare View ── */
        <CompareView
          rows={compareRows}
          fromEnv={resolvedFrom}
          toEnv={resolvedTo}
          fromLabel={getDisplayName(resolvedFrom)}
          toLabel={getDisplayName(resolvedTo)}
          mode={compareMode}
          onRowClick={(cell) => setSelected(cell)}
        />
      ) : filteredActivity.length === 0 ? (
        <div
          className="text-center py-20 text-sm"
          style={{ color: 'var(--text-muted)' }}
        >
          No deployments in this period
        </div>
      ) : (
        /* ── Activity View ── */
        <div className="space-y-6">
          {activityByEnv.map(({ env, events }) => (
            <div key={env} className="space-y-2">
              {envFilter === 'all' && (
                <h3
                  className="text-[12px] font-semibold uppercase tracking-wider px-1"
                  style={{ color: 'var(--text-muted)' }}
                >
                  {getDisplayName(env)}
                </h3>
              )}
              {events.map((evt) => (
                <ActivityCard
                  key={evt.id}
                  event={evt}
                  showEnv={envFilter !== 'all'}
                  getDisplayName={getDisplayName}
                  onClick={() => setSelected(evt)}
                />
              ))}
            </div>
          ))}
        </div>
      )}

      {selected && (
        <DeployEventDetail
          entry={selected}
          product={product!}
          onClose={() => setSelected(null)}
        />
      )}
    </div>
  );
}

/* ── Reusable sub-components ── */

function ExportMenu({
  onCSV,
  onJSON,
  disabled,
}: {
  onCSV: () => void;
  onJSON: () => void;
  disabled: boolean;
}) {
  const [open, setOpen] = useState(false);

  return (
    <div className="relative">
      <button
        onClick={() => setOpen(!open)}
        disabled={disabled}
        className="inline-flex items-center gap-1.5 text-[12px] font-medium px-2.5 py-1.5 rounded-lg transition-colors hover:opacity-80 disabled:opacity-40"
        style={{ color: 'var(--text-muted)', border: '1px solid var(--border-color)' }}
      >
        <Download size={13} />
        Export
      </button>
      {open && (
        <>
          <div className="fixed inset-0 z-10" onClick={() => setOpen(false)} />
          <div
            className="absolute right-0 top-full mt-1 z-20 rounded-lg border shadow-lg py-1 min-w-[120px]"
            style={{ backgroundColor: 'var(--bg-primary)', borderColor: 'var(--border-color)' }}
          >
            <button
              onClick={() => { onCSV(); setOpen(false); }}
              className="w-full text-left px-3 py-1.5 text-[12px] transition-colors hover:bg-[var(--bg-secondary)]"
              style={{ color: 'var(--text-primary)' }}
            >
              Export CSV
            </button>
            <button
              onClick={() => { onJSON(); setOpen(false); }}
              className="w-full text-left px-3 py-1.5 text-[12px] transition-colors hover:bg-[var(--bg-secondary)]"
              style={{ color: 'var(--text-primary)' }}
            >
              Export JSON
            </button>
          </div>
        </>
      )}
    </div>
  );
}

function SortHeader({
  label,
  active,
  dir,
  onClick,
  align,
}: {
  label: string;
  active: boolean;
  dir: 'asc' | 'desc';
  onClick: () => void;
  align: 'left' | 'center';
}) {
  const Icon = active ? (dir === 'asc' ? ChevronUp : ChevronDown) : ChevronsUpDown;
  return (
    <button
      type="button"
      onClick={onClick}
      className={`inline-flex items-center gap-1 transition-colors hover:opacity-80 ${align === 'center' ? 'justify-center' : ''}`}
      style={{ color: active ? 'var(--text-primary)' : 'inherit' }}
      title={active ? `Sorted ${dir}ending — click to ${dir === 'asc' ? 'reverse' : 'reverse'}` : 'Click to sort'}
    >
      {label}
      <Icon size={12} style={{ opacity: active ? 1 : 0.5 }} />
    </button>
  );
}

function SegmentedControl({
  options,
  value,
  onChange,
}: {
  options: { key: string; label: string }[];
  value: string;
  onChange: (key: string) => void;
}) {
  return (
    <div
      className="inline-flex rounded-lg p-0.5 gap-0.5"
      style={{
        backgroundColor: 'var(--bg-secondary)',
        border: '1px solid var(--border-color)',
      }}
    >
      {options.map((opt) => (
        <button
          key={opt.key}
          onClick={() => onChange(opt.key)}
          className="px-3.5 py-1.5 text-[13px] font-medium rounded-md transition-all"
          style={{
            backgroundColor:
              value === opt.key ? 'var(--bg-primary)' : 'transparent',
            color:
              value === opt.key ? 'var(--text-primary)' : 'var(--text-muted)',
            boxShadow:
              value === opt.key ? '0 1px 2px rgba(0,0,0,0.06)' : 'none',
          }}
        >
          {opt.label}
        </button>
      ))}
    </div>
  );
}

function DateTimePicker({
  value,
  onChange,
}: {
  value: string;
  onChange: (v: string) => void;
}) {
  return (
    <input
      type="datetime-local"
      value={value}
      onChange={(e) => onChange(e.target.value)}
      className="px-2.5 py-1.5 rounded-lg border text-[13px] outline-none transition-colors focus:border-[var(--accent)]"
      style={{
        borderColor: 'var(--border-color)',
        backgroundColor: 'var(--bg-primary)',
        color: 'var(--text-primary)',
      }}
    />
  );
}

function EnvPill({
  label,
  active,
  onClick,
}: {
  label: string;
  active: boolean;
  onClick: () => void;
}) {
  return (
    <button
      onClick={onClick}
      className="px-2.5 py-1 text-[12px] font-medium rounded-full transition-all"
      style={{
        backgroundColor: active ? 'var(--accent-muted)' : 'transparent',
        color: active ? 'var(--accent)' : 'var(--text-muted)',
        border: active
          ? '1px solid var(--accent)'
          : '1px solid var(--border-color)',
      }}
    >
      {label}
    </button>
  );
}

function EnvSelect({
  value,
  options,
  getLabel,
  onChange,
}: {
  value: string;
  options: string[];
  getLabel: (env: string) => string;
  onChange: (v: string) => void;
}) {
  return (
    <select
      value={value}
      onChange={(e) => onChange(e.target.value)}
      className="px-2.5 py-1.5 rounded-lg border text-[13px] outline-none transition-colors focus:border-[var(--accent)]"
      style={{
        borderColor: 'var(--border-color)',
        backgroundColor: 'var(--bg-primary)',
        color: 'var(--text-primary)',
      }}
    >
      {options.map((env) => (
        <option key={env} value={env}>
          {getLabel(env)}
        </option>
      ))}
    </select>
  );
}

function CompareView({
  rows,
  fromEnv,
  toEnv,
  fromLabel,
  toLabel,
  mode,
  onRowClick,
}: {
  rows: { service: string; from?: DeploymentStateEntry; to?: DeploymentStateEntry }[];
  fromEnv: string;
  toEnv: string;
  fromLabel: string;
  toLabel: string;
  mode: 'diff' | 'all';
  onRowClick: (cell: DeploymentStateEntry) => void;
}) {
  if (!fromEnv || !toEnv || fromEnv === toEnv) {
    return (
      <div
        className="text-center py-20 text-sm"
        style={{ color: 'var(--text-muted)' }}
      >
        Pick two different environments to compare.
      </div>
    );
  }

  if (rows.length === 0) {
    return (
      <div
        className="text-center py-20 text-sm"
        style={{ color: 'var(--text-muted)' }}
      >
        {mode === 'all'
          ? `No services found for ${fromLabel} or ${toLabel}.`
          : `All services are in sync between ${fromLabel} and ${toLabel}.`}
      </div>
    );
  }

  return (
    <div
      className="rounded-xl border overflow-hidden"
      style={{
        borderColor: 'var(--border-color)',
        backgroundColor: 'var(--bg-secondary)',
      }}
    >
      <table className="w-full text-[13px]">
        <thead>
          <tr style={{ borderBottom: '1px solid var(--border-color)' }}>
            <th className="text-left px-4 py-3 font-medium" style={{ color: 'var(--text-muted)' }}>
              Service
            </th>
            <th className="text-center px-4 py-3 font-medium" style={{ color: 'var(--text-muted)' }}>
              {fromLabel}
            </th>
            <th className="text-center px-4 py-3 font-medium" style={{ color: 'var(--text-muted)' }}>
              {toLabel}
            </th>
          </tr>
        </thead>
        <tbody>
          {rows.map(({ service, from, to }) => (
            <tr key={service} style={{ borderBottom: '1px solid var(--border-color)' }}>
              <td className="px-4 py-3 font-medium" style={{ color: 'var(--text-primary)' }}>
                {service}
              </td>
              <td
                className="text-center px-4 py-2 cursor-pointer transition-colors hover:opacity-80"
                onClick={() => from && onRowClick(from)}
              >
                {from ? (
                  <div className="inline-flex flex-col items-center gap-0.5">
                    <span className="font-mono text-[12px] font-medium" style={{ color: 'var(--text-primary)' }}>
                      v{from.version}
                    </span>
                    <span className="text-[11px]" style={{ color: 'var(--text-muted)' }}>
                      {formatDistanceToNow(new Date(from.deployedAt), { addSuffix: true })}
                    </span>
                  </div>
                ) : (
                  <span style={{ color: 'var(--text-muted)' }}>—</span>
                )}
              </td>
              {(() => {
                // Target is "behind" when either missing or on a different version than source.
                const behind = !to || (!!from && from.version !== to.version);
                return (
                  <td
                    className="text-center px-4 py-2 cursor-pointer transition-colors hover:opacity-80"
                    onClick={() => to && onRowClick(to)}
                  >
                    {to ? (
                      <div className="inline-flex flex-col items-center gap-0.5">
                        <span
                          className="inline-flex items-center gap-1 font-mono text-[12px] font-medium"
                          style={{ color: behind ? 'var(--warning)' : 'var(--text-primary)' }}
                        >
                          {behind && <ArrowUp size={12} />}
                          v{to.version}
                        </span>
                        <span className="text-[11px]" style={{ color: 'var(--text-muted)' }}>
                          {formatDistanceToNow(new Date(to.deployedAt), { addSuffix: true })}
                        </span>
                      </div>
                    ) : (
                      <span className="text-[12px] italic" style={{ color: 'var(--text-muted)' }}>
                        never deployed
                      </span>
                    )}
                  </td>
                );
              })()}
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

const STATUS_STYLES: Record<string, { bg: string; fg: string; label: string }> = {
  succeeded: { bg: 'rgba(34,197,94,0.12)', fg: '#22c55e', label: 'Succeeded' },
  failed: { bg: 'rgba(239,68,68,0.12)', fg: '#ef4444', label: 'Failed' },
  in_progress: { bg: 'rgba(234,179,8,0.12)', fg: '#eab308', label: 'In Progress' },
};

function StatusBadge({ status }: { status?: string }) {
  const s = STATUS_STYLES[status ?? 'succeeded'] ?? STATUS_STYLES.succeeded;
  return (
    <span
      className="inline-flex items-center gap-1 px-1.5 py-0.5 rounded text-[10px] font-semibold uppercase tracking-wide leading-none"
      style={{ backgroundColor: s.bg, color: s.fg }}
    >
      <span
        className="inline-block w-1.5 h-1.5 rounded-full"
        style={{ backgroundColor: s.fg }}
      />
      {s.label}
    </span>
  );
}

function statusColor(status?: string): string {
  if (status === 'failed') return '#ef4444';
  if (status === 'in_progress') return '#eab308';
  return 'var(--text-primary)';
}

function RollbackIndicator({
  isRollback,
  previousVersion,
}: {
  isRollback?: boolean;
  previousVersion?: string | null;
}) {
  if (!isRollback) return null;
  const title = previousVersion ? `Rolled back from v${previousVersion}` : 'Rollback';
  return (
    <span
      className="inline-flex items-center"
      title={title}
      aria-label={title}
    >
      <Undo2 size={12} style={{ color: 'var(--text-muted)' }} />
    </span>
  );
}

const REF_ICONS: Record<string, typeof ExternalLink> = {
  'work-item': Ticket,
  'pull-request': GitPullRequest,
  repository: GitBranch,
  pipeline: Workflow,
};

const STYLE_COLORS: Record<string, string> = {
  primary: 'var(--text-primary)',
  secondary: 'var(--text-secondary)',
  muted: 'var(--text-muted)',
};

function ActivityCard({
  event: evt,
  showEnv,
  getDisplayName,
  onClick,
}: {
  event: DeployEvent;
  showEnv: boolean;
  getDisplayName: (key: string) => string;
  onClick: () => void;
}) {
  const { activityTemplate } = useSettingsStore();

  const resolvedLines = activityTemplate
    .map((line) => ({ text: resolveTemplate(line.template, evt), style: line.style }))
    .filter((l): l is { text: string; style: string } => l.text !== null);

  return (
    <div
      className="rounded-lg border p-4 cursor-pointer transition-colors hover:border-[var(--accent)]"
      style={{
        borderColor: 'var(--border-color)',
        backgroundColor: 'var(--bg-secondary)',
      }}
      onClick={onClick}
    >
      {/* Top row: service → env + version (always shown) */}
      <div className="flex items-center justify-between gap-3">
        <div className="flex items-center gap-2 min-w-0">
          <span
            className="font-medium text-[13px] truncate"
            style={{ color: 'var(--text-primary)' }}
          >
            {evt.service}
          </span>
          {showEnv && (
            <>
              <span className="text-[11px]" style={{ color: 'var(--text-muted)' }}>
                →
              </span>
              <span
                className="badge text-[11px]"
                style={{
                  backgroundColor: 'var(--accent-muted)',
                  color: 'var(--accent)',
                }}
              >
                {getDisplayName(evt.environment)}
              </span>
            </>
          )}
        </div>
        <div className="flex items-center gap-2 shrink-0">
          <span
            className="font-mono text-[12px] font-medium whitespace-nowrap"
            style={{ color: statusColor(evt.status) }}
          >
            {evt.previousVersion
              ? `v${evt.previousVersion} → v${evt.version}`
              : `v${evt.version}`}
          </span>
          <RollbackIndicator
            isRollback={evt.isRollback}
            previousVersion={evt.previousVersion}
          />
          {evt.status && evt.status !== 'succeeded' && (
            <StatusBadge status={evt.status} />
          )}
        </div>
      </div>

      {/* Template lines — last line shares row with reference icons */}
      {resolvedLines.slice(0, -1).map((line, i) => (
        <div
          key={i}
          className="mt-1.5 text-[12px] truncate"
          style={{ color: STYLE_COLORS[line.style] ?? STYLE_COLORS.secondary }}
        >
          {line.text}
        </div>
      ))}

      {/* Last template line + reference links on the same row */}
      {(resolvedLines.length > 0 || evt.references.some((r) => r.url)) && (
        <div className="flex items-center justify-between gap-3 mt-1.5">
          {resolvedLines.length > 0 ? (
            <div
              className="text-[12px] truncate min-w-0"
              style={{
                color:
                  STYLE_COLORS[resolvedLines[resolvedLines.length - 1].style] ??
                  STYLE_COLORS.secondary,
              }}
            >
              {resolvedLines[resolvedLines.length - 1].text}
            </div>
          ) : (
            <span />
          )}
          <div className="flex items-center gap-2 shrink-0">
            {evt.references
              .filter((ref) => ref.url)
              .map((ref, i) => {
                const Icon = REF_ICONS[ref.type] ?? ExternalLink;
                const labels = evt.enrichment?.labels ?? {};
                const enrichedTitle =
                  ref.type === 'work-item' ? labels.workItemTitle :
                  ref.type === 'pull-request' ? labels.prTitle : undefined;
                const tip = [ref.key, ref.title ?? enrichedTitle].filter(Boolean).join(' — ') || ref.type;
                const href = resolveReferenceHref(ref) ?? ref.url;
                return (
                  <a
                    key={i}
                    href={href}
                    target="_blank"
                    rel="noopener noreferrer"
                    className="hover:opacity-80 transition-opacity"
                    style={{ color: 'var(--accent)' }}
                    onClick={(e) => e.stopPropagation()}
                    title={tip}
                  >
                    <Icon size={13} />
                  </a>
                );
              })}
          </div>
        </div>
      )}
    </div>
  );
}
