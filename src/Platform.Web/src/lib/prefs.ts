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

/**
 * Reads a preference holding a set of opaque values, stored comma-separated.
 *
 * Blank entries are dropped, so an empty cookie, a trailing comma, or a hand-edited value all
 * degrade to "nothing selected" rather than a set containing "". Values that the current build no
 * longer recognises are kept rather than pruned — a product that disappears from the API for a
 * release and comes back should come back hidden, the way the user left it.
 */
export function readSetPref(name: string): Set<string> {
  const raw = readPref(name);
  if (raw === null) return new Set();
  return new Set(
    raw
      .split(',')
      .map((v) => v.trim())
      .filter((v) => v.length > 0),
  );
}

/** Companion to {@link readSetPref}. */
export function writeSetPref(name: string, values: Iterable<string>): void {
  writePref(name, Array.from(values).join(','));
}

// ── Keys ──────────────────────────────────────────────────────────────────────────────────
// Namespaced so they can't collide with anything the API sets on the same host.

/** Selected tab on the work-items queue page. */
export const WORK_ITEMS_VIEW_PREF = 'ip.workItems.view';
/** Selected tab on the promotions list page. */
export const PROMOTIONS_VIEW_PREF = 'ip.promotions.view';
/**
 * Products hidden from the deployments overview.
 *
 * Stores what is HIDDEN, not what is shown, so a product added after the user made their pick
 * shows up rather than silently going missing — the failure mode of storing the visible set is
 * that a new service never appears and nobody knows to look for it.
 */
export const DEPLOYMENTS_HIDDEN_PRODUCTS_PREF = 'ip.deployments.hiddenProducts';
