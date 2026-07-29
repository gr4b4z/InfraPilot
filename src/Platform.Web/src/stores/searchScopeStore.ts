import { useEffect } from 'react';
import { create } from 'zustand';

/**
 * What `/` searches, published by whichever page is on screen.
 *
 * A single global "find a work item" was the wrong shape: on the deployments page you are looking for
 * a service, on the promotions list for a promotion. Rather than teach the search dialog about every
 * route, each page registers what searching means where it is, and the dialog just runs it.
 *
 * A page that registers nothing gets the fallback scope, so `/` always does something.
 */
export interface SearchHit {
  /** Stable identity for the result list. */
  id: string;
  title: string;
  /** Second line — product, environment, status. */
  subtitle?: string;
  /** In-app route to open. */
  to: string;
}

export interface SearchScope {
  /** Shown in the dialog header, e.g. "Promotions". */
  label: string;
  placeholder: string;
  /**
   * Runs a query. Called debounced, and may be called again before it settles — the dialog
   * discards stale responses, so implementations don't need their own cancellation.
   */
  search: (query: string) => Promise<SearchHit[]>;
}

interface SearchScopeState {
  scope: SearchScope | null;
  setScope: (scope: SearchScope | null) => void;
}

export const useSearchScopeStore = create<SearchScopeState>()((set) => ({
  scope: null,
  setScope: (scope) => set({ scope }),
}));

/**
 * Registers the search scope for the current page, clearing it on unmount so a stale scope can't
 * outlive the page that published it.
 *
 * `deps` exists because a scope usually closes over page data (the loaded rows, the product name).
 * Pass what the search function reads, the same as any dependency list — the identity of `scope`
 * itself is deliberately not used, since an inline object literal changes on every render.
 */
export function useSearchScope(scope: SearchScope, deps: unknown[]): void {
  const setScope = useSearchScopeStore((s) => s.setScope);
  useEffect(() => {
    setScope(scope);
    return () => setScope(null);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [setScope, ...deps]);
}
