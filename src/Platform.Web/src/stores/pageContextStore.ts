import { useEffect } from 'react';
import { create } from 'zustand';

/** A value a page can describe itself with. Empty and nullish entries are dropped. */
export type ContextValue = string | number | boolean | null | undefined;

interface PageContextState {
  /** The page's human name as shown on screen — "Promotion", "Work item". Null between pages. */
  page: string | null;
  /** What is on the page right now: the record open, its status, the applied filters. */
  state: Record<string, string>;

  publish: (page: string, state: Record<string, string>) => void;
  clear: (page: string) => void;
}

/**
 * What the user is looking at, kept current by the page itself so the assistant can be told on
 * every turn — not only when a Help button is pressed.
 *
 * The assistant is asked to treat the screen as the primary referent: "how do I approve that?"
 * means the work item in front of the user, not the promotion the conversation was about a minute
 * ago. That only works if the screen is described on every message, which is what this store is for.
 */
export const usePageContextStore = create<PageContextState>((set, get) => ({
  page: null,
  state: {},

  publish: (page, state) => set({ page, state }),

  // Only the page that published may clear — a page unmounting after its successor has already
  // published must not wipe the successor's context.
  clear: (page) => {
    if (get().page === page) set({ page: null, state: {} });
  },
}));

/**
 * Declares what this page is showing. Call it from the page component with the values that
 * identify the record on screen and the state the user would describe if asked.
 *
 * Re-published whenever the values change, so a status that flips while the page is open is what
 * the assistant is told next turn. Cleared on unmount.
 */
export function usePageContext(page: string, context?: Record<string, ContextValue>) {
  const cleaned = cleanContext(context);
  // Compared by value: a fresh object literal every render must not re-publish.
  const key = JSON.stringify(cleaned ?? {});

  useEffect(() => {
    usePageContextStore.getState().publish(page, cleaned ?? {});
    return () => usePageContextStore.getState().clear(page);
  }, [page, key]); // eslint-disable-line react-hooks/exhaustive-deps
}

/**
 * Drops entries the page could not fill in. A filter nobody set and a field nobody typed are not
 * context — sending them as empty strings would have the assistant explain absences that are merely
 * defaults.
 *
 * A zero is kept, because "no products yet" and "no policies configured" are exactly the situations
 * where someone reaches for Help.
 */
export function cleanContext(context?: Record<string, ContextValue>): Record<string, string> | undefined {
  if (!context) return undefined;

  const cleaned: Record<string, string> = {};
  for (const [key, value] of Object.entries(context)) {
    if (value === null || value === undefined || value === '') continue;
    cleaned[key] = String(value);
  }

  return Object.keys(cleaned).length > 0 ? cleaned : undefined;
}

/** Upper bound on anchors sent per turn; a wide matrix can carry several hundred cells. */
const MAX_ANCHORS = 400;

/**
 * Every `data-guide-anchor` currently on the page, deduplicated. This is the inventory the
 * assistant's highlight tool picks from, so it rings things that exist rather than things it
 * imagines. Read at send time so it reflects the DOM the user actually sees.
 */
export function collectAnchors(): string[] {
  if (typeof document === 'undefined') return [];
  const seen = new Set<string>();
  for (const el of document.querySelectorAll<HTMLElement>('[data-guide-anchor]')) {
    const name = el.dataset.guideAnchor;
    if (!name) continue;
    seen.add(name);
    if (seen.size >= MAX_ANCHORS) break;
  }
  return [...seen];
}
