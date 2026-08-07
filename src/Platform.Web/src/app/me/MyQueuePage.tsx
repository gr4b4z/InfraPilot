import { useEffect, useState, useMemo } from 'react';
import { Link, useNavigate, useSearchParams } from 'react-router-dom';
import { api } from '@/lib/api';
import type { PendingAssignee, PendingTicket, WorkItemDecision } from '@/lib/api';
import { useAuthStore } from '@/stores/authStore';
import { useMyTasksStore, refreshMyTasks } from '@/stores/myTasksStore';
import { readEnumPref, writePref, WORK_ITEMS_VIEW_PREF } from '@/lib/prefs';
import { FilterPanel, filterLabelClass, filterSelectClass } from '@/components/ui/FilterPanel';
import {
  ListEmptyState,
  type ActiveFilterChip,
  type EmptyStateTone,
} from '@/components/ui/ListEmptyState';
import { CopyViewLinkButton } from '@/components/ui/CopyViewLinkButton';
import { KeyboardList } from '@/components/ui/KeyboardList';
import { RovingGroup } from '@/components/ui/RovingGroup';
import { useSearchScope } from '@/stores/searchScopeStore';
import { useKeyboardListRow } from '@/hooks/keyboardList';
import { useEntityRefresh } from '@/hooks/useEntityEvents';
import { ROW_ACTION_ATTR } from '@/lib/keys';
import { WorkItemParticipants } from '@/components/promotions/WorkItemParticipants';
import { WorkItemEnvironments } from '@/components/promotions/WorkItemEnvironments';
import { MissingRolesBadge } from '@/components/promotions/MissingRoles';
import { decisionStyle, workItemDetailPath } from '@/lib/workItem';
import { useDocumentTitle, scopeTitle } from '@/lib/pageTitle';
import { formatDistanceToNow } from 'date-fns';
import {
  Ticket,
  AlertTriangle,
  Ban,
  CheckCircle,
  XCircle,
  ExternalLink,
  ArrowRight,
  Inbox,
  Unlink,
  Filter,
  UserPlus,
  History,
} from 'lucide-react';
import {
  buildQueueParams,
  hasQueueParams,
  parseQueueParams,
  type DeciderFilterValue,
  type QueueParams,
  type QueueView,
  type TimeFrameValue,
} from './queueFilterParams';
import {
  AssigneeFilter,
  loadAssigneeFilter,
  saveAssigneeFilter,
  type AssigneeFilterValue,
} from './AssigneeFilter';
import {
  ScopeFilter,
  loadScopeFilter,
  saveScopeFilter,
  applyScopeFilter,
  hasActiveScope,
  SCOPE_FILTER_DEFAULT,
  type ScopeFilterValue,
} from './ScopeFilter';
import { useSettingsStore } from '@/stores/settingsStore';

/**
 * "My queue" page — work items awaiting the current user's signoff. Reads
 * GET /api/work-items/me/pending which returns one row per (work item × candidate)
 * after applying authority filters server-side (approver group, excluded role,
 * already-decided), so client-side rendering is straight-through.
 *
 * Four tabs: the items you're answerable for, the items nobody has been put on, the whole pending
 * pool you're authorised to sign off, and your team's decision history. The first two are defined by
 * the promotion policy's required work-item roles (see {@link QueueView}). The pick is remembered in
 * a cookie so coming back lands you where you left off.
 */
export function MyQueuePage() {
  // The URL is the shareable form of this page's state; localStorage is the resumable one. A link
  // carrying any queue parameter wins outright on arrival — see queueFilterParams for why a shared
  // view must not blend with the recipient's saved filters. Read once, on mount: after that the state
  // below owns the view and the effect at the bottom writes it back to the URL.
  const [searchParams, setSearchParams] = useSearchParams();
  const initial = useMemo(
    () => {
      if (hasQueueParams(searchParams)) return parseQueueParams(searchParams, loadQueueView());
      return {
        view: loadQueueView(),
        assignee: loadAssigneeFilter(),
        scope: loadScopeFilter(),
        timeFrame: loadTimeFrame(),
        decider: loadDeciderFilter(),
      };
    },
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [],
  );

  const [tickets, setTickets] = useState<PendingTicket[]>([]);
  // Server-supplied (email, required-role) rollup feeding the person dropdown. Computed against
  // the user's authorized list pre-narrowing — the queue itself, not the org directory — and
  // limited to people holding a policy-required role, so every person offered is one the filter
  // can actually match.
  const [assignees, setAssignees] = useState<PendingAssignee[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  // Hydrated from the link or from localStorage (see `initial`). Only on mount — subsequent updates
  // flow through the onChange callbacks below, which persist and re-write the URL.
  const [assigneeFilter, setAssigneeFilter] = useState<AssigneeFilterValue>(initial.assignee);
  // Product / service / targetEnv narrowing — applied client-side to the loaded queue.
  const [scopeFilter, setScopeFilter] = useState<ScopeFilterValue>(initial.scope);
  // Which slice of the queue is on screen. Cookie-persisted (see lib/prefs).
  const [view, setView] = useState<QueueView>(initial.view);
  // Time frame — only meaningful on the "decided" view; defaults to last day.
  const [timeFrame, setTimeFrame] = useState<TimeFrameValue>(initial.timeFrame);
  // Decider narrowing — only meaningful on the "decided" view. Filters by who clicked
  // decision ("Me" = the current user's own decisions). Persisted via localStorage.
  const [deciderFilter, setDeciderFilter] = useState<DeciderFilterValue>(initial.decider);
  // The auth store already carries the current user's email — same source PromotionDetailPage
  // uses for `currentUserEmail`. No extra API call needed; we just send this email to the
  // server when the user picks "Assigned to me".
  const currentUserEmail = useAuthStore((s) => s.user?.email ?? '');
  // Badges for the two attention tabs. Both come from the shared My-tasks rollup — the same queries
  // these tabs run — so the numbers are live on every tab, not just once you've opened one.
  const assignedToMeCount = useMyTasksStore((s) => s.workItems.length);
  const notAssignedCount = useMyTasksStore((s) => s.unassignedWorkItems.length);
  // Environments are stored by key and shown by display name; the empty state's filter chips have to
  // read back the same way the dropdowns that set them do.
  const getDisplayName = useSettingsStore((s) => s.getDisplayName);

  // Defined as an async function so the initial fetch from `useEffect` can be a
  // microtask (avoids the eslint react-hooks/set-state-in-effect rule and the
  // associated cascading-render warning) while still letting decision handlers
  // call `fetchData()` directly to refresh after a decision.
  const fetchData = async (
    nextView: QueueView,
    filter: AssigneeFilterValue,
    tf: TimeFrameValue,
    decider: DeciderFilterValue,
  ) => {
    setLoading(true);
    setError(null);
    try {
      const apiArg = toApiArg(nextView, filter, currentUserEmail, tf, decider);
      const res = await api.getMyPendingWorkItems(apiArg);
      setTickets(res.tickets ?? []);
      setAssignees(res.assignees ?? []);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to load work items');
    } finally {
      setLoading(false);
    }
  };

  // The queue is a projection over work items and their promotions — refresh on either stream.
  const realtimeTick = useEntityRefresh(['work-item', 'promotion']);

  useEffect(() => {
    void fetchData(view, assigneeFilter, timeFrame, deciderFilter);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [view, assigneeFilter, timeFrame, deciderFilter, currentUserEmail, realtimeTick]);

  /**
   * Mirrors the view into the query string, so from the first filter change onwards the address bar is
   * itself a link to what is on screen. Called from the change handlers rather than from an effect
   * watching the state: a filter change is a user action with a known outcome, and deriving the URL in
   * an effect would cost a render pass whose only job is to fix up the address bar. (Copy link doesn't
   * depend on this having run — it builds the URL from the state directly.)
   *
   * `replace` rather than `push` — changing a dropdown is not a navigation, and pushing would make
   * Back walk out of the page one dropdown at a time.
   */
  const currentParams = (next: Partial<QueueParams> = {}): URLSearchParams =>
    buildQueueParams({
      view,
      assignee: assigneeFilter,
      scope: scopeFilter,
      timeFrame,
      decider: deciderFilter,
      ...next,
    });

  const syncUrl = (next: Partial<QueueParams>) => {
    const params = currentParams(next);
    if (params.toString() === searchParams.toString()) return;
    setSearchParams(params, { replace: true });
  };

  // Each handler does the same three things: persist (so the view resumes), set state (so the page
  // re-renders), and update the URL (so the view can be handed to someone).
  const handleFilterChange = (next: AssigneeFilterValue) => {
    saveAssigneeFilter(next);
    setAssigneeFilter(next);
    syncUrl({ assignee: next });
  };

  const handleScopeChange = (next: ScopeFilterValue) => {
    saveScopeFilter(next);
    setScopeFilter(next);
    syncUrl({ scope: next });
  };

  const handleViewChange = (next: QueueView) => {
    saveQueueView(next);
    setView(next);
    syncUrl({ view: next });
  };

  const handleTimeFrameChange = (next: TimeFrameValue) => {
    saveTimeFrame(next);
    setTimeFrame(next);
    syncUrl({ timeFrame: next });
  };

  const handleDeciderChange = (next: DeciderFilterValue) => {
    saveDeciderFilter(next);
    setDeciderFilter(next);
    syncUrl({ decider: next });
  };

  /**
   * Resets every filter the current tab actually shows, in one pass. Scoped to the visible controls
   * for the same reason {@link activeFilterCount} is: a time frame behind a tab that doesn't use it
   * is not something the reader was told about, so silently rewriting it would be a change they
   * can't see. The URL is written once at the end — the individual handlers each rebuild the
   * parameters from state that hasn't re-rendered yet.
   */
  const clearAllFilters = () => {
    saveScopeFilter(SCOPE_FILTER_DEFAULT);
    setScopeFilter(SCOPE_FILTER_DEFAULT);
    const next: Partial<QueueParams> = { scope: SCOPE_FILTER_DEFAULT };
    if (view === 'pending') {
      saveAssigneeFilter({ mode: 'all' });
      setAssigneeFilter({ mode: 'all' });
      next.assignee = { mode: 'all' };
    }
    if (view === 'decided') {
      saveTimeFrame('all');
      setTimeFrame('all');
      saveDeciderFilter({ mode: 'all' });
      setDeciderFilter({ mode: 'all' });
      next.timeFrame = 'all';
      next.decider = { mode: 'all' };
    }
    syncUrl(next);
  };

  /**
   * Every narrowing in effect on this tab, named and clearable, for the empty state to report. Same
   * controls in the same order the filter panel renders them.
   *
   * Wider than {@link activeFilterCount}, deliberately, and on one control: the decided tab's time
   * frame is listed at its "Last day" default too. The badge counts filters the user changed; this
   * lists filters that are hiding rows, and "your team decided nothing in the last 24 hours" is by
   * far the most common reason that tab comes up empty.
   */
  const activeFilters: ActiveFilterChip[] = [];
  if (view === 'decided' && timeFrame !== 'all') {
    activeFilters.push({
      label: 'Time frame',
      value: TIME_FRAME_LABELS[timeFrame],
      // Clears to "All time", not back to the '1d' default: the point of clearing from an empty
      // list is to widen it, and the default is itself a narrowing.
      onClear: () => handleTimeFrameChange('all'),
    });
  }
  if (view === 'decided' && deciderFilter.mode !== 'all') {
    activeFilters.push({
      label: 'Decided by',
      value: deciderFilter.mode === 'me' ? 'Me' : deciderFilter.displayName,
      onClear: () => handleDeciderChange({ mode: 'all' }),
    });
  }
  if (view === 'pending' && assigneeFilter.mode !== 'all') {
    activeFilters.push({
      label: 'Assigned to',
      value: assigneeLabel(assigneeFilter),
      onClear: () => handleFilterChange({ mode: 'all' }),
    });
  }
  if (scopeFilter.product) {
    activeFilters.push({
      label: 'Product',
      value: scopeFilter.product,
      onClear: () => handleScopeChange({ ...scopeFilter, product: null }),
    });
  }
  if (scopeFilter.service) {
    activeFilters.push({
      label: 'Service',
      value: scopeFilter.service,
      onClear: () => handleScopeChange({ ...scopeFilter, service: null }),
    });
  }
  if (scopeFilter.targetEnv) {
    activeFilters.push({
      // Display name, not the raw key — the chip has to be recognisable as the dropdown's own pick.
      label: 'Target env',
      value: getDisplayName(scopeFilter.targetEnv),
      onClear: () => handleScopeChange({ ...scopeFilter, targetEnv: null }),
    });
  }
  if (scopeFilter.deployedEnv) {
    activeFilters.push({
      label: 'Testable in',
      value: getDisplayName(scopeFilter.deployedEnv),
      onClear: () => handleScopeChange({ ...scopeFilter, deployedEnv: null }),
    });
  }

  // Server-narrowed list × scope filter → what the user actually sees.
  const filteredTickets = useMemo(
    () => applyScopeFilter(tickets, scopeFilter),
    [tickets, scopeFilter],
  );

  // `/` searches the queue that is loaded, client-side: these rows are already the authorised set,
  // and re-querying the server would only be able to narrow by the same filters the panel above
  // offers. Re-registers when the rows change so the search always sees what is on screen.
  useSearchScope(
    {
      label: 'This queue',
      placeholder: 'Filter these work items — key, title, product or service…',
      search: async (query) => {
        const needle = query.toLowerCase();
        return filteredTickets
          .filter((t) =>
            [t.workItemKey, t.title ?? '', t.product, t.service]
              .some((field) => field.toLowerCase().includes(needle)),
          )
          .slice(0, 25)
          .map((t) => ({
            id: `${t.workItemKey}-${t.candidateId}`,
            title: t.title ? `${t.workItemKey} — ${t.title}` : t.workItemKey,
            subtitle: `${t.product} / ${t.service} · ${t.targetEnv}`,
            to: workItemDetailPath(t.workItemKey, t.product, t.targetEnv),
          }));
      },
    },
    [filteredTickets],
  );

  // "Have a look at the items with no QA owner on checkout-api" is what this page gets sent as, and the
  // title has to carry that much or every link to the queue reads identically. Mirrors what the URL
  // carries (see buildQueueParams): filters only meaningful on one tab are reported only on that tab,
  // so the title can't claim a narrowing the recipient cannot see.
  useDocumentTitle([
    VIEW_LABELS[view],
    view === 'pending' ? assigneeTitle(assigneeFilter) : null,
    scopeTitle({
      product: scopeFilter.product,
      service: scopeFilter.service,
      targetEnv: scopeFilter.targetEnv,
    }),
    scopeFilter.deployedEnv && `testable in ${scopeFilter.deployedEnv}`,
    view === 'decided' ? TIME_FRAME_TITLES[timeFrame] : null,
    view === 'decided' ? deciderTitle(deciderFilter) : null,
    'Work items',
  ]);

  // Badge on the collapsed filter toggle. Counts only the controls actually on screen for the
  // current view — a stale time frame behind a collapsed panel on a tab that doesn't use it would
  // be a filter the user can't find and isn't affected by. Mirrors the render conditions below.
  const activeFilterCount = useMemo(() => {
    let n = 0;
    if (view === 'decided') {
      if (timeFrame !== '1d') n++;
      if (deciderFilter.mode !== 'all') n++;
    } else if (view === 'pending' && assigneeFilter.mode !== 'all') {
      // The person select only renders on the pending tab, so a stale pick behind it elsewhere
      // isn't a filter the user can find — or one that's in effect. Mirrors the render condition
      // below.
      n++;
    }
    for (const key of ['product', 'service', 'targetEnv', 'deployedEnv'] as const) {
      if (scopeFilter[key] !== null) n++;
    }
    return n;
  }, [view, timeFrame, deciderFilter, assigneeFilter, scopeFilter]);

  return (
    <div className="space-y-6">
      <div className="flex items-start justify-between gap-4">
        <div>
          <h1
            className="text-xl font-semibold tracking-tight"
            style={{ color: 'var(--text-primary)' }}
          >
            Work items queue
          </h1>
          <p className="text-[13px] mt-1" style={{ color: 'var(--text-muted)' }}>
            {VIEW_SUBTITLES[view]}
          </p>
        </div>
        {/* The point of putting the filters in the URL was so this view could be handed to someone,
            and nobody thinks to look in the address bar for that. Built from the state rather than
            read back off `location`, so it's exact even before the first filter change has written
            the parameters there. */}
        <CopyViewLinkButton params={currentParams()} />
      </div>

      {/* Tabs over the queue, matching the promotions list. Only the two personal-attention tabs
          carry a badge: their counts are fetched for the shell anyway, whereas a number on the
          other two would need a second query per tab just to label a tab nobody has opened. */}
      {/* Scrolls sideways below `sm` rather than wrapping to three rows on a phone, matching the
          promotions list. One tab stop, arrows inside. */}
      <RovingGroup
        ariaLabel="Queue views"
        className="flex items-center gap-2 overflow-x-auto pb-1 sm:flex-wrap sm:overflow-x-visible sm:pb-0"
      >
        {QUEUE_VIEWS.map((key) => {
          const active = view === key;
          const count =
            key === 'mine' ? assignedToMeCount : key === 'not-assigned' ? notAssignedCount : 0;
          return (
            <button
              key={key}
              type="button"
              onClick={() => handleViewChange(key)}
              aria-pressed={active}
              className="flex shrink-0 items-center gap-1.5 whitespace-nowrap rounded-lg border px-3 py-1.5 text-[13px] font-medium transition-colors"
              style={{
                borderColor: active ? 'var(--accent)' : 'var(--border-color)',
                backgroundColor: active ? 'var(--accent-bg)' : 'var(--bg-primary)',
                color: active ? 'var(--accent)' : 'var(--text-secondary)',
              }}
            >
              {VIEW_LABELS[key]}
              {count > 0 && (
                <span
                  className="ml-0.5 px-1.5 rounded-full text-[11px] font-semibold"
                  style={{
                    backgroundColor: active ? 'var(--accent)' : 'var(--warning-bg)',
                    color: active ? '#fff' : 'var(--warning)',
                  }}
                >
                  {count}
                </span>
              )}
            </button>
          );
        })}
      </RovingGroup>

      <FilterPanel activeCount={activeFilterCount}>
        {/* Time frame + decider narrowing are only meaningful on the decided view. */}
        {view === 'decided' && (
          <>
            <TimeFrameFilter value={timeFrame} onChange={handleTimeFrameChange} />
            <DeciderFilter
              value={deciderFilter}
              onChange={handleDeciderChange}
              deciders={assignees}
              currentUserEmail={currentUserEmail}
            />
          </>
        )}
        {/* Person narrowing ("assigned to a required role") is only meaningful for the pending
            pool. On "Assigned to me" the person is the tab; on "Not assigned" there is by
            definition nobody in the roles being asked about; history views have their own
            decider filter. */}
        {view === 'pending' && (
          <AssigneeFilter
            value={assigneeFilter}
            onChange={handleFilterChange}
            assignees={assignees}
          />
        )}
        <ScopeFilter
          value={scopeFilter}
          onChange={handleScopeChange}
          tickets={tickets}
        />
      </FilterPanel>

      {error && (
        <div
          className="flex items-center gap-3 p-4 rounded-xl border"
          style={{
            backgroundColor: 'var(--danger-bg)',
            borderColor: 'var(--danger)',
            color: 'var(--danger)',
          }}
        >
          <XCircle size={18} />
          <span className="text-[13px] font-medium">{error}</span>
        </div>
      )}

      {loading ? (
        <div className="space-y-3">
          {[1, 2, 3].map((i) => (
            <div key={i} className="skeleton h-16" />
          ))}
        </div>
      ) : filteredTickets.length === 0 ? (
        <QueueEmptyState
          view={view}
          filters={activeFilters}
          onClearFilters={clearAllFilters}
          /* Rows the tab loaded that the client-side scope narrowing then hid. Zero means the
             narrowing that emptied the list happened server-side (or there was nothing to begin
             with), which is a different sentence. */
          hiddenByScope={hasActiveScope(scopeFilter) ? tickets.length : 0}
        />
      ) : (
        <div>
          <h2
            className="text-[11px] font-semibold uppercase tracking-wider mb-3"
            style={{ color: 'var(--text-muted)' }}
          >
            {VIEW_LABELS[view]} ({filteredTickets.length})
          </h2>
          <KeyboardList
            className="space-y-2"
            count={filteredTickets.length}
            ariaLabel={`${VIEW_LABELS[view]} work items`}
          >
            {filteredTickets.map((t, index) => (
              <TicketRow
                key={`${t.workItemKey}-${t.candidateId}-${t.decidedAt ?? 'pending'}-${t.decidedByEmail ?? ''}`}
                index={index}
                ticket={t}
                onChanged={() => {
                  void fetchData(view, assigneeFilter, timeFrame, deciderFilter);
                  // Reassigning a work item changes who it's "assigned to", so the shell's
                  // counters and the bell badge are stale the moment this returns.
                  refreshMyTasks();
                }}
              />
            ))}
          </KeyboardList>
        </div>
      )}
    </div>
  );
}

function toApiArg(
  view: QueueView,
  filter: AssigneeFilterValue,
  currentUserEmail: string,
  timeFrame: TimeFrameValue,
  decider: DeciderFilterValue,
):
  | {
      assignee?: string;
      status?: 'pending' | 'decided';
      since?: string;
      roleRequirement?: 'assigned' | 'missing';
    }
  | undefined {
  // Decision-history views ignore participant narrowing but DO honour the decider filter:
  // `assignee` here means "who decided" (a single email; "Me" → current user). The backend
  // maps this param to WorkItemApproval.ApproverEmail on the decided path.
  if (view === 'decided') {
    const since = timeFrameToSince(timeFrame);
    const decidedBy = deciderToEmail(decider, currentUserEmail);
    return { status: 'decided', ...(since ? { since } : {}), ...(decidedBy ? { assignee: decidedBy } : {}) };
  }

  // On the "Assigned to me" tab the person is fixed by the tab (the filter's select is hidden
  // there). `roleRequirement=assigned` is what makes this "items I'm answerable for" rather than
  // "items my name appears on" — the server matches the person against the policy's required
  // roles only.
  if (view === 'mine') {
    const assignee = currentUserEmail || undefined;
    return { assignee, roleRequirement: 'assigned' };
  }

  // "Not assigned" asks about the items, not about a person: items missing somebody in a
  // policy-required role.
  if (view === 'not-assigned') {
    return { roleRequirement: 'missing' };
  }

  // Pending: the person filter narrows to items where the pick holds a policy-required role —
  // the same "answerable for it" bar as the tabs, applied to any person. "Missing a required
  // role" is the same question with nobody in the slot.
  switch (filter.mode) {
    case 'all':
      return undefined;
    case 'me':
      return currentUserEmail
        ? { assignee: currentUserEmail, roleRequirement: 'assigned' }
        : undefined;
    case 'unassigned':
      return { roleRequirement: 'missing' };
    case 'person':
      return filter.email ? { assignee: filter.email, roleRequirement: 'assigned' } : undefined;
  }
}

// ── Queue view (tabs) ────────────────────────────────────────────────────────────────────
// `mine`, `not-assigned` and `pending` are all the pending inbox, narrowed differently:
//  - mine         → the current user holds a role the item's promotion policy REQUIRES. Being named
//                   as, say, a ticket's reporter is not being made answerable for it, so this is a
//                   narrower question than "am I on this item at all" (which `pending` + the person
//                   filter still answers).
//  - not-assigned → at least one policy-required role has nobody in it. The work these items need is
//                   an assignment, not a sign-off, which is why they get their own tab.
//  - pending      → everything the user can sign off, however it's assigned.
// `decided` is the team's decision history for the same authorised set.

// The type itself lives in queueFilterParams — it's part of the URL contract, and the parser has to
// validate against the same list this renders.
export type { QueueView };

const QUEUE_VIEWS = ['mine', 'not-assigned', 'pending', 'decided'] as const;

const VIEW_LABELS: Record<QueueView, string> = {
  mine: 'Assigned to me',
  'not-assigned': 'Not assigned',
  pending: 'Pending',
  decided: 'Decided',
};

const VIEW_SUBTITLES: Record<QueueView, string> = {
  mine: 'Work items where you hold a role the promotion policy requires, awaiting sign-off.',
  'not-assigned':
    'Work items with nobody in a role their promotion policy requires — they need someone assigned.',
  pending: 'Every work item you can sign off, whoever it is assigned to.',
  decided: 'Work items already signed off, by you or anyone else in your approver group.',
};

/**
 * Loads the persisted tab. Defaults to "Assigned to me" — the slice that's actually the user's
 * to act on; the full pending pool is one click away and the tab badge shows what's waiting.
 */
function loadQueueView(): QueueView {
  return readEnumPref(WORK_ITEMS_VIEW_PREF, QUEUE_VIEWS, 'mine');
}

function saveQueueView(value: QueueView): void {
  writePref(WORK_ITEMS_VIEW_PREF, value);
}

// ── Time-frame filter (only meaningful on "decided" view) ────────────────────────────────

export type { TimeFrameValue };

const TIME_FRAME_STORAGE_KEY = 'me.queue.timeFrame';

/**
 * The select's option labels — also what the empty state's chip reads back, so the filter it names
 * is quotable from the dropdown that set it.
 */
const TIME_FRAME_LABELS: Record<TimeFrameValue, string> = {
  '1d': 'Last day',
  '7d': 'Last 7 days',
  '30d': 'Last 30 days',
  all: 'All time',
};

const TIME_FRAME_ORDER: readonly TimeFrameValue[] = ['1d', '7d', '30d', 'all'];

/** The time frame as a document-title segment — terser than the select's own option labels. */
const TIME_FRAME_TITLES: Record<TimeFrameValue, string> = {
  '1d': 'last day',
  '7d': 'last 7 days',
  '30d': 'last 30 days',
  all: 'all time',
};

function loadTimeFrame(): TimeFrameValue {
  try {
    const raw = window.localStorage.getItem(TIME_FRAME_STORAGE_KEY);
    if (raw === '7d' || raw === '30d' || raw === 'all') return raw;
  } catch {
    // Ignore.
  }
  return '1d';
}

function saveTimeFrame(value: TimeFrameValue): void {
  try {
    window.localStorage.setItem(TIME_FRAME_STORAGE_KEY, value);
  } catch {
    // Ignore.
  }
}

/**
 * Maps the time-frame pick into an ISO `since` cutoff for the API. Returns null for "all"
 * (no cutoff — server returns everything).
 */
function timeFrameToSince(value: TimeFrameValue): string | null {
  if (value === 'all') return null;
  const days = value === '1d' ? 1 : value === '7d' ? 7 : 30;
  const cutoff = new Date();
  cutoff.setDate(cutoff.getDate() - days);
  return cutoff.toISOString();
}

function TimeFrameFilter({
  value,
  onChange,
}: {
  value: TimeFrameValue;
  onChange: (next: TimeFrameValue) => void;
}) {
  return (
    <label
      className={filterLabelClass}
      style={{ color: 'var(--text-muted)' }}
    >
      <span>Time frame</span>
      <select
        value={value}
        onChange={(e) => onChange(e.target.value as TimeFrameValue)}
        className={filterSelectClass}
        style={{
          borderColor: 'var(--border-color)',
          backgroundColor: 'var(--bg-primary)',
          color: 'var(--text-primary)',
        }}
      >
        {TIME_FRAME_ORDER.map((t) => (
          <option key={t} value={t}>
            {TIME_FRAME_LABELS[t]}
          </option>
        ))}
      </select>
    </label>
  );
}

// ── Decider filter (only meaningful on "decided" view) ───────────────────────────────────
// Narrows the decision history by who recorded the decision. Distinct from the pending
// AssigneeFilter (which narrows by work-item participant/role) — a decider has no role and
// "unassigned" is not a valid choice, so this is its own lightweight picker.

export type { DeciderFilterValue };

const DECIDER_FILTER_STORAGE_KEY = 'me.queue.deciderFilter';

const DECIDER_ANYONE = '__all__';
const DECIDER_ME = '__me__';

function loadDeciderFilter(): DeciderFilterValue {
  try {
    const raw = window.localStorage.getItem(DECIDER_FILTER_STORAGE_KEY);
    if (!raw) return { mode: 'all' };
    const parsed = JSON.parse(raw);
    if (parsed?.mode === 'me') return { mode: 'me' };
    if (
      parsed?.mode === 'person' &&
      typeof parsed.email === 'string' &&
      typeof parsed.displayName === 'string'
    ) {
      return { mode: 'person', email: parsed.email, displayName: parsed.displayName };
    }
  } catch {
    // Ignore — corrupted entry; fall through to default.
  }
  return { mode: 'all' };
}

function saveDeciderFilter(value: DeciderFilterValue): void {
  try {
    window.localStorage.setItem(DECIDER_FILTER_STORAGE_KEY, JSON.stringify(value));
  } catch {
    // Ignore — quota or disabled storage.
  }
}

/** Resolves the decider filter to a single email for the API, or undefined for "Anyone". */
function deciderToEmail(value: DeciderFilterValue, currentUserEmail: string): string | undefined {
  if (value.mode === 'me') return currentUserEmail || undefined;
  if (value.mode === 'person') return value.email;
  return undefined;
}

function DeciderFilter({
  value,
  onChange,
  deciders,
  currentUserEmail,
}: {
  value: DeciderFilterValue;
  onChange: (next: DeciderFilterValue) => void;
  /** Decider rollup (email, displayName, count) from the decided endpoint, pre-narrowing. */
  deciders: PendingAssignee[];
  currentUserEmail: string;
}) {
  // Dedupe by email — the decided endpoint already returns one row per decider, but guard
  // anyway. Exclude the current user from the named list; "Me" covers them.
  const people = useMemo(() => {
    const seen = new Set<string>();
    const out: Array<{ email: string; displayName: string }> = [];
    for (const d of deciders) {
      if (!d.email || seen.has(d.email.toLowerCase())) continue;
      if (d.email.toLowerCase() === currentUserEmail.toLowerCase()) continue;
      seen.add(d.email.toLowerCase());
      out.push({ email: d.email, displayName: d.displayName });
    }
    return out;
  }, [deciders, currentUserEmail]);

  // If the persisted person is no longer in the list, render "Anyone" rather than a stale pick.
  const selectValue = useMemo(() => {
    if (value.mode === 'me') return DECIDER_ME;
    if (value.mode === 'person') {
      const stillVisible = people.some(
        (p) => p.email.toLowerCase() === value.email.toLowerCase(),
      );
      return stillVisible ? `email:${value.email}` : DECIDER_ANYONE;
    }
    return DECIDER_ANYONE;
  }, [value, people]);

  const handleChange = (next: string) => {
    if (next === DECIDER_ANYONE) return onChange({ mode: 'all' });
    if (next === DECIDER_ME) return onChange({ mode: 'me' });
    if (next.startsWith('email:')) {
      const email = next.slice('email:'.length);
      const person = people.find((p) => p.email === email);
      if (person) onChange({ mode: 'person', email: person.email, displayName: person.displayName });
    }
  };

  return (
    <label
      className={filterLabelClass}
      style={{ color: 'var(--text-muted)' }}
    >
      <span>Decided by</span>
      <select
        value={selectValue}
        onChange={(e) => handleChange(e.target.value)}
        className={filterSelectClass}
        style={{
          borderColor: 'var(--border-color)',
          backgroundColor: 'var(--bg-primary)',
          color: 'var(--text-primary)',
        }}
      >
        <option value={DECIDER_ANYONE}>Anyone</option>
        <option value={DECIDER_ME}>Me</option>
        {people.length > 0 && (
          <optgroup label="Deciders">
            {people.map((p) => (
              <option key={p.email} value={`email:${p.email}`}>
                {p.displayName}
              </option>
            ))}
          </optgroup>
        )}
      </select>
    </label>
  );
}

/**
 * The person filter as a document-title segment, or null when it isn't narrowing anything. Named
 * people use the display name rather than the email — a title is read, not clicked, and a link that
 * arrived carrying only an email will show that until the queue's rollup supplies the name.
 */
function assigneeTitle(filter: AssigneeFilterValue): string | null {
  switch (filter.mode) {
    case 'all':
      return null;
    case 'me':
      return 'assigned to me';
    case 'unassigned':
      return 'unassigned';
    case 'person':
      return filter.displayName ? `assigned to ${filter.displayName}` : null;
  }
}

/** The decider filter as a document-title segment, or null for "Anyone". */
function deciderTitle(decider: DeciderFilterValue): string | null {
  switch (decider.mode) {
    case 'me':
      return 'decided by me';
    case 'person':
      return `decided by ${decider.displayName}`;
    default:
      return null;
  }
}

/** The person filter as the dropdown shows it, for the empty state's chip. */
function assigneeLabel(filter: AssigneeFilterValue): string {
  switch (filter.mode) {
    case 'all':
      return 'Anyone';
    case 'me':
      return 'Me';
    case 'unassigned':
      return 'Missing a required role';
    case 'person':
      return filter.displayName || filter.email || 'a person';
  }
}

// ── Empty state ──────────────────────────────────────────────────────────────────────────

/**
 * The unfiltered empty state per tab — what this tab would contain, and where to look instead.
 *
 * Tone is the editorial call the colour makes: an empty attention tab is the queue being clear,
 * which deserves to read as good news rather than as a shrug, while an empty history is neither.
 */
const QUEUE_EMPTY_STATES: Record<
  QueueView,
  { icon: typeof Inbox; tone: EmptyStateTone; title: string; body: string }
> = {
  mine: {
    icon: CheckCircle,
    tone: 'good',
    title: 'Nothing is waiting on your sign-off',
    body: 'This tab holds work items where you hold a role their promotion policy requires. "Pending" shows everything you are authorised to sign off, whoever it is assigned to.',
  },
  'not-assigned': {
    icon: UserPlus,
    tone: 'good',
    title: 'Every work item has the people its policy requires',
    body: 'A work item lands here when its promotion policy asks for a role — a QA owner, a reviewer — that nobody has been put in, so nobody can sign it off.',
  },
  pending: {
    icon: Inbox,
    tone: 'good',
    title: 'No work items are waiting for sign-off',
    body: 'Everything you are authorised to sign off is done. New items appear here as promotions roll through your environments.',
  },
  decided: {
    icon: History,
    tone: 'neutral',
    title: 'No decisions recorded yet',
    body: 'Sign-offs made by you or by anyone else in your approver group are kept here.',
  },
};

/**
 * What the queue shows instead of rows.
 *
 * The old copy said "No work items match the current filters" and left the reader to go and find
 * which of six controls — three of them behind a collapsed panel on a phone — was responsible. So
 * the filters are named here, each one clearable in place, and the tab is named too: on this page a
 * tab is itself a narrowing, and it is as likely to be the reason as any dropdown.
 */
function QueueEmptyState({
  view,
  filters,
  onClearFilters,
  hiddenByScope,
}: {
  view: QueueView;
  filters: ActiveFilterChip[];
  onClearFilters: () => void;
  /**
   * Rows this tab loaded that the client-side scope narrowing then hid. Zero means nothing was
   * loaded to hide — the list was emptied server-side, or by there being nothing there at all.
   */
  hiddenByScope: number;
}) {
  if (filters.length > 0) {
    const one = filters.length === 1;
    return (
      <ListEmptyState
        icon={Filter}
        tone="filtered"
        title={`No work items match ${one ? 'this filter' : 'these filters'}`}
        body={
          hiddenByScope > 0
            ? `${hiddenByScope} work item${hiddenByScope === 1 ? '' : 's'} on the "${
                VIEW_LABELS[view]
              }" tab ${hiddenByScope === 1 ? 'is' : 'are'} hidden by the narrowing below. Drop a filter to bring ${
                hiddenByScope === 1 ? 'it' : 'them'
              } back.`
            : `Nothing on the "${VIEW_LABELS[view]}" tab survives ${
                one ? 'this narrowing' : `all ${filters.length} narrowings`
              }. Clear one to widen the queue, or try another tab.`
        }
        filters={filters}
        onClearFilters={onClearFilters}
      />
    );
  }

  const { icon, tone, title, body } = QUEUE_EMPTY_STATES[view];
  return <ListEmptyState icon={icon} tone={tone} title={title} body={body} />;
}

/**
 * One queue row. Navigational, not transactional: sign-off (Approve / Issue / Block) and the
 * discussion thread live on the work-item detail page, so the row's job is to surface state and get
 * you there. Assigning people stays inline — it's the one edit that's useful while triaging a list.
 *
 * The whole tile is the click target for "open details", matching the promotions list. Regions with
 * their own behaviour (the tracker link, the participant controls) swallow the click rather than
 * every leaf control having to know about the tile.
 */
function TicketRow({
  index,
  ticket,
  onChanged,
}: {
  /** Position in the list, for {@link useKeyboardListRow}'s roving tabindex. */
  index: number;
  ticket: PendingTicket;
  onChanged: () => void;
}) {
  const navigate = useNavigate();
  // Decided rows are read-only history: decision badge + decider + comment, and no participant
  // editing. The candidate may also have moved on (Approved/Deployed/Rejected/Superseded) so we
  // surface its current status as a hint.
  const decided = ticket.decision != null;
  // Undecided row whose promotion is gone: superseded without the replacement picking the ticket up,
  // or rejected. The item is still ours to resolve, so it stays in the queue — flagged, because the
  // service/version shown belongs to a promotion that is no longer going anywhere.
  const orphaned =
    !decided && !!ticket.candidateStatus && ticket.candidateStatus !== 'Pending';
  const detailPath = workItemDetailPath(ticket.workItemKey, ticket.product, ticket.targetEnv);

  const rowProps = useKeyboardListRow(index, () => navigate(detailPath), {
    label: `${ticket.workItemKey} — ${ticket.product} / ${ticket.service}, ${
      decided ? `decided: ${ticket.decision}` : 'awaiting decision'
    }. Open work item.`,
  });

  return (
    <div
      {...rowProps}
      className="card-hover rounded-xl border p-4 cursor-pointer"
      style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-primary)' }}
    >
      <div className="flex items-start gap-3">
        <Ticket size={16} style={{ color: 'var(--text-muted)', marginTop: 2, flexShrink: 0 }} />
        <div className="flex-1 min-w-0">
          {/* Title row. The key goes to the in-app detail page; the tracker link is a bare icon. */}
          <div className="flex items-baseline gap-2 min-w-0">
            <Link
              to={detailPath}
              className="text-[13px] font-semibold hover:underline shrink-0"
              style={{ color: 'var(--accent)' }}
              title={`Open ${ticket.workItemKey} details`}
            >
              {ticket.workItemKey}
            </Link>
            {ticket.url && (
              <a
                href={ticket.url}
                target="_blank"
                rel="noopener noreferrer"
                onClick={(e) => e.stopPropagation()}
                className="shrink-0 transition-opacity hover:opacity-70"
                style={{ color: 'var(--text-muted)' }}
                {...{ [ROW_ACTION_ATTR]: 'open-external' }}
                title={`Open ${ticket.workItemKey} in ${ticket.provider ?? 'the tracker'}`}
                aria-label={`Open ${ticket.workItemKey} in ${ticket.provider ?? 'the tracker'}`}
              >
                <ExternalLink size={11} />
              </a>
            )}
            {ticket.title && (
              <span
                className="text-[12px] truncate"
                style={{ color: 'var(--text-secondary)' }}
                title={ticket.title}
              >
                {ticket.title}
              </span>
            )}
            {ticket.blockingPromotions > 1 && (
              <span
                className="badge shrink-0"
                style={{ backgroundColor: 'var(--warning-bg)', color: 'var(--warning)' }}
                title={`Referenced by ${ticket.blockingPromotions} pending promotion candidates`}
              >
                ×{ticket.blockingPromotions}
              </span>
            )}
            {orphaned && (
              <span
                className="badge shrink-0"
                style={{ backgroundColor: 'var(--bg-secondary)', color: 'var(--text-muted)' }}
                title={`The promotion carrying this work item is ${ticket.candidateStatus}. The item still needs signing off.`}
              >
                <Unlink size={10} />
                No live promotion
              </span>
            )}
            {/* Nobody in a role the promotion policy requires — shown on every tab, not just "Not
                assigned": an item can be mine as QA owner and still be missing its reviewer. */}
            <MissingRolesBadge roles={ticket.missingRoles} />
          </div>

          {/* Context */}
          <div
            className="flex items-center gap-2 flex-wrap mt-1.5 text-[12px]"
            style={{ color: 'var(--text-secondary)' }}
          >
            <span>
              <span style={{ color: 'var(--text-muted)' }}>Product:</span>{' '}
              <span className="font-medium">{ticket.product}</span>
            </span>
            <span style={{ color: 'var(--text-muted)' }}>·</span>
            <WorkItemEnvironments environments={ticket.environments ?? []} />
            <span style={{ color: 'var(--text-muted)' }}>·</span>
            <span>
              <span style={{ color: 'var(--text-muted)' }}>Service:</span>{' '}
              <span className="font-medium">{ticket.service}</span>
            </span>
            <span style={{ color: 'var(--text-muted)' }}>·</span>
            <span>
              <span style={{ color: 'var(--text-muted)' }}>Version:</span>{' '}
              <span className="font-mono">{ticket.version}</span>
            </span>
          </div>

          {/* Participants — each chip carries its own role icon, and the dashed "Assign" button is
              the empty state, so no separate header is needed here. The wrapper keeps assignment
              clicks from being read as "open the details page". */}
          <div onClick={(e) => e.stopPropagation()}>
            <WorkItemParticipants
              candidateId={ticket.candidateId}
              referenceKey={ticket.workItemKey}
              participants={ticket.participants ?? []}
              onChanged={onChanged}
              /* Orphan rows are read-only for assignment: their candidate is the dead one, which
                 isn't necessarily the candidate the detail page writes people to, so an edit here
                 could land somewhere the user never sees it. The detail page is the place. */
              readOnly={decided || orphaned}
            />
          </div>

          {decided && (
            <DecisionBanner
              decision={ticket.decision!}
              decidedAt={ticket.decidedAt ?? null}
              decidedByName={ticket.decidedByName ?? null}
              decidedByEmail={ticket.decidedByEmail ?? null}
              comment={ticket.decisionComment ?? null}
              candidateStatus={ticket.candidateStatus ?? null}
            />
          )}
        </div>
        <Link
          to={detailPath}
          className="shrink-0 self-center inline-flex items-center gap-1 text-[12px] font-medium transition-opacity hover:opacity-80"
          style={{ color: 'var(--accent)' }}
        >
          Details
          <ArrowRight size={14} />
        </Link>
      </div>
    </div>
  );
}

/**
 * Read-only banner shown on rows that already have a decision (any approver). Surfaces the
 * decision (with colour cue), who made it, when, the comment if any, and the candidate's
 * current status (which may have moved past Approved while the user wasn't looking).
 */
function DecisionBanner({
  decision,
  decidedAt,
  decidedByName,
  decidedByEmail,
  comment,
  candidateStatus,
}: {
  decision: WorkItemDecision;
  decidedAt: string | null;
  decidedByName: string | null;
  decidedByEmail: string | null;
  comment: string | null;
  candidateStatus: string | null;
}) {
  const s = decisionStyle(decision);
  const decider = decidedByName ?? decidedByEmail ?? 'someone';
  const Icon = decision === 'Approved' ? CheckCircle : decision === 'Issue' ? AlertTriangle : Ban;
  return (
    <div
      className="mt-2.5 rounded-lg border px-3 py-2 text-[12px] space-y-1"
      style={{ borderColor: s.color, backgroundColor: s.bg, color: s.color }}
    >
      <div className="inline-flex items-center gap-2 font-medium flex-wrap">
        <Icon size={12} />
        <span>
          {s.attributed} by <span title={decidedByEmail ?? undefined}>{decider}</span>
        </span>
        {decidedAt && (
          <span className="font-normal" style={{ color: 'var(--text-muted)' }}>
            · {formatDistanceToNow(new Date(decidedAt), { addSuffix: true })}
          </span>
        )}
        {candidateStatus && candidateStatus !== 'Pending' && candidateStatus !== 'Unknown' && (
          <span className="font-normal" style={{ color: 'var(--text-muted)' }}>
            · candidate is now <span className="font-medium">{candidateStatus}</span>
          </span>
        )}
      </div>
      {comment && <p style={{ color: 'var(--text-secondary)' }}>“{comment}”</p>}
    </div>
  );
}
