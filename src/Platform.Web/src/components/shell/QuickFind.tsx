import { useEffect, useRef, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { Loader2, Search, Ticket } from 'lucide-react';
import { api } from '@/lib/api';
import type { PromotionCandidate } from '@/lib/api';
import { workItemDetailPath } from '@/lib/workItem';
import { Dialog } from '@/components/ui/Dialog';

/**
 * "Find a work item" — the keyboard entry point to the work the app is about.
 *
 * There is no work-item search endpoint, so this rides the promotions list's `reference` filter,
 * which the server already matches against source-event references (work-item keys, PR numbers,
 * commits). Every candidate that comes back is expanded into its work-item references, which is what
 * the user was actually looking for and what carries the (key, product, targetEnv) triple a detail
 * page needs.
 *
 * The consequence worth knowing: results are work items that are attached to a promotion. An item
 * that has never been part of one isn't reachable here, and no client-side query would change that —
 * it would need a real search endpoint.
 */
interface Hit {
  key: string;
  title: string | null;
  product: string;
  targetEnv: string;
  candidateId: string;
}

/** Long enough that typing "OBS-1" doesn't fire three requests, short enough to feel immediate. */
const DEBOUNCE_MS = 250;

export function QuickFind({ open, onClose }: { open: boolean; onClose: () => void }) {
  const navigate = useNavigate();
  const [query, setQuery] = useState('');
  const [hits, setHits] = useState<Hit[]>([]);
  const [searching, setSearching] = useState(false);
  const [highlighted, setHighlighted] = useState(0);
  const inputRef = useRef<HTMLInputElement>(null);
  const listRef = useRef<HTMLDivElement>(null);

  // Start clean on every open — a stale query from last time is never what you want.
  useEffect(() => {
    if (open) {
      setQuery('');
      setHits([]);
      setHighlighted(0);
    }
  }, [open]);

  useEffect(() => {
    const q = query.trim();
    if (!open || q.length < 2) { setHits([]); setSearching(false); return; }
    let cancelled = false;
    setSearching(true);
    const timer = setTimeout(async () => {
      try {
        const res = await api.listPromotions({ reference: q, limit: 25 });
        if (cancelled) return;
        setHits(expandWorkItems(res.candidates ?? [], q));
        setHighlighted(0);
      } catch {
        if (!cancelled) setHits([]);
      } finally {
        if (!cancelled) setSearching(false);
      }
    }, DEBOUNCE_MS);
    return () => { cancelled = true; clearTimeout(timer); };
  }, [query, open]);

  const openHit = (hit: Hit) => {
    onClose();
    navigate(workItemDetailPath(hit.key, hit.product, hit.targetEnv, hit.candidateId));
  };

  const onKeyDown = (event: React.KeyboardEvent<HTMLInputElement>) => {
    if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
      if (hits.length === 0) return;
      event.preventDefault();
      const next = Math.max(
        0,
        Math.min(highlighted + (event.key === 'ArrowDown' ? 1 : -1), hits.length - 1),
      );
      setHighlighted(next);
      listRef.current?.querySelectorAll<HTMLElement>('[data-hit]')[next]
        ?.scrollIntoView({ block: 'nearest' });
      return;
    }
    if (event.key === 'Enter' && hits[highlighted]) {
      event.preventDefault();
      openHit(hits[highlighted]);
    }
  };

  if (!open) return null;

  return (
    <Dialog onClose={onClose} ariaLabel="Find a work item" initialFocusRef={inputRef}>
      <div
        className="flex items-center gap-2 px-3 py-2.5 border-b"
        style={{ borderColor: 'var(--border-color)' }}
      >
        <Search size={15} style={{ color: 'var(--text-muted)', flexShrink: 0 }} />
        <input
          ref={inputRef}
          value={query}
          onChange={(e) => setQuery(e.target.value)}
          onKeyDown={onKeyDown}
          placeholder="Find a work item — key, PR number or commit…"
          className="flex-1 bg-transparent text-[14px] outline-none"
          style={{ color: 'var(--text-primary)' }}
          role="combobox"
          aria-expanded={hits.length > 0}
          aria-controls="quickfind-results"
          aria-activedescendant={hits.length > 0 ? `quickfind-hit-${highlighted}` : undefined}
        />
        {searching && <Loader2 size={14} className="animate-spin" style={{ color: 'var(--text-muted)' }} />}
      </div>

      <div ref={listRef} id="quickfind-results" role="listbox" className="max-h-80 overflow-y-auto">
        {query.trim().length < 2 ? (
          <p className="px-3 py-3 text-[12px]" style={{ color: 'var(--text-muted)' }}>
            Type at least two characters. Matches work items, pull requests and commits attached to a
            promotion.
          </p>
        ) : !searching && hits.length === 0 ? (
          <p className="px-3 py-3 text-[12px]" style={{ color: 'var(--text-muted)' }}>
            Nothing matched “{query.trim()}”.
          </p>
        ) : (
          hits.map((hit, i) => (
            <button
              key={`${hit.key}-${hit.product}-${hit.targetEnv}-${hit.candidateId}`}
              id={`quickfind-hit-${i}`}
              data-hit
              role="option"
              aria-selected={highlighted === i}
              type="button"
              onMouseEnter={() => setHighlighted(i)}
              onClick={() => openHit(hit)}
              className="w-full flex items-start gap-2 px-3 py-2 text-left transition-colors"
              style={{ backgroundColor: highlighted === i ? 'var(--accent-muted)' : undefined }}
            >
              <Ticket size={13} style={{ color: 'var(--text-muted)', marginTop: 2, flexShrink: 0 }} />
              <span className="min-w-0">
                <span className="block text-[13px] font-medium truncate" style={{ color: 'var(--text-primary)' }}>
                  {hit.key}
                  {hit.title ? ` — ${hit.title}` : ''}
                </span>
                <span className="block text-[11px] truncate" style={{ color: 'var(--text-muted)' }}>
                  {hit.product} · {hit.targetEnv}
                </span>
              </span>
            </button>
          ))
        )}
      </div>

      <div
        className="px-3 py-2 border-t text-[11px] flex items-center gap-3"
        style={{ borderColor: 'var(--border-color)', color: 'var(--text-muted)' }}
      >
        <span>↑↓ to move</span>
        <span>Enter to open</span>
        <span>Esc to close</span>
      </div>
    </Dialog>
  );
}

/**
 * Flattens candidates into their work-item references, keeping only those whose key or title matches
 * the query. The server matched the *candidate* — a bundle can carry ten work items where one
 * matched — so without this the list would offer nine items the user didn't ask for.
 *
 * Deduplicated on (key, product, targetEnv): the same work item can ride several candidates, and
 * that triple is the identity a decision keys on.
 */
function expandWorkItems(candidates: PromotionCandidate[], query: string): Hit[] {
  const needle = query.toLowerCase();
  const seen = new Set<string>();
  const hits: Hit[] = [];
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
        key,
        title,
        product: candidate.product,
        targetEnv: candidate.targetEnv,
        candidateId: candidate.id,
      });
    }
  }
  return hits;
}
