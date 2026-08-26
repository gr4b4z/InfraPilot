/**
 * The promotions list's state as URL query parameters, so a filtered view can be linked.
 *
 * "Take a look at the checkout-api promotions waiting on prod" is the common thing to say about this
 * page, and pasting a link is how people say it. Tab and filters were cookie-persisted only, which
 * made the URL identical for every view and a shared link land the recipient wherever they had left
 * off. Same problem, and the same fix, as the work-items queue (see `me/queueFilterParams`).
 *
 * <p><b>Precedence.</b> A URL carrying any of these parameters wins outright — the recipient's own
 * saved filters are ignored for that visit, and anything the link omits falls back to empty rather
 * than to their state. A link has to show the sender's view; blending the two would produce a third
 * view neither of them chose. With no parameters at all, the saved state is used exactly as before,
 * so the page keeps its "come back to where you were" behaviour.</p>
 *
 * <p>Persistence still runs alongside this: the URL is where a view is shared from, the cookie is
 * where it is resumed from.</p>
 */

/**
 * Which slice of the promotions set the page is showing. Lives here rather than in the page because
 * it is part of the URL contract — the parser has to validate a link's `tab` against the same list
 * the page renders tabs from.
 */
export type PromotionView =
  | 'pending'
  | 'mine'
  | 'needs-attention'
  | 'awaiting-deploy'
  | 'resolved'
  | 'rejected'
  | 'all';

export const PROMOTION_VIEWS = [
  'pending',
  'mine',
  'needs-attention',
  'awaiting-deploy',
  'resolved',
  'rejected',
  'all',
] as const;

/** Everything the promotions page reads out of (or writes into) the query string. */
export interface PromotionParams {
  view: PromotionView;
  product: string;
  service: string;
  targetEnv: string;
  reference: string;
}

// Parameter names. Short and readable — these end up in links people paste to each other, and
// `product` / `service` / `targetEnv` / `reference` are the same names the list API takes.
const P_TAB = 'tab';
const P_PRODUCT = 'product';
const P_SERVICE = 'service';
const P_TARGET_ENV = 'targetEnv';
const P_REFERENCE = 'reference';

const ALL_PARAMS = [P_TAB, P_PRODUCT, P_SERVICE, P_TARGET_ENV, P_REFERENCE] as const;

/** True when the URL is describing a view — i.e. the link carries state to honour. */
export function hasPromotionParams(params: URLSearchParams): boolean {
  return ALL_PARAMS.some((p) => params.has(p));
}

/**
 * Reads a shared view out of the query string. An unknown `tab` falls back to `fallbackView` rather
 * than failing: a link is typed, truncated and edited by hand, and half a view is more useful than
 * an error.
 *
 * The filter values are passed through as free text — `product` and `targetEnv` are dropdowns, but
 * their vocabulary comes from the server and the page already keeps an unmatched selection listed so
 * it can be cleared. A link naming a product that has since gone quiet therefore still shows what it
 * was filtering by.
 */
export function parsePromotionParams(
  params: URLSearchParams,
  fallbackView: PromotionView,
): PromotionParams {
  const str = (name: string): string => (params.get(name) ?? '').trim();

  const tab = str(P_TAB);
  return {
    view: PROMOTION_VIEWS.find((v) => v === tab) ?? fallbackView,
    product: str(P_PRODUCT),
    service: str(P_SERVICE),
    targetEnv: str(P_TARGET_ENV),
    reference: str(P_REFERENCE),
  };
}

/**
 * Serialises the current view into query parameters, omitting any filter that isn't set so the
 * default "All pending" view has a clean URL. Every filter here applies on every tab, so unlike the
 * work-items queue nothing has to be dropped per tab.
 */
export function buildPromotionParams(state: PromotionParams): URLSearchParams {
  const params = new URLSearchParams();
  params.set(P_TAB, state.view);
  if (state.product) params.set(P_PRODUCT, state.product);
  if (state.service) params.set(P_SERVICE, state.service);
  if (state.targetEnv) params.set(P_TARGET_ENV, state.targetEnv);
  if (state.reference) params.set(P_REFERENCE, state.reference);
  return params;
}
