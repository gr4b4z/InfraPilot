import { useCallback, useEffect, useState, useMemo } from 'react';
import { Link, useNavigate, useSearchParams } from 'react-router-dom';
import { api } from '@/lib/api';
import type { PromotionCandidate, PromotionStatus, WorkItemDecision } from '@/lib/api';
import { resolveReferenceHref } from '@/lib/refUrl';
import { decisionStyle, missingRolesLabel, workItemDetailPath } from '@/lib/workItem';
import { WorkItemsNeedingAttentionBadge } from '@/components/promotions/MissingRoles';
import {
  readEnumPref,
  readPref,
  writePref,
  PROMOTIONS_VIEW_PREF,
  PROMOTIONS_PRODUCT_FILTER_PREF,
  PROMOTIONS_SERVICE_FILTER_PREF,
  PROMOTIONS_TARGET_ENV_FILTER_PREF,
  PROMOTIONS_REFERENCE_FILTER_PREF,
} from '@/lib/prefs';
import { PromotionRoute } from '@/components/promotions/PromotionRoute';
import { useEnvControlStyle } from '@/components/environments/useEnvColor';
import { FilterPanel } from '@/components/ui/FilterPanel';
import {
  ListEmptyState,
  type ActiveFilterChip,
  type EmptyStateTone,
} from '@/components/ui/ListEmptyState';
import { CopyViewLinkButton } from '@/components/ui/CopyViewLinkButton';
import { KeyboardList } from '@/components/ui/KeyboardList';
import { RovingGroup } from '@/components/ui/RovingGroup';
import {
  buildPromotionParams,
  hasPromotionParams,
  parsePromotionParams,
  PROMOTION_VIEWS,
  type PromotionParams,
  type PromotionView,
} from './promotionFilterParams';
import { promotionSearchScope } from '@/components/shell/searchScopes';
import { useDocumentTitle, scopeTitle } from '@/lib/pageTitle';
import { useSearchScope } from '@/stores/searchScopeStore';
import { useKeyboardListRow } from '@/hooks/keyboardList';
import { useSettingsStore } from '@/stores/settingsStore';
import { refreshMyTasks } from '@/stores/myTasksStore';
import { useEntityRefresh } from '@/hooks/useEntityEvents';
import { formatDistanceToNow } from 'date-fns';
import {
  AlertTriangle,
  ArrowRight,
  Clock,
  CheckCircle,
  History,
  XCircle,
  Rocket,
  GitPullRequest,
  Ticket,
  ExternalLink,
  Filter,
} from 'lucide-react';

/**
 * Per-candidate work-item signoff state for the list. The list API returns the candidate's own
 * sourceEventReferences (work-items + others) but not their approval state, so we filter to
 * work-items and call /work-items/{key}?... for each.
 *
 * `decisions` carries the per-key outcome so each work-item chip can be tinted by its own state —
 * the counts alone can't tell you *which* item is the blocked one, which is the thing you want to
 * see when scanning a stalled bundle.
 *
 * Fetching is capped at {@link PROGRESS_CANDIDATE_LIMIT} candidates per render: the resolved and
 * "all" tabs are unbounded in a way the pending queue never was, and a chip with no tint is a fine
 * degradation. Rows past the cap render as undecided.
 */
interface WorkItemProgress {
  total: number;
  approved: number;
  /** Carrying an Issue — a flagged problem. Stalls the gate without vetoing the promotion. */
  issues: number;
  /** Held back by a Block. Same effect as an issue, stronger statement. */
  blocked: number;
  /** Work-item key → the decision on it, or null when nobody has decided yet. */
  decisions: Record<string, WorkItemDecision | null>;
  loading: boolean;
}

const PROGRESS_CANDIDATE_LIMIT = 25;

const STATUS_CONFIG: Record<
  PromotionStatus,
  { icon: typeof Clock; color: string; bg: string }
> = {
  Pending: { icon: Clock, color: 'var(--warning)', bg: 'var(--warning-bg)' },
  Approved: { icon: CheckCircle, color: 'var(--info)', bg: 'var(--info-bg)' },
  Deploying: { icon: Rocket, color: 'var(--accent)', bg: 'var(--accent-bg)' },
  Deployed: { icon: CheckCircle, color: 'var(--success)', bg: 'var(--success-bg)' },
  Superseded: { icon: Clock, color: 'var(--text-muted)', bg: 'var(--bg-secondary)' },
  Rejected: { icon: XCircle, color: 'var(--danger)', bg: 'var(--danger-bg)' },
};

/**
 * Which slice of the promotions set the page is showing. `mine` and `needs-attention` narrow `pending`
 * client-side (no refetch); `resolved` and `all` share one lazy fetch; `rejected` has its own so a long
 * resolved tail can't clip it.
 *
 * <p>The two narrowings answer different questions and can both be non-empty for the same promotion:
 * `mine` is "can I approve this now", `needs-attention` is "is somebody missing from its work items".</p>
 *
 * <p>The type and the list itself live in {@link promotionFilterParams} — they're part of the URL
 * contract, and the parser has to validate a link's tab against the same list this renders.</p>
 */
export type { PromotionView };

const VIEW_HEADINGS: Record<PromotionView, string> = {
  pending: 'All pending',
  mine: 'Awaiting my approval',
  'needs-attention': 'Needs attention',
  'awaiting-deploy': 'Approved · awaiting deploy',
  resolved: 'Resolved',
  rejected: 'Rejected',
  all: 'All promotions',
};

/**
 * What each tab shows, as a noun phrase — the subject of the "no … match these filters" sentence.
 * Deliberately spells out the narrowing the tab itself applies: on a filtered empty list the tab is
 * as likely to be the reason as any dropdown, and it isn't one of the chips the panel can clear.
 */
const VIEW_SUBJECTS: Record<PromotionView, string> = {
  pending: 'pending promotions',
  mine: 'promotions you can approve',
  'needs-attention': 'promotions with a work item missing someone',
  'awaiting-deploy': 'approved promotions waiting to deploy',
  resolved: 'resolved promotions',
  rejected: 'rejected promotions',
  all: 'promotions',
};

/**
 * The unfiltered empty state per tab. Tone is the editorial call: an empty "Awaiting my approval" or
 * "Needs attention" is the queue being clear, which reads better in green than as a shrug; history
 * tabs with nothing in them are neither good nor bad.
 */
const EMPTY_STATES: Record<
  PromotionView,
  { icon: typeof Clock; tone: EmptyStateTone; title: string; body: string }
> = {
  pending: {
    icon: CheckCircle,
    tone: 'good',
    title: 'No promotions are waiting for approval',
    body: 'When a new version is ready to move up an environment, it lands here for review.',
  },
  mine: {
    icon: CheckCircle,
    tone: 'good',
    title: 'Nothing is waiting on you',
    body: 'This tab holds the promotions you are authorised to approve. Others may still be pending for someone else — "All pending" shows those.',
  },
  'needs-attention': {
    icon: CheckCircle,
    tone: 'good',
    title: 'Every pending promotion has the people it needs',
    body: 'A promotion shows up here when one of its work items has nobody in a role its policy requires, so nobody can sign it off.',
  },
  'awaiting-deploy': {
    icon: Rocket,
    tone: 'neutral',
    title: 'Nothing approved is waiting to deploy',
    body: 'Approved promotions sit here until the deployment that carries them runs.',
  },
  resolved: {
    icon: Clock,
    tone: 'neutral',
    title: 'Nothing has been resolved yet',
    body: 'Promotions that were approved, deployed, rejected or superseded are kept here as history.',
  },
  rejected: {
    icon: XCircle,
    tone: 'neutral',
    title: 'No promotion has been rejected',
    body: 'A promotion someone turns down is kept here along with the reason they gave.',
  },
  all: {
    icon: GitPullRequest,
    tone: 'neutral',
    title: 'No promotions recorded yet',
    body: 'Every promotion, at any status, appears here once one is created for a product you can see.',
  },
};

function EmptyState({
  view,
  filters,
  onClearFilters,
}: {
  view: PromotionView;
  /** The dropdowns and boxes currently narrowing the fetch, as clearable chips. */
  filters: ActiveFilterChip[];
  onClearFilters: () => void;
}) {
  // Filters win over the per-tab copy. "No rejected promotions" is a claim about the whole system,
  // and printing it while a product filter is quietly narrowing the fetch is how an empty list gets
  // mistaken for a fact.
  if (filters.length > 0) {
    const one = filters.length === 1;
    return (
      <ListEmptyState
        icon={Filter}
        tone="filtered"
        title={`No ${VIEW_SUBJECTS[view]} match ${one ? 'this filter' : 'these filters'}`}
        body={`The "${VIEW_HEADINGS[view]}" tab has nothing left once ${
          one ? 'this narrowing is' : `all ${filters.length} narrowings are`
        } applied. Drop one to widen the list, or try another tab — a promotion you are looking for may have moved on already.`}
        filters={filters}
        onClearFilters={onClearFilters}
      />
    );
  }

  const { icon, tone, title, body } = EMPTY_STATES[view];
  return <ListEmptyState icon={icon} tone={tone} title={title} body={body} />;
}

/**
 * A filter value that survives leaving the page. Writes the cookie on every change — the same shape
 * as `useState`, so call sites are unchanged.
 *
 * The initial value comes from the caller rather than the cookie: on arrival a link's filters take
 * precedence over the saved ones, and only the page can tell whether the URL is describing a view
 * (see {@link parsePromotionParams}). The returned setter persists but does not touch the URL — the
 * page's own handlers do that, since a shareable link is built from every filter at once.
 */
function usePersistedFilter(prefKey: string, initial: string): [string, (next: string) => void] {
  const [value, setValue] = useState(initial);

  const update = useCallback(
    (next: string) => {
      setValue(next);
      writePref(prefKey, next);
    },
    [prefKey],
  );

  return [value, update];
}

export function PromotionsPage() {
  const getDisplayName = useSettingsStore((s) => s.getDisplayName);
  const getOrderedEnvironments = useSettingsStore((s) => s.getOrderedEnvironments);
  // The URL is the shareable form of this page's state; the cookies are the resumable one. A link
  // carrying any promotion parameter wins outright on arrival — see promotionFilterParams for why a
  // shared view must not blend with the recipient's saved filters. Read once, on mount: after that
  // the state below owns the view and the change handlers write it back to the URL.
  const [searchParams, setSearchParams] = useSearchParams();
  const initial = useMemo(
    () => {
      const savedView = readEnumPref(PROMOTIONS_VIEW_PREF, PROMOTION_VIEWS, 'pending');
      if (hasPromotionParams(searchParams)) return parsePromotionParams(searchParams, savedView);
      return {
        view: savedView,
        product: readPref(PROMOTIONS_PRODUCT_FILTER_PREF) ?? '',
        service: readPref(PROMOTIONS_SERVICE_FILTER_PREF) ?? '',
        targetEnv: readPref(PROMOTIONS_TARGET_ENV_FILTER_PREF) ?? '',
        reference: readPref(PROMOTIONS_REFERENCE_FILTER_PREF) ?? '',
      };
    },
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [],
  );
  // The page is pending-by-default: `candidates` holds only Pending promotions.
  // Resolved (Approved/Deploying/Deployed/Rejected) promotions are never fetched
  // until the user explicitly opens the resolved section (lazy-loaded below).
  const [candidates, setCandidates] = useState<PromotionCandidate[]>([]);
  const [loading, setLoading] = useState(true);
  // Cookie-persisted, like the tab above: list → detail → back is the main path through this page,
  // and filters that don't survive it aren't worth setting. `usePersistedFilter` writes on change;
  // the initial values come from the link if there is one, otherwise from the cookies (see `initial`).
  const [productFilter, persistProduct] = usePersistedFilter(
    PROMOTIONS_PRODUCT_FILTER_PREF,
    initial.product,
  );
  const [serviceFilter, persistService] = usePersistedFilter(
    PROMOTIONS_SERVICE_FILTER_PREF,
    initial.service,
  );
  const [targetEnvFilter, persistTargetEnv] = usePersistedFilter(
    PROMOTIONS_TARGET_ENV_FILTER_PREF,
    initial.targetEnv,
  );
  const [referenceFilter, persistReference] = usePersistedFilter(
    PROMOTIONS_REFERENCE_FILTER_PREF,
    initial.reference,
  );
  // Filter vocabulary. Fetched once and never re-fetched on a filter change — that is the whole
  // point of it (see api.getPromotionFilterOptions).
  const [filterOptions, setFilterOptions] = useState<{ products: string[]; targetEnvs: string[] }>(
    { products: [], targetEnvs: [] },
  );
  const [selected, setSelected] = useState<Set<string>>(new Set());
  const [bulkLoading, setBulkLoading] = useState(false);
  const [workItemProgress, setWorkItemProgress] = useState<Record<string, WorkItemProgress>>({});
  // Cookie-persisted so the tab you work from is the tab you come back to — unless a link named one.
  const [view, setView] = useState<PromotionView>(initial.view);
  // Approved-awaiting-deploy set. Fetched eagerly (alongside Pending) so the tab
  // badge shows a live count without the user having to open the tab first.
  const [awaitingDeploy, setAwaitingDeploy] = useState<PromotionCandidate[]>([]);
  const [awaitingDeployLoading, setAwaitingDeployLoading] = useState(true);
  // Everything, any status — backs both the "All" and "Resolved" tabs. Lazy: null means
  // "not fetched yet", which is how the effect below knows to go and get it.
  const [archive, setArchive] = useState<PromotionCandidate[] | null>(null);
  const [archiveLoading, setArchiveLoading] = useState(false);
  // Rejected gets its own single-status fetch rather than being filtered out of `archive`: the
  // no-status query caps its non-Pending tail, which would silently clip old rejections.
  const [rejected, setRejected] = useState<PromotionCandidate[] | null>(null);
  const [rejectedLoading, setRejectedLoading] = useState(false);
  const targetEnvSelectStyle = useEnvControlStyle(targetEnvFilter);

  // `/` searches promotions while this page is up. No dependencies — the scope hits the server, so
  // it doesn't close over anything on screen.
  useSearchScope(promotionSearchScope(), []);

  // The tab and the filters are the whole point of the URL parameters above: this page is meant to be
  // handed over as "the checkout-api promotions waiting on prod". The title says the same thing the
  // link does, so it survives being pasted into a chat and read before anyone clicks it.
  useDocumentTitle([
    VIEW_HEADINGS[view],
    scopeTitle({ product: productFilter, service: serviceFilter, targetEnv: targetEnvFilter }),
    referenceFilter && `ref ${referenceFilter}`,
    'Promotions',
  ]);

  /**
   * Mirrors the view into the query string, so from the first filter change onwards the address bar is
   * itself a link to what is on screen. Called from the change handlers rather than from an effect
   * watching the state: a filter change is a user action with a known outcome, and deriving the URL in
   * an effect would cost a render pass whose only job is to fix up the address bar. (Copy link doesn't
   * depend on this having run — it builds the URL from the state directly.)
   *
   * `replace` rather than `push` — changing a filter is not a navigation, and pushing would make Back
   * walk out of the page one dropdown (or one keystroke of the service box) at a time.
   */
  const currentParams = (next: Partial<PromotionParams> = {}): URLSearchParams =>
    buildPromotionParams({
      view,
      product: productFilter,
      service: serviceFilter,
      targetEnv: targetEnvFilter,
      reference: referenceFilter,
      ...next,
    });

  /**
   * The filters this page has set, in the audit page's own parameter names — so "the checkout-api
   * promotions on prod" becomes "what happened to the checkout-api promotions on prod". The tab and
   * window aren't carried: this list's tabs are states (pending, resolved) and the audit page's are
   * kinds of action, so there is nothing to map them onto. It picks its own default window.
   */
  const auditLinkParams = (): URLSearchParams => {
    const params = new URLSearchParams();
    if (productFilter) params.set('product', productFilter);
    if (serviceFilter) params.set('service', serviceFilter);
    if (targetEnvFilter) params.set('targetEnv', targetEnvFilter);
    return params;
  };

  const syncUrl = (next: Partial<PromotionParams>) => {
    const params = currentParams(next);
    if (params.toString() === searchParams.toString()) return;
    setSearchParams(params, { replace: true });
  };

  // Each handler does the same three things: persist (so the view resumes), set state (so the page
  // re-renders), and update the URL (so the view can be handed to someone).
  const changeView = (next: PromotionView) => {
    writePref(PROMOTIONS_VIEW_PREF, next);
    setView(next);
    syncUrl({ view: next });
  };

  const handleProductChange = (next: string) => {
    persistProduct(next);
    syncUrl({ product: next });
  };

  const handleServiceChange = (next: string) => {
    persistService(next);
    syncUrl({ service: next });
  };

  const handleTargetEnvChange = (next: string) => {
    persistTargetEnv(next);
    syncUrl({ targetEnv: next });
  };

  const handleReferenceChange = (next: string) => {
    persistReference(next);
    syncUrl({ reference: next });
  };

  const clearAllFilters = () => {
    persistProduct('');
    persistService('');
    persistTargetEnv('');
    persistReference('');
    // One URL write rather than four: the handlers above each build the parameters from state that
    // hasn't re-rendered yet, so calling them in sequence would leave three of the four behind.
    syncUrl({ product: '', service: '', targetEnv: '', reference: '' });
  };

  /**
   * The narrowings currently applied to the fetch, for the empty state to name and clear. Every
   * filter here is server-side, so an empty list can't be attributed between them — the panel lists
   * all of them and lets the reader drop whichever one it is.
   */
  const activeFilters: ActiveFilterChip[] = [];
  if (productFilter) {
    activeFilters.push({
      label: 'Product',
      value: productFilter,
      onClear: () => handleProductChange(''),
    });
  }
  if (serviceFilter) {
    activeFilters.push({
      label: 'Service',
      value: serviceFilter,
      onClear: () => handleServiceChange(''),
    });
  }
  if (targetEnvFilter) {
    activeFilters.push({
      // The display name, not the raw key — it's what the dropdown shows, and the chip has to be
      // recognisable as the control the reader set.
      label: 'Target env',
      value: getDisplayName(targetEnvFilter),
      onClear: () => handleTargetEnvChange(''),
    });
  }
  if (referenceFilter) {
    activeFilters.push({
      label: 'Reference',
      value: referenceFilter,
      onClear: () => handleReferenceChange(''),
    });
  }

  // Identity of the current filter set — the dependency that invalidates a lazy fetch in flight.
  const filterKey = `${productFilter}|${serviceFilter}|${targetEnvFilter}|${referenceFilter}`;

  // Secondary filters shared by every fetch on this page.
  const filterParams = () => {
    const params: Record<string, string> = {};
    if (productFilter) params.product = productFilter;
    if (serviceFilter) params.service = serviceFilter;
    if (targetEnvFilter) params.targetEnv = targetEnvFilter;
    if (referenceFilter) params.reference = referenceFilter;
    return params;
  };

  const fetchData = () => {
    setLoading(true);
    api
      .listPromotions({ status: 'Pending', ...filterParams() })
      .then((data) => setCandidates(data.candidates || []))
      .catch(() => setCandidates([]))
      .finally(() => setLoading(false));
  };

  // Approved-but-not-yet-deployed. Separate fetch (single-status ⇒ backend allows up
  // to 200) so the count is available for the tab badge before the tab is opened.
  const fetchAwaitingDeploy = () => {
    setAwaitingDeployLoading(true);
    api
      .listPromotions({ status: 'Approved', ...filterParams() })
      .then((data) => setAwaitingDeploy(data.candidates || []))
      .catch(() => setAwaitingDeploy([]))
      .finally(() => setAwaitingDeployLoading(false));
  };

  // Filter vocabulary — fetched once on mount, deliberately outside the filter-change effect below.
  // Refetching it with the filters applied is exactly the bug this replaced.
  useEffect(() => {
    let cancelled = false;
    api
      .getPromotionFilterOptions()
      .then((o) => {
        if (!cancelled) setFilterOptions(o);
      })
      .catch(() => {
        // Leave the dropdowns with only their current selection; the rest of the page is unaffected.
      });
    return () => {
      cancelled = true;
    };
  }, []);

  // Server-pushed promotion changes rerun the same invalidation a filter change does; work-item
  // sign-offs refresh the per-row progress cells without refetching the candidate lists.
  const promotionsTick = useEntityRefresh(['promotion']);
  const workItemsTick = useEntityRefresh(['work-item']);

  useEffect(() => {
    fetchData();
    fetchAwaitingDeploy();
    // A filter change invalidates the lazy sets — drop them back to "not fetched" so the
    // effect below refetches with the new filters when their tab is next shown.
    setArchive(null);
    setRejected(null);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [productFilter, serviceFilter, targetEnvFilter, referenceFilter, promotionsTick]);

  // Lazy loads for the tabs that aren't fetched up front. Fires on tab change and after a filter
  // change has reset the sets to null. `filterKey` is a dependency so a filter change tears down an
  // in-flight request rather than letting its stale result land — and a `null` set is the only
  // trigger, so a settled fetch (success or failure) can't loop.
  useEffect(() => {
    let cancelled = false;
    if ((view === 'all' || view === 'resolved') && archive === null) {
      setArchiveLoading(true);
      api
        .listPromotions(filterParams())
        .then((data) => { if (!cancelled) setArchive(data.candidates || []); })
        .catch(() => { if (!cancelled) setArchive([]); })
        .finally(() => { if (!cancelled) setArchiveLoading(false); });
    }
    if (view === 'rejected' && rejected === null) {
      setRejectedLoading(true);
      api
        .listPromotions({ status: 'Rejected', ...filterParams() })
        .then((data) => { if (!cancelled) setRejected(data.candidates || []); })
        .catch(() => { if (!cancelled) setRejected([]); })
        .finally(() => { if (!cancelled) setRejectedLoading(false); });
    }
    return () => { cancelled = true; };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [view, archive, rejected, filterKey]);

  const pending = useMemo(() => candidates.filter((c) => c.status === 'Pending'), [candidates]);
  const resolved = useMemo(
    () => (archive ?? []).filter((c) => c.status !== 'Pending'),
    [archive],
  );

  const approvablePending = useMemo(() => pending.filter((c) => c.canApprove), [pending]);

  // Pending promotions carrying a work item with nobody in a role its policy requires. Filtered from
  // the already-fetched pending set rather than queried, like `mine`: the gaps ride along on every
  // candidate in the list response, so the tab and its badge cost nothing and stay live as people are
  // assigned. Scoped to Pending because that's where an assignment still changes the outcome.
  const needsAttention = useMemo(
    () => pending.filter((c) => (c.workItemRoleGaps ?? []).length > 0),
    [pending],
  );

  // The rows the active tab shows, and whether we're still waiting on them.
  const displayed = useMemo((): PromotionCandidate[] => {
    switch (view) {
      case 'mine':
        return approvablePending;
      case 'needs-attention':
        return needsAttention;
      case 'awaiting-deploy':
        return awaitingDeploy;
      case 'resolved':
        return resolved;
      case 'rejected':
        return rejected ?? [];
      case 'all':
        return archive ?? [];
      default:
        return pending;
    }
  }, [view, pending, approvablePending, needsAttention, awaitingDeploy, resolved, rejected, archive]);

  const displayedLoading =
    view === 'awaiting-deploy'
      ? awaitingDeployLoading
      : view === 'resolved' || view === 'all'
        ? archiveLoading || archive === null
        : view === 'rejected'
          ? rejectedLoading || rejected === null
          : loading;

  // Work-item signoff state for the rows on screen. One request per work item per candidate, fanned
  // out concurrently within a candidate and sequentially across them so a wide tab doesn't open
  // hundreds of sockets at once. A cancellation guard avoids overwriting state when the list churns
  // mid-flight (a filter or tab change).
  const progressTargets = useMemo(
    () => displayed.slice(0, PROGRESS_CANDIDATE_LIMIT),
    [displayed],
  );

  useEffect(() => {
    let cancelled = false;
    (async () => {
      for (const c of progressTargets) {
        // An edge that doesn't create work items has no sign-off state to fetch — its references are
        // change-set history. Skipping it saves a request per reference that could only 404.
        if (c.tracksWorkItems === false) continue;
        const tickets = (c.sourceEventReferences ?? []).filter(
          (r) => r.type === 'work-item' && (r.key ?? '').trim().length > 0,
        );
        if (tickets.length === 0) {
          setWorkItemProgress((prev) => ({
            ...prev,
            [c.id]: { total: 0, approved: 0, issues: 0, blocked: 0, decisions: {}, loading: false },
          }));
          continue;
        }
        // Mark the row as loading once per candidate so the cell can show a hint.
        setWorkItemProgress((prev) => ({
          ...prev,
          [c.id]: prev[c.id] ?? {
            total: tickets.length,
            approved: 0,
            issues: 0,
            blocked: 0,
            decisions: {},
            loading: true,
          },
        }));
        try {
          const ctxs = await Promise.all(
            tickets.map((t) =>
              api
                .getWorkItemContext(t.key ?? '', c.product, c.targetEnv)
                .then((ctx) => ({ key: t.key ?? '', ctx }))
                .catch(() => ({ key: t.key ?? '', ctx: null })),
            ),
          );
          if (cancelled) return;
          let approved = 0;
          let issues = 0;
          let blocked = 0;
          const decisions: Record<string, WorkItemDecision | null> = {};
          for (const { key, ctx } of ctxs) {
            const decision = ctx?.approvals?.[0]?.decision ?? null;
            decisions[key] = decision;
            if (decision === 'Approved') approved++;
            else if (decision === 'Issue') issues++;
            else if (decision === 'Blocked') blocked++;
          }
          setWorkItemProgress((prev) => ({
            ...prev,
            [c.id]: {
              total: tickets.length,
              approved,
              issues,
              blocked,
              decisions,
              loading: false,
            },
          }));
        } catch {
          if (cancelled) return;
          setWorkItemProgress((prev) => ({
            ...prev,
            [c.id]: {
              total: tickets.length,
              approved: 0,
              issues: 0,
              blocked: 0,
              decisions: {},
              loading: false,
            },
          }));
        }
      }
    })();
    return () => {
      cancelled = true;
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [progressTargets.map((c) => c.id).join(','), workItemsTick]);

  // Known target envs from the currently-loaded candidate set, for the dropdown. Keeping the
  // current filter selection in the list even if nothing matches so the user can clear it.
  // Configured order, not alphabetical: that's the deployment order (dev → staging → prod), the
  // sequence a reader is already thinking in when picking a target environment.
  const targetEnvOptions = useMemo(() => {
    const set = new Set(filterOptions.targetEnvs);
    // Keep the current selection listed even if nothing carries it any more, or there is no way
    // to clear a filter that has outlived its promotions.
    if (targetEnvFilter) set.add(targetEnvFilter);
    return getOrderedEnvironments(Array.from(set));
  }, [filterOptions.targetEnvs, targetEnvFilter, getOrderedEnvironments]);

  const productOptions = useMemo(() => {
    const set = new Set(filterOptions.products);
    if (productFilter) set.add(productFilter);
    return Array.from(set).sort();
  }, [filterOptions.products, productFilter]);

  const allApprovableSelected =
    approvablePending.length > 0 && approvablePending.every((c) => selected.has(c.id));

  const toggleSelect = (id: string) => {
    setSelected((prev) => {
      const next = new Set(prev);
      if (next.has(id)) next.delete(id);
      else next.add(id);
      return next;
    });
  };

  const toggleSelectAll = () => {
    if (allApprovableSelected) {
      setSelected(new Set());
    } else {
      setSelected(new Set(approvablePending.map((c) => c.id)));
    }
  };

  const handleBulkApprove = async () => {
    if (selected.size === 0) return;
    setBulkLoading(true);
    try {
      await api.bulkApprovePromotions(Array.from(selected));
      setSelected(new Set());
    } finally {
      // Refresh either way: on failure the server may still have approved some of the batch, so
      // re-reading is the only way to know what actually happened. Approved rows leave Pending and
      // land in the awaiting-deploy set, and the lazy tabs are now stale — drop them so they
      // refetch when next opened rather than showing a pre-approval snapshot.
      fetchData();
      fetchAwaitingDeploy();
      setArchive(null);
      setRejected(null);
      setBulkLoading(false);
      // The sidebar counter and the bell badge counted these as awaiting the user.
      refreshMyTasks();
    }
  };

  return (
    <div className="space-y-6">
      {/* Header */}
      <div className="flex items-start justify-between gap-4">
        <div>
          <h1 className="text-xl font-semibold tracking-tight" style={{ color: 'var(--text-primary)' }}>
            Promotions
          </h1>
          <p className="text-[13px] mt-1" style={{ color: 'var(--text-muted)' }}>
            Review and approve version promotions across environments
          </p>
        </div>
        <div className="flex shrink-0 items-center gap-2">
          {/* The audit page answers the questions this list can't: what already happened, and who did
              it. Linked from here because that is where somebody stands when they think to ask — and
              it carries the current product/service/env narrowing across, so the question stays
              scoped to whatever they were already looking at. */}
          <Link
            to={`/promotions/audit?${auditLinkParams().toString()}`}
            className="inline-flex items-center gap-1.5 rounded-lg border px-3 py-1.5 text-[12px] font-medium transition-opacity hover:opacity-80"
            style={{
              borderColor: 'var(--border-color)',
              backgroundColor: 'var(--bg-primary)',
              color: 'var(--text-secondary)',
            }}
            title="Recent actions taken on promotions — approvals, rejections, sign-offs and deploys"
          >
            <History size={12} />
            Audit
          </Link>
          {/* The point of putting the filters in the URL was so this view could be handed to someone,
              and nobody thinks to look in the address bar for that. Built from the state rather than
              read back off `location`, so it's exact even before the first filter change has written
              the parameters there. */}
          <CopyViewLinkButton params={currentParams()} />
        </div>
      </div>

      {/* Secondary filters */}
      <FilterPanel
        activeCount={[productFilter, serviceFilter, targetEnvFilter, referenceFilter].filter(Boolean).length}
      >
        <select
          value={productFilter}
          onChange={(e) => handleProductChange(e.target.value)}
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
          value={serviceFilter}
          onChange={(e) => handleServiceChange(e.target.value)}
          className="rounded-lg border px-3 py-1.5 text-[13px]"
          style={{
            borderColor: 'var(--border-color)',
            backgroundColor: 'var(--bg-primary)',
            color: 'var(--text-primary)',
          }}
        />
        {/* When a target env is selected the control itself takes that environment's colour,
            so an active env filter is visible without reading the dropdown. */}
        <select
          value={targetEnvFilter}
          onChange={(e) => handleTargetEnvChange(e.target.value)}
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
        <input
          type="text"
          placeholder="Reference (PR, work item, commit...)"
          value={referenceFilter}
          onChange={(e) => handleReferenceChange(e.target.value)}
          className="rounded-lg border px-3 py-1.5 text-[13px] sm:min-w-[240px]"
          style={{
            borderColor: 'var(--border-color)',
            backgroundColor: 'var(--bg-primary)',
            color: 'var(--text-primary)',
          }}
        />
      </FilterPanel>

      {/* Tabs over the promotions set. Resolved sits here rather than behind a "show resolved"
          disclosure at the bottom of the page: it's a slice of the same list, and hiding it below
          the fold made looking something up feel like an archaeology exercise. Counts are only
          shown for the eagerly-fetched tabs — a badge on a lazy tab would either lie until you
          opened it or force the fetch the laziness exists to avoid. */}
      {/* Seven pills wrap to several rows on a phone and push the list off-screen, so below `sm` this
          scrolls sideways instead — the usual mobile tab strip. One tab stop, arrows inside. */}
      <RovingGroup
        ariaLabel="Promotion views"
        className="flex items-center gap-2 overflow-x-auto pb-1 sm:flex-wrap sm:overflow-x-visible sm:pb-0"
      >
        {([
          { key: 'pending', label: 'All pending', count: pending.length, showBadge: false },
          { key: 'mine', label: 'Awaiting my approval', count: approvablePending.length, showBadge: true },
          { key: 'needs-attention', label: 'Needs attention', count: needsAttention.length, showBadge: true },
          { key: 'awaiting-deploy', label: 'Approved · awaiting deploy', count: awaitingDeploy.length, showBadge: true },
          { key: 'resolved', label: 'Resolved', count: 0, showBadge: false },
          { key: 'rejected', label: 'Rejected', count: 0, showBadge: false },
          { key: 'all', label: 'All', count: 0, showBadge: false },
        ] as const).map((tab) => {
          const active = view === tab.key;
          return (
            <button
              key={tab.key}
              type="button"
              onClick={() => changeView(tab.key)}
              aria-pressed={active}
              className="flex shrink-0 items-center gap-1.5 whitespace-nowrap rounded-lg border px-3 py-1.5 text-[13px] font-medium transition-colors"
              style={{
                borderColor: active ? 'var(--accent)' : 'var(--border-color)',
                backgroundColor: active ? 'var(--accent-bg)' : 'var(--bg-primary)',
                color: active ? 'var(--accent)' : 'var(--text-secondary)',
              }}
            >
              {tab.label}
              {tab.showBadge && tab.count > 0 && (
                <span
                  className="ml-0.5 px-1.5 rounded-full text-[11px] font-semibold"
                  style={{
                    backgroundColor: active ? 'var(--accent)' : 'var(--warning-bg)',
                    color: active ? '#fff' : 'var(--warning)',
                  }}
                >
                  {tab.count}
                </span>
              )}
            </button>
          );
        })}
      </RovingGroup>

      {displayedLoading ? (
        <div className="space-y-3">
          {[1, 2, 3].map((i) => (
            <div key={i} className="skeleton h-24" />
          ))}
        </div>
      ) : displayed.length === 0 ? (
        <EmptyState view={view} filters={activeFilters} onClearFilters={clearAllFilters} />
      ) : (
        <div>
          <div className="flex items-center justify-between mb-3">
            <div className="flex items-center gap-3">
              {/* Bulk-select is opt-in: only offered in the "Awaiting my approval" view,
                 where every row is something you can act on. The other lists stay
                 action-per-row (Review →) without checkbox clutter. */}
              {view === 'mine' && approvablePending.length > 0 && (
                <input
                  type="checkbox"
                  checked={allApprovableSelected}
                  onChange={toggleSelectAll}
                  className="rounded"
                />
              )}
              <h2
                className="text-[11px] font-semibold uppercase tracking-wider"
                style={{ color: 'var(--text-muted)' }}
              >
                {VIEW_HEADINGS[view]} ({displayed.length})
              </h2>
            </div>
            {view === 'mine' && selected.size > 0 && (
              <button
                onClick={handleBulkApprove}
                disabled={bulkLoading}
                className="flex items-center gap-1.5 px-3 py-1.5 rounded-lg text-[12px] font-medium transition-opacity"
                style={{
                  backgroundColor: 'var(--success-solid)',
                  color: '#fff',
                  opacity: bulkLoading ? 0.6 : 1,
                }}
              >
                <CheckCircle size={12} />
                {bulkLoading ? 'Approving...' : `Approve selected (${selected.size})`}
              </button>
            )}
          </div>
          <KeyboardList className="space-y-2" count={displayed.length} ariaLabel={VIEW_HEADINGS[view]}>
            {displayed.map((c, index) => (
              <CandidateCard
                key={c.id}
                index={index}
                candidate={c}
                /* The urgency tint only means something where every row is genuinely waiting
                   on someone. On a mixed list the status badge carries that instead. */
                urgent={c.status === 'Pending'}
                selectable={view === 'mine' && c.canApprove}
                selected={selected.has(c.id)}
                onToggleSelect={() => toggleSelect(c.id)}
                workItemProgress={workItemProgress[c.id]}
                awaitingCue={view !== 'mine'}
              />
            ))}
          </KeyboardList>
          {displayed.length > PROGRESS_CANDIDATE_LIMIT && (
            <p className="mt-3 text-[11px]" style={{ color: 'var(--text-muted)' }}>
              Work-item sign-off state is loaded for the first {PROGRESS_CANDIDATE_LIMIT} rows;
              beyond that, work-item chips are shown without their decision colour. Narrow the
              filters above to see it for a specific product or service.
            </p>
          )}
        </div>
      )}
    </div>
  );
}

function CandidateCard({
  index,
  candidate,
  urgent,
  selectable,
  selected,
  onToggleSelect,
  workItemProgress,
  awaitingCue,
}: {
  /** Position in the list, for {@link useKeyboardListRow}'s roving tabindex. */
  index: number;
  candidate: PromotionCandidate;
  urgent?: boolean;
  selectable?: boolean;
  selected?: boolean;
  onToggleSelect?: () => void;
  workItemProgress?: WorkItemProgress;
  /** Show the "Awaiting your approval" cue when the user can act (used in the all-pending view). */
  awaitingCue?: boolean;
}) {
  const navigate = useNavigate();
  const cfg = STATUS_CONFIG[candidate.status] ?? STATUS_CONFIG.Pending;
  const StatusIcon = cfg.icon;
  // Inline "+N more" expansion for the work-item chip row — collapsed by default so a large
  // bundle doesn't turn the card into a wall of chips.
  const [showAllTickets, setShowAllTickets] = useState(false);
  const MAX_TICKETS = 5;

  // Work items on this promotion with nobody in a role the policy requires. Keyed for the per-chip
  // flag below; the count and the distinct roles drive the card-level badge. Not memoised — a bundle
  // is a handful of entries, and the list re-renders far less often than it would cost to track.
  const roleGaps = candidate.workItemRoleGaps ?? [];
  const missingRolesByKey = new Map(roleGaps.map((g) => [g.workItemKey, g.missingRoles]));
  const distinctMissingRoles = Array.from(new Set(roleGaps.flatMap((g) => g.missingRoles)));
  // Dev-only edge: the policy creates no work items, so the references are history and there is
  // nothing to sign off. The server already returns no role gaps for these.
  const untracked = candidate.tracksWorkItems === false;

  const rowProps = useKeyboardListRow(index, () => navigate(`/promotions/${candidate.id}`), {
    label: `${candidate.product} / ${candidate.service} to ${candidate.targetEnv} — ${candidate.status}. Open promotion.`,
  });

  return (
    <div
      {...rowProps}
      className="card-hover rounded-xl border p-4 cursor-pointer flex items-start gap-3"
      style={{
        borderColor: urgent ? cfg.color + '40' : 'var(--border-color)',
        backgroundColor: 'var(--bg-primary)',
        borderLeft: candidate.canApprove ? `3px solid var(--warning)` : undefined,
      }}
    >
      {selectable && (
        <input
          type="checkbox"
          checked={selected}
          onClick={(e) => e.stopPropagation()}
          onChange={onToggleSelect}
          className="rounded mt-1 shrink-0"
        />
      )}
      <div className="flex-1 min-w-0">
        {/* Wraps rather than compresses: on a phone the title and the badges cannot all fit on one
           line, and a squashed "Awaiting your approval" pill reading over three lines inside its own
           border is worse than the same pill sitting on the next row at full size. */}
        <div className="flex flex-wrap items-center gap-x-2 gap-y-1 mb-1">
          <h3
            className="text-[14px] font-semibold truncate min-w-0"
            style={{ color: 'var(--text-primary)' }}
          >
            {candidate.product} / {candidate.service}
          </h3>
          {/* The status badge only carries information once status varies (the resolved list).
             In the all-pending list it's constant noise, so drop it there and surface the
             actionable "Awaiting your approval" cue instead. */}
          {candidate.status !== 'Pending' && (
            <span
              className="badge shrink-0 whitespace-nowrap"
              style={{ backgroundColor: cfg.bg, color: cfg.color }}
            >
              <StatusIcon size={10} />
              {candidate.status}
            </span>
          )}
          {awaitingCue && candidate.canApprove && (
            <span
              className="badge shrink-0 whitespace-nowrap"
              style={{ backgroundColor: 'var(--warning-bg)', color: 'var(--warning)' }}
            >
              <Clock size={10} />
              Awaiting your approval
            </span>
          )}
          {/* Work items with nobody in a policy-required role. Sits with the status badges rather
             than down by the ticket chips: it's a property of the promotion's readiness, and it has
             to be visible without expanding a truncated chip row. */}
          <WorkItemsNeedingAttentionBadge
            count={roleGaps.length}
            roles={distinctMissingRoles}
          />
        </div>
        {/* Where it lands and how that environment's version moves. Wraps on a narrow viewport
           so the pieces drop to their own line instead of running off the edge of the screen. */}
        <div className="text-[12px]" style={{ color: 'var(--text-secondary)' }}>
          <PromotionRoute
            product={candidate.product}
            service={candidate.service}
            sourceEnv={candidate.sourceEnv}
            targetEnv={candidate.targetEnv}
            version={candidate.version}
            targetCurrentVersion={candidate.targetCurrentVersion}
            sourceBranch={candidate.sourceBranch}
          />
        </div>
        <div className="flex items-center gap-4 mt-2 text-[11px]" style={{ color: 'var(--text-muted)' }}>
          <span className="flex items-center gap-1">
            <Clock size={10} />
            {formatDistanceToNow(new Date(candidate.createdAt), { addSuffix: true })}
          </span>
          <WorkItemsBadge candidate={candidate} progress={workItemProgress} />
        </div>
        {/* Work items — key + optional title, tinted by their own sign-off state so a stalled bundle
           shows you *which* item is holding it. The chip opens the work-item detail page (sign-off,
           comments, people); the icon beside it opens the ticket in its tracker. To narrow the list
           to one work item instead, use the reference filter at the top of the page. */}
        {(() => {
          const tickets = (candidate.sourceEventReferences ?? []).filter(
            (r) => r.type === 'work-item' && (r.key ?? '').trim().length > 0,
          );
          if (tickets.length === 0) return null;
          const visibleTickets = showAllTickets ? tickets : tickets.slice(0, MAX_TICKETS);
          const hiddenTickets = tickets.length - visibleTickets.length;
          return (
            <div className="flex items-center gap-1.5 flex-wrap mt-2">
              {visibleTickets.map((ref, i) => {
                const workItemKey = ref.key ?? '';
                const href = resolveReferenceHref({
                  type: ref.type,
                  url: ref.url ?? undefined,
                  provider: ref.provider ?? undefined,
                  revision: ref.revision ?? undefined,
                });
                const chipLabel = ref.title ? `${ref.key} — ${ref.title}` : ref.key!;
                // Undecided (or state not loaded — see PROGRESS_CANDIDATE_LIMIT) keeps the neutral
                // chip, so a tint always means somebody has actually decided something.
                const decision = workItemProgress?.decisions[workItemKey] ?? null;
                const decided = decision ? decisionStyle(decision) : null;
                // Unfilled policy-required roles win the chip's tint over the sign-off state: an item
                // nobody owns is the thing to act on, and it can't have been signed off anyway.
                const missingRoles = missingRolesByKey.get(workItemKey);
                const needsPeople = (missingRoles?.length ?? 0) > 0;
                // On an edge that doesn't create work items there is no detail page to open — the
                // reference is change-set history. Rendered as plain text so the ticket is still
                // visible rather than a link to a 404. The tracker icon beside it still works.
                const chipStyle = {
                  backgroundColor: needsPeople
                    ? 'var(--warning-bg)'
                    : decided?.bg ?? 'var(--bg-secondary)',
                  color: needsPeople ? 'var(--warning)' : decided?.color ?? 'var(--text-secondary)',
                  maxWidth: 200,
                } as const;
                const chipTitle = [
                  ref.title ? `${ref.key} — ${ref.title}` : workItemKey,
                  needsPeople ? `Needs ${missingRolesLabel(missingRoles!)}` : null,
                  untracked
                    ? "Work items aren't tracked on this edge"
                    : decided
                      ? decided.label
                      : 'Not signed off yet',
                ]
                  .filter(Boolean)
                  .join(' · ');
                const chipBody = (
                  <>
                    {needsPeople ? (
                      <AlertTriangle size={10} style={{ color: 'var(--warning)', flexShrink: 0 }} />
                    ) : (
                      <Ticket
                        size={10}
                        style={{ color: decided?.color ?? 'var(--text-muted)', flexShrink: 0 }}
                      />
                    )}
                    <span className="font-medium truncate">{chipLabel}</span>
                  </>
                );
                return (
                  <span key={`wi-${i}`} className="inline-flex items-center gap-1 text-[10px]">
                    {untracked ? (
                      <span
                        className="inline-flex items-center gap-1 px-1.5 py-0.5 rounded"
                        style={chipStyle}
                        title={chipTitle}
                      >
                        {chipBody}
                      </span>
                    ) : (
                      <Link
                        to={workItemDetailPath(
                          workItemKey,
                          candidate.product,
                          candidate.targetEnv,
                          candidate.id,
                        )}
                        onClick={(e) => e.stopPropagation()}
                        className="inline-flex items-center gap-1 px-1.5 py-0.5 rounded transition-opacity hover:opacity-80"
                        style={chipStyle}
                        title={chipTitle}
                      >
                        {chipBody}
                      </Link>
                    )}
                    {href && (
                      <a
                        href={href}
                        target="_blank"
                        rel="noopener noreferrer"
                        onClick={(e) => e.stopPropagation()}
                        style={{ color: 'var(--text-muted)' }}
                        className="transition-opacity hover:opacity-80"
                        title={`Open ${workItemKey} in ${ref.provider ?? 'the tracker'}`}
                        aria-label={`Open ${workItemKey} in ${ref.provider ?? 'the tracker'}`}
                      >
                        <ExternalLink size={10} />
                      </a>
                    )}
                  </span>
                );
              })}
              {hiddenTickets > 0 && (
                <button
                  type="button"
                  onClick={(e) => {
                    e.stopPropagation();
                    setShowAllTickets(true);
                  }}
                  className="text-[10px] font-medium px-1.5 py-0.5 rounded transition-opacity hover:opacity-80"
                  style={{ color: 'var(--text-muted)', backgroundColor: 'var(--bg-secondary)' }}
                >
                  +{hiddenTickets} more
                </button>
              )}
            </div>
          );
        })()}
      </div>
      {/* Explicit per-row action. The whole card is the click target (navigates to detail);
         this is the visible CTA so the row reads as an action, not a static record. A right
         chevron (not ↗) — it stays in-app.

         Hidden below `sm`: on a phone it competes for the width the content actually needs, and
         a tap anywhere on the card already does the same thing, so it buys nothing there. */}
      <span
        className="hidden sm:inline-flex shrink-0 self-center items-center gap-1 text-[12px] font-medium"
        style={{ color: candidate.canApprove ? 'var(--accent)' : 'var(--text-muted)' }}
      >
        {candidate.canApprove ? 'Review' : 'View'}
        <ArrowRight size={14} />
      </span>
    </div>
  );
}

/**
 * Inline work-item-progress indicator for the list. The list response surfaces the candidate's own
 * work-item refs (sourceEventReferences) but not approval state, so the parent fetches
 * /work-items/{key}?... for the rows on screen. Rows past the fetch cap fall back to the bare count.
 */
function WorkItemsBadge({
  candidate,
  progress,
}: {
  candidate: PromotionCandidate;
  progress: WorkItemProgress | undefined;
}) {
  const bundleSize = (candidate.sourceEventReferences ?? []).filter(
    (r) => r.type === 'work-item',
  ).length;
  // An edge that doesn't create work items has no sign-off state to report — and no progress was
  // fetched for it. Say why rather than showing a bare count that looks like it's waiting on someone.
  if (candidate.tracksWorkItems === false) {
    return (
      <span
        className="inline-flex items-center gap-1"
        style={{ color: 'var(--text-muted)' }}
        title="This edge doesn't create work items — nothing here needs a sign-off."
      >
        <Ticket size={10} />
        Work items not tracked
      </span>
    );
  }
  if (bundleSize === 0) {
    return (
      <span
        className="inline-flex items-center gap-1"
        style={{ color: 'var(--text-muted)' }}
        title="This promotion has no work items"
      >
        <Ticket size={10} />
        No work items
      </span>
    );
  }
  if (!progress) {
    return (
      <span
        className="inline-flex items-center gap-1"
        title={`${bundleSize} work-item(s)`}
      >
        <Ticket size={10} />
        {bundleSize}
      </span>
    );
  }
  if (progress.loading) {
    return (
      <span className="inline-flex items-center gap-1" title="Loading work item state…">
        <Ticket size={10} />
        {progress.approved}/{progress.total}
        <ProgressBar approved={progress.approved} total={progress.total} />
      </span>
    );
  }
  // Held-back items are called out in the label: without them a stalled bundle looks identical to
  // one nobody has looked at yet, which is the opposite of the truth. Blocks lead — they're the
  // stronger call of the two.
  const held = [
    progress.blocked > 0 ? `${progress.blocked} blocked` : null,
    progress.issues > 0 ? `${progress.issues} issue${progress.issues === 1 ? '' : 's'}` : null,
  ]
    .filter(Boolean)
    .join(' · ');
  const label = progress.approved === 0
    ? held || 'Awaiting'
    : held
      ? `${progress.approved}/${progress.total} approved · ${held}`
      : `${progress.approved}/${progress.total} approved`;
  const undecided = progress.total - progress.approved - progress.issues - progress.blocked;
  return (
    <span
      className="inline-flex items-center gap-1.5"
      title={[
        `${progress.approved} approved`,
        progress.blocked > 0 ? `${progress.blocked} blocked` : null,
        progress.issues > 0 ? `${progress.issues} with issues` : null,
        `${undecided} pending`,
      ]
        .filter(Boolean)
        .join(', ')}
    >
      <Ticket size={10} />
      {label}
      <ProgressBar
        approved={progress.approved}
        total={progress.total}
        issues={progress.issues}
        blocked={progress.blocked}
      />
    </span>
  );
}

function ProgressBar({
  approved,
  total,
  issues = 0,
  blocked = 0,
}: {
  approved: number;
  total: number;
  issues?: number;
  blocked?: number;
}) {
  if (total === 0) return null;
  const approvedPct = (approved / total) * 100;
  const issuesPct = (issues / total) * 100;
  const blockedPct = (blocked / total) * 100;
  return (
    <span
      className="inline-block rounded-full overflow-hidden"
      style={{
        width: 36,
        height: 4,
        backgroundColor: 'var(--bg-secondary)',
        border: '1px solid var(--border-color)',
      }}
    >
      <span
        className="inline-block align-top h-full"
        style={{ width: `${approvedPct}%`, backgroundColor: 'var(--success)' }}
      />
      {issuesPct > 0 && (
        <span
          className="inline-block align-top h-full"
          style={{ width: `${issuesPct}%`, backgroundColor: 'var(--warning)' }}
        />
      )}
      {blockedPct > 0 && (
        <span
          className="inline-block align-top h-full"
          style={{ width: `${blockedPct}%`, backgroundColor: 'var(--danger)' }}
        />
      )}
    </span>
  );
}
