import { api } from '@/lib/api';
import type { WorkItemContext } from '@/lib/api';
import { subscribeEntityEvents, subscribeReconnect, type EntityChangedEvent } from '@/lib/realtime';

/**
 * Short-lived cache of work-item sign-off context, for the promotions list's progress cells.
 *
 * The list fans out one GET /work-items/{key} per work item on its first rows, and used to repeat
 * the whole fan-out on every mount, every change of the rows on screen and every work-item event
 * from anyone — so each sign-off had every open list refetch every ticket it shows. Here an entry is
 * dropped by exactly the realtime events that can change it, so a rerun refetches only what changed
 * and reuses the rest. The TTL is the backstop for an event this tab never received.
 */
const TTL_MS = 45_000;

interface Entry {
  key: string;
  product: string;
  service: string;
  targetEnv: string;
  /** When the request was issued — the response is no older than this. */
  fetchedAt: number;
  result: Promise<WorkItemContext>;
}

const entries = new Map<string, Entry>();
let subscribed = false;

function sameIdentity(value: string, wanted: string | null | undefined): boolean {
  // A field the event doesn't carry narrows nothing. Case-insensitive because the server matches
  // keys that way in places; over-invalidating costs one request, under-invalidating a wrong cell.
  return !wanted || value.toLowerCase() === wanted.toLowerCase();
}

/**
 * Drops every entry matching the identity fields the event carries: a work-item event with its full
 * (key, product, service, environment) drops one entry, one without a service — the sign-off
 * broadcasts have none — drops that ticket for every service, and one with no key drops the lot.
 */
export function invalidateWorkItemContext(
  evt: Pick<EntityChangedEvent, 'key' | 'product' | 'service' | 'environment'>,
): void {
  for (const [id, entry] of entries) {
    if (
      sameIdentity(entry.key, evt.key) &&
      sameIdentity(entry.product, evt.product) &&
      sameIdentity(entry.service, evt.service) &&
      sameIdentity(entry.targetEnv, evt.environment)
    ) {
      entries.delete(id);
    }
  }
}

function ensureSubscribed(): void {
  if (subscribed) return;
  subscribed = true;
  // Never unsubscribed: the cache outlives the page so a remount can reuse it, and an entry has to
  // hear about changes made while no list was mounted to stay trustworthy.
  subscribeEntityEvents((evt) => {
    if (evt.entity === 'work-item') {
      invalidateWorkItemContext(evt);
    } else if (evt.entity === 'promotion' && evt.action === 'created') {
      // A new promotion clears the Issue and Blocked sign-offs on the work items it carries (the
      // fresh build is a different thing to judge) and that sends no work-item event. The broadcast
      // names its product and target env but not the tickets, so drop that whole slice.
      invalidateWorkItemContext({ product: evt.product, environment: evt.environment });
    }
  });
  // Events sent while the connection was down are gone — any entry may be stale.
  subscribeReconnect(() => entries.clear());
}

/** {@link api.getWorkItemContext}, answered from the cache when a live entry exists. */
export function getWorkItemContextCached(
  key: string,
  product: string,
  service: string,
  targetEnv: string,
): Promise<WorkItemContext> {
  ensureSubscribed();
  const now = Date.now();
  const id = JSON.stringify([key, product, service, targetEnv]);
  const hit = entries.get(id);
  if (hit && now - hit.fetchedAt < TTL_MS) return hit.result;

  for (const [staleId, entry] of entries) {
    if (now - entry.fetchedAt >= TTL_MS) entries.delete(staleId);
  }
  const entry: Entry = {
    key,
    product,
    service,
    targetEnv,
    fetchedAt: now,
    result: api.getWorkItemContext(key, product, service, targetEnv),
  };
  entries.set(id, entry);
  // A failure isn't an answer: forget it so the next run asks again instead of replaying the error.
  entry.result.catch(() => {
    if (entries.get(id) === entry) entries.delete(id);
  });
  return entry.result;
}
