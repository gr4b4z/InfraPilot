import { useCallback, useEffect, useRef, useState } from 'react';
import {
  subscribeEntityEvents,
  subscribeReconnect,
  type EntityChangedEvent,
} from '@/lib/realtime';

interface EntityEventOptions {
  /**
   * Trailing coalescing window: the callback runs once events have been quiet this long. Server
   * mutations often burst (a deploy supersedes three promotions, a bulk approve fires per
   * candidate) — one refetch at the end beats one per event.
   */
  debounceMs?: number;
  /**
   * Longest a burst that never goes quiet can hold the callback back. Without it an approval rush
   * with events closer together than `debounceMs` would freeze the page until the rush ends.
   */
  maxWaitMs?: number;
  /** Extra narrowing beyond entity type, e.g. only events for the id this page shows. */
  filter?: (evt: EntityChangedEvent) => boolean;
}

/** Upper bound of the random delay before a tab that comes back into view catches up. */
const VISIBLE_CATCH_UP_JITTER_MS = 1500;

/**
 * Runs `onChange` (coalesced) whenever the server broadcasts a change to one of the given entity
 * types — and after every reconnect, since events sent while the connection was down are gone.
 * `evt` is the batch's last event, or null when the batch includes a reconnect ("anything may have
 * changed"). `changedAt` is when (client clock) the batch's newest change was seen, so a caller can
 * tell that a fetch started since then already covers it.
 *
 * A hidden tab doesn't run the callback: it remembers that something changed and runs it once when
 * the tab is shown again, after a random delay of up to {@link VISIBLE_CATCH_UP_JITTER_MS} so tabs
 * coming back together don't refetch in lockstep. Every tab receives every event, so refetching in
 * the background multiplied each server change by the number of tabs open — for data nobody was
 * looking at.
 *
 * The callback and filter are kept in refs, so inline closures are fine and never cause
 * resubscription churn.
 */
export function useEntityEvent(
  entities: string[],
  onChange: (evt: EntityChangedEvent | null, changedAt: number) => void,
  options?: EntityEventOptions,
): void {
  const onChangeRef = useRef(onChange);
  const filterRef = useRef(options?.filter);
  useEffect(() => {
    onChangeRef.current = onChange;
    filterRef.current = options?.filter;
  });
  const debounceMs = options?.debounceMs ?? 1000;
  const maxWaitMs = Math.max(debounceMs, options?.maxWaitMs ?? 5000);
  const entitiesKey = entities.join(',');

  useEffect(() => {
    const wanted = new Set(entitiesKey.split(','));
    let timer: number | undefined;
    // The undelivered batch. `dirty` survives a hidden spell; the rest describes what it holds.
    let dirty = false;
    let lastEvent: EntityChangedEvent | null = null;
    let sawReconnect = false;
    let changedAt = 0;
    // When the current coalescing window opened — the anchor for `maxWaitMs`.
    let windowOpenedAt: number | undefined;

    const isHidden = () => document.visibilityState === 'hidden';

    const cancelTimer = () => {
      if (timer !== undefined) window.clearTimeout(timer);
      timer = undefined;
      windowOpenedAt = undefined;
    };

    const flush = () => {
      timer = undefined;
      windowOpenedAt = undefined;
      // Still hidden: keep the batch for the catch-up run when the tab is shown.
      if (!dirty || isHidden()) return;
      const evt = sawReconnect ? null : lastEvent;
      dirty = false;
      lastEvent = null;
      sawReconnect = false;
      onChangeRef.current(evt, changedAt);
    };

    const trigger = (evt: EntityChangedEvent | null) => {
      const now = Date.now();
      dirty = true;
      changedAt = now;
      if (evt) lastEvent = evt;
      else sawReconnect = true;
      if (isHidden()) {
        cancelTimer();
        return;
      }
      if (timer !== undefined) window.clearTimeout(timer);
      windowOpenedAt ??= now;
      const wait = Math.max(0, Math.min(debounceMs, windowOpenedAt + maxWaitMs - now));
      timer = window.setTimeout(flush, wait);
    };

    const onVisibilityChange = () => {
      if (isHidden()) {
        // A window still open when the tab went to the background would otherwise fire there.
        cancelTimer();
        return;
      }
      if (dirty && timer === undefined) {
        timer = window.setTimeout(flush, Math.random() * VISIBLE_CATCH_UP_JITTER_MS);
      }
    };

    const unsubscribeEvents = subscribeEntityEvents((evt) => {
      if (!wanted.has(evt.entity)) return;
      if (filterRef.current && !filterRef.current(evt)) return;
      trigger(evt);
    });
    const unsubscribeReconnect = subscribeReconnect(() => trigger(null));
    document.addEventListener('visibilitychange', onVisibilityChange);

    return () => {
      unsubscribeEvents();
      unsubscribeReconnect();
      document.removeEventListener('visibilitychange', onVisibilityChange);
      cancelTimer();
    };
  }, [entitiesKey, debounceMs, maxWaitMs]);
}

/**
 * The lowest-friction way to make an existing `useEffect(fetchData, [deps])` live: returns a
 * counter that increments (coalesced, and held while the tab is hidden) on matching entity events
 * — add it to the effect's deps and the page refetches on every relevant server change.
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
