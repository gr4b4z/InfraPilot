import type { AssigneeFilterValue } from './AssigneeFilter';
import type { ScopeFilterValue } from './ScopeFilter';
import { SCOPE_FILTER_DEFAULT } from './ScopeFilter';

/**
 * The work-items queue's state as URL query parameters, so a filtered view can be linked.
 *
 * "Have a look at the items with no QA owner on checkout-api" is the most common thing anyone needs to
 * say about this page, and pasting a link is how people say it. The filters were persisted per browser
 * only, which made the URL identical for every view and a shared link land the recipient wherever they
 * had left off.
 *
 * <p><b>Precedence.</b> A URL carrying any of these parameters wins outright — the recipient's own
 * saved filters are ignored for that visit, and anything the link omits falls back to the tab's
 * default rather than to their state. A link has to show the sender's view; blending the two would
 * produce a third view neither of them chose. With no parameters at all, the saved state is used
 * exactly as before, so the page keeps its "come back to where you were" behaviour.</p>
 *
 * <p>Persistence still runs alongside this: the URL is where a view is shared from, localStorage is
 * where it is resumed from.</p>
 */

export type QueueView = 'mine' | 'not-assigned' | 'pending' | 'decided';
export type TimeFrameValue = '1d' | '7d' | '30d' | 'all';
export type DeciderFilterValue =
  | { mode: 'all' }
  | { mode: 'me' }
  | { mode: 'person'; email: string; displayName: string };

/** Everything the queue page reads out of (or writes into) the query string. */
export interface QueueParams {
  view: QueueView;
  assignee: AssigneeFilterValue;
  scope: ScopeFilterValue;
  timeFrame: TimeFrameValue;
  decider: DeciderFilterValue;
}

// Parameter names. Short and readable — these end up in links people paste to each other.
const P_TAB = 'tab';
const P_ROLE = 'role';
const P_ASSIGNEE = 'assignee';
const P_PRODUCT = 'product';
const P_SERVICE = 'service';
const P_TARGET_ENV = 'targetEnv';
const P_TESTABLE_IN = 'testableIn';
const P_TIME_FRAME = 'since';
const P_DECIDED_BY = 'decidedBy';

const ALL_PARAMS = [
  P_TAB,
  P_ROLE,
  P_ASSIGNEE,
  P_PRODUCT,
  P_SERVICE,
  P_TARGET_ENV,
  P_TESTABLE_IN,
  P_TIME_FRAME,
  P_DECIDED_BY,
] as const;

const VIEWS: readonly QueueView[] = ['mine', 'not-assigned', 'pending', 'decided'];
const TIME_FRAMES: readonly TimeFrameValue[] = ['1d', '7d', '30d', 'all'];

/** Sentinels shared between the URL and the filter values. Deliberately readable in a link. */
const ME = 'me';
const UNASSIGNED = 'unassigned';

/** True when the URL is describing a view — i.e. the link carries filter state to honour. */
export function hasQueueParams(params: URLSearchParams): boolean {
  return ALL_PARAMS.some((p) => params.has(p));
}

/**
 * Reads a shared view out of the query string. Unknown or malformed values fall back to the default
 * for that field rather than failing: a link is typed, truncated and edited by hand, and half a view
 * is more useful than an error.
 */
export function parseQueueParams(params: URLSearchParams, fallbackView: QueueView): QueueParams {
  const str = (name: string): string | null => {
    const raw = params.get(name);
    const trimmed = (raw ?? '').trim();
    return trimmed.length > 0 ? trimmed : null;
  };

  const tab = str(P_TAB);
  const view = VIEWS.find((v) => v === tab) ?? fallbackView;

  const timeFrameRaw = str(P_TIME_FRAME);
  const timeFrame = TIME_FRAMES.find((t) => t === timeFrameRaw) ?? '1d';

  return {
    view,
    assignee: parseAssignee(str(P_ROLE), str(P_ASSIGNEE)),
    scope: {
      product: str(P_PRODUCT),
      service: str(P_SERVICE),
      targetEnv: str(P_TARGET_ENV),
      deployedEnv: str(P_TESTABLE_IN),
    },
    timeFrame,
    decider: parseDecider(str(P_DECIDED_BY)),
  };
}

function parseAssignee(role: string | null, assignee: string | null): AssigneeFilterValue {
  if (assignee === null) return { role, mode: 'all' };
  if (assignee.toLowerCase() === ME) return { role, mode: 'me' };
  if (assignee.toLowerCase() === UNASSIGNED) return { role, mode: 'unassigned' };
  // A link carries the email but not the display name — the queue's own rollup supplies the name once
  // it loads, and until then the email is a truthful label.
  return { role, mode: 'person', email: assignee, displayName: assignee };
}

function parseDecider(decidedBy: string | null): DeciderFilterValue {
  if (decidedBy === null) return { mode: 'all' };
  if (decidedBy.toLowerCase() === ME) return { mode: 'me' };
  return { mode: 'person', email: decidedBy, displayName: decidedBy };
}

/**
 * Serialises the current view into query parameters, omitting anything at its default so a plain
 * "Assigned to me" view has a clean URL. Parameters only meaningful on one tab (the decided view's
 * time frame and decider) are omitted elsewhere, so a copied link can't carry a filter the recipient
 * cannot see.
 */
export function buildQueueParams(state: QueueParams): URLSearchParams {
  const params = new URLSearchParams();
  params.set(P_TAB, state.view);

  if (state.assignee.role) params.set(P_ROLE, state.assignee.role);

  // The person is fixed by the tab on `mine`, and meaningless on `not-assigned` (nobody holds the
  // role) — matching where MyQueuePage hides the person select.
  const personApplies = state.view === 'pending';
  if (personApplies) {
    switch (state.assignee.mode) {
      case 'me':
        params.set(P_ASSIGNEE, ME);
        break;
      case 'unassigned':
        params.set(P_ASSIGNEE, UNASSIGNED);
        break;
      case 'person':
        if (state.assignee.email) params.set(P_ASSIGNEE, state.assignee.email);
        break;
      case 'all':
        break;
    }
  }

  if (state.scope.product) params.set(P_PRODUCT, state.scope.product);
  if (state.scope.service) params.set(P_SERVICE, state.scope.service);
  if (state.scope.targetEnv) params.set(P_TARGET_ENV, state.scope.targetEnv);
  if (state.scope.deployedEnv) params.set(P_TESTABLE_IN, state.scope.deployedEnv);

  if (state.view === 'decided') {
    if (state.timeFrame !== '1d') params.set(P_TIME_FRAME, state.timeFrame);
    if (state.decider.mode === 'me') params.set(P_DECIDED_BY, ME);
    else if (state.decider.mode === 'person') params.set(P_DECIDED_BY, state.decider.email);
  }

  return params;
}

/** Default scope, re-exported so the page can reset to it when a link omits every scope field. */
export const QUEUE_SCOPE_DEFAULT = SCOPE_FILTER_DEFAULT;
