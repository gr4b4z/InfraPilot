import { create } from 'zustand';

/** One element the assistant wants ringed, by its `data-guide-anchor`. */
export interface HighlightTarget {
  anchor: string;
  /** Short caption shown beside the ring. */
  label?: string;
}

interface HighlightState {
  targets: HighlightTarget[];
  /**
   * Pathname the rings belong to. They are drawn only while the user is on that page, and cleared
   * once they leave it — a ring for the staging cell has no business surviving a hop to Settings.
   */
  route: string | null;
  /**
   * Whether the user has reached `route` since the rings were set. Rings often arrive together with
   * the navigation that makes them meaningful, a render or two before the router commits it, so
   * "not on the route yet" and "left the route" have to be told apart — only the second clears.
   */
  arrived: boolean;

  show: (targets: HighlightTarget[], route: string) => void;
  arrive: () => void;
  dismiss: (anchor: string) => void;
  clear: () => void;
}

/**
 * Rings the assistant has put on the page. Unlike the walkthrough spotlight these are not modal:
 * nothing is dimmed, the page stays fully usable, and several elements can be ringed at once. They
 * stay until the next assistant turn, a navigation away, Escape, or their own close button.
 */
export const useHighlightStore = create<HighlightState>((set, get) => ({
  targets: [],
  route: null,
  arrived: false,

  show: (targets, route) => set({ targets, route, arrived: false }),

  arrive: () => set({ arrived: true }),

  dismiss: (anchor) => {
    const remaining = get().targets.filter((t) => t.anchor !== anchor);
    set(remaining.length > 0 ? { targets: remaining } : { targets: [], route: null, arrived: false });
  },

  clear: () => set({ targets: [], route: null, arrived: false }),
}));
