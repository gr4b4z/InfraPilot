import { useEffect, useRef, useState } from 'react';
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
