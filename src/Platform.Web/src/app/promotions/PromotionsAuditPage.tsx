import { useCallback, useEffect, useMemo, useState } from 'react';
import { Link, useNavigate, useSearchParams } from 'react-router-dom';
import {
  AlertTriangle,
  ArrowRight,
  Ban,
  Bot,
  CheckCircle,
  ChevronDown,
  Clock,
  FileText,
  Filter,
  GitPullRequest,
  History,
  MessageSquare,
  RefreshCw,
  Rocket,
  ScrollText,
  ShieldAlert,
  Undo2,
  User,
  UserCog,
  XCircle,
} from 'lucide-react';
import { formatDistanceToNow } from 'date-fns';
import { api } from '@/lib/api';
import type { PromotionAuditEntry, PromotionAuditResponse } from '@/lib/api';
import { workItemDetailPath } from '@/lib/workItem';
import { EnvBadge } from '@/components/environments/EnvBadge';
import { useEnvControlStyle } from '@/components/environments/useEnvColor';
import { FilterPanel } from '@/components/ui/FilterPanel';
import {
  ListEmptyState,
  type ActiveFilterChip,
} from '@/components/ui/ListEmptyState';
import { CopyViewLinkButton } from '@/components/ui/CopyViewLinkButton';
import { KeyboardList } from '@/components/ui/KeyboardList';
import { RovingGroup } from '@/components/ui/RovingGroup';
import { useKeyboardListRow } from '@/hooks/keyboardList';
import { useEntityRefresh } from '@/hooks/useEntityEvents';
import { useDocumentTitle, scopeTitle } from '@/lib/pageTitle';
import { useSettingsStore } from '@/stores/settingsStore';
import {
  AUDIT_RANGES,
  AUDIT_TABS,
  EMPTY_AUDIT_PARAMS,
  TAB_CATEGORIES,
  buildAuditParams,
  hasAuditParams,
  parseAuditParams,
  resolveAuditWindow,
  type AuditParams,
  type AuditRange,
  type AuditTab,
} from './promotionAuditFilterParams';

/**
 * The promotions audit page: everything that has been done to a promotion, newest first.
 *
 * <p>It exists for the questions that arrive as questions — "what was approved today?", "what went to
 * prod last week and who signed it off?", "did anything new come in this morning?". Every one of them
 * is a window plus a kind of action plus, sometimes, an environment, so those three are the page: a
 * range strip, a tab strip, and the filters. The answer is a link (see
 * {@link promotionAuditFilterParams}), because the question almost always came from someone else.</p>
 *
 * <p><b>What it is not.</b> The promotions list answers "what needs doing"; this answers "what was
 * done". Rows here are history — they never offer an action, and the status badge on a row is the
 * promotion's status now, not what it was at the time. Every row links through to the promotion,
 * which is where acting on one happens.</p>
 *
 * <p>One request backs the whole page. The server returns the rows, the per-action counts the tabs are
 * badged with, and the actors the dropdown offers — all counted under the other filters, so a tab
 * badge can't advertise rows a product filter has already excluded.</p>
 */

/** How each range reads on its button, and in the sentence the empty state writes. */
const RANGE_LABELS: Record<AuditRange, string> = {
  today: 'Today',
  '24h': 'Last 24 hours',
  '7d': 'Last 7 days',
  '30d': 'Last 30 days',
  all: 'All time',
};

/** The same windows as a noun phrase, for prose: "no approvals in the last 7 days". */
const RANGE_PHRASES: Record<AuditRange, string> = {
  today: 'today',
  '24h': 'in the last 24 hours',
  '7d': 'in the last 7 days',
  '30d': 'in the last 30 days',
  all: 'ever',
};

const TAB_LABELS: Record<AuditTab, string> = {
  all: 'All activity',
  approvals: 'Approvals',
  rejections: 'Rejections',
  created: 'New promotions',
  'work-items': 'Work items',
  deploys: 'Deploys',
  other: 'Everything else',
};

/** The subject of the "nothing … happened" sentence, per tab. */
const TAB_SUBJECTS: Record<AuditTab, string> = {
  all: 'has happened to a promotion',
  approvals: 'was approved',
  rejections: 'was rejected',
  created: 'was created',
  'work-items': 'was signed off, flagged or blocked',
  deploys: 'was deployed',
  other: 'else was changed',
};

/**
 * How each action reads on a row, and how it is tinted.
 *
 * <p>Copy lives here rather than on the server — the server owns which actions are "an approval"
 * (that decides what a link means), the page owns how one is worded. `verb` is the whole of what the
 * row says happened, phrased to follow the actor's name: "Maja Nowak · approved this promotion".</p>
 *
 * <p>An action missing from this map still renders: it falls back to its raw name, which is ugly and
 * legible, and is what a newly-added audit action gets until someone writes it a line.</p>
 */
const ACTION_STYLES: Record<
  string,
  { verb: string; icon: typeof Clock; color: string; bg: string }
> = {
  'promotion.candidate.created': {
    verb: 'created this promotion',
    icon: GitPullRequest,
    color: 'var(--text-secondary)',
    bg: 'var(--bg-secondary)',
  },
  'promotion.candidate.updated': {
    verb: 'updated the change set',
    icon: RefreshCw,
    color: 'var(--text-secondary)',
    bg: 'var(--bg-secondary)',
  },
  'promotion.approval.recorded': {
    verb: 'signed off — one approval towards the gate',
    icon: CheckCircle,
    color: 'var(--info)',
    bg: 'var(--info-bg)',
  },
  'promotion.approved': {
    verb: 'approved this promotion',
    icon: CheckCircle,
    color: 'var(--success)',
    bg: 'var(--success-bg)',
  },
  'promotion.bypassed': {
    verb: 'force-approved this promotion, skipping its gate',
    icon: ShieldAlert,
    color: 'var(--warning)',
    bg: 'var(--warning-bg)',
  },
  'promotion.approval.cancelled': {
    verb: 'took the approval back',
    icon: Undo2,
    color: 'var(--warning)',
    bg: 'var(--warning-bg)',
  },
  'promotion.rejected': {
    verb: 'rejected this promotion',
    icon: XCircle,
    color: 'var(--danger)',
    bg: 'var(--danger-bg)',
  },
  'promotion.deployed': {
    verb: 'deployed — the version is live in the target',
    icon: Rocket,
    color: 'var(--accent)',
    bg: 'var(--accent-bg)',
  },
  'promotion.policy.reapplied': {
    verb: 're-applied the promotion policy',
    icon: ScrollText,
    color: 'var(--text-secondary)',
    bg: 'var(--bg-secondary)',
  },
  'promotion.comment.added': {
    verb: 'commented',
    icon: MessageSquare,
    color: 'var(--text-secondary)',
    bg: 'var(--bg-secondary)',
  },
  'promotion.ticket.approved': {
    verb: 'signed off a work item',
    icon: CheckCircle,
    color: 'var(--success)',
    bg: 'var(--success-bg)',
  },
  'promotion.ticket.issue-raised': {
    verb: 'raised an issue on a work item',
    icon: AlertTriangle,
    color: 'var(--warning)',
    bg: 'var(--warning-bg)',
  },
  'promotion.ticket.blocked': {
    verb: 'blocked a work item',
    icon: Ban,
    color: 'var(--danger)',
    bg: 'var(--danger-bg)',
  },
  'work-item.decisions.reset': {
    verb: 'work-item sign-offs were reset by a new version',
    icon: RefreshCw,
    color: 'var(--warning)',
    bg: 'var(--warning-bg)',
  },
  'promotion.participant.upserted': {
    verb: 'set a participant on the promotion',
    icon: UserCog,
    color: 'var(--text-secondary)',
    bg: 'var(--bg-secondary)',
  },
  'promotion.participant.removed': {
    verb: 'removed a participant from the promotion',
    icon: UserCog,
    color: 'var(--text-secondary)',
    bg: 'var(--bg-secondary)',
  },
  'promotion.reference.participant.upserted': {
    verb: 'assigned somebody to a work item',
    icon: UserCog,
    color: 'var(--text-secondary)',
    bg: 'var(--bg-secondary)',
  },
  'promotion.reference.participant.removed': {
    verb: 'unassigned somebody from a work item',
    icon: UserCog,
    color: 'var(--text-secondary)',
    bg: 'var(--bg-secondary)',
  },
};

const FALLBACK_STYLE = {
  icon: FileText,
  color: 'var(--text-secondary)',
  bg: 'var(--bg-secondary)',
};

function actionStyle(action: string) {
  return ACTION_STYLES[action] ?? { ...FALLBACK_STYLE, verb: action };
}

/**
 * The stat tiles across the top: the four counts that answer the common questions without anybody
 * having to pick a tab first. Each one is the tab it summarises, so reading the number and then
 * seeing what it's made of is one click.
 */
const SUMMARY_TILES: { tab: AuditTab; label: string; icon: typeof Clock; color: string }[] = [
  { tab: 'approvals', label: 'Approved', icon: CheckCircle, color: 'var(--success)' },
  { tab: 'created', label: 'Created', icon: GitPullRequest, color: 'var(--info)' },
  { tab: 'rejections', label: 'Rejected', icon: XCircle, color: 'var(--danger)' },
  { tab: 'deploys', label: 'Deployed', icon: Rocket, color: 'var(--accent)' },
];

/** Rows per request. Enough that a normal day arrives in one page. */
const PAGE_SIZE = 50;

export function PromotionsAuditPage() {
  const getDisplayName = useSettingsStore((s) => s.getDisplayName);
  const getOrderedEnvironments = useSettingsStore((s) => s.getOrderedEnvironments);
  // The URL is the whole of this page's memory: no cookies, because a stale window silently narrowing
  // an audit answer is worse than starting from the default (see promotionAuditFilterParams).
  const [searchParams, setSearchParams] = useSearchParams();
  const [state, setState] = useState<AuditParams>(() =>
    hasAuditParams(searchParams) ? parseAuditParams(searchParams) : EMPTY_AUDIT_PARAMS,
  );

  const [feed, setFeed] = useState<PromotionAuditResponse | null>(null);
  const [loading, setLoading] = useState(true);
  const [failed, setFailed] = useState(false);
  // Accumulated rows across "Load more" presses. Kept separately from `feed` so the facets and total
  // always come from the newest response while the rows keep growing.
  const [rows, setRows] = useState<PromotionAuditEntry[]>([]);
  const [page, setPage] = useState(1);
  const [loadingMore, setLoadingMore] = useState(false);
  const [filterOptions, setFilterOptions] = useState<{ products: string[]; targetEnvs: string[] }>({
    products: [],
    targetEnvs: [],
  });

  const targetEnvSelectStyle = useEnvControlStyle(state.targetEnv);

  useDocumentTitle([
    TAB_LABELS[state.tab],
    RANGE_LABELS[state.range],
    scopeTitle({ product: state.product, service: state.service, targetEnv: state.targetEnv }),
    'Promotions audit',
  ]);

  const currentParams = useCallback(
    (next: Partial<AuditParams> = {}): URLSearchParams => buildAuditParams({ ...state, ...next }),
    [state],
  );

  /**
   * Applies a change to the view: state for the render, URL for the hand-off.
   *
   * `replace` rather than `push` — picking a filter is not a navigation, and pushing would make Back
   * walk out of the page one dropdown at a time. The paging cursor resets, because every one of these
   * changes what the rows are a page of.
   */
  const change = (next: Partial<AuditParams>) => {
    const merged = { ...state, ...next };
    setState(merged);
    setPage(1);
    const params = buildAuditParams(merged);
    if (params.toString() !== searchParams.toString()) {
      setSearchParams(params, { replace: true });
    }
  };

  const clearAllFilters = () =>
    change({ product: '', service: '', targetEnv: '', actor: '', action: '' });

  // Filter vocabulary for the product and environment dropdowns. Shared with the promotions list, and
  // deliberately unfiltered: options derived from the current view collapse to what is already picked.
  useEffect(() => {
    let cancelled = false;
    api
      .getPromotionFilterOptions()
      .then((o) => {
        if (!cancelled) setFilterOptions(o);
      })
      .catch(() => {
        // Leave the dropdowns holding only the current selection; the feed itself is unaffected.
      });
    return () => {
      cancelled = true;
    };
  }, []);

  // A promotion changing anywhere means a new row landed here. The audit trail is append-only, so
  // refetching from page 1 is the whole update — which does drop any "Load more" pages the reader had
  // pulled in. Accepted rather than worked around: a new row at the top shifts every offset below it,
  // so the alternative is a page whose later rows quietly no longer line up with its earlier ones.
  const promotionsTick = useEntityRefresh(['promotion', 'work-item']);

  // The identity of the query, and the effect's dependency. The window is resolved from the clock
  // inside the fetch rather than held in state: a `from` that changes on every render would retrigger
  // the fetch that depends on it, forever.
  const queryKey = [
    state.range,
    state.tab,
    state.product,
    state.service,
    state.targetEnv,
    state.actor,
    state.action,
  ].join('|');

  const requestParams = useCallback(
    (forPage: number) => ({
      ...resolveAuditWindow(state.range),
      category: TAB_CATEGORIES[state.tab].join(',') || undefined,
      action: state.action || undefined,
      actor: state.actor || undefined,
      product: state.product || undefined,
      service: state.service || undefined,
      targetEnv: state.targetEnv || undefined,
      page: forPage,
      pageSize: PAGE_SIZE,
    }),
    [state],
  );

  useEffect(() => {
    let cancelled = false;
    setLoading(true);
    setFailed(false);
    api
      .getPromotionAudit(requestParams(1))
      .then((data) => {
        if (cancelled) return;
        setFeed(data);
        setRows(data.entries);
        setPage(1);
      })
      .catch(() => {
        if (cancelled) return;
        setFeed(null);
        setRows([]);
        setFailed(true);
      })
      .finally(() => {
        if (!cancelled) setLoading(false);
      });
    return () => {
      cancelled = true;
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [queryKey, promotionsTick]);

  const loadMore = () => {
    const next = page + 1;
    setLoadingMore(true);
    api
      .getPromotionAudit(requestParams(next))
      .then((data) => {
        // Appended by id rather than concatenated blindly: a row written between the two requests
        // shifts the window, and the same entry can land on both pages.
        setRows((prev) => {
          const seen = new Set(prev.map((r) => r.id));
          return [...prev, ...data.entries.filter((e) => !seen.has(e.id))];
        });
        setFeed(data);
        setPage(next);
      })
      .catch(() => {
        // Keep what's on screen — the button stays, so the retry is the same press again.
      })
      .finally(() => setLoadingMore(false));
  };

  /** Per-category counts, summed from the action facets the server returned. */
  const categoryCounts = useMemo(() => {
    const counts: Record<string, number> = {};
    for (const facet of feed?.actions ?? []) {
      counts[facet.category] = (counts[facet.category] ?? 0) + facet.count;
    }
    return counts;
  }, [feed?.actions]);

  const tabCount = useCallback(
    (tab: AuditTab): number => {
      const categories = TAB_CATEGORIES[tab];
      if (categories.length === 0) {
        return (feed?.actions ?? []).reduce((sum, f) => sum + f.count, 0);
      }
      return categories.reduce((sum, c) => sum + (categoryCounts[c] ?? 0), 0);
    },
    [feed?.actions, categoryCounts],
  );

  const productOptions = useMemo(() => {
    const set = new Set(filterOptions.products);
    // Keep the current pick listed even when nothing carries it any more, or a filter that has
    // outlived its promotions can't be cleared.
    if (state.product) set.add(state.product);
    return Array.from(set).sort();
  }, [filterOptions.products, state.product]);

  const targetEnvOptions = useMemo(() => {
    const set = new Set(filterOptions.targetEnvs);
    if (state.targetEnv) set.add(state.targetEnv);
    // Deployment order (dev → staging → prod), which is the order a reader is already thinking in.
    return getOrderedEnvironments(Array.from(set));
  }, [filterOptions.targetEnvs, state.targetEnv, getOrderedEnvironments]);

  /** Actors seen in the current window, for the dropdown. Busiest first — that's who you're after. */
  const actorOptions = useMemo(() => feed?.actors ?? [], [feed?.actors]);

  const selectedActorName = useMemo(
    () => actorOptions.find((a) => a.id === state.actor)?.name ?? state.actor,
    [actorOptions, state.actor],
  );

  /** Action names present in the window, so the precise filter offers only real choices. */
  const actionOptions = useMemo(() => {
    const present = (feed?.actions ?? []).map((f) => f.action);
    if (state.action && !present.includes(state.action)) present.push(state.action);
    return present;
  }, [feed?.actions, state.action]);

  const activeFilters: ActiveFilterChip[] = [];
  if (state.product) {
    activeFilters.push({
      label: 'Product',
      value: state.product,
      onClear: () => change({ product: '' }),
    });
  }
  if (state.service) {
    activeFilters.push({
      label: 'Service',
      value: state.service,
      onClear: () => change({ service: '' }),
    });
  }
  if (state.targetEnv) {
    activeFilters.push({
      label: 'Target env',
      value: getDisplayName(state.targetEnv),
      onClear: () => change({ targetEnv: '' }),
    });
  }
  if (state.actor) {
    activeFilters.push({
      label: 'Who',
      value: selectedActorName,
      onClear: () => change({ actor: '' }),
    });
  }
  if (state.action) {
    activeFilters.push({
      label: 'Action',
      value: actionStyle(state.action).verb,
      onClear: () => change({ action: '' }),
    });
  }

  const activeFilterCount = activeFilters.length;
  const total = feed?.total ?? 0;
  const hasMore = rows.length < total;

  // Rows under a day heading each. "Today" is what makes the day question answerable at a glance, and
  // grouping is the only way a feed of timestamps reads as a set of days rather than a list.
  const days = useMemo(() => groupByDay(rows), [rows]);

  return (
    <div className="space-y-6">
      {/* Header */}
      <div className="flex items-start justify-between gap-4">
        <div>
          <h1
            className="text-xl font-semibold tracking-tight"
            style={{ color: 'var(--text-primary)' }}
          >
            Promotions audit
          </h1>
          <p className="text-[13px] mt-1" style={{ color: 'var(--text-muted)' }}>
            Every action taken on a promotion — who did it, when, and to what
          </p>
        </div>
        {/* The reason the window and the filters are in the URL: an answer to somebody's question is
            handed over as a link, and nobody thinks to look in the address bar for one. */}
        <CopyViewLinkButton
          params={currentParams()}
          title="Copy a link to this view — the window, the tab and every filter travel with it"
        />
      </div>

      {/* The window. First control on the page because every question here starts with one: today,
          this week, or the whole history. */}
      <RovingGroup
        ariaLabel="Time window"
        className="flex items-center gap-2 overflow-x-auto pb-1 sm:flex-wrap sm:overflow-x-visible sm:pb-0"
      >
        {AUDIT_RANGES.map((range) => {
          const active = state.range === range;
          return (
            <button
              key={range}
              type="button"
              onClick={() => change({ range })}
              aria-pressed={active}
              className="flex shrink-0 items-center gap-1.5 whitespace-nowrap rounded-lg border px-3 py-1.5 text-[13px] font-medium transition-colors"
              style={{
                borderColor: active ? 'var(--accent)' : 'var(--border-color)',
                backgroundColor: active ? 'var(--accent-bg)' : 'var(--bg-primary)',
                color: active ? 'var(--accent)' : 'var(--text-secondary)',
              }}
            >
              {range === 'today' && <Clock size={12} />}
              {RANGE_LABELS[range]}
            </button>
          );
        })}
      </RovingGroup>

      {/* What happened in that window, in four numbers. Counted under the filters but not under the
          tab, so this reads the same whichever tab is open — it's the summary, not the selection. */}
      <div className="grid grid-cols-2 gap-3 sm:grid-cols-4">
        {SUMMARY_TILES.map((tile) => {
          const count = tabCount(tile.tab);
          const active = state.tab === tile.tab;
          const Icon = tile.icon;
          return (
            <button
              key={tile.tab}
              type="button"
              onClick={() => change({ tab: active ? 'all' : tile.tab })}
              aria-pressed={active}
              className="rounded-xl border p-3 text-left transition-colors"
              style={{
                borderColor: active ? tile.color : 'var(--border-color)',
                backgroundColor: 'var(--bg-primary)',
              }}
              title={
                active
                  ? `Showing ${tile.label.toLowerCase()} — press again for all activity`
                  : `Show only what was ${tile.label.toLowerCase()} ${RANGE_PHRASES[state.range]}`
              }
            >
              <span
                className="flex items-center gap-1.5 text-[11px] font-semibold uppercase tracking-wider"
                style={{ color: 'var(--text-muted)' }}
              >
                <Icon size={12} style={{ color: tile.color }} />
                {tile.label}
              </span>
              <span
                className="mt-1 block text-[22px] font-semibold leading-none"
                style={{ color: loading ? 'var(--text-muted)' : 'var(--text-primary)' }}
              >
                {loading ? '–' : count}
              </span>
            </button>
          );
        })}
      </div>

      {/* Filters */}
      <FilterPanel activeCount={activeFilterCount}>
        <select
          value={state.product}
          onChange={(e) => change({ product: e.target.value })}
          className="rounded-lg border px-3 py-1.5 text-[13px]"
          style={{
            borderColor: 'var(--border-color)',
            backgroundColor: 'var(--bg-primary)',
            color: 'var(--text-primary)',
          }}
        >
          <option value="">All products</option>
          {productOptions.map((p) => (
            <option key={p} value={p}>
              {p}
            </option>
          ))}
        </select>
        <input
          type="text"
          placeholder="Service search..."
          value={state.service}
          onChange={(e) => change({ service: e.target.value })}
          className="rounded-lg border px-3 py-1.5 text-[13px]"
          style={{
            borderColor: 'var(--border-color)',
            backgroundColor: 'var(--bg-primary)',
            color: 'var(--text-primary)',
          }}
        />
        {/* Takes the environment's own colour when one is picked, so "this is the prod view" is
            visible without reading the dropdown. */}
        <select
          value={state.targetEnv}
          onChange={(e) => change({ targetEnv: e.target.value })}
          className="rounded-lg border px-3 py-1.5 text-[13px] font-medium"
          style={targetEnvSelectStyle}
        >
          <option value="">All target envs</option>
          {targetEnvOptions.map((env) => (
            <option key={env} value={env}>
              {getDisplayName(env)}
            </option>
          ))}
        </select>
        {/* "…and who did it". Only the people who actually did something in this window are offered,
            busiest first, so the list is short and every entry has rows behind it. */}
        <select
          value={state.actor}
          onChange={(e) => change({ actor: e.target.value })}
          className="rounded-lg border px-3 py-1.5 text-[13px]"
          style={{
            borderColor: 'var(--border-color)',
            backgroundColor: 'var(--bg-primary)',
            color: 'var(--text-primary)',
          }}
        >
          <option value="">Anyone</option>
          {actorOptions.map((a) => (
            <option key={a.id} value={a.id}>
              {a.name} ({a.count})
            </option>
          ))}
          {/* A link may name somebody who has done nothing in the current window. Keep the selection
              listed so it can be seen and cleared rather than silently reverting to "Anyone". */}
          {state.actor && !actorOptions.some((a) => a.id === state.actor) && (
            <option value={state.actor}>{state.actor} (0)</option>
          )}
        </select>
        {/* The precise filter, for when a tab is too coarse — "only bypasses", "only comments". */}
        <select
          value={state.action}
          onChange={(e) => change({ action: e.target.value })}
          className="rounded-lg border px-3 py-1.5 text-[13px]"
          style={{
            borderColor: 'var(--border-color)',
            backgroundColor: 'var(--bg-primary)',
            color: 'var(--text-primary)',
          }}
        >
          <option value="">Any action</option>
          {actionOptions.map((a) => (
            <option key={a} value={a}>
              {actionStyle(a).verb}
            </option>
          ))}
        </select>
      </FilterPanel>

      {/* Tabs over the kinds of action. Badged from the same facets the tiles use, so a tab never
          advertises rows the filters have already excluded. */}
      <RovingGroup
        ariaLabel="Activity kinds"
        className="flex items-center gap-2 overflow-x-auto pb-1 sm:flex-wrap sm:overflow-x-visible sm:pb-0"
      >
        {AUDIT_TABS.map((tab) => {
          const active = state.tab === tab;
          const count = tabCount(tab);
          return (
            <button
              key={tab}
              type="button"
              onClick={() => change({ tab })}
              aria-pressed={active}
              className="flex shrink-0 items-center gap-1.5 whitespace-nowrap rounded-lg border px-3 py-1.5 text-[13px] font-medium transition-colors"
              style={{
                borderColor: active ? 'var(--accent)' : 'var(--border-color)',
                backgroundColor: active ? 'var(--accent-bg)' : 'var(--bg-primary)',
                color: active ? 'var(--accent)' : 'var(--text-secondary)',
              }}
            >
              {TAB_LABELS[tab]}
              {!loading && count > 0 && (
                <span
                  className="ml-0.5 rounded-full px-1.5 text-[11px] font-semibold"
                  style={{
                    backgroundColor: active ? 'var(--accent)' : 'var(--bg-secondary)',
                    color: active ? '#fff' : 'var(--text-muted)',
                  }}
                >
                  {count}
                </span>
              )}
            </button>
          );
        })}
      </RovingGroup>

      {loading ? (
        <div className="space-y-3">
          {[1, 2, 3, 4].map((i) => (
            <div key={i} className="skeleton h-16" />
          ))}
        </div>
      ) : failed ? (
        <ListEmptyState
          icon={AlertTriangle}
          tone="neutral"
          title="The activity feed could not be loaded"
          body="The request for the audit trail failed. Nothing has been lost — the trail is written when each action happens, so retrying is safe."
        />
      ) : rows.length === 0 ? (
        <AuditEmptyState
          tab={state.tab}
          range={state.range}
          filters={activeFilters}
          onClearFilters={clearAllFilters}
        />
      ) : (
        <div className="space-y-6">
          <div className="flex items-center justify-between">
            <h2
              className="text-[11px] font-semibold uppercase tracking-wider"
              style={{ color: 'var(--text-muted)' }}
            >
              {TAB_LABELS[state.tab]} · {RANGE_LABELS[state.range]}
            </h2>
            <span className="text-[11px]" style={{ color: 'var(--text-muted)' }}>
              {rows.length === total
                ? `${total} ${total === 1 ? 'action' : 'actions'}`
                : `${rows.length} of ${total}`}
            </span>
          </div>

          {days.map((day) => (
            <div key={day.key}>
              {/* Sticky so the day a row belongs to is still on screen after scrolling into it —
                  which is the entire question on this page. */}
              <h3
                className="sticky top-0 z-10 -mx-1 mb-2 px-1 py-1 text-[11px] font-semibold uppercase tracking-wider backdrop-blur"
                style={{ color: 'var(--text-muted)', backgroundColor: 'var(--bg-secondary)' }}
              >
                {day.label}
                <span className="ml-2 font-normal normal-case tracking-normal">
                  {day.entries.length} {day.entries.length === 1 ? 'action' : 'actions'}
                </span>
              </h3>
              <KeyboardList
                className="space-y-2"
                count={day.entries.length}
                ariaLabel={`${TAB_LABELS[state.tab]}, ${day.label}`}
                autoFocus={false}
              >
                {day.entries.map((entry, index) => (
                  <AuditRow key={entry.id} index={index} entry={entry} />
                ))}
              </KeyboardList>
            </div>
          ))}

          {hasMore && (
            <button
              type="button"
              onClick={loadMore}
              disabled={loadingMore}
              className="w-full rounded-xl border py-2.5 text-[13px] font-medium transition-opacity hover:opacity-80"
              style={{
                borderColor: 'var(--border-color)',
                backgroundColor: 'var(--bg-primary)',
                color: 'var(--text-secondary)',
                opacity: loadingMore ? 0.6 : 1,
              }}
            >
              {loadingMore ? 'Loading…' : `Load ${Math.min(PAGE_SIZE, total - rows.length)} more`}
            </button>
          )}
        </div>
      )}
    </div>
  );
}

/**
 * The empty panel. A filtered empty feed and an empty window are different claims — "nothing was
 * approved today" is a fact worth stating plainly, while the same page with a product filter set is
 * only saying something about that product — so the copy names whichever it is.
 */
function AuditEmptyState({
  tab,
  range,
  filters,
  onClearFilters,
}: {
  tab: AuditTab;
  range: AuditRange;
  filters: ActiveFilterChip[];
  onClearFilters: () => void;
}) {
  if (filters.length > 0) {
    const one = filters.length === 1;
    return (
      <ListEmptyState
        icon={Filter}
        tone="filtered"
        title={`Nothing ${TAB_SUBJECTS[tab]} ${RANGE_PHRASES[range]} that matches ${
          one ? 'this filter' : 'these filters'
        }`}
        body={`Drop ${
          one ? 'it' : 'one of them'
        } to widen the answer, or take the window out to a longer one — an audit trail is only ever as long as the window you ask for.`}
        filters={filters}
        onClearFilters={onClearFilters}
      />
    );
  }

  return (
    <ListEmptyState
      icon={History}
      tone="neutral"
      title={`Nothing ${TAB_SUBJECTS[tab]} ${RANGE_PHRASES[range]}`}
      body={
        range === 'all'
          ? 'No promotion activity has been recorded yet. Every approval, rejection, sign-off and deploy lands here as it happens.'
          : 'Try a longer window — this page only shows what happened inside the one selected above.'
      }
    />
  );
}

/**
 * One recorded action.
 *
 * Reads as a sentence: who, what they did, and which promotion it was. The promotion — product,
 * service, source → target, version — is on every row rather than only on a change of subject,
 * because rows are read one at a time here (people scan for the one line they were asked about) and
 * a row that needs the one above it to be understood is no use pasted into a chat.
 */
function AuditRow({ index, entry }: { index: number; entry: PromotionAuditEntry }) {
  const navigate = useNavigate();
  const [showDetails, setShowDetails] = useState(false);
  const style = actionStyle(entry.action);
  const Icon = style.icon;
  const system = entry.actorType === 'system';
  const timestamp = new Date(entry.timestamp);

  const rowProps = useKeyboardListRow(index, () => navigate(`/promotions/${entry.candidateId}`), {
    label: `${entry.actorName} ${style.verb} — ${entry.product} / ${entry.service} ${entry.version} to ${entry.targetEnv}. Open promotion.`,
  });

  // Only worth offering where there is something the row hasn't already said.
  const hasDetails = entry.details !== null && Object.keys(entry.details).length > 0;

  return (
    <div
      {...rowProps}
      className="card-hover rounded-xl border p-3 cursor-pointer"
      style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-primary)' }}
    >
      <div className="flex items-start gap-3">
        <span
          aria-hidden
          className="mt-0.5 flex h-6 w-6 shrink-0 items-center justify-center rounded-lg"
          style={{ backgroundColor: style.bg, color: style.color }}
        >
          <Icon size={13} />
        </span>

        <div className="min-w-0 flex-1">
          {/* Who and what. Wraps rather than truncates — the verb is the row's content, and a
              half-shown one makes the line unreadable. */}
          <div className="flex flex-wrap items-baseline gap-x-1.5 gap-y-1 text-[13px]">
            <span
              className="inline-flex items-center gap-1 font-semibold"
              style={{ color: 'var(--text-primary)' }}
            >
              {system ? <Bot size={11} /> : <User size={11} />}
              {entry.actorName}
            </span>
            <span style={{ color: 'var(--text-secondary)' }}>{style.verb}</span>
            {entry.workItemKey && (
              /* Straight to the ticket — a work-item row is almost always read in order to go and
                 look at the ticket it names. stopPropagation so it doesn't also open the promotion. */
              <Link
                to={workItemDetailPath(entry.workItemKey, entry.product, entry.targetEnv, entry.candidateId)}
                onClick={(e) => e.stopPropagation()}
                className="font-medium underline decoration-dotted underline-offset-2"
                style={{ color: 'var(--accent)' }}
              >
                {entry.workItemKey}
              </Link>
            )}
            {entry.role && (
              <span className="badge" style={{ backgroundColor: 'var(--bg-secondary)', color: 'var(--text-muted)' }}>
                {entry.role}
              </span>
            )}
          </div>

          {/* Which promotion. */}
          <div
            className="mt-1.5 flex flex-wrap items-center gap-x-2 gap-y-1 text-[12px]"
            style={{ color: 'var(--text-secondary)' }}
          >
            <span className="font-medium truncate" style={{ color: 'var(--text-primary)' }}>
              {entry.product} / {entry.service}
            </span>
            <EnvBadge env={entry.sourceEnv} suffix={`(${entry.version})`} />
            <ArrowRight size={11} className="shrink-0" style={{ color: 'var(--text-muted)' }} />
            <EnvBadge env={entry.targetEnv} />
          </div>

          {/* The human's name on a gate that opened. The row's own actor is the evaluator that
              noticed, so without this the answer to "who approved it" would read "System". */}
          {entry.approvedBy && entry.approvedBy.length > 0 && (
            <p className="mt-1.5 text-[12px]" style={{ color: 'var(--text-secondary)' }}>
              <span style={{ color: 'var(--text-muted)' }}>Approved by </span>
              <span className="font-medium">{entry.approvedBy.map((a) => a.name).join(', ')}</span>
            </p>
          )}

          {/* What they said. A bypass reason is not optional — it is the record of why a gate was
              skipped — so it is tinted rather than shown as an ordinary comment. */}
          {entry.reason && (
            <p
              className="mt-1.5 rounded-lg px-2 py-1 text-[12px]"
              style={{ backgroundColor: 'var(--warning-bg)', color: 'var(--warning)' }}
            >
              {entry.reason}
            </p>
          )}
          {entry.comment && (
            <p
              className="mt-1.5 whitespace-pre-wrap text-[12px] italic"
              style={{ color: 'var(--text-secondary)' }}
            >
              “{entry.comment}”
            </p>
          )}

          {hasDetails && (
            <button
              type="button"
              onClick={(e) => {
                e.stopPropagation();
                setShowDetails((v) => !v);
              }}
              aria-expanded={showDetails}
              className="mt-1.5 inline-flex items-center gap-1 text-[11px] font-medium"
              style={{ color: 'var(--text-muted)' }}
            >
              <ChevronDown
                size={11}
                className={`transition-transform duration-150 ${showDetails ? 'rotate-180' : ''}`}
              />
              {showDetails ? 'Hide' : 'Show'} what was recorded
            </button>
          )}
          {showDetails && (
            /* The raw payload. Actions differ in what they record, and a page that only rendered the
               fields it knows about would quietly drop the one somebody is asking about. */
            <pre
              className="mt-1.5 overflow-x-auto rounded-lg border p-2 text-[11px]"
              style={{
                borderColor: 'var(--border-color)',
                backgroundColor: 'var(--bg-secondary)',
                color: 'var(--text-secondary)',
              }}
            >
              {JSON.stringify(entry.details, null, 2)}
            </pre>
          )}
        </div>

        {/* When, and where the promotion stands now. The status is deliberately labelled as current
            rather than as the state at the time — an audit row that implied otherwise would be
            actively misleading on a promotion that has since moved on. */}
        <div className="shrink-0 text-right">
          <time
            className="block text-[11px] tabular-nums"
            style={{ color: 'var(--text-muted)' }}
            dateTime={entry.timestamp}
            title={`${timestamp.toLocaleString()} · ${formatDistanceToNow(timestamp, { addSuffix: true })}`}
          >
            {timestamp.toLocaleTimeString(undefined, { hour: '2-digit', minute: '2-digit' })}
          </time>
          <span
            className="mt-1 block text-[10px] uppercase tracking-wider"
            style={{ color: 'var(--text-muted)' }}
            title={`This promotion is ${entry.candidateStatus} now — not necessarily what it was when this happened`}
          >
            now {entry.candidateStatus}
          </span>
        </div>
      </div>
    </div>
  );
}

/**
 * Splits the feed into days, in the order it arrived (newest first).
 *
 * Local days, not UTC ones: "what was approved today" is a question about the reader's calendar, and
 * a feed that put this morning's approvals under yesterday because the server counts in UTC would be
 * answering a question nobody asked.
 */
function groupByDay(entries: PromotionAuditEntry[]): {
  key: string;
  label: string;
  entries: PromotionAuditEntry[];
}[] {
  const days: { key: string; label: string; entries: PromotionAuditEntry[] }[] = [];
  for (const entry of entries) {
    const date = new Date(entry.timestamp);
    const key = dayKey(date);
    const last = days[days.length - 1];
    if (last && last.key === key) {
      last.entries.push(entry);
      continue;
    }
    days.push({ key, label: dayLabel(date), entries: [entry] });
  }
  return days;
}

function dayKey(date: Date): string {
  return `${date.getFullYear()}-${date.getMonth()}-${date.getDate()}`;
}

/**
 * "Today" / "Yesterday" / a written date. The two relative labels are the ones people ask in, and
 * anything older is easier to place from the date itself than from "5 days ago".
 */
function dayLabel(date: Date): string {
  const today = new Date();
  if (dayKey(date) === dayKey(today)) return 'Today';
  const yesterday = new Date(today);
  yesterday.setDate(today.getDate() - 1);
  if (dayKey(date) === dayKey(yesterday)) return 'Yesterday';
  return date.toLocaleDateString(undefined, {
    weekday: 'short',
    day: 'numeric',
    month: 'short',
    year: date.getFullYear() === today.getFullYear() ? undefined : 'numeric',
  });
}
