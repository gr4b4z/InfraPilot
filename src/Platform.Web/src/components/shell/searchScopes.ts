import { api } from '@/lib/api';
import type { PromotionCandidate } from '@/lib/api';
import { workItemDetailPath } from '@/lib/workItem';
import type { SearchHit, SearchScope } from '@/stores/searchScopeStore';

/**
 * The search scopes that more than one page needs, plus the fallback.
 *
 * Kept out of the page components because two of them are shared: the promotions list and the
 * promotion detail page both search promotions, and any page without its own scope searches work
 * items.
 */

/** Cap on server-side promotion lookups. The dialog shows a short list, not a report. */
const SEARCH_LIMIT = 25;

/**
 * Work items — the fallback scope.
 *
 * There is no work-item search endpoint, so this rides the promotions list's `reference` filter,
 * which the server already matches against source-event references (work-item keys, PR numbers,
 * commits). The consequence worth knowing: it finds work items *attached to a promotion*. One that
 * never was isn't reachable, and no client-side query would change that.
 */
export function workItemSearchScope(): SearchScope {
  return {
    label: 'Work items',
    placeholder: 'Find a work item — key, PR number or commit…',
    search: async (query) => {
      const res = await api.listPromotions({ reference: query, limit: SEARCH_LIMIT });
      return expandWorkItems(res.candidates ?? [], query);
    },
  };
}

/** Promotions, by product, service, target env or attached reference. */
export function promotionSearchScope(): SearchScope {
  return {
    label: 'Promotions',
    placeholder: 'Find a promotion — product, service, env or reference…',
    search: async (query) => {
      // Two passes because the server matches references and product/service separately: a reference
      // lookup finds "the promotion carrying OBS-900", a product lookup finds "acme's promotions".
      // Merged and de-duplicated so typing either kind of thing works.
      const [byReference, byProduct] = await Promise.all([
        api.listPromotions({ reference: query, limit: SEARCH_LIMIT }).catch(() => ({ candidates: [] })),
        api.listPromotions({ product: query, limit: SEARCH_LIMIT }).catch(() => ({ candidates: [] })),
      ]);
      const seen = new Set<string>();
      const hits: SearchHit[] = [];
      // Both halves are already server-filtered, so everything that came back is a match — the only
      // work left is dropping the candidates that satisfied both queries.
      for (const c of [...(byReference.candidates ?? []), ...(byProduct.candidates ?? [])]) {
        if (seen.has(c.id)) continue;
        seen.add(c.id);
        hits.push({
          id: c.id,
          title: `${c.product} / ${c.service} → ${c.targetEnv}`,
          subtitle: `v${c.version} · ${c.status}`,
          to: `/promotions/${c.id}`,
        });
      }
      return hits;
    },
  };
}

/**
 * Flattens candidates into their work-item references, keeping only those whose key or title matches.
 *
 * The server matched the *candidate* — a bundle can carry ten work items where one matched — so
 * without this the list would offer nine items the user didn't ask for. De-duplicated on
 * (key, product, targetEnv), the triple a decision keys on.
 */
function expandWorkItems(candidates: PromotionCandidate[], query: string): SearchHit[] {
  const needle = query.toLowerCase();
  const seen = new Set<string>();
  const hits: SearchHit[] = [];
  for (const candidate of candidates) {
    for (const ref of candidate.sourceEventReferences ?? []) {
      if (ref.type !== 'work-item') continue;
      const key = (ref.key ?? '').trim();
      if (!key) continue;
      const title = ref.title ?? null;
      const matches = key.toLowerCase().includes(needle)
        || (title ?? '').toLowerCase().includes(needle);
      if (!matches) continue;
      const identity = `${key}|${candidate.product}|${candidate.targetEnv}`;
      if (seen.has(identity)) continue;
      seen.add(identity);
      hits.push({
        id: identity,
        title: title ? `${key} — ${title}` : key,
        subtitle: `${candidate.product} · ${candidate.targetEnv}`,
        to: workItemDetailPath(key, candidate.product, candidate.targetEnv, candidate.id),
      });
    }
  }
  return hits;
}
