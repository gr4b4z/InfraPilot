import { useEffect } from 'react';
import { getAppName, getPageTitle } from './runtimeConfig';

/**
 * The document title, as a description of the page you are actually on.
 *
 * It used to be set once at boot from `config.json` and never again, so every route in the app —
 * and every filtered view of a route — shared one title. That is wrong in three places people
 * actually look: the browser tab (a dozen identical tabs), the history and bookmark lists, and the
 * preview a chat client renders for a pasted link. The filters live in the URL specifically so a
 * view can be handed to someone (see `promotionFilterParams`, `queueFilterParams`); the title is
 * the part of that hand-off the recipient reads before clicking.
 *
 * <p><b>Order.</b> Most specific segment first, app name last — `ABC-123 · checkout-api → prod ·
 * Work item · InfraPilot`. Titles are truncated from the end everywhere they are shown (tab strips
 * hardest of all), so the section name is the part that can afford to be cut; the work item's key
 * is not.</p>
 *
 * <p><b>Environments are raw keys, not display names.</b> A shared link and its title then say the
 * same thing — `targetEnv=prod` in the query string, `→ prod` in the title — and no page needs the
 * settings store loaded before it can name itself. (The page bodies keep using the configured
 * display names; only the title takes the key.)</p>
 */

/** Between segments. A middle dot reads as a separator without looking like part of a name. */
const SEPARATOR = ' · ';

/**
 * One segment. Falsy values are dropped rather than rendered, so a call site can pass a filter
 * straight through (`referenceFilter && \`ref ${referenceFilter}\``) without pre-filtering.
 */
export type TitlePart = string | null | undefined | false;

/**
 * Builds the title for a page from its segments. With no segments at all it falls back to the
 * configured `pageTitle` — the full "InfraPilot | Infrastructure Portal" form, which is the right
 * thing for a page that has no context of its own to report.
 */
export function buildPageTitle(parts: TitlePart[]): string {
  const context = parts
    .map((part) => (typeof part === 'string' ? part.trim() : ''))
    .filter((part) => part.length > 0);
  if (context.length === 0) return getPageTitle();
  return [...context, getAppName()].join(SEPARATOR);
}

/**
 * Sets `document.title` from the current page's segments, and puts the configured default back when
 * the page unmounts.
 *
 * <p>The restore matters: without it a route that sets no title — or the gap while a redirect
 * resolves — would keep showing the previous page's, which is worse than a generic title because it
 * is confidently wrong.</p>
 *
 * <p>The effect depends on the joined string rather than the array, so call sites can build their
 * parts inline. A fresh array every render is the normal case here, and the resulting title is
 * usually identical.</p>
 */
export function useDocumentTitle(parts: TitlePart[]): void {
  const title = buildPageTitle(parts);

  useEffect(() => {
    document.title = title;
    return () => {
      document.title = getPageTitle();
    };
  }, [title]);
}

/**
 * The `product / service → env` shape that most of this app's views are narrowed by, as one
 * segment. Any of the three may be missing — a filter set to only a target environment is a normal
 * view, and the arrow needs something on its left to be an arrow, so that case reads `into prod`
 * rather than a dangling `→ prod`.
 *
 * Returns null when nothing is set, so an unfiltered view's title stays clean.
 */
export function scopeTitle(scope: {
  product?: string | null;
  service?: string | null;
  targetEnv?: string | null;
}): string | null {
  const path = [scope.product, scope.service]
    .map((v) => (v ?? '').trim())
    .filter((v) => v.length > 0)
    .join('/');
  const env = (scope.targetEnv ?? '').trim();
  if (path && env) return `${path} → ${env}`;
  if (path) return path;
  if (env) return `into ${env}`;
  return null;
}
