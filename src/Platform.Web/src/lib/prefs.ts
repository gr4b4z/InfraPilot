/**
 * Cookie-backed UI preferences.
 *
 * Used for the "which tab was I on" picks that should survive a reload. Cookies rather than
 * localStorage so the pick also travels with the document request — the prerender/SSR pass and
 * any future server-rendered shell can read the same value the client wrote, which localStorage
 * can't offer. Older per-page filters (assignee, scope, time frame) still live in localStorage;
 * they're untouched here.
 *
 * Values are opaque short strings. Every read is validated against a known set by the caller
 * (see {@link readEnumPref}) — a stale or hand-edited cookie must never put the UI in a state
 * it can't render.
 */

const ONE_YEAR_SECONDS = 60 * 60 * 24 * 365;

export function readPref(name: string): string | null {
  if (typeof document === 'undefined') return null;
  const prefix = `${encodeURIComponent(name)}=`;
  for (const part of document.cookie.split(';')) {
    const entry = part.trim();
    if (!entry.startsWith(prefix)) continue;
    try {
      return decodeURIComponent(entry.slice(prefix.length));
    } catch {
      // Malformed encoding — treat as absent rather than throwing on every render.
      return null;
    }
  }
  return null;
}

export function writePref(name: string, value: string): void {
  if (typeof document === 'undefined') return;
  document.cookie =
    `${encodeURIComponent(name)}=${encodeURIComponent(value)}` +
    `; path=/; max-age=${ONE_YEAR_SECONDS}; SameSite=Lax`;
}

/**
 * Reads a preference constrained to a known set of values, falling back when the cookie is
 * absent or holds something the current build no longer understands.
 */
export function readEnumPref<T extends string>(
  name: string,
  allowed: readonly T[],
  fallback: T,
): T {
  const raw = readPref(name);
  return raw !== null && (allowed as readonly string[]).includes(raw) ? (raw as T) : fallback;
}

// ── Keys ──────────────────────────────────────────────────────────────────────────────────
// Namespaced so they can't collide with anything the API sets on the same host.

/** Selected tab on the work-items queue page. */
export const WORK_ITEMS_VIEW_PREF = 'ip.workItems.view';
/** Selected tab on the promotions list page. */
export const PROMOTIONS_VIEW_PREF = 'ip.promotions.view';

// Secondary filters on the promotions list. Persisted for the same reason the tab is: opening a
// promotion and coming back is the single most common thing anybody does on that page, and losing
// the narrowing every time makes the filters not worth setting.
//
// Safe to persist because FilterPanel counts what is set, shows the count on its toggle, and starts
// open when anything is active — a filter can't quietly shrink the list with no explanation.
export const PROMOTIONS_PRODUCT_FILTER_PREF = 'ip.promotions.filter.product';
export const PROMOTIONS_SERVICE_FILTER_PREF = 'ip.promotions.filter.service';
export const PROMOTIONS_TARGET_ENV_FILTER_PREF = 'ip.promotions.filter.targetEnv';
export const PROMOTIONS_REFERENCE_FILTER_PREF = 'ip.promotions.filter.reference';
