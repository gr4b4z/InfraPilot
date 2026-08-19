import type { PromotionAuditCategory } from '@/lib/api';

/**
 * The promotions audit page's state as URL query parameters.
 *
 * Same contract, and the same reasoning, as {@link promotionFilterParams} for the promotions list: a
 * view of this page is a thing people hand to each other ("here's everything that went to prod last
 * week"), and a link that lands the recipient on their own saved filters instead is not that. This
 * page leans on it harder than the list does, because the whole point of it is answering a question
 * someone asked — the answer and the link are the same artefact.
 *
 * <p>Nothing here is cookie-persisted. The list page resumes where you left off because you work out
 * of it all day; this one is opened to ask something, and a stale window silently narrowing the
 * answer is a worse failure than starting from the default every time.</p>
 */

/**
 * The window the feed covers, as a token rather than a pair of instants.
 *
 * <p>This is what makes a shared link mean the same thing tomorrow: `range=today` re-resolves against
 * the reader's own clock — and their own timezone, which is the part that matters for a calendar day.
 * Pinning the two absolute instants into the URL would turn "today" into "last Tuesday" the moment
 * anybody bookmarked it.</p>
 */
export type AuditRange = 'today' | '24h' | '7d' | '30d' | 'all';

export const AUDIT_RANGES = ['today', '24h', '7d', '30d', 'all'] as const;

/** Default window. Long enough that the page has something to say on arrival, short enough to scan. */
export const DEFAULT_AUDIT_RANGE: AuditRange = '7d';

/**
 * Which slice of the activity the page is showing. A tab is a named set of the server's categories
 * (see `PromotionAuditCategories` on the API side) — the categories are the data, these groupings are
 * the reading: nobody asks "show me approval-step rows", they ask about approvals.
 */
export type AuditTab =
  | 'all'
  | 'approvals'
  | 'rejections'
  | 'created'
  | 'work-items'
  | 'deploys'
  | 'other';

export const AUDIT_TABS = [
  'all',
  'approvals',
  'rejections',
  'created',
  'work-items',
  'deploys',
  'other',
] as const;

/**
 * The categories each tab asks the server for. `all` asks for none — an empty list means unfiltered,
 * not "nothing".
 *
 * <p>An approval, the signature that produced it and an approval taken back all live under
 * "Approvals": they are the same conversation, and separating them would leave a cancelled approval
 * with no tab of its own to be found in. "Everything else" carries the rest, including `other` — the
 * server resolves that against the actions actually present, so an audit action nobody has taught
 * this page about still lands somewhere a reader can find it.</p>
 */
export const TAB_CATEGORIES: Record<AuditTab, PromotionAuditCategory[]> = {
  all: [],
  approvals: ['approved', 'approval-step', 'cancelled'],
  rejections: ['rejected'],
  created: ['created'],
  'work-items': ['work-item'],
  deploys: ['deployed'],
  other: ['updated', 'comment', 'people', 'other'],
};

/** Everything the audit page reads out of (or writes into) the query string. */
export interface AuditParams {
  range: AuditRange;
  tab: AuditTab;
  product: string;
  service: string;
  targetEnv: string;
  /** An actor id, as the dropdown sets it, or a name fragment when typed by hand. */
  actor: string;
  /** A single raw action name, for the "just this one kind" case the tabs are too coarse for. */
  action: string;
}

export const EMPTY_AUDIT_PARAMS: AuditParams = {
  range: DEFAULT_AUDIT_RANGE,
  tab: 'all',
  product: '',
  service: '',
  targetEnv: '',
  actor: '',
  action: '',
};

// Parameter names. These end up in pasted links, and `product` / `service` / `targetEnv` / `action`
// are the names the API itself takes.
const P_RANGE = 'range';
const P_TAB = 'tab';
const P_PRODUCT = 'product';
const P_SERVICE = 'service';
const P_TARGET_ENV = 'targetEnv';
const P_ACTOR = 'actor';
const P_ACTION = 'action';

const ALL_PARAMS = [P_RANGE, P_TAB, P_PRODUCT, P_SERVICE, P_TARGET_ENV, P_ACTOR, P_ACTION] as const;

/** True when the URL is describing a view — i.e. the link carries state to honour. */
export function hasAuditParams(params: URLSearchParams): boolean {
  return ALL_PARAMS.some((p) => params.has(p));
}

/**
 * Reads a view out of the query string. An unrecognised `range` or `tab` falls back to the default
 * rather than failing: links get truncated and hand-edited, and half a view beats an error page.
 */
export function parseAuditParams(params: URLSearchParams): AuditParams {
  const str = (name: string): string => (params.get(name) ?? '').trim();

  const range = str(P_RANGE);
  const tab = str(P_TAB);
  return {
    range: AUDIT_RANGES.find((r) => r === range) ?? DEFAULT_AUDIT_RANGE,
    tab: AUDIT_TABS.find((t) => t === tab) ?? 'all',
    product: str(P_PRODUCT),
    service: str(P_SERVICE),
    targetEnv: str(P_TARGET_ENV),
    actor: str(P_ACTOR),
    action: str(P_ACTION),
  };
}

/**
 * Serialises the view into query parameters, omitting anything unset so a default view has a clean
 * URL. The range and tab are always written: they are the two things a reader of the link most needs
 * stated rather than inferred.
 */
export function buildAuditParams(state: AuditParams): URLSearchParams {
  const params = new URLSearchParams();
  params.set(P_RANGE, state.range);
  params.set(P_TAB, state.tab);
  if (state.product) params.set(P_PRODUCT, state.product);
  if (state.service) params.set(P_SERVICE, state.service);
  if (state.targetEnv) params.set(P_TARGET_ENV, state.targetEnv);
  if (state.actor) params.set(P_ACTOR, state.actor);
  if (state.action) params.set(P_ACTION, state.action);
  return params;
}

/**
 * Turns a range token into the absolute window to ask the API for. Resolved at fetch time from the
 * clock, never held in state — a `from` that changes on every render would re-trigger the fetch that
 * depends on it, forever.
 *
 * <p>`today` is local midnight, not 24 hours ago: "what was approved today?" is a question about the
 * calendar day the person asking is living in. The others are rolling windows, which is what "last 7
 * days" means when anybody says it out loud.</p>
 */
export function resolveAuditWindow(range: AuditRange, now: Date = new Date()): { from?: string } {
  if (range === 'all') return {};
  if (range === 'today') {
    const midnight = new Date(now);
    midnight.setHours(0, 0, 0, 0);
    return { from: midnight.toISOString() };
  }
  const hours = range === '24h' ? 24 : range === '7d' ? 24 * 7 : 24 * 30;
  return { from: new Date(now.getTime() - hours * 60 * 60 * 1000).toISOString() };
}
