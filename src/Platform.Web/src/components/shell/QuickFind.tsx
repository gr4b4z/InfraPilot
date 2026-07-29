import { useEffect, useMemo, useRef, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { Loader2, Search } from 'lucide-react';
import { Dialog } from '@/components/ui/Dialog';
import { useSearchScopeStore, type SearchHit } from '@/stores/searchScopeStore';
import { workItemSearchScope } from './searchScopes';

/** Long enough that typing "OBS-1" doesn't fire three requests, short enough to feel immediate. */
const DEBOUNCE_MS = 250;

/**
 * `/` — search whatever the current page lists.
 *
 * The scope comes from the page (see `searchScopeStore`), so on the promotions list this searches
 * promotions and on a product's deployments it searches that product's services. A page that
 * registers nothing falls back to work items, which is the most common thing to go looking for and
 * keeps `/` from being a dead key anywhere.
 */
export function QuickFind({ open, onClose }: { open: boolean; onClose: () => void }) {
  const navigate = useNavigate();
  const registered = useSearchScopeStore((s) => s.scope);
  const scope = useMemo(() => registered ?? workItemSearchScope(), [registered]);

  const [query, setQuery] = useState('');
  const [hits, setHits] = useState<SearchHit[]>([]);
  const [searching, setSearching] = useState(false);
  const [highlighted, setHighlighted] = useState(0);
  const inputRef = useRef<HTMLInputElement>(null);
  const listRef = useRef<HTMLDivElement>(null);

  // Start clean on every open — a stale query from last time is never what you want, and the scope
  // may have changed since.
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
        const results = await scope.search(q);
        if (cancelled) return;
        setHits(results);
        setHighlighted(0);
      } catch {
        if (!cancelled) setHits([]);
      } finally {
        if (!cancelled) setSearching(false);
      }
    }, DEBOUNCE_MS);
    return () => { cancelled = true; clearTimeout(timer); };
  }, [query, open, scope]);

  const openHit = (hit: SearchHit) => {
    onClose();
    navigate(hit.to);
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
    <Dialog onClose={onClose} ariaLabel={`Search ${scope.label}`} initialFocusRef={inputRef}>
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
          placeholder={scope.placeholder}
          className="flex-1 bg-transparent text-[14px] outline-none"
          style={{ color: 'var(--text-primary)' }}
          role="combobox"
          aria-expanded={hits.length > 0}
          aria-controls="quickfind-results"
          aria-activedescendant={hits.length > 0 ? `quickfind-hit-${highlighted}` : undefined}
        />
        {/* Naming the scope is what stops `/` feeling unpredictable — you can see what it will search
            before you type. */}
        <span
          className="shrink-0 px-1.5 py-0.5 rounded text-[10px] font-medium"
          style={{ backgroundColor: 'var(--accent-bg)', color: 'var(--accent)' }}
        >
          {scope.label}
        </span>
        {searching && <Loader2 size={14} className="animate-spin" style={{ color: 'var(--text-muted)' }} />}
      </div>

      <div ref={listRef} id="quickfind-results" role="listbox" className="max-h-80 overflow-y-auto">
        {query.trim().length < 2 ? (
          <p className="px-3 py-3 text-[12px]" style={{ color: 'var(--text-muted)' }}>
            Type at least two characters.
          </p>
        ) : !searching && hits.length === 0 ? (
          <p className="px-3 py-3 text-[12px]" style={{ color: 'var(--text-muted)' }}>
            Nothing matched “{query.trim()}”.
          </p>
        ) : (
          hits.map((hit, i) => (
            <button
              key={hit.id}
              id={`quickfind-hit-${i}`}
              data-hit
              role="option"
              aria-selected={highlighted === i}
              type="button"
              tabIndex={-1}
              onMouseEnter={() => setHighlighted(i)}
              onClick={() => openHit(hit)}
              className="w-full flex flex-col items-start px-3 py-2 text-left transition-colors"
              style={{ backgroundColor: highlighted === i ? 'var(--accent-muted)' : undefined }}
            >
              <span className="text-[13px] font-medium truncate w-full" style={{ color: 'var(--text-primary)' }}>
                {hit.title}
              </span>
              {hit.subtitle && (
                <span className="text-[11px] truncate w-full" style={{ color: 'var(--text-muted)' }}>
                  {hit.subtitle}
                </span>
              )}
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
