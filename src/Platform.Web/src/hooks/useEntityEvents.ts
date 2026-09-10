import { useCallback, useEffect, useRef, useState } from 'react';
import {
  subscribeEntityEvents,
  subscribeReconnect,
  type EntityChangedEvent,
} from '@/lib/realtime';

interface EntityEventOptions {
  /**
   * Coalescing window. Server mutations often burst (a deploy supersedes three promotions,
   * a bulk approve fires per candidate) — one refetch at the end beats one per event.
   */
  debounceMs?: number;
  /** Extra narrowing beyond entity type, e.g. only events for the id this page shows. */
  filter?: (evt: EntityChangedEvent) => boolean;
}

/**
 * Runs `onChange` (debounced) whenever the server broadcasts a change to one of the given entity
 * types — and after every reconnect, since events sent while the connection was down are gone.
 * `evt` is null on those reconnect refreshes.
 *
 * The callback and filter are kept in refs, so inline closures are fine and never cause
 * resubscription churn.
 */
export function useEntityEvent(
  entities: string[],
  onChange: (evt: EntityChangedEvent | null) => void,
  options?: EntityEventOptions,
): void {
  const onChangeRef = useRef(onChange);
  const filterRef = useRef(options?.filter);
  useEffect(() => {
    onChangeRef.current = onChange;
    filterRef.current = options?.filter;
  });
  const debounceMs = options?.debounceMs ?? 300;
  const entitiesKey = entities.join(',');

  useEffect(() => {
    const wanted = new Set(entitiesKey.split(','));
    let timer: number | undefined;
    let lastEvent: EntityChangedEvent | null = null;

    const trigger = (evt: EntityChangedEvent | null) => {
      lastEvent = evt;
      if (timer !== undefined) window.clearTimeout(timer);
      timer = window.setTimeout(() => {
        timer = undefined;
        onChangeRef.current(lastEvent);
      }, debounceMs);
    };

    const unsubscribeEvents = subscribeEntityEvents((evt) => {
      if (!wanted.has(evt.entity)) return;
      if (filterRef.current && !filterRef.current(evt)) return;
      trigger(evt);
    });
    const unsubscribeReconnect = subscribeReconnect(() => trigger(null));

    return () => {
      unsubscribeEvents();
      unsubscribeReconnect();
      if (timer !== undefined) window.clearTimeout(timer);
    };
  }, [entitiesKey, debounceMs]);
}

/**
 * The lowest-friction way to make an existing `useEffect(fetchData, [deps])` live: returns a
 * counter that increments (debounced) on matching entity events — add it to the effect's deps
 * and the page refetches on every relevant server change.
 */
export function useEntityRefresh(entities: string[], options?: EntityEventOptions): number {
  const [tick, setTick] = useState(0);
  useEntityEvent(entities, () => setTick((t) => t + 1), options);
  return tick;
}

/**
 * Companion to `useEntityRefresh` for fetch effects that also depend on filters or route params.
 * Returns a function to call once per effect run with the identity of the query being fetched; it
 * answers whether this run is a *background refresh* of the query already on screen (a realtime
 * tick, a reconnect) rather than a *new query* (mount, filter or route change).
 *
 * The distinction is what keeps live lists calm: a new query has nothing valid to show, so a
 * skeleton is right — but a refresh of the same query should keep the current rows mounted and let
 * React swap the response into them by key. Flipping the page's `loading` flag on a refresh unmounts
 * the whole list, flashes a skeleton, and drops the reader's scroll position every time an event
 * lands.
 *
 *   const isBackgroundRefresh = useIsBackgroundRefresh();
 *   useEffect(() => {
 *     void fetchData({ silent: isBackgroundRefresh(queryKey) });
 *   }, [queryKey, refreshTick]);
 *
 * Effects with no query of their own (the same request every time) don't need this — there the
 * first run is the only one that should show a skeleton, so `silent: refreshTick > 0` says it.
 */
export function useIsBackgroundRefresh(): (queryKey: string) => boolean {
  const lastQueryKey = useRef<string | null>(null);
  return useCallback((queryKey: string) => {
    const sameQuery = lastQueryKey.current === queryKey;
    lastQueryKey.current = queryKey;
    return sameQuery;
  }, []);
}
